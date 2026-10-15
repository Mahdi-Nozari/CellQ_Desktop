using System.Windows.Input;
using CellQ.Desktop.Infrastructure;
using CellQ.Desktop.Services;
using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

public enum Destination { Device, DataLogs, FirmwareUpdate, Diagnostics, Settings, Help }

public sealed class NavigationItem(Destination destination, string label, string iconData, ICommand command) : ObservableObject
{
    private bool _isSelected;
    public Destination Destination { get; } = destination;
    public string Label { get; } = label;
    public string IconData { get; } = iconData;
    public ICommand Command { get; } = command;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private PageViewModel _currentPage;
    public IReadOnlyList<NavigationItem> MainNavigation { get; }
    public IReadOnlyList<NavigationItem> UtilityNavigation { get; }
    public IReadOnlyDictionary<Destination, PageViewModel> Pages { get; }
    public ConnectionViewModel Connection { get; }
    public PageViewModel CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }

    public ShellViewModel(ConnectionViewModel connection, ILogDialogs? dialogs = null, IFirmwareReleases? releases = null, IFirmwareUploader? uploader = null, IFirmwareDialogs? firmwareDialogs = null, SettingsViewModel? settings = null, ILogGraphWindows? graphs = null)
    {
        Connection = connection;
        var preferences = settings ?? new SettingsViewModel();
        Pages = new Dictionary<Destination, PageViewModel>
        {
            [Destination.Device] = new DeviceViewModel(connection),
            [Destination.DataLogs] = new DataLogsViewModel(connection, dialogs ?? new LogDialogs(() => System.Windows.Application.Current.MainWindow, System.Windows.Application.Current.Dispatcher, () => preferences.DownloadDirectory), graphs ?? new LogGraphWindows(() => System.Windows.Application.Current.MainWindow, () => preferences.DownloadDirectory)),
            [Destination.FirmwareUpdate] = new FirmwareViewModel(connection, releases, uploader, firmwareDialogs),
            [Destination.Diagnostics] = new DiagnosticsViewModel(connection),
            [Destination.Settings] = preferences,
            [Destination.Help] = new HelpViewModel()
        };
        var navigate = new RelayCommand(value => { if (value is Destination page) Select(page); });
        MainNavigation = [
            new(Destination.Device, "_Device", "M2,2 H18 V13 H2 Z M10,13 V18 M6,18 H14", navigate),
            new(Destination.DataLogs, "Data _Logs", "M4,1 H12 L17,6 V19 H4 Z M12,1 V6 H17 M7,10 H14 M7,14 H14", navigate),
            new(Destination.FirmwareUpdate, "_Firmware Update", "M10,16 V2 M5,7 L10,2 L15,7 M3,14 V19 H17 V14", navigate),
            new(Destination.Diagnostics, "Dia_gnostics", "M1,10 H5 L8,3 L12,17 L15,10 H19", navigate)
        ];
        UtilityNavigation = [
            new(Destination.Settings, "_Settings", "M2,4 H18 M2,10 H18 M2,16 H18 M6,1 V7 M14,7 V13 M8,13 V19", navigate),
            new(Destination.Help, "_Help", "M5,6 C5,0 15,0 15,6 C15,10 10,9 10,13 M10,16 V18", navigate)
        ];
        _currentPage = Pages[Destination.Device];
        Select(Destination.Device);
    }

    public void Select(Destination destination)
    {
        CurrentPage = Pages[destination];
        foreach (var item in MainNavigation.Concat(UtilityNavigation))
            item.IsSelected = item.Destination == destination;
    }
    public async ValueTask DisposeAsync()
    {
        await ((SettingsViewModel)Pages[Destination.Settings]).FlushAsync();
        await ((FirmwareViewModel)Pages[Destination.FirmwareUpdate]).DisposeAsync();
        await ((DataLogsViewModel)Pages[Destination.DataLogs]).DisposeAsync();
        await Connection.DisposeAsync();
    }
}
