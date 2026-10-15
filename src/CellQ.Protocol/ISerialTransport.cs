namespace CellQ.Protocol;

public sealed record SerialConfiguration(int BaudRate = 250000, int DataBits = 8, bool DtrEnabled = false, bool RtsEnabled = false)
{
    public string Parity => "None";
    public int StopBits => 1;
    public string Handshake => "None";
}

public interface ISerialTransport : IAsyncDisposable
{
    Task OpenAsync(string portName, SerialConfiguration configuration, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

public interface IPortCatalog { Task<IReadOnlyList<string>> GetPortsAsync(CancellationToken cancellationToken); }
