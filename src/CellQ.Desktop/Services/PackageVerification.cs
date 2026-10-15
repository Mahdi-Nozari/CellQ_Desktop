using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;

namespace CellQ.Desktop.Services;

// Explicit offline delivery check: it cannot create a serial transport or send a network request.
internal static class PackageVerification
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint CountExecutableIcons(string file, int index, IntPtr large, IntPtr small, uint count);
    private sealed class EmptyPorts : IPortCatalog
    { public Task<IReadOnlyList<string>> GetPortsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>([]); }
    private sealed class NoFolderDialog : IFolderPicker
    { public string? ChooseFolder(string initialDirectory) => throw new InvalidOperationException("Package verification cannot show a folder dialog."); }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The delivery check deliberately asserts that Assembly.Location is empty in a single-file publish.")]
    internal static async Task RunAsync(App app, string output, string? logPath = null)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new IOException("The portable package must run as x64.");
        if (CountExecutableIcons(Environment.ProcessPath!, -1, IntPtr.Zero, IntPtr.Zero, 0) == 0) throw new IOException("The executable is missing its application icon.");
        if (typeof(App).Assembly.Location.Length != 0 || typeof(object).Assembly.Location.Length != 0)
            throw new IOException("The app and runtime core library must both come from the single-file bundle.");
        using var process = Process.GetCurrentProcess();
        string extraction = Path.GetFullPath(Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? Path.Combine(Path.GetTempPath(), ".net")) + Path.DirectorySeparatorChar;
        // The current SDK links CoreCLR into the single-file host. Older hosts may extract it.
        var coreClr = process.Modules.Cast<ProcessModule>().SingleOrDefault(module => string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
        if (coreClr is not null && !coreClr.FileName.StartsWith(extraction, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The runtime was loaded from outside the portable extraction cache.");
        string runtime = coreClr?.FileName ?? Environment.ProcessPath!;
        // Metadata enumeration must also work from the bundle; it does not open or probe COM ports.
        _ = await new SerialPortCatalog().GetPortsAsync(default);
        string tools = await new EmbeddedUploaderFiles(Path.Combine(output, "Tools")).GetDirectoryAsync(default);
        var lines = new ConcurrentQueue<string>();
        int exit = await new UploaderProcess().RunAsync(new(Path.Combine(tools, "avrdude.exe"), tools, ["--help"]), lines.Enqueue, TimeSpan.FromSeconds(15));
        if (exit != 0 || !lines.Any(line => line.Contains("avrdude", StringComparison.OrdinalIgnoreCase))) throw new IOException("The embedded uploader did not pass its offline help check.");
        var settings = new SettingsViewModel(new AppSettingsStore(Path.Combine(output, "settings.json"), Path.Combine(output, "Documents")), new NoFolderDialog());
        var window = new MainWindow(new DeviceConnectionService(() => throw new InvalidOperationException("Package verification cannot open a serial port.")), new EmptyPorts(), settings);
        if (window.Icon is null) throw new IOException("The window icon is missing from the bundle.");
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.Resources.MergedDictionaries.Add(app.Resources);
        root.DataContext = window.ViewModel;
        TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(root, 14);
        root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        try
        {
            foreach (var destination in Enum.GetValues<Destination>())
            {
                window.ViewModel.Select(destination);
                root.Measure(new Size(1040, 700)); root.Arrange(new Rect(0, 0, 1040, 700)); root.UpdateLayout();
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1040, 700, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var image = File.Create(Path.Combine(output, destination + ".png")); encoder.Save(image);
            }
            if (window.ViewModel.Connection.State.Phase != ConnectionPhase.Disconnected) throw new IOException("Package verification unexpectedly changed connection state.");
        }
        finally { await window.ViewModel.DisposeAsync(); }
        if (typeof(App).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))) throw new IOException("The release must not include CSV demo data.");
        string graphCheck = "";
        if (logPath is not null)
        {
            var graphWindow = new CellQ.Desktop.Views.LogGraphWindow(GraphSource.Saved(logPath));
            var graphRoot = (FrameworkElement)graphWindow.Content; graphWindow.Content = null;
            graphRoot.Resources.MergedDictionaries.Add(app.Resources); graphRoot.DataContext = graphWindow.ViewModel;
            try
            {
                await graphWindow.ViewModel.LoadAsync();
                if (graphWindow.ViewModel.Data is not { Count: > 0 }) throw new IOException("The local CSV did not load correctly.");
                var deadline = Stopwatch.StartNew();
                while (!graphWindow.Plot.HasFrame)
                {
                    graphRoot.Measure(new Size(1000, 660)); graphRoot.Arrange(new Rect(0, 0, 1000, 660)); graphRoot.UpdateLayout();
                    await Task.Delay(20);
                    if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new IOException("The portable graph did not render.");
                }
                var bitmap = new RenderTargetBitmap(1000, 660, 96, 96, PixelFormats.Pbgra32); bitmap.Render(graphRoot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var picture = File.Create(Path.Combine(output, "LocalGraph.png")); encoder.Save(picture);
                graphCheck = "local CSV pressure graph rendered; ";
            }
            finally { graphWindow.Plot.Stop(); graphWindow.ViewModel.Dispose(); graphWindow.Close(); }
        }
        process.Refresh();
        string wpf = process.Modules.Cast<ProcessModule>().Single(module => string.Equals(module.ModuleName, "wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase)).FileName;
        if (!wpf.StartsWith(extraction, StringComparison.OrdinalIgnoreCase)) throw new IOException("WPF was loaded from outside the portable extraction cache.");
        await File.WriteAllTextAsync(Path.Combine(output, "PASS.txt"), $"Single-file x64 app; bundled runtime {Environment.Version}; all six WPF pages rendered at minimum size; {graphCheck}no CSV demo data included; uploader manifest/files verified and native --help passed; license/source included. No network requests, serial ports or uploads.\nTools: {tools}\nRuntime host: {runtime}\nExtracted WPF: {wpf}\n");
    }
}
