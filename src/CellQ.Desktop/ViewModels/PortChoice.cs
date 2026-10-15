using CellQ.Desktop.Infrastructure;

namespace CellQ.Desktop.ViewModels;

public sealed class PortChoice(string portName) : ObservableObject
{
    private bool _identified;
    public string PortName { get; } = portName;
    public string Label => _identified ? $"CellQ ({PortName})" : PortName;
    internal void SetIdentified(bool identified)
    {
        if (_identified == identified) return;
        _identified = identified;
        Raise(nameof(Label));
    }
}
