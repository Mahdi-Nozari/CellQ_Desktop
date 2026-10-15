using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CellQ.Desktop;
using CellQ.Desktop.Services;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.UiChecks;

internal static partial class Program
{
    private static void PortPickerChecks(FrameworkElement root, string output)
    {
        WindowsPortDevice[] devices =
        [
            new("COM10", "Arduino Mega 2560 (COM10)"),
            new("COM3", "Arduino Mega 2560 Rev3 (COM3)"),
            new("com10", "arduino mega2560 (com10)"),
            new("COM2", "Arduino Uno (COM2)"),
            new("COM4", "USB-SERIAL CH340 (COM4)"),
            new("COM5", "USB Serial Device (COM5)"),
            new("COM6", "Arduino Mega 1280 (COM6)"),
            new("LPT1", "Arduino Mega 2560 (LPT1)"),
            new("COM7", null),
            new(null, "Arduino Mega 2560"),
            new("COM8", "Arduino Megaphone (COM8)"),
            new("COM0", "Arduino Mega 2560 (COM0)"),
            new("COM9", "Arduino MegaFake (COM9)")
        ];
        Require(SerialPortCatalog.SelectMegaPorts(devices).SequenceEqual(["COM3", "COM10"]), "Windows-named Mega 2560 ports only; numeric sorting/deduplication; unknown and unsupported names excluded");
        Require(SerialPortCatalog.SelectMegaPorts([new("COM12", "Arduino Mega (COM12)"), new("COM11", "Arduino Mega 2560 R3 (COM11)")]).SequenceEqual(["COM11", "COM12"]), "Generic Windows Mega and R3 names are recognized without showing those names in the app");
        int reads = 0;
        bool fail = false;
        var catalog = new SerialPortCatalog(token =>
        {
            token.ThrowIfCancellationRequested(); reads++;
            if (fail) throw new Win32Exception("Test metadata unavailable.");
            return devices;
        });
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { catalog.GetPortsAsync(cancelled.Token).GetAwaiter().GetResult(); Require(false, "Cancelled lookup must not execute"); }
            catch (OperationCanceledException) { Require(reads == 0, "Cancelled discovery never queries Windows or opens a port"); }
        }
        // Exercise the real read-only native API as well as injected metadata. No serial transport is opened.
        var native = new SerialPortCatalog().GetPortsAsync(default).GetAwaiter().GetResult();
        Require(native.All(port => port.StartsWith("COM", StringComparison.Ordinal)), "Native Windows port metadata can be read without new dependencies or opening hardware");

        var fake = FakeDevice.Responding("IDLE", "test-only-fixture");
        var service = new DeviceConnectionService(() => fake);
        var settings = new SettingsViewModel(new AppSettingsStore(Path.Combine(output, "port-picker", Guid.NewGuid().ToString("N"), "settings.json")), new TestFolderPicker());
        var window = new MainWindow(service, catalog, settings);
        var shell = window.ViewModel;
        var connection = shell.Connection;
        root.DataContext = shell;
        shell.Select(Destination.Device);
        var label = Descendants<TextBlock>(root).Single(text => text.Text == "DEVICE MONITOR");
        label.Text = "SIMULATED DEVICE LIST";
        try
        {
            Refresh();
            var pickers = Descendants<ComboBox>(root).Where(box => box.ItemsSource == connection.Ports).ToArray();
            Require(pickers.Length == 2 && connection.Ports.Select(port => port.Label).SequenceEqual(["COM3", "COM10"]), "Ribbon and Device selectors show only raw COM labels before identification");
            Require(fake.OpenCount == 0 && fake.Writes.IsEmpty && connection.SelectedPort is null, "Finding multiple candidates selects none and sends no identification commands");
            connection.SelectedPort = "COM10"; Layout(root, 1120, 760);
            Require(pickers.All(box => Equals(box.SelectedValue, "COM10")), "Both selectors share the selected raw COM port");
            Refresh();
            Require(connection.SelectedPort == "COM10" && pickers.All(box => Equals(box.SelectedValue, "COM10")), "Refreshing unchanged results preserves selection in both selectors");
            connection.SelectedPort = "COM2";
            Require(!connection.ConnectCommand.CanExecute(null), "Filtered ports cannot be connected through the UI command");
            connection.SelectedPort = "COM10";
            Save(root, Path.Combine(output, "Device-port-list-simulator.png"), 1040, 700);

            connection.ConnectCommand.Execute(null);
            UiUntil(() => !connection.ConnectCommand.IsExecuting && connection.State.Phase == ConnectionPhase.Connected, root);
            Layout(root, 1040, 700);
            Require(fake.PortName == "COM10" && fake.OpenCount == 1 && fake.Writes.SequenceEqual(["CQ1,HELLO\n"]), "Connecting uses COM10, never the CellQ display label, and sends only identification");
            Require(connection.Ports.Select(port => port.Label).SequenceEqual(["COM3", "CellQ (COM10)"]), "Only the successfully identified selected port receives the CellQ label");
            Require(pickers.All(box => box.SelectedItem is PortChoice { Label: "CellQ (COM10)" } && Equals(box.SelectedValue, "COM10")), "Both selectors update CellQ text while preserving their actual COM10 value");
            Require(Descendants<TextBlock>(root).Where(IsPresented).All(text => !text.Text.Contains("Arduino", StringComparison.OrdinalIgnoreCase)), "Visible Device and ribbon text do not name the board hardware");
            int beforeConnectedRefresh = reads;
            Refresh();
            Require(reads == beforeConnectedRefresh && connection.SelectedPort == "COM10", "Discovery cannot replace the active shared connection selection");
            Save(root, Path.Combine(output, "Device-CellQ-label-simulator.png"), 1040, 700);
            var disconnect = service.DisconnectAsync(); UiUntil(() => disconnect.IsCompleted, root); disconnect.GetAwaiter().GetResult(); Layout(root, 1120, 760);
            Require(connection.Ports.All(port => port.Label == port.PortName) && fake.Writes.Count == 1, "Disconnect removes CellQ labels without sending device commands or keeping stale identity");

            devices = [new("COM10", "Arduino Mega 2560 (COM10)"), new("COM1", "Arduino Mega 2560 (COM1)")];
            Refresh();
            Require(connection.SelectedPort == "COM10" && connection.Ports.Select(port => port.PortName).SequenceEqual(["COM1", "COM10"]), "Adding/removing other devices preserves the selected COM10 identity and order");
            devices = [new("COM1", "Arduino Mega 2560 (COM1)")];
            Refresh();
            Require(connection.SelectedPort == "COM1" && pickers.All(box => Equals(box.SelectedValue, "COM1")), "Removing the selection chooses the only remaining candidate in both selectors");
            fail = true;
            connection.RefreshPortsCommand.Execute(null);
            UiUntil(() => !connection.RefreshPortsCommand.IsExecuting, root);
            Require(connection.HasError && connection.Ports.Single().PortName == "COM1" && connection.SelectedPort == "COM1", "Metadata failure is reported and does not fall back to unrelated ports");
            fail = false;
            devices = [new("COM10", "USB Serial Device (COM10)")];
            Refresh();
            Require(connection.Ports.Count == 0 && connection.SelectedPort is null && !connection.ConnectCommand.CanExecute(null), "A reused COM number with a different Windows device name is not retained");
            Require(connection.HasError && connection.ErrorHint.Contains("No matching USB connections", StringComparison.Ordinal), "Empty filtered discovery has a plain-language explanation without claiming CellQ was identified");
            Require(fake.OpenCount == 1 && fake.Writes.Count == 1, "Discovery, labels, sorting and errors never probe ports or enter maintenance");
        }
        finally
        {
            label.Text = "DEVICE MONITOR";
            var disposal = shell.DisposeAsync().AsTask(); UiUntil(() => disposal.IsCompleted, root); disposal.GetAwaiter().GetResult();
        }

        void Refresh()
        {
            var refresh = connection.RefreshPortsAsync();
            UiUntil(() => refresh.IsCompleted, root);
            refresh.GetAwaiter().GetResult();
            Layout(root, 1120, 760);
        }
    }
}
