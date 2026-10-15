using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

public sealed class DeviceViewModel(ConnectionViewModel connection) : PageViewModel("Device", "Connect your CellQ and check its status.", "DEVICE", "↻ Refresh status", "")
{
    public ConnectionViewModel Connection { get; } = connection;
    public override string ContentLabel => "Connection status";
    public override ICommand FirstActionCommand => Connection.RefreshHelloCommand;
    public override bool FirstActionAvailable => true;
}

public sealed class DiagnosticsViewModel : PageViewModel
{
    private string _filter = "";
    private bool _debugOnly;
    private bool _showTechnicalDetails;
    private DiagnosticMessage? _selectedMessage;
    public ConnectionViewModel Connection { get; }
    public ICollectionView FilteredMessages { get; }
    public DiagnosticMessage? SelectedMessage { get => _selectedMessage; set => Set(ref _selectedMessage, value); }
    public bool ShowTechnicalDetails { get => _showTechnicalDetails; set { if (Set(ref _showTechnicalDetails, value)) { SelectedMessage = null; FilteredMessages.Refresh(); } } }
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) FilteredMessages.Refresh(); } }
    public bool DebugOnly { get => _debugOnly; set { if (Set(ref _debugOnly, value)) FilteredMessages.Refresh(); } }
    public override string ContentLabel => "Connection activity";
    public override ICommand FirstActionCommand => Connection.RefreshHelloCommand;
    public override ICommand SecondActionCommand => Connection.ClearViewCommand;
    public override bool FirstActionAvailable => true;
    public override bool SecondActionAvailable => true;
    public DiagnosticsViewModel(ConnectionViewModel connection) : base("Diagnostics", "Check the connection or view messages for support.", "DIAGNOSTICS", "↻ Refresh status", "Clear messages")
    {
        Connection = connection;
        FilteredMessages = CollectionViewSource.GetDefaultView(connection.Messages);
        FilteredMessages.Filter = item => item is DiagnosticMessage message &&
            (ShowTechnicalDetails ? (!DebugOnly || message.Kind == "Debug") : DevicePresentation.IsActivity(message)) &&
            (ShowTechnicalDetails ? message.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase) || message.Kind.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                : DevicePresentation.Activity(message).Contains(Filter, StringComparison.OrdinalIgnoreCase));
    }
}
