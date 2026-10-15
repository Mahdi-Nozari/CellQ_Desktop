using System.Text.RegularExpressions;
using CellQ.Protocol;

namespace CellQ.Desktop.Services;

public sealed class SerialPortCatalog : IPortCatalog
{
    private readonly Func<CancellationToken, IReadOnlyList<WindowsPortDevice>> _readDevices;

    public SerialPortCatalog() : this(WindowsPortDevices.Read) { }
    internal SerialPortCatalog(Func<CancellationToken, IReadOnlyList<WindowsPortDevice>> readDevices) => _readDevices = readDevices;

    public Task<IReadOnlyList<string>> GetPortsAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<string>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = _readDevices(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return SelectMegaPorts(devices);
    }, cancellationToken);

    internal static IReadOnlyList<string> SelectMegaPorts(IEnumerable<WindowsPortDevice> devices) => devices
        .Where(device => device.PortName is not null && device.FriendlyName is not null
            && Regex.IsMatch(device.PortName.Trim(), @"^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(device.FriendlyName.Trim(), @"^Arduino\s+Mega(?:\s*2560)?(?:\s+(?:Rev\.?\s*|R)\d+)?\s*(?:\(COM[1-9][0-9]*\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .Select(device => device.PortName!.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => uint.TryParse(name.AsSpan(3), out var number) ? number : uint.MaxValue)
        .ThenBy(name => name, StringComparer.Ordinal)
        .ToArray();
}
