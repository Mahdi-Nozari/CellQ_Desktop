namespace CellQ.Protocol;

public enum ConnectionPhase { Disconnected, Connecting, Connected, Error, Updating }
public sealed record ConnectionSnapshot(ConnectionPhase Phase, string? PortName = null, string? FirmwareVersion = null, DeviceMode Mode = DeviceMode.Unknown, DateTimeOffset? IdentifiedAt = null, bool RequestPending = false, string? Error = null, bool WorkflowActive = false, bool MaintenanceOwned = false)
{
    public static ConnectionSnapshot Disconnected { get; } = new(ConnectionPhase.Disconnected);
}
public sealed record DiagnosticMessage(DateTimeOffset ReceivedAt, string Kind, string Text)
{
    public DateTimeOffset LocalReceivedAt => ReceivedAt.ToLocalTime();
}
public sealed class ProtocolException(string message) : IOException(message);
