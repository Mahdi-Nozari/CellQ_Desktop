using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace CellQ.Protocol;

public sealed record LogProtocolOptions
{
    // Host policies, not timeout numbers supplied by protocol v1.
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan NextTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HashTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public int TransferRetries { get; init; } = 3;
}
public sealed class MaintenanceDeniedException(string message) : IOException(message);
public sealed class DeviceReplyException(string response) : IOException("The device refused the request or returned an unexpected reply: " + response)
{
    public string Response { get; } = response;
}
public sealed class TransferIntegrityException(string message) : IOException(message);
public sealed class MaintenanceRecoveryException(string message, Exception? inner = null) : IOException(message, inner);

public sealed record DeviceLog(string RemotePath, string Filename, string Folder, uint Size, string? PresetName = null);
internal readonly record struct FileHash(uint Size, uint Crc32);
internal readonly record struct TransferEnd(uint Size, uint StreamCrc32);
internal readonly record struct TransferStart(uint Size, uint Offset);

internal static class LogPaths
{
    public static bool Directory(string path) => path == "/DATA" || path == "/DATA/NO_RTC" ||
        (path.StartsWith("/DATA/", StringComparison.Ordinal) && path.Length == 14 && path.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0);
    public static bool Name(string name) => name.Length is > 0 and <= 255 && name is not ("." or "..") &&
        name.All(c => c is >= ' ' and <= '~' && ",/\\:<>\"|*?".IndexOf(c) < 0) && !name.EndsWith(' ') && !name.EndsWith('.');
    public static void File(string path)
    {
        int slash = path.LastIndexOf('/');
        if (slash <= 6 || !Directory(path[..slash]) || !Name(path[(slash + 1)..])) throw new ProtocolException("Only listed log files under a dated DATA folder or NO_RTC are allowed.");
    }
    public static uint Number(string field) => uint.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new ProtocolException("Invalid unsigned protocol number.");
    public static uint Crc(string field) => field.Length is >= 1 and <= 8 && field.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') &&
        uint.TryParse(field, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var crc) ? crc : throw new ProtocolException("Expected an uppercase CRC32 hexadecimal value.");
}

public sealed partial class DeviceConnectionService
{
    private CancellationTokenSource? _workflowCancellation;
    private int _maintenanceMayRemain;
    public bool MaintenanceMayRemain => Volatile.Read(ref _maintenanceMayRemain) != 0;
    private bool RouteRequest(Session session, ProtocolMessage message)
    {
        lock (_sync)
        {
            if (session.PendingRequest is null || session.Cancellation.IsCancellationRequested) return false;
            if (!session.PendingRequest.Writer.TryWrite(message)) throw new ProtocolException("Too many queued protocol messages.");
            return true;
        }
    }

    internal async Task<T> WithMaintenanceAsync<T>(LogProtocolOptions options, Func<LogSession, CancellationToken, Task<T>> action, CancellationToken cancellationToken, bool existingOnly = false)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        var session = _session;
        bool entered = false, exited = false, enterRequested = false;
        Exception? operationError = null;
        CancellationTokenSource? workflow = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (session is null || Snapshot.Phase != ConnectionPhase.Connected) throw new IOException("Connect to your CellQ before accessing logs.");
            workflow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cancellation.Token);
            lock (_sync) _workflowCancellation = workflow;
            SetState(session, Snapshot with { WorkflowActive = true });
            // A fresh observation prevents a stale Idle snapshot from admitting a test.
            var hello = await IdentifyAsync(session).ConfigureAwait(false);
            AcceptIdentity(session, Snapshot.PortName, hello);
            workflow.Token.ThrowIfCancellationRequested();
            if (existingOnly && hello.Mode != DeviceMode.Maintenance) throw new MaintenanceDeniedException("The device is not in Maintenance mode.");
            if (hello.Mode == DeviceMode.Test) throw new MaintenanceDeniedException("A test is running or paused. Finish it on the device before accessing logs.");
            var client = new LogSession(this, session, options);
            if (hello.Mode != DeviceMode.Maintenance)
            {
                enterRequested = true;
                // Once entry is transmitted, await its bounded ACK even if the user stops.
                // This lets cancellation safely EXIT an accepted entry instead of losing its mode transition.
                await client.OkAsync("ENTER", CancellationToken.None).ConfigureAwait(false);
            }
            entered = true;
            SetState(session, Snapshot with { Mode = DeviceMode.Maintenance, MaintenanceOwned = true });
            if (hello.Mode == DeviceMode.Maintenance && !existingOnly)
                await client.CancelAsync(CancellationToken.None).ConfigureAwait(false); // Recover an old LIST/GET/check, only on this explicit action.
            return await action(client, workflow.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            operationError = error;
            if (enterRequested && !entered && error is DeviceReplyException && session is not null && !session.Cancellation.IsCancellationRequested)
            {
                // ENTER has replied, but its error encoding is not specified by v1.
                // Observe the actual mode instead of interpreting a guessed error code.
                try
                {
                    var observed = await IdentifyAsync(session).ConfigureAwait(false);
                    AcceptIdentity(session, Snapshot.PortName, observed);
                    if (observed.Mode == DeviceMode.Maintenance)
                    {
                        entered = true;
                        SetState(session, Snapshot with { MaintenanceOwned = true });
                    }
                }
                catch (Exception identificationError)
                {
                    Interlocked.Exchange(ref _maintenanceMayRemain, 1);
                    var uncertain = new MaintenanceRecoveryException("The entry reply was not accepted and device state could not be checked. Reconnect and use Return to normal if needed.", identificationError);
                    await FailOrCancelAsync(session, uncertain).ConfigureAwait(false);
                    throw uncertain;
                }
            }
            if (enterRequested && !entered && error is not DeviceReplyException) Interlocked.Exchange(ref _maintenanceMayRemain, 1);
            if (session is not null && !entered && error is IOException or TimeoutException && error is not (DeviceReplyException or MaintenanceDeniedException))
                await FailOrCancelAsync(session, new MaintenanceRecoveryException("Device state is uncertain. Reconnect and use Return to normal if Maintenance mode is reported.", error)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Exception? cleanupError = null;
            if (entered && session is not null)
            {
                if (!session.Cancellation.IsCancellationRequested && Snapshot.Phase == ConnectionPhase.Connected)
                {
                    try
                    {
                        var client = new LogSession(this, session, options);
                        if (!existingOnly) await client.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                        await client.OkAsync("EXIT", CancellationToken.None).ConfigureAwait(false);
                        exited = true;
                        Interlocked.Exchange(ref _maintenanceMayRemain, 0);
                        SetState(session, Snapshot with { Mode = DeviceMode.Unknown, MaintenanceOwned = false });
                        var hello = await IdentifyAsync(session).ConfigureAwait(false);
                        AcceptIdentity(session, Snapshot.PortName, hello);
                    }
                    catch (Exception error)
                    {
                        cleanupError = new MaintenanceRecoveryException(exited
                            ? "The device accepted return to its normal screen, but could not be identified again. Reconnect to check its status."
                            : "The device could not return to its normal screen. Reconnect and choose Return to normal.", error);
                        try { await FailOrCancelAsync(session, cleanupError).ConfigureAwait(false); }
                        catch (Exception closeError) { cleanupError = new MaintenanceRecoveryException(cleanupError.Message + " " + closeError.Message, cleanupError); }
                    }
                }
                else cleanupError = new MaintenanceRecoveryException("The connection was lost during log access. Reconnect, then resume or choose Return to normal.");
            }
            lock (_sync) _workflowCancellation = null;
            if (session is not null) SetState(session, Snapshot with { WorkflowActive = false, MaintenanceOwned = false });
            workflow?.Dispose();
            _operations.Release();
            if (cleanupError is not null)
            {
                if (!exited) Interlocked.Exchange(ref _maintenanceMayRemain, 1);
                Emit("Error", cleanupError.Message);
                if (operationError is null) throw cleanupError;
            }
        }
    }

    public Task ReturnToNormalAsync(CancellationToken cancellationToken = default) =>
        WithMaintenanceAsync(new LogProtocolOptions(), (client, _) => Task.FromResult(true), cancellationToken, existingOnly: true);

    internal sealed class LogSession(DeviceConnectionService owner, Session session, LogProtocolOptions options)
    {
        private async Task<T> RequestAsync<T>(string command, Func<ChannelReader<ProtocolMessage>, CancellationToken, Task<T>> read, CancellationToken token)
        {
            if (command.Any(c => c < 32 || c > 126) || Encoding.ASCII.GetByteCount(command) + 1 > 79) throw new ProtocolException("Command exceeds protocol v1's ASCII line limit.");
            token.ThrowIfCancellationRequested();
            var channel = Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Cancellation.Token);
            lock (owner._sync)
            {
                if (session.PendingRequest is not null || session.PendingHello is not null) throw new InvalidOperationException("Another request is active.");
                session.PendingRequest = channel;
            }
            owner.SetState(session, owner.Snapshot with { RequestPending = true });
            try
            {
                owner.Emit("TX", command);
                await session.Transport.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), linked.Token).ConfigureAwait(false);
                return await read(channel.Reader, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (owner._sync) if (ReferenceEquals(session.PendingRequest, channel)) session.PendingRequest = null;
                channel.Writer.TryComplete();
                owner.SetState(session, owner.Snapshot with { RequestPending = false });
            }
        }
        private static async Task<ProtocolMessage> ReadAsync(ChannelReader<ProtocolMessage> reader, TimeSpan timeout, CancellationToken token)
        {
            var message = await reader.ReadAsync(token).AsTask().WaitAsync(timeout, token).ConfigureAwait(false);
            if (message is FramingError fault) throw new TransferIntegrityException(fault.Description);
            return message;
        }
        private static string Line(ProtocolMessage message) => message is ResponseLine response ? response.Text : throw new ProtocolException("Expected a CQ1 response line.");
        public Task<bool> OkAsync(string command, CancellationToken token) => RequestAsync("CQ1," + command, async (reader, ct) =>
        {
            string line = Line(await ReadAsync(reader, options.CommandTimeout, ct).ConfigureAwait(false));
            if (line != "CQ1,OK," + command) throw new DeviceReplyException(line);
            return true;
        }, token);
        public Task<bool> CancelAsync(CancellationToken token) => RequestAsync("CQ1,CANCEL", async (reader, ct) =>
        {
            // A timed-out NEXT or check can produce a late result before CANCEL's reply.
            var deadline = DateTimeOffset.UtcNow + options.CommandTimeout;
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("The device did not confirm cancellation.");
                var message = await reader.ReadAsync(ct).AsTask().WaitAsync(remaining, ct).ConfigureAwait(false);
                if (message is ResponseLine { Text: "CQ1,OK,CANCEL" }) return true;
                owner.Emit("Notice", "Discarded an old response while waiting for cancellation.");
            }
        }, token);
        public Task<IReadOnlyList<DeviceLog>> ListAsync(string directory, CancellationToken token)
        {
            if (!LogPaths.Directory(directory)) throw new ProtocolException("Invalid log directory.");
            return RequestAsync<IReadOnlyList<DeviceLog>>("CQ1,LIST," + directory, async (reader, ct) =>
            {
                var first = Line(await ReadAsync(reader, options.CommandTimeout, ct).ConfigureAwait(false));
                if (first != "CQ1,LIST,BEGIN") throw new DeviceReplyException(first);
                var entries = new List<DeviceLog>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    string line = Line(await ReadAsync(reader, options.CommandTimeout, ct).ConfigureAwait(false));
                    if (line == "CQ1,LIST,END") return entries;
                    var parts = line.Split(',');
                    if (parts.Length != 5 || parts[0] != "CQ1" || parts[1] != "ITEM" || !LogPaths.Name(parts[2]) || parts[3] is not ("D" or "F")) throw new ProtocolException("Invalid log listing item: " + line);
                    if (!names.Add(parts[2]) || entries.Count == 100000) throw new ProtocolException("Duplicate or excessive log listing entries.");
                    uint size = LogPaths.Number(parts[4]);
                    string path = directory + "/" + parts[2];
                    if (parts[3] == "D") { if (directory != "/DATA" || !LogPaths.Directory(path)) throw new ProtocolException("Unexpected directory outside generated DATA folders."); }
                    else LogPaths.File(path);
                    entries.Add(new DeviceLog(path, parts[2], parts[3], size));
                }
            }, token);
        }
        public Task<FileHash> HashAsync(string path, CancellationToken token)
        {
            LogPaths.File(path);
            return RequestAsync("CQ1,HASH," + path, async (reader, ct) =>
            {
                string start = Line(await ReadAsync(reader, options.CommandTimeout, ct).ConfigureAwait(false));
                if (start != "CQ1,OK,HASHING") throw new DeviceReplyException(start);
                var fields = Line(await ReadAsync(reader, options.HashTimeout, ct).ConfigureAwait(false)).Split(',');
                if (fields.Length != 4 || fields[0] != "CQ1" || fields[1] != "HASH") throw new ProtocolException("Invalid whole-file hash response.");
                return new FileHash(LogPaths.Number(fields[2]), LogPaths.Crc(fields[3]));
            }, token);
        }
        public Task<TransferStart> GetAsync(string path, uint offset, CancellationToken token)
        {
            LogPaths.File(path);
            return RequestAsync("CQ1,GET," + path + "," + offset.ToString(CultureInfo.InvariantCulture), async (reader, ct) =>
            {
                var line = Line(await ReadAsync(reader, options.CommandTimeout, ct).ConfigureAwait(false));
                var fields = line.Split(',');
                if (fields.Length != 4 || fields[0] != "CQ1" || fields[1] != "GET") throw new DeviceReplyException(line);
                return new TransferStart(LogPaths.Number(fields[2]), LogPaths.Number(fields[3]));
            }, token);
        }
        public Task<ProtocolMessage> NextAsync(CancellationToken token) => RequestAsync("CQ1,NEXT", (reader, ct) => ReadAsync(reader, options.NextTimeout, ct), token);
        public TransferEnd End(ProtocolMessage message)
        {
            var fields = Line(message).Split(',');
            if (fields.Length != 4 || fields[0] != "CQ1" || fields[1] != "EOF") throw new ProtocolException("Invalid transfer end response.");
            return new TransferEnd(LogPaths.Number(fields[2]), LogPaths.Crc(fields[3]));
        }
        public Task<bool> DeleteAsync(string path, uint size, uint crc, CancellationToken token)
        {
            LogPaths.File(path);
            return RequestAsync($"CQ1,DELETE,{path},{size.ToString(CultureInfo.InvariantCulture)},{crc:X8}", async (reader, ct) =>
            {
                string first = Line(await ReadAsync(reader, options.CommandTimeout, ct).ConfigureAwait(false));
                if (first != "CQ1,OK,CHECKING") throw new DeviceReplyException(first);
                string final = Line(await ReadAsync(reader, options.HashTimeout, ct).ConfigureAwait(false));
                if (final != "CQ1,OK,DELETE") throw new DeviceReplyException(final);
                return true;
            }, token);
        }
    }
}
