using System.IO;
using CellQ.Desktop.Services;
using CellQ.Protocol;

namespace CellQ.UiChecks;

internal static partial class Program
{
    private static async Task PackagingChecks(string output)
    {
        string root = Path.Combine(output, "embedded-tools", Guid.NewGuid().ToString("N"), "Path with spaces آزمون");
        var embedded = new EmbeddedUploaderFiles(root);
        Require(!Directory.Exists(root), "Constructing the firmware page leaves the tool cache untouched");
        string directory = await embedded.GetDirectoryAsync(default);
        Require(EmbeddedUploaderFiles.Filenames.All(name => File.Exists(Path.Combine(directory, name))), "Embedded executable, configuration, pinned manifest, notices, license and matching source all extract");
        await new BundledMegaUploader(directory).ValidateAsync(default);
        string executable = Path.Combine(directory, "avrdude.exe");
        var before = File.GetLastWriteTimeUtc(executable);
        using (var locked = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            Require(await embedded.GetDirectoryAsync(default) == directory && File.GetLastWriteTimeUtc(executable) == before, "Verified tools are reused without replacing a locked executable");
        string configuration = Path.Combine(directory, "avrdude.conf");
        await File.WriteAllTextAsync(configuration, "corrupted cache");
        await embedded.GetDirectoryAsync(default); await new BundledMegaUploader(directory).ValidateAsync(default);
        Require(new FileInfo(configuration).Length > 1000, "A corrupted tool cache is repaired from embedded originals before it can be used");
        File.Delete(configuration); await embedded.GetDirectoryAsync(default);
        Require(File.Exists(configuration), "Missing cached tools are restored without loose distribution files");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new EmbeddedUploaderFiles(root).GetDirectoryAsync(default)));
        Require(concurrent.All(path => path == directory) && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Independent instances safely share the verified cache with no remaining scratch files");
        string coldRoot = root + "-concurrent-first-use";
        var firstUse = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new EmbeddedUploaderFiles(coldRoot).GetDirectoryAsync(default)));
        Require(firstUse.Distinct().Count() == 1 && EmbeddedUploaderFiles.Filenames.All(name => File.Exists(Path.Combine(firstUse[0], name))) && !Directory.EnumerateFiles(firstUse[0], "*.tmp").Any(), "Concurrent first-use extraction produces one complete verified cache");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        string cancelledRoot = root + "-cancelled";
        try { await new EmbeddedUploaderFiles(cancelledRoot).GetDirectoryAsync(cancelled.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { Require(!Directory.Exists(cancelledRoot), "Cancellation before extraction writes no files"); }
        string partialRoot = root + "-partial";
        using var interrupted = new CancellationTokenSource();
        var partial = new EmbeddedUploaderFiles(partialRoot, name =>
        {
            using var source = typeof(EmbeddedUploaderFiles).Assembly.GetManifestResourceStream("CellQ.Uploader." + name)!;
            using var bytes = new MemoryStream(); source.CopyTo(bytes);
            return name == "avrdude.exe" ? new InterruptedResource(bytes.ToArray(), interrupted) : new MemoryStream(bytes.ToArray());
        });
        try { await partial.GetDirectoryAsync(interrupted.Token); throw new Exception("Partial extraction was not cancelled"); }
        catch (OperationCanceledException)
        {
            Require(!Directory.EnumerateFiles(partialRoot, "*.tmp", SearchOption.AllDirectories).Any() && !Directory.EnumerateFiles(partialRoot, "avrdude.exe", SearchOption.AllDirectories).Any(), "Cancellation during extraction removes only its incomplete scratch file and never finalizes the tool");
        }
        await new EmbeddedUploaderFiles(partialRoot).GetDirectoryAsync(default);
        Require(Directory.EnumerateFiles(partialRoot, "avrdude.exe", SearchOption.AllDirectories).Any(), "A new attempt recovers from an interrupted extraction");
        try { await new EmbeddedUploaderFiles(root + "-invalid", _ => new MemoryStream([0])).GetDirectoryAsync(default); throw new Exception("Bad embedded manifest was accepted"); }
        catch (IOException) { Require(true, "A changed embedded manifest is refused before uploader launch"); }
        string blocked = root + "-blocked"; await File.WriteAllTextAsync(blocked, "not a directory");
        try { await new EmbeddedUploaderFiles(blocked).GetDirectoryAsync(default); throw new Exception("Invalid cache path was accepted"); }
        catch (IOException) { Require(true, "An unavailable cache reports failure instead of bypassing bundle checks"); }
        var process = new PackagingProcess();
        var uploader = new BundledMegaUploader(embedded.GetDirectoryAsync, process);
        var lines = new List<string>(); uploader.Output += lines.Add;
        await uploader.ValidateAsync(default);
        await uploader.UploadAsync("COM999", Path.Combine(root, "test-only.hex"), _ => { });
        Require(process.Invocation?.Executable == executable && process.Invocation.WorkingDirectory == directory && lines.Contains("fake process only"), "The production uploader resolver uses extracted paths and retains technical output with a fake upload process");
    }

    private sealed class InterruptedResource(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
        {
            await destination.WriteAsync(ToArray().AsMemory(0, Math.Min(256, (int)Length)), token);
            cancellation.Cancel(); token.ThrowIfCancellationRequested();
        }
    }
    private sealed class PackagingProcess : IUploaderProcess
    {
        public UploaderInvocation? Invocation { get; private set; }
        public Task<int> RunAsync(UploaderInvocation invocation, Action<string> output, TimeSpan timeout)
        { Invocation = invocation; output("fake process only"); return Task.FromResult(0); }
    }
}
