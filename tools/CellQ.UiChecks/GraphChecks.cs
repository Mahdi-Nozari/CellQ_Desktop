using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using CellQ.Desktop;
using CellQ.Desktop.Controls;
using CellQ.Desktop.Services;
using CellQ.Desktop.ViewModels;
using CellQ.Desktop.Views;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.UiChecks;

internal static partial class Program
{
    private sealed class GraphRequests : ILogGraphWindows
    {
        public int Files;
        public string? Path;
        public void ShowFile(string path) => Path = path;
        public void ChooseFile() => Files++;
    }
    private static void GraphChecks(App app, FrameworkElement mainRoot, string output)
    {
        int opened = 0;
        var connectionService = new DeviceConnectionService(() => { opened++; throw new InvalidOperationException("Graph tests must not open hardware."); });
        var connection = new ConnectionViewModel(connectionService, new FakePortCatalog(), app.Dispatcher);
        var requests = new GraphRequests();
        var shell = new ShellViewModel(connection, settings: TestSettings(output), graphs: requests);
        var logs = (DataLogsViewModel)shell.Pages[Destination.DataLogs];
        mainRoot.DataContext = shell; shell.Select(Destination.DataLogs); Layout(mainRoot, 1120, 760);
        Require(Descendants<Button>(mainRoot).Single(button => Equals(button.Content, "Open log file…")).IsEnabled && !Descendants<Button>(mainRoot).Any(button => Equals(button.Content, "Sample graph")), "Local graphs work offline and the sample action is removed");
        Require(!typeof(App).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)), "The production assembly contains no bundled CSV demo data");
        Require(!logs.ViewGraphCommand.CanExecute(null), "An un-downloaded device row cannot trigger an implicit download to graph");
        logs.OpenGraphFileCommand.Execute(null);
        Require(requests.Files == 1 && opened == 0 && connection.State.Phase == ConnectionPhase.Disconnected, "Offline graph actions never connect or enter maintenance");
        string savedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(output, "graph-local", Guid.NewGuid().ToString("N"), "copy.csv"));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(savedPath)!);
        using (var fixture = typeof(Program).Assembly.GetManifestResourceStream("CellQ.UiChecks.PressureLogFixture.csv")!) using (var destination = File.Create(savedPath)) fixture.CopyTo(destination);
        var row = new LogRow(new("/DATA/20261011/log_001.csv", "log_001.csv", "20261011", (uint)new FileInfo(savedPath).Length)) { Verified = new("/DATA/20261011/log_001.csv", savedPath, (uint)new FileInfo(savedPath).Length, 0, new string('A', 64)) };
        logs.Logs.Add(row); logs.SelectedLog = row; logs.ViewGraphCommand.Execute(null);
        Require(requests.Path == savedPath && opened == 0, "View graph uses the selected local path and no serial workflow");
        var disposal = shell.DisposeAsync().AsTask(); UiUntil(() => disposal.IsCompleted, mainRoot); disposal.GetAwaiter().GetResult();

        ThemeService.Apply("Light");
        var window = new LogGraphWindow(GraphSource.Saved(savedPath));
        var model = window.ViewModel;
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.Resources.MergedDictionaries.Add(app.Resources); root.DataContext = model;
        TextElement.SetFontFamily(root, new FontFamily("Segoe UI")); TextElement.SetFontSize(root, 14); root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        var load = model.LoadAsync(); GraphWait(root, 1000, 660, () => load.IsCompleted); load.GetAwaiter().GetResult();
        var plot = Descendants<PressurePlot>(root).Single();
        GraphWait(root, 1000, 660, () => plot.HasFrame);
        Require(model.Data is { Count: 8027, PresetName: "Heart Test" } && model.SourceKind == "Saved log" && model.SourceName == "copy.csv" && model.SourceNotice.StartsWith("Local CSV", StringComparison.Ordinal), "The popup loads a real local CSV and labels its source honestly");
        Require(model.Span == 30000 && model.Data!.PressureAt(0, 0) == 25.8, "The initial graph shows thirty seconds using actual kPa values");
        var checks = Descendants<CheckBox>(root).ToArray();
        Require(checks.Length == 6 && checks.All(check => check.IsChecked == true) && model.Channels.Select(channel => ((SolidColorBrush)channel.Brush).Color).Distinct().Count() == 6, "All six channels have independent checked visibility controls and distinct display-relative colors");
        checks[2].IsChecked = false;
        Require(!model.Channels[2].IsVisible && model.Channels.Where(channel => channel.Index != 2).All(channel => channel.IsVisible), "Hiding one channel preserves the other five");
        checks[2].IsChecked = true;
        model.SpanText = "15";
        var spanBox = Descendants<TextBox>(root).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Time span");
        var enter = spanBox.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.Enter);
        enter.Command.Execute(null);
        Require(model.Span == 15000 && model.SpanText == "00:00:15", "Enter applies an editable duration and normalizes its readable time format");
        model.JumpText = "00:01:40"; model.JumpCommand.Execute(null);
        Require(model.Start == 92500 && model.CursorTime == 100000, "Jump centers the graph on the requested time and marks it");
        var jumpBox = Descendants<TextBox>(root).Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Jump to elapsed time");
        var go = Descendants<Button>(root).Single(button => Equals(button.Content, "Go"));
        InvokeGraphButton(Descendants<Button>(root).Single(button => Equals(button.Content, "Show whole test")));
        GraphWait(root, 1000, 660, () => model.Span == model.Viewport!.Range);
        jumpBox.Text = "100";
        InvokeGraphButton(go);
        GraphWait(root, 1000, 660, () => model.CursorTime == 100000 && plot.HasFrame);
        Require(model.Span == 15000 && model.Start == 92500 && model.JumpText == "00:01:40", "Typing a jump and clicking Go from whole-test view restores detail, centers the requested time and normalizes the entry");
        jumpBox.Text = "00:00:45";
        jumpBox.InputBindings.OfType<KeyBinding>().Single(binding => binding.Key == Key.Enter).Command.Execute(null);
        GraphWait(root, 1000, 660, () => plot.HasFrame);
        Require(model.Start == 37500 && model.CursorTime == 45000, "Jump Enter binding uses the actual textbox value and preserves the chosen time span");
        var scroll = Descendants<System.Windows.Controls.Primitives.ScrollBar>(root).Single(bar => System.Windows.Automation.AutomationProperties.GetName(bar) == "Graph time position");
        scroll.Value = 50000;
        Require(model.Start == 50000, "The horizontal scrollbar pans to the chosen elapsed time");
        double span = model.Span; model.ZoomAt(.25, .8);
        Require(model.Span < span, "Mouse-wheel zoom logic changes the time window");
        model.SpanText = "not a time"; model.ApplySpanCommand.Execute(null);
        Require(model.StatusMessage.StartsWith("Enter a time", StringComparison.Ordinal), "Invalid zoom text produces a readable correction");
        model.JumpText = "99:00:00"; double previous = model.Start; model.JumpCommand.Execute(null);
        Require(model.Start == previous && model.StatusMessage.Contains("recorded range"), "An out-of-range jump does not move into empty time");
        model.ShowAllCommand.Execute(null);
        model.JumpText = "00:00:00";
        Require(model.Start == 0 && model.Span == 161550 && model.MaximumStart == 0, "Show whole test resets zoom/pan to the full recorded duration");
        GraphWait(root, 1000, 660, () => plot.HasFrame);
        SaveGraph(root, System.IO.Path.Combine(output, "LocalGraph-whole-test.png"), 1000, 660);
        model.SpanText = "30"; model.ApplySpanCommand.Execute(null); model.PanTo(0); model.SetCursor(106);
        Require(model.Channels[0].Reading == 25.8.ToString("0.0", CultureInfo.CurrentCulture) + " kPa" && model.ReadingTime.EndsWith("00:00:00.106", StringComparison.Ordinal), "Cursor readout uses the closest actual sample and its exact recorded time");
        GraphWait(root, 1000, 660, () => plot.HasFrame); SaveGraph(root, System.IO.Path.Combine(output, "LocalGraph-Light.png"), 1000, 660);
        ThemeService.Apply("Dark"); GraphWait(root, 1000, 660, () => plot.HasFrame);
        Require(model.Channels.All(channel => Contrast(((SolidColorBrush)channel.Brush).Color, ((SolidColorBrush)app.Resources["SurfaceBrush"]).Color) >= 3), "Dark graph traces are adapted for readable contrast while keeping the six display hues");
        SaveGraph(root, System.IO.Path.Combine(output, "LocalGraph-Dark.png"), 1000, 660);
        ThemeService.Apply("Light"); GraphWait(root, 900, 640, () => plot.HasFrame);
        Require(Descendants<Button>(root).Where(button => button.IsVisible || button.Visibility == Visibility.Visible).All(button => button.ActualWidth > 0), "Graph controls remain available at a compact content size");
        SaveGraph(root, System.IO.Path.Combine(output, "LocalGraph-minimum.png"), 900, 640);
        foreach (var checkbox in checks) checkbox.IsChecked = false;
        GraphWait(root, 900, 640, () => plot.HasFrame);
        Require(model.Channels.All(channel => !channel.IsVisible) && model.Data!.Count == 8027, "Hiding every curve preserves the loaded data and renders an honest empty-channel state");
        SaveGraph(root, System.IO.Path.Combine(output, "LocalGraph-channels-hidden.png"), 900, 640);
        window.Close(); plot.Stop(); model.Dispose();
        Brush before = model.Channels[0].Brush; ThemeService.Apply("Dark");
        Require(ReferenceEquals(before, model.Channels[0].Brush), "Closing the popup unsubscribes theme events and stops its rendering/loading work");
        ThemeService.Apply("Light");
        using var bad = new LogGraphViewModel(new("Malformed local CSV", () => new MemoryStream([1, 2, 3])));
        var failed = bad.LoadAsync(); GraphWait(root, 900, 570, () => failed.IsCompleted); failed.GetAwaiter().GetResult();
        Require(!bad.HasData && !bad.IsLoading && bad.StatusMessage.StartsWith("Could not show this log", StringComparison.Ordinal), "Malformed local CSV fails clearly without displaying fake graph readings");
        using var fileModel = new LogGraphViewModel(GraphSource.Saved(savedPath));
        var fileLoad = fileModel.LoadAsync(); GraphWait(root, 900, 570, () => fileLoad.IsCompleted); fileLoad.GetAwaiter().GetResult();
        Require(fileModel.HasData && fileModel.SourceKind == "Saved log" && !fileModel.SourceNotice.Contains("device connection"), "Local CSVs open with their own honest source label independently of hardware");
    }
    private static void GraphWait(FrameworkElement root, int width, int height, Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        do { Layout(root, width, height); if (clock.Elapsed.TotalSeconds > 12) throw new TimeoutException("The graph did not finish loading or rendering."); if (!ready()) Thread.Sleep(5); } while (!ready());
    }
    private static void InvokeGraphButton(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void SaveGraph(FrameworkElement root, string path, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static double Contrast(Color a, Color b)
    {
        static double Part(byte value) { double scaled = value / 255d; return scaled <= .04045 ? scaled / 12.92 : Math.Pow((scaled + .055) / 1.055, 2.4); }
        static double Light(Color value) => .2126 * Part(value.R) + .7152 * Part(value.G) + .0722 * Part(value.B);
        double first = Light(a), second = Light(b); return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
}
