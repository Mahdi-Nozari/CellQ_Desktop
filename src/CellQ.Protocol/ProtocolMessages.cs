namespace CellQ.Protocol;

public abstract record ProtocolMessage;
public sealed record DebugLine(string Text) : ProtocolMessage;
public sealed record ResponseLine(string Text) : ProtocolMessage;
public sealed record OtherLine(string Text) : ProtocolMessage;
public sealed record DataFrame(uint Offset, byte[] Payload) : ProtocolMessage;
public enum FramingErrorKind { InvalidMarker, InvalidText, LineTooLong, InvalidHeader, CrcMismatch, Truncated }
public sealed record FramingError(FramingErrorKind Kind, string Description) : ProtocolMessage;

public enum DeviceMode { Unknown, Idle, Manual, Test, Maintenance }
public sealed record HelloResponse(string FirmwareVersion, DeviceMode Mode)
{
    public static bool TryParse(string text, out HelloResponse? response)
    {
        response = null;
        var fields = text.Split(',');
        if (fields.Length != 4 || fields[0] != "CQ1" || fields[1] != "HELLO" || string.IsNullOrWhiteSpace(fields[2])) return false;
        var mode = fields[3] switch { "IDLE" => DeviceMode.Idle, "MANUAL" => DeviceMode.Manual, "TEST" => DeviceMode.Test, "MAINT" => DeviceMode.Maintenance, _ => DeviceMode.Unknown };
        if (mode == DeviceMode.Unknown) return false;
        response = new(fields[2], mode);
        return true;
    }
}
