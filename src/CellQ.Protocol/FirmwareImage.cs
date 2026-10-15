using System.Security.Cryptography;
using System.Text;

namespace CellQ.Protocol;

public static class FirmwareImage
{
    // Mega2560 bootloader profile reserves the final 8 KiB; never write bootloader/fuses/EEPROM.
    public const uint ApplicationBytes = 253952;
    public static string ParseChecksum(byte[] bytes, string filename)
    {
        string suffix = "  " + filename + "\n";
        if (bytes.Length != 64 + suffix.Length || bytes.Any(b => b > 127)) throw new IOException("The checksum file must contain one exact SHA-256 line ending in LF.");
        string text = Encoding.ASCII.GetString(bytes);
        if (text[..64].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || text[64..] != suffix) throw new IOException("The checksum format or firmware filename does not match the release.");
        return text[..64];
    }
    public static async Task VerifyFileAsync(string path, long expectedSize, string digest, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await VerifyAsync(file, expectedSize, digest, token).ConfigureAwait(false);
    }
    internal static async Task VerifyAsync(FileStream file, long expectedSize, string digest, CancellationToken token)
    {
        if (file.Length != expectedSize || file.Length > GitHubFirmwareReleases.MaximumHexBytes) throw new IOException("The verified firmware package size changed.");
        file.Position = 0;
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest) throw new IOException("Firmware SHA-256 verification failed. No update is allowed.");
        ValidateHex(bytes); file.Position = 0;
    }
    public static void ValidateHex(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > GitHubFirmwareReleases.MaximumHexBytes || bytes.Any(b => b > 127 || b == 0)) throw new IOException("Invalid Intel HEX file.");
        var lines = Encoding.ASCII.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var written = new bool[ApplicationBytes]; uint baseAddress = 0; bool eof = false, data = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index]; if (line.Length == 0 && index == lines.Length - 1) continue;
            if (eof || line.Length < 11 || line[0] != ':' || (line.Length - 1) % 2 != 0) throw new IOException("Malformed Intel HEX record or data after EOF.");
            byte[] record;
            try { record = Convert.FromHexString(line[1..]); } catch (FormatException error) { throw new IOException("Invalid hexadecimal firmware record.", error); }
            if (record.Length != record[0] + 5 || (record.Sum(b => b) & 255) != 0) throw new IOException("Intel HEX record length/checksum failed.");
            int count = record[0], address = (record[1] << 8) | record[2];
            switch (record[3])
            {
                case 0:
                    if (count == 0 || address + count > 65536 || (ulong)baseAddress + (uint)address + (uint)count > ApplicationBytes) throw new IOException("This firmware file contains unsupported data. Check releases again or contact support.");
                    for (uint offset = baseAddress + (uint)address; offset < baseAddress + (uint)address + count; offset++) { if (written[offset]) throw new IOException("Overlapping Intel HEX records."); written[offset] = true; }
                    data = true; break;
                case 1: if (count != 0 || address != 0) throw new IOException("Invalid Intel HEX EOF record."); eof = true; break;
                case 2: case 4:
                    if (count != 2 || address != 0) throw new IOException("Invalid Intel HEX address record.");
                    baseAddress = (uint)((record[4] << 8) | record[5]) << (record[3] == 2 ? 4 : 16); break;
                case 3: case 5:
                    if (count != 4 || address != 0) throw new IOException("Invalid Intel HEX start-address record.");
                    uint start = record[3] == 3 ? (uint)(((record[4] << 8) | record[5]) * 16 + (record[6] << 8) + record[7]) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(4, 4));
                    if (start >= ApplicationBytes) throw new IOException("Firmware start address is outside the application area."); break;
                default: throw new IOException("Unsupported Intel HEX record type.");
            }
        }
        if (!eof || !data || !written[0] || !written[1]) throw new IOException("Firmware has no complete EOF/reset vector.");
    }
}

/// <summary>Only a release download that passes all checks can construct this package.</summary>
public sealed class VerifiedFirmware : IDisposable
{
    internal VerifiedFirmware(FirmwareRelease release, string path, string digest, string directory) { Release = release; Path = path; Sha256 = digest; _directory = directory; }
    private readonly string _directory;
    private bool _disposed;
    public FirmwareRelease Release { get; }
    public string Path { get; }
    public string Sha256 { get; }
    internal async Task<FileStream> LockAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        try { await FirmwareImage.VerifyAsync(file, Release.Hex.Size, Sha256, token).ConfigureAwait(false); return file; }
        catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (File.Exists(Path)) File.Delete(Path);
        Directory.Delete(_directory); _disposed = true;
    }
}
