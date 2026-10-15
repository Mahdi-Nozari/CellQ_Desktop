using System.Collections.ObjectModel;
using System.Windows.Threading;
using CellQ.Desktop.Infrastructure;
using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

public sealed class ConnectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceConnectionService _service;
    private readonly IPortCatalog _catalog;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Queue<DiagnosticMessage> _pending = [];
    private readonly object _queueLock = new();
    private ConnectionSnapshot _state;
    private string? _selectedPort;
    private string? _commandError;
    private bool _paused;
    private int _discarded;
    private bool _disposed;
    public ObservableCollection<PortChoice> Ports { get; } = [];
    public ObservableCollection<DiagnosticMessage> Messages { get; } = [];
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand RefreshPortsCommand { get; }
    public AsyncCommand RefreshHelloCommand { get; }
    public RelayCommand ClearViewCommand { get; }
    public ConnectionSnapshot State => _state;
    internal DeviceConnectionService Service => _service;
    public string? SelectedPort { get => _selectedPort; set { if (Set(ref _selectedPort, value)) ConnectCommand.Notify(); } }
    public bool Paused { get => _paused; set => Set(ref _paused, value); }
    public bool CanChoosePort => !_state.WorkflowActive && _state.Phase is ConnectionPhase.Disconnected or ConnectionPhase.Error;
    public bool ShowConnect => CanChoosePort;
    public bool ShowDisconnect => !CanChoosePort;
    public string DisconnectLabel => _state.Phase == ConnectionPhase.Connecting ? "Cancel" : "Disconnect";
    public string PortLabel => _state.Phase == ConnectionPhase.Disconnected ? "No connected device" : _state.PortName ?? "No open port";
    public string FirmwareLabel => _state.FirmwareVersion ?? "—";
    public string FirmwareSidebarLabel => "Firmware version " + FirmwareLabel;
    public string ModeLabel => DevicePresentation.Mode(_state.Mode);
    public string StatusLabel => _state.Phase switch { ConnectionPhase.Updating => "Updating firmware", ConnectionPhase.Connected => "Connected · " + ModeLabel, ConnectionPhase.Connecting => "Connecting…", ConnectionPhase.Error => "Connection problem", _ => "Disconnected" };
    public string Tone => _state.Phase == ConnectionPhase.Error ? "Error" : _state.Phase is ConnectionPhase.Connecting or ConnectionPhase.Updating ? "Info" : _state.Mode == DeviceMode.Test ? "Warning" : _state.Phase == ConnectionPhase.Connected ? "Success" : "Neutral";
    public string LastHello => _state.IdentifiedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
    public string RequestLabel => _state.WorkflowActive ? "Device operation in progress…" : _state.RequestPending ? "Updating device information…" : _state.Phase == ConnectionPhase.Connected ? "Connected. Refresh status to check again." : "Connect to view device information.";
    public string? ErrorMessage => _state.Error ?? _commandError;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string ErrorHint => _state.Phase == ConnectionPhase.Connected
        ? "The action could not be completed. Try again; technical details are available below."
        : Ports.Count == 0 && _state.Phase == ConnectionPhase.Disconnected
        ? "No matching USB connections found. Connect your CellQ, then choose Find devices."
        : "Could not connect to the device. Check the USB cable and selected connection, then try again. Close any other app using this device.";
    public bool ExistingMaintenance => _state.Mode == DeviceMode.Maintenance;
    public string ConnectionDescription => _state.Phase switch
    {
        ConnectionPhase.Updating => "Firmware installation or reconnection is in progress. Keep USB and device power connected.",
        ConnectionPhase.Connecting => "Connecting to your CellQ…",
        ConnectionPhase.Connected => "Your CellQ is connected. Choose Refresh status to check its latest information.",
        ConnectionPhase.Error => "Check the USB connection and try connecting again.",
        _ => "Connect the USB cable, choose its connection from the list, then select Connect."
    };
    public string ModeDescription => _state.Mode switch
    {
        DeviceMode.Idle => "The device reported Idle when last checked.",
        DeviceMode.Manual => "The device is in Manual mode. Individual valve states are not available here.",
        DeviceMode.Test => "A test is running or paused. Use the device itself to manage the test.",
        DeviceMode.Maintenance when _state.MaintenanceOwned => "The app is performing a maintenance operation. Wait for it to finish before disconnecting.",
        DeviceMode.Maintenance => "The device is in Maintenance mode. Open Data Logs to resume or choose Return to normal.",
        _ => "Device information will appear after you connect."
    };
    public string FooterLabel => StatusLabel + (_state.PortName is null ? "" : " · " + _state.PortName);
    public string BufferLabel => $"{Messages.Count} retained messages · {Volatile.Read(ref _discarded)} older messages discarded";

    public ConnectionViewModel(DeviceConnectionService service, IPortCatalog catalog, Dispatcher dispatcher)
    {
        _service = service; _catalog = catalog; _dispatcher = dispatcher; _state = service.Snapshot;
        ConnectCommand = new(async () => { _commandError = null; await service.ConnectAsync(SelectedPort!); }, () => ShowConnect && Ports.Any(port => port.PortName == SelectedPort), OnCommandError);
        DisconnectCommand = new(() => service.DisconnectAsync(), () => ShowDisconnect && !_state.WorkflowActive, OnCommandError);
        RefreshHelloCommand = new(() => service.RefreshHelloAsync(), () => _state.Phase == ConnectionPhase.Connected && !_state.RequestPending && !_state.WorkflowActive, OnCommandError);
        RefreshPortsCommand = new(RefreshPortsAsync, () => CanChoosePort, OnCommandError);
        ClearViewCommand = new(_ => { lock (_queueLock) { _pending.Clear(); Interlocked.Exchange(ref _discarded, 0); } Messages.Clear(); Raise(nameof(BufferLabel)); });
        service.StateChanged += OnState;
        service.MessageReceived += OnMessage;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => DrainMessages(), dispatcher);
        _timer.Start();
    }
    public async Task RefreshPortsAsync()
    {
        if (!CanChoosePort) return;
        var ports = await _catalog.GetPortsAsync(CancellationToken.None);
        if (!CanChoosePort) return; // A connection may have started while Windows was being queried.
        string? selection = SelectedPort;
        foreach (var removed in Ports.Where(port => !ports.Contains(port.PortName)).ToArray()) Ports.Remove(removed);
        for (int index = 0; index < ports.Count; index++)
        {
            var choice = Ports.FirstOrDefault(port => port.PortName == ports[index]);
            if (choice is null) Ports.Insert(index, new(ports[index]));
            else if (Ports.IndexOf(choice) != index) Ports.Move(Ports.IndexOf(choice), index);
        }
        SelectedPort = ports.Contains(selection ?? "") ? selection : Ports.Count == 1 ? Ports[0].PortName : null;
        _commandError = ports.Count == 0 ? "No matching USB connections found. Connect your CellQ, then choose Find devices." : null;
        Raise(nameof(ErrorMessage)); Raise(nameof(HasError)); Raise(nameof(ErrorHint));
    }
    private void OnCommandError(Exception error) { _commandError = error.Message; Raise(nameof(ErrorMessage)); Raise(nameof(HasError)); Raise(nameof(ErrorHint)); }
    private void OnState(ConnectionSnapshot state) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _state = state; _commandError = null;
        if (state.Phase == ConnectionPhase.Connected && state.FirmwareVersion is not null && state.PortName is not null)
        {
            if (!Ports.Any(port => port.PortName == state.PortName)) Ports.Add(new(state.PortName));
            SelectedPort = state.PortName;
        }
        foreach (var port in Ports)
            port.SetIdentified(state.Phase == ConnectionPhase.Connected && state.FirmwareVersion is not null && state.PortName == port.PortName);
        // All display strings are derived from this immutable connection snapshot.
        Raise("");
        ConnectCommand.Notify(); DisconnectCommand.Notify(); RefreshHelloCommand.Notify(); RefreshPortsCommand.Notify();
    });
    private void OnMessage(DiagnosticMessage message)
    {
        lock (_queueLock)
        {
            if (_pending.Count == 2000) { _pending.Dequeue(); Interlocked.Increment(ref _discarded); }
            _pending.Enqueue(message);
        }
    }
    public void DrainMessages()
    {
        if (_disposed) return;
        if (Paused) { Raise(nameof(BufferLabel)); return; }
        for (int i = 0; i < 200; i++)
        {
            DiagnosticMessage? message;
            lock (_queueLock) { if (_pending.Count == 0) break; message = _pending.Dequeue(); }
            if (Messages.Count == 2000) { Messages.RemoveAt(0); Interlocked.Increment(ref _discarded); }
            Messages.Add(message);
        }
        Raise(nameof(BufferLabel));
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _service.DisposeAsync();
        _disposed = true; _timer.Stop();
        _service.StateChanged -= OnState; _service.MessageReceived -= OnMessage;
    }
}
