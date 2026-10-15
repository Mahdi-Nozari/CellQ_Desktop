using System.Text;

namespace CellQ.Protocol;

/// <summary>Owns the sole transport, reader, and serialized protocol operations.</summary>
public sealed partial class DeviceConnectionService(Func<ISerialTransport> createTransport, TimeSpan? helloTimeout = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _sync = new();
    private readonly TimeSpan _helloTimeout = helloTimeout ?? TimeSpan.FromSeconds(3);
    private ConnectionSnapshot _snapshot = ConnectionSnapshot.Disconnected;
    private Session? _session;
    private bool _disposed;
    private Task? _disposal;
    public ConnectionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action<ConnectionSnapshot>? StateChanged;
    public event Action<DiagnosticMessage>? MessageReceived;

    public async Task ConnectAsync(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? session = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                if (Snapshot.Phase != ConnectionPhase.Error) throw new InvalidOperationException("Disconnect the current device before opening another port.");
                await StopAsync(_session).ConfigureAwait(false);
            }
            session = new Session(createTransport(), cancellationToken);
            lock (_sync) _session = session;
            SetState(session, new(ConnectionPhase.Connecting, portName));
            Emit("Connection", $"Opening {portName}: 250000 baud, 8-N-1, DTR off, RTS off.");
            await session.Transport.OpenAsync(portName, new SerialConfiguration(), session.Cancellation.Token).ConfigureAwait(false);
            session.Reader = Task.Run(() => ReadLoopAsync(session));
            var hello = await IdentifyAsync(session, allowSyntaxRetry: true).ConfigureAwait(false);
            AcceptIdentity(session, portName, hello);
        }
        catch (Exception error) when (session is not null)
        {
            await FailOrCancelAsync(session, error).ConfigureAwait(false);
            throw;
        }
        finally { _operations.Release(); }
    }

    public async Task RefreshHelloAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        Session? session = null;
        try
        {
            session = _session;
            if (session is null || Snapshot.Phase != ConnectionPhase.Connected) throw new InvalidOperationException("No identified device is connected.");
            using var registration = cancellationToken.Register(() => Cancel(session));
            var hello = await IdentifyAsync(session).ConfigureAwait(false);
            AcceptIdentity(session, Snapshot.PortName, hello);
        }
        catch (Exception error) when (session is not null)
        {
            await FailOrCancelAsync(session, error).ConfigureAwait(false);
            throw;
        }
        finally { _operations.Release(); }
    }

    private async Task<HelloResponse> IdentifyAsync(Session session, bool allowSyntaxRetry = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) session.PendingHello = completion;
            SetState(session, Snapshot with { RequestPending = true });
            string response;
            try
            {
                Emit("TX", "CQ1,HELLO");
                await session.Transport.WriteAsync(Encoding.ASCII.GetBytes("CQ1,HELLO\n"), session.Cancellation.Token).ConfigureAwait(false);
                response = await completion.Task.WaitAsync(_helloTimeout, session.Cancellation.Token).ConfigureAwait(false);
            }
            catch (TimeoutException) { throw new TimeoutException($"HELLO did not complete within {_helloTimeout.TotalSeconds:g} seconds. The port was closed to avoid a stale response."); }
            finally { lock (_sync) session.PendingHello = null; }

            if (HelloResponse.TryParse(response, out var hello)) return hello!;
            if (!allowSyntaxRetry || attempt != 0 || response != "CQ1,ERR,SYNTAX")
                throw new ProtocolException("Invalid or unexpected response while awaiting CQ1,HELLO: " + response);

            // The explicit rejection completed the first request. Retry only this harmless
            // identification command, on the same port; uncertain responses never permit a retry.
            Emit("Notice", "The device rejected the first identification request (CQ1,ERR,SYNTAX). Waiting 250 ms, then retrying CQ1,HELLO once on the same connection.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), session.Cancellation.Token).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(Session session)
    {
        var buffer = new byte[512];
        try
        {
            while (true)
            {
                int count = await session.Transport.ReadAsync(buffer, session.Cancellation.Token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The serial connection ended.");
                foreach (var message in session.Parser.Feed(buffer.AsSpan(0, count))) Process(session, message);
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!session.Cancellation.IsCancellationRequested)
            {
                session.Failure = error;
                lock (_sync) session.PendingHello?.TrySetException(error);
                lock (_sync) session.PendingRequest?.Writer.TryComplete(error);
                SetState(session, new(ConnectionPhase.Error, Snapshot.PortName, Error: error.Message, WorkflowActive: Snapshot.WorkflowActive, MaintenanceOwned: Snapshot.MaintenanceOwned));
                Emit("Error", "Serial read failed: " + error.Message);
                Cancel(session);
                try { await session.Transport.DisposeAsync().ConfigureAwait(false); } catch (Exception closeError) { Emit("Error", closeError.Message); }
            }
        }
        finally { foreach (var remainder in session.Parser.Complete()) Process(session, remainder); }
    }

    private void Process(Session session, ProtocolMessage message)
    {
        switch (message)
        {
            case DebugLine debug: Emit("Debug", debug.Text); break;
            case OtherLine other: Emit("Other", other.Text); break;
            case DataFrame frame:
                Emit("Binary", $"Valid CRC16 frame: offset {frame.Offset}, {frame.Payload.Length} payload bytes.");
                RouteRequest(session, frame);
                break;
            case FramingError framing:
                Emit("Framing error", framing.Description);
                lock (_sync) session.PendingHello?.TrySetException(new ProtocolException(framing.Description));
                RouteRequest(session, framing);
                break;
            case ResponseLine response:
                Emit("Protocol", response.Text);
                if (RouteRequest(session, response)) break;
                lock (_sync)
                {
                    if (session.PendingHello is null) { Emit("Notice", "Unsolicited CQ1 response; reported device mode was not changed."); break; }
                    session.PendingHello.TrySetResult(response.Text);
                }
                break;
        }
    }

    public async Task DisconnectAsync()
    {
        var session = Volatile.Read(ref _session);
        // A live log workflow cancels its operation and exits maintenance before closing.
        lock (_sync)
        {
            if (_workflowCancellation is not null) _workflowCancellation.Cancel();
            else if (session is not null) Cancel(session);
        }
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null) await StopAsync(_session).ConfigureAwait(false);
            SetState(null, ConnectionSnapshot.Disconnected);
            Emit("Connection", "Port closed.");
        }
        finally { _operations.Release(); }
    }

    private async Task FailOrCancelAsync(Session session, Exception error)
    {
        bool cancelled = error is OperationCanceledException && session.Failure is null;
        var failure = session.Failure ?? error;
        string? port = Snapshot.PortName;
        await StopAsync(session).ConfigureAwait(false);
        SetState(null, cancelled ? ConnectionSnapshot.Disconnected with { WorkflowActive = Snapshot.WorkflowActive } : new(ConnectionPhase.Error, port, Error: failure.Message, WorkflowActive: Snapshot.WorkflowActive));
        Emit(cancelled ? "Connection" : "Error", cancelled ? "Connection cancelled; port closed." : failure.Message);
    }

    private async Task StopAsync(Session session)
    {
        Cancel(session);
        try { await session.Transport.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            SetState(session, new(ConnectionPhase.Error, Snapshot.PortName, Error: "Port close failed: " + error.Message, WorkflowActive: Snapshot.WorkflowActive, MaintenanceOwned: Snapshot.MaintenanceOwned));
            Emit("Error", "Port close failed: " + error.Message);
            throw; // Retain ownership; opening another port is prohibited until close succeeds.
        }
        if (session.Reader is not null) await session.Reader.ConfigureAwait(false);
        lock (_sync) { if (ReferenceEquals(_session, session)) _session = null; }
        session.Cancellation.Dispose();
    }
    private static void Cancel(Session session) { try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    private void SetState(Session? session, ConnectionSnapshot state)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_session, session)) return;
            Volatile.Write(ref _snapshot, state);
            StateChanged?.Invoke(state);
        }
    }
    private void Emit(string kind, string text) => MessageReceived?.Invoke(new(DateTimeOffset.UtcNow, kind, text));
    private void AcceptIdentity(Session session, string? portName, HelloResponse hello)
    {
        lock (_sync)
        {
            session.Cancellation.Token.ThrowIfCancellationRequested();
            if (session.Failure is not null) throw new IOException("Serial connection failed while identifying the device.", session.Failure);
            if (hello.Mode != DeviceMode.Maintenance) Interlocked.Exchange(ref _maintenanceMayRemain, 0);
            SetState(session, Snapshot with { Phase = ConnectionPhase.Connected, PortName = portName, FirmwareVersion = hello.FirmwareVersion, Mode = hello.Mode, IdentifiedAt = DateTimeOffset.UtcNow, RequestPending = false, Error = null });
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            if (_disposal is null || _disposal.IsFaulted) _disposal = DisconnectAsync();
            return new ValueTask(_disposal);
        }
    }
    internal sealed class Session(ISerialTransport transport, CancellationToken cancellationToken)
    {
        public ISerialTransport Transport { get; } = transport;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public StreamParser Parser { get; } = new();
        public Task? Reader { get; set; }
        public TaskCompletionSource<string>? PendingHello { get; set; }
        public System.Threading.Channels.Channel<ProtocolMessage>? PendingRequest { get; set; }
        public Exception? Failure { get; set; }
    }
}
