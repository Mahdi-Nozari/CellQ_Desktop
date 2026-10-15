namespace CellQ.Protocol;

public enum LogPhase { Ready, Listing, Downloading, Verifying, Complete, Cancelled, Denied, Error }
public sealed record LogProgress(LogPhase Phase, string Message, uint Bytes = 0, uint Total = 0, VerifiedLog? Verified = null);

/// <summary>Explicit log workflows, using the connection's exclusive protocol lease.</summary>
public sealed class DataLogService(DeviceConnectionService connection, LogProtocolOptions? options = null)
{
    private readonly LogProtocolOptions _options = options ?? new();
    public event Action<LogProgress>? Progress;
    public VerifiedLog? LastVerified { get; private set; }
    private void Report(LogProgress state) => Progress?.Invoke(state);
    public async Task<VerifiedLog> UseSavedCopyAsync(DeviceLog log, string path, CancellationToken token = default)
    {
        LogPaths.File(log.RemotePath);
        LastVerified = null;
        Report(new(LogPhase.Verifying, "Checking the saved copy…"));
        try
        {
            var verified = await LogFileStore.LoadVerifiedAsync(log, path, token).ConfigureAwait(false);
            LastVerified = verified;
            Report(new(LogPhase.Complete, "Saved copy checked. Device deletion still requires your confirmation.", verified.Size, verified.Size, verified));
            return verified;
        }
        catch (Exception error) { ReportFailure(error); throw; }
    }

    public async Task<IReadOnlyList<DeviceLog>> ListAsync(CancellationToken token = default, bool includePresetNames = false)
    {
        Report(new(LogPhase.Listing, "Reading device logs…"));
        try
        {
            var files = await connection.WithMaintenanceAsync(_options, async (client, ct) =>
            {
                var roots = await client.ListAsync("/DATA", ct).ConfigureAwait(false);
                var result = new List<DeviceLog>();
                foreach (var root in roots.Where(item => item.Folder == "D"))
                {
                    var children = await client.ListAsync(root.RemotePath, ct).ConfigureAwait(false);
                    result.AddRange(children.Where(item => item.Folder == "F").Select(item => item with { Folder = root.Filename }));
                }
                var ordered = result.OrderByDescending(item => item.Folder).ThenBy(item => item.Filename, StringComparer.OrdinalIgnoreCase).ToArray();
                if (includePresetNames)
                    for (int index = 0; index < ordered.Length; index++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Report(new(LogPhase.Listing, $"Reading preset names ({index + 1} of {ordered.Length})…"));
                        ordered[index] = ordered[index] with { PresetName = await ReadPresetNameAsync(client, ordered[index], ct).ConfigureAwait(false) };
                    }
                return (IReadOnlyList<DeviceLog>)ordered;
            }, token).ConfigureAwait(false);
            Report(new(LogPhase.Complete, files.Count == 0 ? "No logs were found on the device." : $"{files.Count} logs found. The device has returned to its normal screen."));
            return files;
        }
        catch (Exception error) { ReportFailure(error, browsing: true); throw; }
    }

    private async Task<string?> ReadPresetNameAsync(DeviceConnectionService.LogSession client, DeviceLog log, CancellationToken token)
    {
        if (log.Size == 0) return null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var start = await client.GetAsync(log.RemotePath, 0, token).ConfigureAwait(false);
                if (start.Size != log.Size || start.Offset != 0) throw new TransferIntegrityException("The device file changed while reading preset names. Browse device logs again.");
                var bytes = new byte[LogCsvHeader.MaximumBytes];
                var crc = new Crc32();
                int count = 0;
                uint offset = 0;
                while (true)
                {
                    var message = await client.NextAsync(token).ConfigureAwait(false);
                    if (message is DataFrame frame)
                    {
                        if (frame.Offset != offset || (long)offset + frame.Payload.Length > log.Size)
                            throw new TransferIntegrityException("The device sent an unexpected CSV header position.");
                        crc.Append(frame.Payload);
                        offset += (uint)frame.Payload.Length;
                        int copy = Math.Min(frame.Payload.Length, bytes.Length - count);
                        frame.Payload.AsSpan(0, copy).CopyTo(bytes.AsSpan(count));
                        count += copy;
                        if (LogCsvHeader.TryReadPresetName(bytes.AsSpan(0, count), false, out var name)) return name;
                    }
                    else
                    {
                        var end = client.End(message);
                        if (end.Size != log.Size || offset != log.Size || end.StreamCrc32 != crc.Value)
                            throw new TransferIntegrityException("The CSV header transfer size or checksum did not match.");
                        LogCsvHeader.TryReadPresetName(bytes.AsSpan(0, count), true, out var name);
                        return name;
                    }
                }
            }
            catch (Exception error) when (error is TransferIntegrityException or TimeoutException && attempt < _options.TransferRetries)
            {
                Report(new(LogPhase.Listing, "Retrying the CSV header…"));
            }
            finally
            {
                // End this preview before reading another file. Cancellation also drains late replies.
                await client.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<VerifiedLog> DownloadAsync(DeviceLog log, string destination, CancellationToken token = default)
    {
        LogPaths.File(log.RemotePath); LogFileStore.ValidateDestination(destination);
        LastVerified = null;
        Report(new(LogPhase.Downloading, "Preparing download…", Total: log.Size));
        try
        {
            var verified = await connection.WithMaintenanceAsync(_options, async (client, ct) =>
            {
                var identity = await client.HashAsync(log.RemotePath, ct).ConfigureAwait(false);
                if (identity.Size != log.Size) throw new TransferIntegrityException("The device file changed since the list was loaded. Browse device logs again.");
                await using var file = await LogFileStore.OpenAsync(destination, log.RemotePath, identity, ct).ConfigureAwait(false);
                bool finalized = false;
                try
                {
                    int retries = 0;
                    while (true)
                    {
                        uint startOffset = file.VerifiedLength;
                        var streamCrc = new Crc32();
                        try
                        {
                            var start = await client.GetAsync(log.RemotePath, startOffset, ct).ConfigureAwait(false);
                            if (start.Size != identity.Size || start.Offset != startOffset) throw new TransferIntegrityException("The device did not accept the verified resume position.");
                            while (true)
                            {
                                var message = await client.NextAsync(ct).ConfigureAwait(false);
                                if (message is DataFrame frame)
                                {
                                    await file.AppendAsync(frame, ct).ConfigureAwait(false);
                                    streamCrc.Append(frame.Payload);
                                    Report(new(LogPhase.Downloading, $"Downloading {log.Filename}…", file.VerifiedLength, identity.Size));
                                    continue;
                                }
                                var end = client.End(message);
                                if (end.Size != identity.Size || file.VerifiedLength != identity.Size || end.StreamCrc32 != streamCrc.Value)
                                    throw new TransferIntegrityException("The download size or transfer checksum did not match.");
                                break;
                            }
                            break;
                        }
                        catch (Exception error) when (error is TransferIntegrityException or TimeoutException)
                        {
                            await file.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                            await client.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                            if (retries++ >= _options.TransferRetries) throw;
                            Report(new(LogPhase.Downloading, "Retrying from the last checked position…", file.VerifiedLength, identity.Size));
                        }
                    }
                    Report(new(LogPhase.Verifying, "Checking the saved file…", file.VerifiedLength, identity.Size));
                    var finalHash = await client.HashAsync(log.RemotePath, ct).ConfigureAwait(false);
                    var result = await file.FinalizeAsync(finalHash, ct, log.PresetName).ConfigureAwait(false);
                    finalized = true; LastVerified = result;
                    Report(new(LogPhase.Verifying, "Copy verified. Returning the device to its normal screen…", result.Size, result.Size, result));
                    return result;
                }
                finally
                {
                    // Save every fully checked frame on cancel/unplug. A failed write tail is excluded.
                    if (!finalized && file.Stream.CanWrite) await file.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
            Report(new(LogPhase.Complete, "Download complete. Your saved copy is verified.", verified.Size, verified.Size, verified));
            return verified;
        }
        catch (Exception error) { ReportFailure(error); throw; }
    }

    public async Task<bool> DeleteVerifiedAsync(VerifiedLog verified, Func<DeletionConfirmation, CancellationToken, Task<bool>> confirm, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(confirm); LogPaths.File(verified.RemotePath);
        await using var local = LogFileStore.OpenVerified(verified);
        await LogFileStore.VerifyLocalAsync(local, verified, token).ConfigureAwait(false);
        if (!await confirm(new(verified.RemotePath, verified.LocalPath, verified.Size), token).ConfigureAwait(false)) return false;
        await LogFileStore.VerifyLocalAsync(local, verified, token).ConfigureAwait(false);
        try
        {
            await connection.WithMaintenanceAsync(_options, async (client, ct) =>
            {
                Report(new(LogPhase.Verifying, "Checking the device file before deletion…"));
                var current = await client.HashAsync(verified.RemotePath, ct).ConfigureAwait(false);
                if (current.Size != verified.Size || current.Crc32 != verified.Crc32) throw new TransferIntegrityException("The device file has changed. It will not be deleted.");
                await client.DeleteAsync(verified.RemotePath, verified.Size, verified.Crc32, ct).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
            Report(new(LogPhase.Complete, "Deleted from the device. Your verified local copy was kept."));
            return true;
        }
        catch (Exception error) { ReportFailure(error); throw; }
    }
    private void ReportFailure(Exception error, bool browsing = false) => Report(new(error switch
    {
        OperationCanceledException when connection.Snapshot.Phase != ConnectionPhase.Error => LogPhase.Cancelled,
        MaintenanceDeniedException or DeviceReplyException => LogPhase.Denied,
        _ => LogPhase.Error
    }, error is OperationCanceledException && connection.Snapshot.Phase != ConnectionPhase.Error ? browsing ? "Stopped reading logs." : "Stopped. Checked progress was kept for resume." : error.Message, Verified: LastVerified));
}
