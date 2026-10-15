using System.IO.Ports;
using System.IO;
using CellQ.Protocol;

namespace CellQ.Desktop.Services;

public sealed class SerialPortTransport : ISerialTransport
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    public async Task OpenAsync(string portName, SerialConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration != new SerialConfiguration()) throw new ArgumentException("Only protocol v1 serial settings are permitted.");
        var port = CreateConfiguredPort(portName);
        try
        {
            await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); port.Open(); cancellationToken.ThrowIfCancellationRequested(); }, cancellationToken).ConfigureAwait(false);
            _port = port;
        }
        catch { port.Dispose(); throw; }
    }
    internal static SerialPort CreateConfiguredPort(string portName) => new(portName, 250000, Parity.None, 8, StopBits.One)
        {
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            ReadTimeout = 250,
            WriteTimeout = 1000
        };
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => new(Task.Run(() =>
    {
        var port = _port ?? throw new IOException("Serial port is closed.");
        var bytes = new byte[buffer.Length];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { int count = port.Read(bytes, 0, bytes.Length); bytes.AsSpan(0, count).CopyTo(buffer.Span); return count; }
            catch (TimeoutException) { /* Poll cancellation without treating idle debug traffic as a fault. */ }
        }
    }, cancellationToken));

    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = _port ?? throw new IOException("Serial port is closed.");
        var array = bytes.ToArray();
        port.Write(array, 0, array.Length);
    }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _closeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var port = _port;
            if (port is not null) { await Task.Run(port.Dispose).ConfigureAwait(false); _port = null; }
        }
        finally { _closeGate.Release(); }
    }
}

