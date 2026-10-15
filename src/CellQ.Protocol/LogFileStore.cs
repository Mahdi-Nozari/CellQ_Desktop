using System.Security.Cryptography;
using System.Text.Json;

namespace CellQ.Protocol;

public sealed record VerifiedLog(string RemotePath, string LocalPath, uint Size, uint Crc32, string Sha256, string? PresetName = null);
public sealed record DeletionConfirmation(string RemotePath, string LocalPath, uint Size);
internal sealed record ResumeRecord(int Version, string RemotePath, uint Size, uint RemoteCrc32, uint VerifiedLength, uint PrefixCrc32, string PrefixSha256);

/// <summary>Only a user-chosen destination and its owned .part sidecars may be written.</summary>
internal sealed class LogFileStore : IAsyncDisposable
{
    private readonly string _target;
    private readonly string _metadata;
    public FileStream Stream { get; }
    public uint VerifiedLength { get; private set; }
    private uint _checkpointLength;
    private readonly string _remote;
    private readonly FileHash _identity;
    private readonly Crc32 _wholeCrc = new();
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private LogFileStore(string target, string remote, FileHash identity, FileStream stream)
    {
        _target = target; _remote = remote; _identity = identity; Stream = stream; _metadata = target + ".part.json";
    }
    public static void ValidateDestination(string target)
    {
        target = Path.GetFullPath(target);
        if (File.Exists(target) || File.Exists(target + ".verified.json")) throw new IOException("A completed file or verification receipt already exists here. Choose another filename; it will not be overwritten.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new IOException("Choose an existing save folder.");
        if (File.Exists(target + ".part") != File.Exists(target + ".part.json")) throw new IOException("The interrupted download has no matching progress record. Choose another filename to keep the existing files untouched.");
        foreach (string path in new[] { target + ".part", target + ".part.json" }) RejectLink(path);
    }
    private static void RejectLink(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked files cannot be used for verified log storage.");
    }
    public static async Task<LogFileStore> OpenAsync(string target, string remote, FileHash identity, CancellationToken token)
    {
        target = Path.GetFullPath(target); ValidateDestination(target);
        bool resume = File.Exists(target + ".part");
        ResumeRecord? record = null;
        if (resume)
        {
            record = JsonSerializer.Deserialize<ResumeRecord>(await File.ReadAllTextAsync(target + ".part.json", token).ConfigureAwait(false));
            if (record is null || record.Version != 1 || record.RemotePath != remote || record.Size != identity.Size || record.RemoteCrc32 != identity.Crc32 || record.VerifiedLength > identity.Size)
                throw new TransferIntegrityException("The device file or saved progress has changed. Choose another filename to start a new download.");
        }
        var stream = new FileStream(target + ".part", resume ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var store = new LogFileStore(target, remote, identity, stream);
        try
        {
            if (record is not null)
            {
                if (stream.Length < record.VerifiedLength) throw new TransferIntegrityException("The incomplete copy is shorter than its verified progress.");
                await store.ReadPrefixAsync(record.VerifiedLength, token).ConfigureAwait(false);
                if (store._wholeCrc.Value != record.PrefixCrc32 || Convert.ToHexString(store._sha.GetCurrentHash()) != record.PrefixSha256)
                    throw new TransferIntegrityException("The incomplete local copy has changed and cannot safely resume.");
                stream.SetLength(record.VerifiedLength); // Discard only an uncheckpointed crash tail, after validating the saved prefix.
                stream.Position = record.VerifiedLength;
                store.VerifiedLength = store._checkpointLength = record.VerifiedLength;
            }
            else await store.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
            return store;
        }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private async Task ReadPrefixAsync(uint count, CancellationToken token)
    {
        var buffer = new byte[65536]; long remaining = count;
        while (remaining > 0)
        {
            int read = await Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token).ConfigureAwait(false);
            if (read == 0) throw new TransferIntegrityException("The saved copy ended before its verified offset.");
            _wholeCrc.Append(buffer.AsSpan(0, read)); _sha.AppendData(buffer.AsSpan(0, read)); remaining -= read;
        }
    }
    public async Task AppendAsync(DataFrame frame, CancellationToken token)
    {
        if (frame.Offset != VerifiedLength || (long)VerifiedLength + frame.Payload.Length > _identity.Size)
            throw new TransferIntegrityException("The device sent an unexpected file position or too many bytes.");
        await Stream.WriteAsync(frame.Payload, token).ConfigureAwait(false);
        _wholeCrc.Append(frame.Payload); _sha.AppendData(frame.Payload);
        VerifiedLength += (uint)frame.Payload.Length;
        if (VerifiedLength - _checkpointLength >= 4096) await CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
    }
    public async Task CheckpointAsync(CancellationToken token)
    {
        Stream.SetLength(VerifiedLength);
        await Stream.FlushAsync(token).ConfigureAwait(false);
        Stream.Flush(flushToDisk: true);
        var record = new ResumeRecord(1, _remote, _identity.Size, _identity.Crc32, VerifiedLength, _wholeCrc.Value, Convert.ToHexString(_sha.GetCurrentHash()));
        await WriteJsonAsync(_metadata, record, overwrite: true, token).ConfigureAwait(false);
        _checkpointLength = VerifiedLength;
    }
    private static async Task WriteJsonAsync<T>(string path, T value, bool overwrite, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, value, cancellationToken: token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); } // Only our uniquely created metadata scratch file.
    }
    public async Task<VerifiedLog> FinalizeAsync(FileHash finalHash, CancellationToken token, string? expectedPresetName = null)
    {
        if (VerifiedLength != _identity.Size || Stream.Length != _identity.Size || finalHash != _identity || _wholeCrc.Value != finalHash.Crc32)
            throw new TransferIntegrityException("Whole-file verification failed. The local copy remains incomplete.");
        await CheckpointAsync(token).ConfigureAwait(false);
        // Reread disk bytes while holding the exclusive file handle, rather than trusting write buffers.
        Stream.Position = 0;
        var actual = await HashLocalAsync(Stream, token).ConfigureAwait(false);
        string sha = Convert.ToHexString(_sha.GetCurrentHash());
        if (actual.Size != _identity.Size || actual.Crc32 != _identity.Crc32 || actual.Sha256 != sha)
            throw new TransferIntegrityException("The saved bytes did not pass final verification.");
        string? presetName = await LogCsvHeader.ReadPresetNameAsync(Stream, token).ConfigureAwait(false);
        if (expectedPresetName is not null && presetName != expectedPresetName)
            throw new TransferIntegrityException("The preset name changed since browsing. Browse device logs again before saving this file.");
        token.ThrowIfCancellationRequested();
        await Stream.DisposeAsync().ConfigureAwait(false);
        File.Move(_target + ".part", _target, overwrite: false);
        var verified = new VerifiedLog(_remote, _target, _identity.Size, _identity.Crc32, sha, presetName);
        // The final file is checked again before creating its verification receipt.
        // Finish the small commit once the verified file is renamed; cancellation must not
        // leave a completed copy without its receipt halfway through this commit.
        await using (var completed = OpenVerified(verified)) await VerifyLocalAsync(completed, verified, CancellationToken.None).ConfigureAwait(false);
        await WriteJsonAsync(_target + ".verified.json", verified, overwrite: false, CancellationToken.None).ConfigureAwait(false);
        File.Delete(_metadata); // The now-consumed resume record owned by this download.
        return verified;
    }
    public static FileStream OpenVerified(VerifiedLog verified)
    {
        RejectLink(verified.LocalPath);
        return new FileStream(verified.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    public static async Task<VerifiedLog> LoadVerifiedAsync(DeviceLog log, string localPath, CancellationToken token)
    {
        localPath = Path.GetFullPath(localPath); string receiptPath = localPath + ".verified.json";
        RejectLink(receiptPath);
        if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 65536)
            throw new TransferIntegrityException("Choose a completed copy saved by this app, with its verification receipt.");
        var receipt = JsonSerializer.Deserialize<VerifiedLog>(await File.ReadAllTextAsync(receiptPath, token).ConfigureAwait(false));
        if (receipt is null || receipt.RemotePath != log.RemotePath || receipt.Size != log.Size || receipt.Sha256 is null || receipt.Sha256.Length != 64)
            throw new TransferIntegrityException("This saved copy does not match the selected device log.");
        var verified = receipt with { LocalPath = localPath }; // Only the path explicitly chosen by the user is opened.
        await using var file = OpenVerified(verified);
        await VerifyLocalAsync(file, verified, token).ConfigureAwait(false);
        return verified with { PresetName = await LogCsvHeader.ReadPresetNameAsync(file, token).ConfigureAwait(false) };
    }
    public static async Task VerifyLocalAsync(FileStream stream, VerifiedLog verified, CancellationToken token)
    {
        stream.Position = 0;
        var actual = await HashLocalAsync(stream, token).ConfigureAwait(false);
        if (actual.Size != verified.Size || actual.Crc32 != verified.Crc32 || actual.Sha256 != verified.Sha256)
            throw new TransferIntegrityException("The local copy has changed. Device deletion is unavailable until a matching copy is verified.");
    }
    private static async Task<(long Size, uint Crc32, string Sha256)> HashLocalAsync(FileStream stream, CancellationToken token)
    {
        var crc = new Crc32(); using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long size = 0; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            size += count; crc.Append(buffer.AsSpan(0, count)); sha.AppendData(buffer.AsSpan(0, count));
        }
        return (size, crc.Value, Convert.ToHexString(sha.GetHashAndReset()));
    }
    public async ValueTask DisposeAsync() { await Stream.DisposeAsync().ConfigureAwait(false); _sha.Dispose(); }
}
