using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CellQ.Protocol;

public sealed record FirmwareAsset(long Id, string Name, long Size, Uri Url);
public sealed record FirmwareRelease(long Id, string Version, DateTimeOffset Published, string Notes, FirmwareAsset Hex, FirmwareAsset Checksum)
{
    public string Tag => "v" + Version;
    public string PageUrl => $"https://github.com/Mahdi-Nozari/cellq_firmware/releases/tag/{Tag}";
}

public interface IFirmwareReleases
{
    Task<IReadOnlyList<FirmwareRelease>> CheckAsync(CancellationToken token);
    Task<VerifiedFirmware> DownloadAsync(FirmwareRelease release, Action<FirmwareProgress> progress, CancellationToken token);
}

/// <summary>Fixed repository and strict v1 asset policy. No local-file or source-archive fallback.</summary>
public sealed class GitHubFirmwareReleases(HttpClient http, string cacheDirectory) : IFirmwareReleases
{
    public const string Repository = "Mahdi-Nozari/cellq_firmware";
    public const int MaximumHexBytes = 8 * 1024 * 1024;
    private const string Api = "https://api.github.com/repos/" + Repository + "/releases";
    private static readonly Regex StableVersion = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static bool TryVersion(string value, out Version? version)
    {
        version = null;
        return StableVersion.IsMatch(value) && Version.TryParse(value, out version);
    }
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };

    public async Task<IReadOnlyList<FirmwareRelease>> CheckAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90)); token = timeout.Token;
        var releases = new List<FirmwareRelease>();
        for (int page = 1; page <= 10; page++)
        {
            using var response = await GetAsync(new($"{Api}?per_page=100&page={page}"), false, token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response, 2 * 1024 * 1024, token).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("GitHub returned an unexpected release list.");
            foreach (var row in json.RootElement.EnumerateArray())
            {
                var release = Parse(row);
                if (release is not null) releases.Add(release);
            }
            if (json.RootElement.GetArrayLength() < 100)
            {
                if (releases.Select(r => r.Tag).Distinct(StringComparer.Ordinal).Count() != releases.Count) throw new IOException("GitHub returned ambiguous release tags.");
                return releases.OrderByDescending(r => Version.Parse(r.Version)).ToArray();
            }
        }
        throw new IOException("The release list exceeds the supported limit; no incomplete list was used.");
    }
    private static FirmwareRelease? Parse(JsonElement row)
    {
        try { return ParseRelease(row); }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new IOException("The published release information is malformed. No package was accepted; check releases again.", error); }
    }
    private static FirmwareRelease? ParseRelease(JsonElement row)
    {
        if (row.GetProperty("draft").GetBoolean() || row.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = row.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !TryVersion(tag[1..], out _)) return null;
        if (!row.TryGetProperty("published_at", out var date) || date.ValueKind != JsonValueKind.String || !date.TryGetDateTimeOffset(out var published)) return null;
        string prefix = "cellq-mega2560-" + tag;
        var assets = row.GetProperty("assets").EnumerateArray().ToArray();
        FirmwareAsset? Asset(string extension, long maximum)
        {
            string name = prefix + extension;
            var matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1) return null;
            var a = matches[0]; long id = a.GetProperty("id").GetInt64(), size = a.GetProperty("size").GetInt64();
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
            if (id <= 0 || size <= 0 || size > maximum || a.GetProperty("state").GetString() != "uploaded" || a.GetProperty("browser_download_url").GetString() != expected) return null;
            return new(id, name, size, new(expected));
        }
        var hex = Asset(".hex", MaximumHexBytes); var checksum = Asset(".sha256", 512);
        long releaseId = row.GetProperty("id").GetInt64();
        return hex is null || checksum is null || releaseId <= 0 ? null : new(releaseId, tag[1..], published, row.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", hex, checksum);
    }
    public async Task<VerifiedFirmware> DownloadAsync(FirmwareRelease release, Action<FirmwareProgress> progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3)); token = timeout.Token;
        if (!TryVersion(release.Version, out _)) throw new IOException("Invalid release version.");
        // Re-fetch authoritative metadata; callers cannot forge an asset URL or stale release pair.
        using var metadata = await GetAsync(new(Api + "/tags/" + release.Tag), false, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await ReadBoundedAsync(metadata, 2 * 1024 * 1024, token).ConfigureAwait(false));
        var current = Parse(json.RootElement);
        if (current is null || current != release) throw new IOException("The selected release changed or no longer has the required assets. Check releases again.");
        string directory = Path.Combine(Path.GetFullPath(cacheDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string part = Path.Combine(directory, release.Hex.Name + ".part"), final = Path.Combine(directory, release.Hex.Name);
        try
        {
            byte[] checksum;
            using (var response = await GetAsync(release.Checksum.Url, true, token).ConfigureAwait(false)) checksum = await ReadBoundedAsync(response, 512, token).ConfigureAwait(false);
            if (checksum.LongLength != release.Checksum.Size) throw new IOException("The checksum asset size did not match the published release.");
            string digest = FirmwareImage.ParseChecksum(checksum, release.Hex.Name);
            using (var response = await GetAsync(release.Hex.Url, true, token).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is long size && size != release.Hex.Size) throw new IOException("The firmware asset size did not match the published release.");
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                var buffer = new byte[81920]; long count = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
                    count += read; if (count > release.Hex.Size) throw new IOException("The firmware asset exceeded its published size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); progress(new(FirmwarePhase.Downloading, "Downloading published firmware…", count * 100d / release.Hex.Size));
                }
                if (count != release.Hex.Size) throw new IOException("The firmware download was incomplete.");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
            }
            progress(new(FirmwarePhase.Verifying, "Checking SHA-256 and firmware records…"));
            await FirmwareImage.VerifyFileAsync(part, release.Hex.Size, digest, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(part, final, false);
            return new VerifiedFirmware(release, final, digest, directory);
        }
        catch { if (File.Exists(part)) File.Delete(part); if (File.Exists(final)) File.Delete(final); Directory.Delete(directory); throw; }
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool asset, CancellationToken token)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 || !AllowedHost(uri.Host, asset)) throw new IOException("Release download used an unapproved address.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CellQ-Desktop/1.0");
            if (!asset) { request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json")); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            // Detect unsafe auto-redirecting HttpClient injection as well.
            if (response.RequestMessage?.RequestUri is { } actual && (actual.Scheme != "https" || actual.Port != 443 || actual.UserInfo.Length > 0 || !AllowedHost(actual.Host, asset))) { response.Dispose(); throw new IOException("Release download redirected to an unapproved address."); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (!asset || location is null || redirect == 5) throw new IOException("Unexpected release redirect.");
                uri = new Uri(uri, location); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode; response.Dispose();
                throw new IOException(status == HttpStatusCode.NotFound ? "The firmware repository or selected release is unavailable. Check repository access; no firmware was installed." : $"GitHub could not complete the release request (HTTP {(int)status}). Try again later.");
            }
            return response;
        }
        throw new IOException("Too many release redirects.");
    }
    private static bool AllowedHost(string host, bool asset) => asset ? host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" : host == "api.github.com";
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new IOException("Release response exceeded its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true) { int read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break; if (output.Length + read > maximum) throw new IOException("Release response exceeded its size limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}
