using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CellQ.Protocol;

namespace CellQ.Testing;

/// <summary>Test fixtures only: no live HTTP, executable launch, or physical port access.</summary>
public sealed class FirmwareHttpSimulator : HttpMessageHandler
{
    public byte[] Hex { get; set; } = Encoding.ASCII.GetBytes(":020000000C945E\n:00000001FF\n");
    public string Version { get; set; } = "1.1.0";
    public bool Empty { get; set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public byte[]? ChecksumOverride { get; set; }
    public byte[]? HexOverride { get; set; }
    public Action<JsonObject>? Edit { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public string Filename => "cellq-mega2560-v" + Version + ".hex";
    public byte[] Checksum => ChecksumOverride ?? Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Hex)) + "  " + Filename + "\n");
    public JsonObject ReleaseJson()
    {
        var assets = new JsonArray();
        foreach (var extension in new[] { ".hex", ".sha256" })
        {
            string name = "cellq-mega2560-v" + Version + extension;
            assets.Add(new JsonObject { ["id"] = extension == ".hex" ? 11 : 12, ["name"] = name, ["size"] = extension == ".hex" ? Hex.Length : Checksum.Length, ["state"] = "uploaded", ["browser_download_url"] = $"https://github.com/{GitHubFirmwareReleases.Repository}/releases/download/v{Version}/{name}" });
        }
        var release = new JsonObject { ["id"] = 10, ["tag_name"] = "v" + Version, ["draft"] = false, ["prerelease"] = false, ["published_at"] = "2026-10-10T12:00:00Z", ["body"] = "SIMULATED RELEASE ONLY — not published firmware.", ["assets"] = assets };
        Edit?.Invoke(release); return release;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Enqueue(request.RequestUri!); token.ThrowIfCancellationRequested();
        if (Override is not null && await Override(request, token) is { } overridden) return overridden;
        if (Status != HttpStatusCode.OK) return new(Status);
        string path = request.RequestUri!.AbsolutePath;
        byte[] bytes = path.EndsWith(".sha256", StringComparison.Ordinal) ? Checksum : path.EndsWith(".hex", StringComparison.Ordinal) ? HexOverride ?? Hex
            : Encoding.UTF8.GetBytes(path.Contains("/tags/", StringComparison.Ordinal) ? ReleaseJson().ToJsonString() : Empty ? "[]" : new JsonArray(ReleaseJson()).ToJsonString());
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
}

public sealed class FirmwareUploaderSimulator(LogDeviceSimulator device) : IFirmwareUploader
{
    public int Uploads { get; private set; }
    public Exception? ValidationError { get; set; }
    public Exception? UploadError { get; set; }
    public string ReturningVersion { get; set; } = "1.1.0";
    public string ReturningMode { get; set; } = "IDLE";
    public Func<Task>? BeforeUpload { get; set; }
    public string? SelectedPort { get; private set; }
    public Task ValidateAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValidationError is null ? Task.CompletedTask : Task.FromException(ValidationError); }
    public async Task UploadAsync(string port, string hexPath, Action<FirmwareProgress> progress)
    {
        Uploads++; SelectedPort = port;
        if (BeforeUpload is not null) await BeforeUpload();
        if (UploadError is not null) throw UploadError;
        progress(new(FirmwarePhase.Installing, "SIMULATED FLASH ONLY", 50));
        device.FirmwareVersion = ReturningVersion; device.Mode = ReturningMode;
    }
}
