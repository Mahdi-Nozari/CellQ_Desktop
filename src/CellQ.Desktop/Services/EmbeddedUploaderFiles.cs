using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using CellQ.Protocol;

namespace CellQ.Desktop.Services;

// Independent of the executable's location, including single-file publish and read-only folders.
internal sealed class EmbeddedUploaderFiles
{
    internal static readonly string[] Filenames = ["uploader-manifest.json", "avrdude.exe", "avrdude.conf", "COPYING", "NOTICE.md", "avrdude-v8.3-source.zip"];
    private readonly string _directory;
    private readonly Func<string, Stream> _open;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal EmbeddedUploaderFiles(string? cacheRoot = null, Func<string, Stream>? open = null)
    {
        _directory = Path.Combine(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellQ", "Tools"),
            "avrdude", BundledMegaUploader.Version, BundledMegaUploader.ManifestSha256);
        _open = open ?? (name => typeof(EmbeddedUploaderFiles).Assembly.GetManifestResourceStream("CellQ.Uploader." + name)
            ?? throw new IOException("The firmware installation tool is missing from this app. Download CellQ again."));
    }

    internal async Task<string> GetDirectoryAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The firmware tool cache must be a regular folder.");
            await using var cacheLease = await LockCacheAsync(token).ConfigureAwait(false);
            foreach (string name in Filenames)
            {
                await using var source = _open(name);
                string expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
                if (name == "uploader-manifest.json" && expected != BundledMegaUploader.ManifestSha256)
                    throw new IOException("The embedded firmware tool manifest failed verification.");
                string target = Path.Combine(_directory, name);
                if (await MatchesAsync(target, expected, token).ConfigureAwait(false)) continue;
                string temporary = Path.Combine(_directory, "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await using (var input = _open(name))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    if (!await MatchesAsync(temporary, expected, token).ConfigureAwait(false)) throw new IOException("Firmware tool extraction failed verification.");
                    token.ThrowIfCancellationRequested();
                    try { File.Move(temporary, target, overwrite: true); }
                    catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && File.Exists(target))
                    {
                        // Another process may have finished extraction or locked a valid uploader.
                        if (!await MatchesAsync(target, expected, token).ConfigureAwait(false)) throw;
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            await new BundledMegaUploader(_directory).ValidateAsync(token).ConfigureAwait(false);
            return _directory;
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> LockCacheAsync(CancellationToken token)
    {
        string path = Path.Combine(_directory, ".extraction.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The firmware tool cache contains a redirected lock file.");
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("Another CellQ window is preparing firmware tools. Wait for it to finish and try again.", error);
                await Task.Delay(40, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The firmware tool cache contains a redirected file.");
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) == expected;
        }
        catch (FileNotFoundException) { return false; } // A concurrent extraction moved its scratch file.
    }
}
