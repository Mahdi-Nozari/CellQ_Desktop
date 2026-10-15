using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Channels;
using CellQ.Protocol;

namespace CellQ.Testing;

/// <summary>Test-only byte-stream device; never opens a physical serial port.</summary>
public sealed class FakeDevice : ISerialTransport
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private byte[]? _chunk;
    private int _offset;
    private int _closed;
    public ConcurrentQueue<string> Writes { get; } = new();
    public SerialConfiguration? Configuration { get; private set; }
    public string? PortName { get; private set; }
    public bool IsOpen { get; private set; }
    public int OpenCount { get; private set; }
    public int CloseCount { get; private set; }
    public bool FailClose { get; set; }
    public Func<CancellationToken, Task>? OnOpen { get; set; }
    public Func<string, Task>? OnWrite { get; set; }
    public async Task OpenAsync(string portName, SerialConfiguration configuration, CancellationToken cancellationToken)
    {
        Configuration = configuration; PortName = portName; OpenCount++;
        if (OnOpen is not null) await OnOpen(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IsOpen = true;
    }
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_chunk is null || _offset == _chunk.Length)
        {
            try { _chunk = await _incoming.Reader.ReadAsync(cancellationToken); _offset = 0; }
            catch (ChannelClosedException error) { if (error.InnerException is not null) throw new IOException(error.InnerException.Message, error); return 0; }
        }
        int count = Math.Min(buffer.Length, _chunk.Length - _offset);
        _chunk.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
        return count;
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string text = Encoding.ASCII.GetString(bytes.Span);
        Writes.Enqueue(text);
        if (OnWrite is not null) await OnWrite(text).WaitAsync(cancellationToken);
    }
    public void Feed(byte[] bytes) => _incoming.Writer.TryWrite(bytes);
    public void Feed(string text) => Feed(Encoding.ASCII.GetBytes(text));
    public void LoseConnection(string? reason = null) => _incoming.Writer.TryComplete(reason is null ? null : new IOException(reason));
    public ValueTask DisposeAsync()
    {
        if (FailClose) throw new IOException("Simulated close failure.");
        if (Interlocked.Exchange(ref _closed, 1) == 0) { IsOpen = false; CloseCount++; _incoming.Writer.TryComplete(); }
        return ValueTask.CompletedTask;
    }
    public static FakeDevice Responding(string mode = "IDLE", string version = "test-firmware")
    {
        var device = new FakeDevice();
        device.OnWrite = _ => { device.Feed($"CQ1,HELLO,{version},{mode}\n"); return Task.CompletedTask; };
        return device;
    }
}

public sealed class FakePortCatalog : IPortCatalog
{
    public Task<IReadOnlyList<string>> GetPortsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["COM_TEST"]);
}
