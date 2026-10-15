using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

/// <summary>Human-readable presentation only; never changes or infers protocol state.</summary>
public static class DevicePresentation
{
    public static string Mode(DeviceMode mode) => mode switch
    {
        DeviceMode.Idle => "Idle",
        DeviceMode.Manual => "Manual mode",
        DeviceMode.Test => "Test active",
        DeviceMode.Maintenance => "Maintenance mode",
        _ => "Not available"
    };

    public static bool IsActivity(DiagnosticMessage message) => message.Kind is "Connection" or "TX" or "Protocol" or "Error" or "Framing error";

    public static string Activity(DiagnosticMessage message) => message.Kind switch
    {
        "TX" when message.Text == "CQ1,HELLO" => "Checking device information…",
        "TX" when message.Text == "CQ1,ENTER" => "Preparing to access device logs…",
        "TX" when message.Text == "CQ1,EXIT" => "Returning the device to its normal screen…",
        "TX" when message.Text.StartsWith("CQ1,DELETE,", StringComparison.Ordinal) => "Deleting the confirmed device log…",
        "TX" => "Accessing device logs…",
        "Protocol" when HelloResponse.TryParse(message.Text, out var hello) => $"Device replied · Firmware {hello!.FirmwareVersion} · {Mode(hello.Mode)}",
        "Protocol" => "A device reply was received. See technical details for the original message.",
        "Connection" when message.Text.StartsWith("Opening ", StringComparison.Ordinal) => "Connecting to the device…",
        "Connection" when message.Text.Contains("cancelled", StringComparison.OrdinalIgnoreCase) => "Connection cancelled.",
        "Connection" => "Disconnected from the device.",
        "Framing error" => "A device message could not be read. See technical details.",
        "Error" => "There was a connection problem. Check the USB cable and try reconnecting.",
        _ => "Device message received."
    };
}
