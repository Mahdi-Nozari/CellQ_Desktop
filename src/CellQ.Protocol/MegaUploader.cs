using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CellQ.Protocol;

public sealed record UploaderInvocation(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments);
public interface IUploaderProcess
{
    // Completion, including an error, means no process still owns the selected port.
    Task<int> RunAsync(UploaderInvocation invocation, Action<string> output, TimeSpan warningTimeout);
}

public sealed class UploaderProcess : IUploaderProcess
{
    public async Task<int> RunAsync(UploaderInvocation invocation, Action<string> output, TimeSpan warningTimeout)
    {
        var start = new ProcessStartInfo(invocation.Executable) { WorkingDirectory = invocation.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in invocation.Arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("The bundled uploader could not start.");
        var stdout = PumpAsync(process.StandardOutput, output); var stderr = PumpAsync(process.StandardError, output);
        var exit = process.WaitForExitAsync();
        if (await Task.WhenAny(exit, Task.Delay(warningTimeout)).ConfigureAwait(false) != exit)
            SafeOutput(output, "Uploader is taking longer than expected. The app is waiting for it to release the connection. Keep power connected; contact support before ending the uploader externally.");
        // Never kill an in-progress flash or reconnect while its process remains alive.
        await exit.ConfigureAwait(false); await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }
    private static async Task PumpAsync(StreamReader reader, Action<string> output)
    {
        var buffer = new char[256]; var line = new StringBuilder();
        while (true)
        {
            int count = await reader.ReadAsync(buffer).ConfigureAwait(false); if (count == 0) break;
            for (int i = 0; i < count; i++)
            {
                char value = buffer[i];
                if (value is '\r' or '\n' || line.Length == 4096) { if (line.Length > 0) { SafeOutput(output, line.ToString()); line.Clear(); } }
                if (value is not ('\r' or '\n')) line.Append(value);
            }
        }
        if (line.Length > 0) SafeOutput(output, line.ToString());
    }
    private static void SafeOutput(Action<string> output, string line) { try { output(line); } catch (Exception) { /* Display callbacks must not abandon a running uploader. */ } }
}

public sealed class BundledMegaUploader : IFirmwareUploader
{
    private readonly Func<CancellationToken, Task<string>> _directory;
    public BundledMegaUploader(string directory, IUploaderProcess? process = null)
        : this(_ => Task.FromResult(directory), process) { }
    public BundledMegaUploader(Func<CancellationToken, Task<string>> directory, IUploaderProcess? process = null)
    { _directory = directory; _process = process ?? new UploaderProcess(); }
    public const string Version = "8.3";
    // Set to the verified distribution manifest when bundling the official archive.
    public const string ManifestSha256 = "2e1dd21ce45ce8d2879230b6df6aff7b64da210c38b2ab86fca47000f07f2da6";
    private readonly IUploaderProcess _process;
    private static readonly Regex PortPattern = new(@"\ACOM[1-9][0-9]{0,4}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Percentage = new(@"([0-9]{1,3})%", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public event Action<string>? Output;
    public async Task ValidateAsync(CancellationToken token)
        => await ValidateDirectoryAsync(await _directory(token).ConfigureAwait(false), token).ConfigureAwait(false);
    private static async Task ValidateDirectoryAsync(string directory, CancellationToken token)
    {
        string manifest = Path.Combine(directory, "uploader-manifest.json");
        if (!File.Exists(manifest)) throw new IOException("The firmware installation tool is missing. Reinstall the complete app package.");
        byte[] bytes = await File.ReadAllBytesAsync(manifest, token).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != ManifestSha256) throw new IOException("The bundled uploader manifest failed verification.");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? throw new IOException("Invalid uploader manifest.");
        if (!files.ContainsKey("avrdude.exe") || !files.ContainsKey("avrdude.conf")) throw new IOException("The uploader bundle is incomplete.");
        foreach (var file in files)
        {
            if (Path.GetFileName(file.Key) != file.Key || file.Key is "." or "..") throw new IOException("Invalid uploader bundle path.");
            await using var input = new FileStream(Path.Combine(directory, file.Key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != file.Value) throw new IOException("Bundled uploader verification failed: " + file.Key);
        }
    }
    public static IReadOnlyList<string> Arguments(string config, string port, string hex)
    {
        if (!PortPattern.IsMatch(port)) throw new IOException("Select a Windows COM port for the confirmed device.");
        return ["-N", "-C", config, "-p", "atmega2560", "-c", "wiring", "-P", @"\\.\" + port, "-b", "115200", "-D", "-U", "flash:w:" + hex + ":i", "-U", "flash:v:" + hex + ":i"];
    }
    public async Task UploadAsync(string port, string hexPath, Action<FirmwareProgress> progress)
    {
        string directory = await _directory(CancellationToken.None).ConfigureAwait(false);
        await ValidateDirectoryAsync(directory, CancellationToken.None).ConfigureAwait(false);
        // Hold the executable/config/DLL bundle against mutation for the entire process lifetime.
        var locks = new List<FileStream>();
        try
        {
            var manifest = new FileStream(Path.Combine(directory, "uploader-manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            locks.Add(manifest);
            if (manifest.Length > 8192) throw new IOException("Invalid uploader manifest size.");
            var manifestBytes = new byte[(int)manifest.Length]; await manifest.ReadExactlyAsync(manifestBytes).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != ManifestSha256) throw new IOException("The uploader manifest changed before installation.");
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestBytes)!;
            foreach (var file in files)
            {
                var handle = new FileStream(Path.Combine(directory, file.Key), FileMode.Open, FileAccess.Read, FileShare.Read);
                locks.Add(handle);
                if (Convert.ToHexStringLower(SHA256.HashData(handle)) != file.Value) throw new IOException("The uploader bundle changed before installation.");
            }
            var invocation = new UploaderInvocation(Path.Combine(directory, "avrdude.exe"), directory, Arguments(Path.Combine(directory, "avrdude.conf"), port, hexPath));
            int exit = await _process.RunAsync(invocation, line =>
            {
                Output?.Invoke(line);
                var match = Percentage.Match(line);
                bool reading = line.Contains("Reading", StringComparison.OrdinalIgnoreCase) || line.Contains("Verifying", StringComparison.OrdinalIgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int percent) && percent <= 100)
                    progress(new(FirmwarePhase.Installing, reading ? "Checking the firmware written to the device." : "Installing firmware. Keep USB and power connected.", percent));
                else if (line.StartsWith("Uploader is taking", StringComparison.Ordinal)) progress(new(FirmwarePhase.Installing, line));
            }, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            if (exit != 0) throw new IOException($"Firmware installation failed (exit {exit}). Check technical details before retrying.");
        }
        finally { foreach (var handle in locks) handle.Dispose(); }
    }
}
