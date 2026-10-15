using System.Windows;
using System.ComponentModel;
using CellQ.Desktop.Services;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;

namespace CellQ.Desktop;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _isClosing;
    public ShellViewModel ViewModel { get; }
    public MainWindow(DeviceConnectionService? service = null, IPortCatalog? catalog = null, SettingsViewModel? settings = null)
    {
        ViewModel = new(new ConnectionViewModel(service ?? new DeviceConnectionService(() => new SerialPortTransport()), catalog ?? new SerialPortCatalog(), Dispatcher), settings: settings);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += (_, _) => ViewModel.Connection.RefreshPortsCommand.Execute(null);
        Closing += OnClosing;
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_isClosing) return;
        if (((FirmwareViewModel)ViewModel.Pages[Destination.FirmwareUpdate]).IsInstalling)
        {
            MessageBox.Show(this, "Firmware installation or its version check is still in progress. Keep USB and power connected and wait for the result before closing.", "Firmware update in progress", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _isClosing = true;
        IsEnabled = false;
        try { await ViewModel.DisposeAsync(); }
        catch (Exception error)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                IsEnabled = true; _isClosing = false;
                MessageBox.Show(this, "The serial port could not be closed. Retry closing the app.\n\n" + error.Message, "CellQ connection", MessageBoxButton.OK, MessageBoxImage.Error);
            });
            return;
        }
        // A synchronous disconnect must still wait until the original Closing event returns.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ViewModel.Connection.Service.MaintenanceMayRemain)
                MessageBox.Show(this, "The device could not be returned to its normal screen. Reconnect next time and use Return to normal on Data Logs.", "CellQ log access", MessageBoxButton.OK, MessageBoxImage.Warning);
            _allowClose = true; Close();
        });
    }
}
