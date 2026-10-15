using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CellQ.Desktop;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;
using CellQ.Testing;
using CellQ.Desktop.Services;
using System.IO.Ports;
using System.Net.Http;

namespace CellQ.UiChecks;

internal static partial class Program
{
    private static int _checks;
    private static readonly BindingErrors Errors = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/ui-checks");
            Directory.CreateDirectory(output);
            PresentationTraceSources.DataBindingSource.Listeners.Add(Errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            var app = new App();
            app.InitializeComponent();
            // Async view-model commands must resume on the STA dispatcher, as in the running app.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            using (var configuredPort = SerialPortTransport.CreateConfiguredPort("COM1"))
            {
                Require(!configuredPort.IsOpen && configuredPort.BaudRate == 250000 && configuredPort.DataBits == 8 && configuredPort.Parity == Parity.None && configuredPort.StopBits == StopBits.One, "Actual SerialPort factory sets 250000 8-N-1 without opening hardware");
                Require(!configuredPort.DtrEnable && !configuredPort.RtsEnable && configuredPort.Handshake == Handshake.None, "Actual SerialPort factory explicitly disables DTR, RTS, and handshake before open");
            }
            var fake = FakeDevice.Responding("MAINT", "test-only-fixture");
            var service = new DeviceConnectionService(() => fake);
            var settingsStore = new AppSettingsStore(Path.Combine(output, "settings", Guid.NewGuid().ToString("N"), "settings.json"));
            var folderPicker = new TestFolderPicker();
            var window = new MainWindow(service, new FakePortCatalog(), new SettingsViewModel(settingsStore, folderPicker));
            var shell = window.ViewModel;
            var icon = new IconBitmapDecoder(new Uri("pack://application:,,,/CellQ.Desktop;component/Assets/CellQ.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Require(window.Icon is not null && icon.Frames.Any(frame => frame.PixelWidth == 16) && icon.Frames.Any(frame => frame.PixelWidth == 256), "The app windows load the replaceable multi-size CQ icon, including Explorer and taskbar sizes");
            var iconPreview = new PngBitmapEncoder(); iconPreview.Frames.Add(icon.Frames.OrderByDescending(frame => frame.PixelWidth).First());
            using (var iconFile = File.Create(Path.Combine(output, "AppIcon.png"))) iconPreview.Save(iconFile);
            Require(window.Width == 1120 && window.Height == 760, "Approved initial dimensions");
            Require(window.MinWidth == 1040 && window.MinHeight == 700, "Practical minimum dimensions");

            // Render the actual WPF content without opening an interactive window.
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            // The detached preview root must receive resource-change notifications too.
            root.Resources.MergedDictionaries.Add(app.Resources);
            root.DataContext = shell;
            root.UseLayoutRounding = true;
            TextElement.SetFontFamily(root, new FontFamily("Segoe UI"));
            TextElement.SetFontSize(root, 14);
            root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            foreach (var item in shell.MainNavigation.Concat(shell.UtilityNavigation))
            {
                item.Command.Execute(item.Destination);
                Layout(root, 1120, 760);
                Require(shell.CurrentPage == shell.Pages[item.Destination], $"Navigate to {item.Destination}");
                Require(shell.MainNavigation.Concat(shell.UtilityNavigation).Count(n => n.IsSelected) == 1, "Exactly one destination selected");
                Require(Descendants<TextBlock>(root).Any(t => t.Text == shell.CurrentPage.Title), "Page title renders");
                Require(Descendants<Button>(root).Where(b => b.Content is string text && (text == "Connect" || item.Destination != Destination.FirmwareUpdate && text == shell.CurrentPage.FirstAction)).All(b => !b.IsEnabled), "Device actions unavailable before port selection or identification; release lookup is device-independent");
                Require(Descendants<Button>(root).Count(b => b.DataContext is NavigationItem) == 6, "All six destinations available");
                Save(root, Path.Combine(output, $"{item.Destination}.png"), 1120, 760);
                if (item.Destination == Destination.Help) SaveHelpPreviews(root, output, "Light");
            }

            var logs = (DataLogsViewModel)shell.Pages[Destination.DataLogs];
            Require(logs.Logs.Count == 0 && ReferenceEquals(logs.Connection, shell.Connection), "Data Logs is live, empty, and uses the shared connection");
            Require(!logs.BrowseCommand.CanExecute(null) && !logs.DownloadCommand.CanExecute(null) && !logs.DeleteCommand.CanExecute(null), "Log operations unavailable before connection and selection");
            shell.Select(Destination.DataLogs);
            Layout(root, 1120, 760);
            Require(Descendants<TextBlock>(root).Any(t => t.Text == "Choose Browse device logs to read the device." && t.Visibility == Visibility.Visible), "Unloaded device list explained honestly");
            Save(root, Path.Combine(output, "DataLogs-empty.png"), 1120, 760);

            var device = (DeviceViewModel)shell.Pages[Destination.Device];
            var diagnostics = (DiagnosticsViewModel)shell.Pages[Destination.Diagnostics];
            Require(ReferenceEquals(device.Connection, diagnostics.Connection) && ReferenceEquals(device.Connection, shell.Connection), "Device, Diagnostics, and shell share one connection model");
            Require(diagnostics.FilteredMessages.IsEmpty, "Diagnostics starts with no invented debug messages");
            Require(!diagnostics.ShowTechnicalDetails, "Diagnostics starts with readable activity and hides raw technical traffic");
            foreach (var mode in new[] { DeviceMode.Idle, DeviceMode.Manual, DeviceMode.Test, DeviceMode.Maintenance })
                Require(DevicePresentation.Mode(mode) is not ("IDLE" or "MANUAL" or "TEST" or "MAINT"), "All known device modes have readable names");
            Require(fake.OpenCount == 0 && fake.Writes.IsEmpty, "Opening pages does not open a port or transmit commands");
            service.ConnectAsync("COM_TEST").GetAwaiter().GetResult();
            Layout(root, 1120, 760);
            Require(shell.Connection.State.Mode == DeviceMode.Maintenance && shell.Connection.FirmwareLabel == "test-only-fixture", "UI derives real reported fields from transport responses");
            shell.Select(Destination.Device); Layout(root, 1120, 760);
            Require(Descendants<Button>(root).Any(b => b.Content is string text && text.Contains("Refresh status")), "Everyday refresh action uses plain language");
            Require(Descendants<Expander>(root).Any(e => e.Header is string text && text == "Technical details" && !e.IsExpanded), "Device serial details are collapsed by default");
            Require(Descendants<TextBlock>(root).Where(IsPresented).All(t => !t.Text.Contains("DTR") && !t.Text.Contains("HELLO")), "Device default view does not expose serial terminology");
            var technicalDetails = Descendants<Expander>(root).Single(); technicalDetails.IsExpanded = true; Layout(root, 1120, 760);
            Require(Descendants<TextBlock>(root).Any(t => IsPresented(t) && t.Text.Contains("DTR off")), "Serial settings remain available on request");
            technicalDetails.IsExpanded = false;
            shell.Select(Destination.Diagnostics); Layout(root, 1120, 760);
            Require(fake.Writes.Count == 1, "Navigating Device/Diagnostics does not enter maintenance or send another request");
            fake.Feed("D,raw-test-debug,42\n");
            UiUntil(() => { shell.Connection.DrainMessages(); return shell.Connection.Messages.Any(m => m.Kind == "Debug" && m.Text == "D,raw-test-debug,42"); }, root);
            Require(diagnostics.FilteredMessages.Cast<DiagnosticMessage>().All(DevicePresentation.IsActivity), "Default Diagnostics filters out raw device chatter without removing original data");
            Require(Descendants<TextBlock>(root).Where(IsPresented).All(t => !t.Text.Contains("CQ1,") && !t.Text.Contains("250000")), "Default Diagnostics shows readable messages without protocol commands");
            diagnostics.Filter = "Device replied";
            Require(diagnostics.FilteredMessages.Cast<DiagnosticMessage>().Any(), "Default search matches the displayed plain-language activity");
            diagnostics.Filter = "";
            diagnostics.ShowTechnicalDetails = true;
            diagnostics.DebugOnly = true; diagnostics.Filter = "RAW-TEST";
            Require(diagnostics.FilteredMessages.Cast<DiagnosticMessage>().Single().Text == "D,raw-test-debug,42", "Diagnostics preserves and filters actual raw D lines");
            diagnostics.Filter = "";
            string longDebug = "D," + new string('x', 1000);
            fake.Feed(longDebug + "\n");
            UiUntil(() => { shell.Connection.DrainMessages(); return shell.Connection.Messages.Any(m => m.Text == longDebug); }, root);
            diagnostics.SelectedMessage = shell.Connection.Messages.Single(m => m.Text == longDebug);
            Layout(root, 1120, 760);
            Require(Descendants<TextBox>(root).Any(t => t.IsReadOnly && t.Text == longDebug), "Full raw debug text remains selectable and copyable without visual truncation");
            diagnostics.ShowTechnicalDetails = false; Layout(root, 1120, 760);
            Require(diagnostics.SelectedMessage is null && diagnostics.FilteredMessages.Cast<DiagnosticMessage>().All(DevicePresentation.IsActivity), "Hiding technical messages clears raw selection and restores readable activity");
            diagnostics.ShowTechnicalDetails = true;
            shell.Connection.Paused = true;
            int beforePause = shell.Connection.Messages.Count;
            service.RefreshHelloAsync().GetAwaiter().GetResult();
            Layout(root, 1120, 760); shell.Connection.DrainMessages();
            Require(shell.Connection.Messages.Count == beforePause && service.Snapshot.Mode == DeviceMode.Maintenance && !service.Snapshot.RequestPending, "Pause display keeps parser and HELLO reader running");
            shell.Connection.Paused = false; shell.Connection.DrainMessages();
            shell.Connection.ClearViewCommand.Execute(null);
            Require(shell.Connection.Messages.Count == 0 && service.Snapshot.Phase == ConnectionPhase.Connected, "Clear view only clears local display buffer");
            diagnostics.DebugOnly = false; diagnostics.Filter = "";
            service.DisconnectAsync().GetAwaiter().GetResult(); Layout(root, 1120, 760);
            Require(shell.Connection.State.Phase == ConnectionPhase.Disconnected && shell.Connection.FirmwareLabel == "—" && shell.Connection.ModeLabel == "Not available", "Disconnected UI clears stale real values");
            Require(fake.Writes.All(w => w == "CQ1,HELLO\n") && fake.Writes.Count == 2, "All UI connection/diagnostic tests sent only HELLO");
            LogWorkflowChecks(app, root, output);
            FirmwareWorkflowChecks(app, root, output);
            PortPickerChecks(root, output);
            GraphChecks(app, root, output);
            root.DataContext = shell;
            diagnostics.ShowTechnicalDetails = false;
            shell.Select(Destination.Device);
            Save(root, Path.Combine(output, "Device-minimum.png"), 1040, 700);
            shell.Select(Destination.Diagnostics);
            Save(root, Path.Combine(output, "Diagnostics-minimum.png"), 1040, 700);
            shell.Select(Destination.DataLogs);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                Save(root, Path.Combine(output, $"DataLogs-{(int)(scale * 100)}percent.png"), 1120, 760, scale);
                Require(root.ActualWidth == 1120 && root.ActualHeight == 760, "DPI render retains logical dimensions");
            }
            Save(root, Path.Combine(output, "DataLogs-minimum.png"), 1040, 700);
            foreach (var button in Descendants<Button>(root).Where(b => b.DataContext is NavigationItem))
            {
                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(new Size(button.ActualWidth, button.ActualHeight)));
                Require(bounds.Top >= 0 && bounds.Bottom <= 700, "Navigation fits minimum height");
            }
            SettingsChecks(app, root, shell, settingsStore, folderPicker, output);
            Task.Run(() => PackagingChecks(output)).GetAwaiter().GetResult();
            Require(fake.OpenCount == 1 && fake.Writes.Count == 2 && fake.Writes.All(w => w == "CQ1,HELLO\n"), "Settings changes never open a port or transmit device commands");
            Require(Errors.Messages.Count == 0, "No WPF binding warnings or errors: " + string.Join(Environment.NewLine, Errors.Messages));
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Require(closed, "Window closes after disposing the shared connection without reentrant Closing");
            Console.WriteLine($"PASS: {_checks} UI checks. Actual WPF renders: {output}");
            app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void LogWorkflowChecks(App app, FrameworkElement root, string output)
    {
        var sim = new LogDeviceSimulator();
        sim.Files.Add("/DATA/20261009/log_001.csv", System.Text.Encoding.UTF8.GetBytes("CELLQ_DATA,,1\nTEST_START,20261009T210217\nLOG_CREATED,20261009T210217\nFIRST_LOG_ELAPSED,0\nPRESET_ID,5\nPRESET_REV,35\nPRESET_NAME,Heart Test\nSOURCE,ANALOG\nSAMPLE_INTERVAL_MS,20\ntime,pressure\n1,20\n2,21\n"));
        sim.Files.Add("/DATA/NO_RTC/log_002.csv", [1, 2, 3]);
        var directory = Path.Combine(output, "local", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var dialogs = new TestLogDialogs(Path.Combine(directory, "verified.csv"));
        var service = new DeviceConnectionService(sim.CreateTransport);
        var connection = new ConnectionViewModel(service, new FakePortCatalog(), app.Dispatcher);
        connection.Ports.Add(new("COM_SIMULATOR")); connection.SelectedPort = "COM_SIMULATOR";
        var shell = new ShellViewModel(connection, dialogs, settings: TestSettings(output));
        var logs = (DataLogsViewModel)shell.Pages[Destination.DataLogs];
        root.DataContext = shell; shell.Select(Destination.DataLogs); Layout(root, 1120, 760);
        Require(sim.Commands.IsEmpty, "Opening Data Logs does not identify or enter maintenance");
        service.ConnectAsync("COM_SIMULATOR").GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(sim.Commands.All(command => command == "CQ1,HELLO"), "Connection and Data Logs navigation still send only HELLO");
        logs.BrowseCommand.Execute(null);
        UiUntil(() => !logs.BrowseCommand.IsExecuting, root); logs.DrainProgress(); Layout(root, 1120, 760);
        Require(logs.Logs.Count == 2 && logs.SelectedLog is not null && sim.Mode == "IDLE", "Explicit browse populates live files and returns device to normal");
        Require(!logs.DeleteCommand.CanExecute(null), "Deletion unavailable without a verified local copy");
        Require(logs.Logs.Single(row => row.Filename == "log_001.csv").PresetName == "Heart Test" && logs.Logs.All(row => row.Verified is null), "Preset names appear during browse without falsely marking a header preview as a verified saved copy");
        Require(Descendants<DataGrid>(root).Single().Columns.Any(column => Equals(column.Header, "Preset")), "The log table exposes the preset beside the original filename");
        logs.Filter = "HEART";
        Require(logs.FilteredLogs.Cast<LogRow>().Single().Filename == "log_001.csv", "Users can find a log by preset name before downloading");
        logs.Filter = "LOG_001";
        Require(logs.FilteredLogs.Cast<LogRow>().Single().Filename == "log_001.csv", "Case-insensitive live filename search");
        logs.Filter = "NO_RTC";
        Require(logs.FilteredLogs.Cast<LogRow>().Count() == 1, "Live folder search");
        logs.Filter = "missing";
        Require(logs.FilteredLogs.IsEmpty && logs.EmptyMessage == "No logs match your search.", "Empty filtered list explained without claiming the card is empty");
        logs.Filter = "";
        logs.SelectedLog = logs.Logs.Single(row => row.Filename == "log_001.csv");
        logs.DownloadCommand.Execute(null);
        UiUntil(() => !logs.DownloadCommand.IsExecuting, root); logs.DrainProgress(); Layout(root, 1120, 760);
        Require(logs.SelectedLog.Verified is not null && File.Exists(dialogs.Destination) && sim.Mode == "IDLE", "Download command saves actual verified bytes and exits maintenance");
        Require(logs.SelectedLog.Verified?.PresetName == "Heart Test", "The completed copy's preset metadata comes from the checked CSV bytes");
        Require(logs.DeleteCommand.CanExecute(null), "Verified local copy enables conditional deletion");
        var previewLabel = Descendants<TextBlock>(root).Single(text => text.Text == "DEVICE MONITOR");
        previewLabel.Text = "SIMULATED TEST DEVICE";
        Save(root, Path.Combine(output, "DataLogs-simulator.png"), 1120, 760);
        Save(root, Path.Combine(output, "DataLogs-presets-Light-simulator.png"), 1040, 700);
        ThemeService.Apply("Dark");
        Save(root, Path.Combine(output, "DataLogs-presets-Dark-simulator.png"), 1040, 700);
        Require(Descendants<TextBlock>(root).Where(text => text.Text == "Heart Test").All(text => text.Foreground is System.Windows.Media.SolidColorBrush brush && brush.Color == ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["TextBrush"]).Color), "Preset values use readable theme text in Dark");
        ThemeService.Apply("Light");
        dialogs.Confirm = false; int count = sim.Commands.Count;
        logs.DeleteCommand.Execute(null); UiUntil(() => !logs.DeleteCommand.IsExecuting, root);
        Require(sim.Commands.Count == count && logs.Logs.Count == 2 && dialogs.Confirmations == 1, "Declining the visible deletion confirmation keeps the device unchanged");
        dialogs.Confirm = true;
        logs.DeleteCommand.Execute(null); UiUntil(() => !logs.DeleteCommand.IsExecuting, root); logs.DrainProgress();
        Require(logs.Logs.Count == 1 && sim.Files.Count == 1 && File.Exists(dialogs.Destination), "Confirmed deletion removes only the device entry and keeps the local copy");
        sim.Files.Clear(); logs.BrowseCommand.Execute(null); UiUntil(() => !logs.BrowseCommand.IsExecuting, root); logs.DrainProgress();
        Require(logs.Logs.Count == 0 && logs.EmptyMessage == "No logs found on the device.", "Real empty card state is shown");
        Save(root, Path.Combine(output, "DataLogs-empty-simulator.png"), 1120, 760);
        sim.Mode = "TEST"; service.RefreshHelloAsync().GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(!logs.BrowseCommand.CanExecute(null) && logs.AccessHint.Contains("running or paused"), "Live test state blocks access with a readable reason");
        sim.Mode = "MAINT"; service.RefreshHelloAsync().GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(logs.ReturnNormalCommand.CanExecute(null), "Existing maintenance exposes an explicit recovery action");
        logs.ReturnNormalCommand.Execute(null); UiUntil(() => !logs.ReturnNormalCommand.IsExecuting, root); Layout(root, 1120, 760);
        Require(sim.Mode == "IDLE" && !logs.ShowReturnNormal, "Return to normal exits only on explicit action");
        previewLabel.Text = "DEVICE MONITOR";
        var disposal = shell.DisposeAsync().AsTask(); UiUntil(() => disposal.IsCompleted, root); disposal.GetAwaiter().GetResult();
    }
    private static void FirmwareWorkflowChecks(App app, FrameworkElement root, string output)
    {
        using var httpFixture = new FirmwareHttpSimulator(); using var client = new HttpClient(httpFixture);
        var directory = Path.Combine(output, "firmware", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var releases = new GitHubFirmwareReleases(client, directory);
        var sim = new LogDeviceSimulator { FirmwareVersion = "1.0.0" };
        var uploader = new FirmwareUploaderSimulator(sim); var dialogs = new TestFirmwareDialogs();
        var service = new DeviceConnectionService(sim.CreateTransport);
        var connection = new ConnectionViewModel(service, new FakePortCatalog(), app.Dispatcher);
        var shell = new ShellViewModel(connection, releases: releases, uploader: uploader, firmwareDialogs: dialogs, settings: TestSettings(output));
        var firmware = (FirmwareViewModel)shell.Pages[Destination.FirmwareUpdate];
        root.DataContext = shell; shell.Select(Destination.FirmwareUpdate); Layout(root, 1120, 760);
        Require(ReferenceEquals(firmware.Connection, connection) && firmware.CheckCommand.CanExecute(null) && !firmware.InstallCommand.CanExecute(null), "Firmware is live; release lookup works disconnected, installation is disabled");
        Require(sim.Commands.IsEmpty && uploader.Uploads == 0, "Opening firmware page cannot prepare/reset a device");
        Require(!firmware.ShowLatestStatus, "Installed-version comparison stays hidden until releases have been checked");
        httpFixture.Empty = true; firmware.CheckCommand.Execute(null); UiUntil(() => !firmware.CheckCommand.IsExecuting, root); firmware.DrainProgress();
        Require(firmware.ReleaseStatus == "No matching release" && !firmware.DownloadCommand.CanExecute(null) && !firmware.CanInstall, "No published assets keeps firmware installation unavailable");
        Require(firmware.ShowLatestStatus && firmware.LatestStatusLabel == "No published update available" && firmware.LatestStatusTone == "Neutral", "Empty releases never claim the device has the latest version");
        var label = Descendants<TextBlock>(root).Single(text => text.Text == "DEVICE MONITOR"); label.Text = "SIMULATED TEST DEVICE";
        Save(root, Path.Combine(output, "FirmwareUpdate-no-release-simulator.png"), 1120, 760);
        httpFixture.Empty = false; firmware.CheckCommand.Execute(null); UiUntil(() => !firmware.CheckCommand.IsExecuting, root); firmware.DrainProgress();
        Require(firmware.Releases.Count == 1 && firmware.SelectedRelease?.Version == "1.1.0" && sim.Commands.IsEmpty, "Simulated published release appears without hardware commands");
        Require(firmware.LatestStatusLabel == "Connect to check installed version" && firmware.LatestStatusTone == "Neutral", "Disconnected release lookup asks for a device before comparing versions");
        firmware.DownloadCommand.Execute(null); UiUntil(() => !firmware.DownloadCommand.IsExecuting, root); firmware.DrainProgress();
        Require(firmware.PackageLabel == "Checked" && !firmware.CanInstall && sim.Commands.IsEmpty, "Package verification alone never enters maintenance");
        service.ConnectAsync("COM7").GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(firmware.CanInstall && firmware.InstallCommand.CanExecute(null) && uploader.Uploads == 0 && sim.Commands.All(c => c == "CQ1,HELLO"), "Verified package and connected device enable update without a hardware questionnaire or maintenance entry");
        Require(firmware.LatestStatusLabel == "Update available: 1.1.0" && firmware.LatestReleaseLabel == "Latest release: 1.1.0" && Descendants<TextBlock>(root).Any(t => t.Text == firmware.LatestStatusLabel && IsPresented(t)), "Visible label compares the connected version against the checked release");
        var visibleText = string.Join(" ", Descendants<TextBlock>(root).Where(IsPresented).Select(t => t.Text));
        var hardwareTerms = new[] { "Arduino", "Mega", "ATmega", "clone", "genuine", "physical board" };
        Require(!Descendants<CheckBox>(root).Any(IsPresented) && hardwareTerms.All(term => !visibleText.Contains(term, StringComparison.OrdinalIgnoreCase)), "Normal firmware page contains no board checklist or microcontroller jargon");
        var confirmationText = FirmwareDialogs.ConfirmationMessage(new("COM7", "1.0.0", "1.1.0", new string('a', 64)));
        Require(confirmationText.Contains("1.0.0") && confirmationText.Contains("1.1.0") && confirmationText.Contains("Keep USB and power connected") && !confirmationText.Contains("COM7") && !confirmationText.Contains(new string('a', 64)) && hardwareTerms.All(term => !confirmationText.Contains(term, StringComparison.OrdinalIgnoreCase)), "Real confirmation shows versions and connection reminder without hardware or checksum details");
        Require(Descendants<Expander>(root).Any(e => Equals(e.Header, "Technical details") && !e.IsExpanded), "Firmware technical output hidden by default");
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 }) Save(root, Path.Combine(output, $"FirmwareUpdate-ready-simulator-{(int)(scale * 100)}percent.png"), 1120, 760, scale);
        Save(root, Path.Combine(output, "FirmwareUpdate-ready-minimum-simulator.png"), 1040, 700);
        dialogs.Confirm = false; firmware.InstallCommand.Execute(null); UiUntil(() => !firmware.InstallCommand.IsExecuting, root); firmware.DrainProgress();
        Require(dialogs.Confirmations == 1 && sim.Commands.All(c => c == "CQ1,HELLO") && uploader.Uploads == 0, "Declined confirmation sends no ENTER/FWPREP or uploader call");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool uploading = false;
        uploader.BeforeUpload = async () => { uploading = true; await gate.Task; };
        dialogs.Confirm = true; firmware.InstallCommand.Execute(null); UiUntil(() => uploading, root); firmware.DrainProgress(); Layout(root, 1120, 760);
        Require(firmware.IsInstalling && !firmware.CancelCommand.CanExecute(null) && !connection.ConnectCommand.CanExecute(null) && !connection.DisconnectCommand.CanExecute(null) && !connection.RefreshHelloCommand.CanExecute(null), "Active uploader disables cancel and competing connection commands");
        Require(connection.StatusLabel == "Updating firmware", "Persistent connection status honestly shows uploader handoff");
        Require(firmware.LatestStatusLabel == "Checking installed version…" && firmware.LatestStatusTone != "Success", "During installation the old identity cannot claim the latest version is installed");
        Layout(root, 1040, 700);
        var progressBounds = Descendants<ProgressBar>(root).Single().TransformToAncestor(root).TransformBounds(new Rect(new Size(100, 6)));
        Require(progressBounds.Top >= 219 && progressBounds.Bottom < 660, "Firmware upload progress stays visible at the practical minimum window size");
        var progressPanel = Descendants<Border>(root).Single(b => b.Name == "ProgressPanel");
        var filePanel = Descendants<Border>(root).Single(b => b.Name == "UpdateFilePanel");
        var progressPanelBounds = progressPanel.TransformToAncestor(root).TransformBounds(new Rect(progressPanel.RenderSize));
        var filePanelBounds = filePanel.TransformToAncestor(root).TransformBounds(new Rect(filePanel.RenderSize));
        // WPF rounds each edge to physical pixels; equal star columns can differ by two pixels.
        double pixelTolerance = 2 / VisualTreeHelper.GetDpi(root).DpiScaleX + 0.01;
        Require(Math.Abs(progressPanelBounds.Top - filePanelBounds.Top) <= pixelTolerance && Math.Abs(progressPanelBounds.Width - filePanelBounds.Width) <= pixelTolerance && progressPanelBounds.Right < filePanelBounds.Left && filePanelBounds.Right <= 1040, $"Firmware progress and update file share equal visible columns at the minimum window size (progress: {progressPanelBounds}; file: {filePanelBounds})");
        var installButton = Descendants<Button>(filePanel).Single(b => Equals(b.Content, "Update firmware"));
        var installButtonBounds = installButton.TransformToAncestor(root).TransformBounds(new Rect(installButton.RenderSize));
        Require(Descendants<ComboBox>(filePanel).Any() && Descendants<TextBlock>(filePanel).Any(t => t.Text == "Selected update: 1.1.0" && IsPresented(t)) && IsPresented(installButton) && installButtonBounds.Bottom < 660, "Release selection, selected-update version and installation action share one panel and fit without scrolling at the minimum window size");
        Layout(root, 1120, 760);
        Save(root, Path.Combine(output, "FirmwareUpdate-installing-simulator.png"), 1120, 760);
        gate.SetResult(); UiUntil(() => !firmware.InstallCommand.IsExecuting, root); firmware.DrainProgress(); Layout(root, 1120, 760);
        Require(firmware.StatusLabel == "Complete" && connection.FirmwareLabel == "1.1.0" && uploader.Uploads == 1 && !firmware.CanInstall, "Only verified returning version completes update; no same-version reinstall");
        Require(firmware.LatestStatusLabel == "Latest version installed" && firmware.LatestStatusTone == "Success" && Descendants<TextBlock>(root).Any(t => t.Text == "Latest version installed" && IsPresented(t)), "Confirmed reconnection refreshes the visible latest-version label");
        Save(root, Path.Combine(output, "FirmwareUpdate-complete-simulator.png"), 1120, 760);
        service.DisconnectAsync().GetAwaiter().GetResult();
        Layout(root, 1120, 760);
        Require(firmware.LatestStatusLabel == "Connect to check installed version" && firmware.LatestStatusTone == "Neutral", "Disconnect clears the successful installed-version claim");
        sim.FirmwareVersion = "2.0.0"; service.ConnectAsync("COM7").GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(firmware.LatestStatusLabel == "Newer than latest release" && firmware.LatestStatusTone != "Success", "A device ahead of published firmware is distinguished from an exact latest-version match");
        sim.FirmwareVersion = "unknown"; service.RefreshHelloAsync().GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(firmware.LatestStatusLabel == "Version could not be compared" && firmware.LatestStatusTone == "Neutral", "Unknown installed versions never produce a latest-version claim");
        sim.FirmwareVersion = "1.1.0"; service.RefreshHelloAsync().GetAwaiter().GetResult(); Layout(root, 1120, 760);
        int commandsBeforeCheck = sim.Commands.Count;
        firmware.CheckCommand.Execute(null); UiUntil(() => !firmware.CheckCommand.IsExecuting, root); firmware.DrainProgress(); Layout(root, 1120, 760);
        Require(firmware.LatestStatusLabel == "Latest version installed" && firmware.LatestStatusTone == "Success" && firmware.Message == "Your CellQ already has the latest version." && firmware.PackageLabel == "Not checked" && sim.Commands.Count == commandsBeforeCheck, "Checking releases identifies an already-current device without downloading or entering maintenance");
        Save(root, Path.Combine(output, "FirmwareUpdate-latest-installed-simulator.png"), 1120, 760);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        httpFixture.Override = async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal)) return null;
            await releaseGate.Task.WaitAsync(token);
            httpFixture.Version = "1.9.0"; var older = httpFixture.ReleaseJson();
            httpFixture.Version = "1.10.0"; var latest = httpFixture.ReleaseJson();
            httpFixture.Version = "1.1.0"; var installedRelease = httpFixture.ReleaseJson();
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("[" + older.ToJsonString() + "," + latest.ToJsonString() + "," + installedRelease.ToJsonString() + "]") };
        };
        firmware.CheckCommand.Execute(null); Layout(root, 1120, 760);
        Require(firmware.CheckCommand.IsExecuting && !firmware.ShowLatestStatus, "Rechecking releases immediately hides the previous successful comparison");
        releaseGate.SetResult(); UiUntil(() => !firmware.CheckCommand.IsExecuting, root); firmware.DrainProgress();
        firmware.SelectedRelease = firmware.Releases.Single(r => r.Version == "1.1.0"); Layout(root, 1120, 760);
        Require(firmware.LatestStatusLabel == "Update available: 1.10.0" && firmware.LatestReleaseLabel == "Latest release: 1.10.0" && firmware.AssetLabel == "Selected update: 1.1.0", "Latest release status and explicitly labelled selected-update version stay distinct when choosing an older release");
        httpFixture.Override = null;
        sim.Mode = "MAINT"; service.RefreshHelloAsync().GetAwaiter().GetResult(); Layout(root, 1120, 760);
        Require(firmware.ReturnNormalCommand.CanExecute(null), "Firmware page offers explicit existing-maintenance recovery");
        Require(Descendants<Button>(root).Any(b => Equals(b.Content, "Return to normal") && IsPresented(b)), "Firmware recovery action is visible while idle in maintenance");
        firmware.ReturnNormalCommand.Execute(null); UiUntil(() => !firmware.ReturnNormalCommand.IsExecuting, root);
        Require(sim.Mode == "IDLE", "Firmware recovery exits without upload");
        httpFixture.Status = System.Net.HttpStatusCode.Forbidden; firmware.CheckCommand.Execute(null); UiUntil(() => !firmware.CheckCommand.IsExecuting, root); firmware.DrainProgress();
        Require(firmware.StatusLabel == "Needs attention" && firmware.ReleaseStatus == "Not checked" && !firmware.CanInstall, "Network/access failure is not reported as no releases or success");
        Require(!firmware.ShowLatestStatus, "Failed release checks cannot display a stale latest-version claim");
        Save(root, Path.Combine(output, "FirmwareUpdate-error-simulator.png"), 1120, 760);
        label.Text = "DEVICE MONITOR";
        var disposal = shell.DisposeAsync().AsTask(); UiUntil(() => disposal.IsCompleted, root); disposal.GetAwaiter().GetResult();
    }
    private sealed class TestFirmwareDialogs : IFirmwareDialogs
    {
        public bool Confirm { get; set; }
        public int Confirmations { get; private set; }
        public Task<bool> ConfirmAsync(FirmwareConfirmation confirmation, CancellationToken token) { Confirmations++; return Task.FromResult(Confirm); }
    }
    private sealed class TestLogDialogs(string destination) : ILogDialogs
    {
        public string Destination { get; } = destination;
        public bool Confirm { get; set; }
        public int Confirmations { get; private set; }
        public string? ChooseDestination(DeviceLog log) => Destination;
        public string? ChooseSavedCopy(DeviceLog log) => Destination;
        public Task<bool> ConfirmDeletionAsync(DeletionConfirmation confirmation, CancellationToken token)
        { Confirmations++; return Task.FromResult(Confirm); }
    }
    private static void Require(bool result, string name)
    {
        if (!result) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
    }
    private static void UiUntil(Func<bool> condition, FrameworkElement root)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Expected UI traffic was not displayed.");
            Layout(root, 1120, 760);
            System.Threading.Thread.Sleep(5);
        }
    }
    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        root.UpdateLayout();
    }
    private static void SaveHelpPreviews(FrameworkElement root, string output, string theme)
    {
        Save(root, Path.Combine(output, $"Help-{theme}-minimum.png"), 1040, 700);
        var help = Descendants<CellQ.Desktop.Views.HelpView>(root).Single();
        var scroll = Descendants<ScrollViewer>(help).First();
        var shortcuts = Descendants<Expander>(help).Single();
        shortcuts.IsExpanded = true;
        Layout(root, 1040, 700);
        scroll.ScrollToEnd();
        Save(root, Path.Combine(output, $"Help-{theme}-bottom.png"), 1040, 700);
        shortcuts.IsExpanded = false;
        scroll.ScrollToTop();
        Layout(root, 1120, 760);
    }
    private static void Save(FrameworkElement root, string path, int width, int height, double scale = 1)
    {
        Layout(root, width, height);
        var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)(195 * scale), (int)(120 * scale), 1, 1), pixel, 4, 0);
        var canvas = ((SolidColorBrush)Application.Current.Resources["CanvasBrush"]).Color;
        Require(pixel[0] == canvas.B && pixel[1] == canvas.G && pixel[2] == canvas.R && pixel[3] == 255, "Workspace canvas uses the selected theme and is opaque in WPF renders");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    // The offscreen root is detached from its Window. IsVisible includes the absence of
    // a live presentation source, so inspect template/ancestor Visibility instead.
    private static bool IsPresented(DependencyObject item)
    {
        for (DependencyObject? ancestor = item; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
