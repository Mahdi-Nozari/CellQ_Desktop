using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.Protocol.Tests;

internal static class FirmwareChecks
{
    private static Action<bool, string> Check = null!;
    private static readonly FirmwareWorkflowOptions Fast = new() { CommandTimeout = TimeSpan.FromMilliseconds(80), ReconnectDelay = TimeSpan.Zero, ReconnectAttempts = 2 };
    public static async Task RunAllAsync(Action<bool, string> check, Func<string, Func<Task>, Task> run)
    {
        Check = check;
        await run("Firmware: strict checksum grammar and original-byte SHA-256", async () =>
        {
            using var f = new Fixture(); var good = f.Http.Checksum;
            Check(FirmwareImage.ParseChecksum(good, f.Http.Filename) == Convert.ToHexStringLower(SHA256.HashData(f.Http.Hex)), "Exact lowercase checksum accepted.");
            string text = Encoding.ASCII.GetString(good);
            foreach (string invalid in new[] { text.ToUpperInvariant(), text.Replace("  ", " "), text.Replace("  ", "   "), text.Replace("\n", "\r\n"), text.TrimEnd('\n'), text + "\n", text.Replace(f.Http.Filename, "other.hex"), " " + text, text[..63] + text[64..], text + "extra" })
                await Refuses(() => Task.Run(() => FirmwareImage.ParseChecksum(Encoding.ASCII.GetBytes(invalid), f.Http.Filename)));
            await Refuses(() => Task.Run(() => FirmwareImage.ParseChecksum(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(good).ToArray(), f.Http.Filename)));
        });
        await run("Firmware: Intel HEX records, EOF, overlaps, reset vector and protected ranges", async () =>
        {
            using var f = new Fixture(); FirmwareImage.ValidateHex(f.Http.Hex);
            var invalid = new[] { ":020000000C945F\n:00000001FF\n", ":020000000C945E\n", ":00000001FF\n", ":020000000C945E\n:020000000C945E\n:00000001FF\n", ":020001000C945D\n:00000001FF\n", ":020000000C945E\n:00000001FF\n:00000001FF\n", ":020000040003F7\n:02E000000C947E\n:00000001FF\n", ":02000004FFFFFC\n:020000000C945E\n:00000001FF\n", "\n:020000000C945E\n:00000001FF\n", "not hex", ":020000000C94\n:00000001FF\n" };
            foreach (var hex in invalid) await Refuses(() => Task.Run(() => FirmwareImage.ValidateHex(Encoding.ASCII.GetBytes(hex))));
            FirmwareImage.ValidateHex(Encoding.ASCII.GetBytes(":020000000C945E\r\n:00000001FF\r\n")); Check(true, "HEX CRLF is allowed, unlike checksum CRLF.");
        });
        await run("Firmware: release discovery uses exact tag and asset pair", async () =>
        {
            using var f = new Fixture(); var releases = await f.Catalog.CheckAsync(default);
            Check(releases.Count == 1 && releases[0].Version == "1.1.0" && releases[0].Hex.Name == f.Http.Filename, "Published pair discovered.");
            Check(f.Http.Requests.All(uri => uri.Scheme == "https" && uri.Host == "api.github.com"), "Discovery is HTTPS metadata only.");
        });
        foreach (var kind in new[] { "draft", "prerelease", "badtag", "missinghex", "missingchecksum", "duplicate", "wrongname", "badurl", "oversize", "notuploaded", "unpublished" })
            await run("Firmware: unavailable release policy — " + kind, async () =>
            {
                using var f = new Fixture(); f.Http.Edit = row =>
                {
                    var assets = (JsonArray)row["assets"]!;
                    switch (kind)
                    {
                        case "draft": case "prerelease": row[kind] = true; break;
                        case "badtag": row["tag_name"] = "v1.1.0-beta"; break;
                        case "missinghex": assets.RemoveAt(0); break;
                        case "missingchecksum": assets.RemoveAt(1); break;
                        case "duplicate": assets.Add(assets[0]!.DeepClone()); break;
                        case "wrongname": assets[0]!["name"] = "source.zip"; break;
                        case "badurl": assets[0]!["browser_download_url"] = "http://untrusted.example/firmware.hex"; break;
                        case "oversize": assets[0]!["size"] = GitHubFirmwareReleases.MaximumHexBytes + 1; break;
                        case "notuploaded": assets[0]!["state"] = "new"; break;
                        case "unpublished": row["published_at"] = null; break;
                    }
                };
                Check((await f.Catalog.CheckAsync(default)).Count == 0, "No unsafe asset becomes a candidate.");
            });
        await run("Firmware: empty releases differ from HTTP failure", async () =>
        {
            using var f = new Fixture(); f.Http.Empty = true; Check((await f.Catalog.CheckAsync(default)).Count == 0, "No fabricated release.");
            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError }) { f.Http.Status = status; await Refuses(() => f.Catalog.CheckAsync(default)); }
        });
        await run("Firmware: verified temporary download and package mutation refusal", async () =>
        {
            using var f = new Fixture(); var package = await f.Download(); Check(File.ReadAllBytes(package.Path).SequenceEqual(f.Http.Hex), "Original HEX bytes retained.");
            Check(!Directory.EnumerateFiles(f.Root, "*.part", SearchOption.AllDirectories).Any(), "Only verified package finalized.");
            File.WriteAllText(package.Path, "tampered"); await f.Connect(); await Refuses(() => f.Install());
            Check(f.Device.Commands.All(c => c == "CQ1,HELLO") && f.Uploader.Uploads == 0, "Modified package cannot prepare or upload.");
        });
        await run("Firmware: malformed release fields are rejected without a candidate", async () =>
        {
            using var f = new Fixture(); f.Http.Edit = row => row.Remove("assets"); await Refuses(() => f.Catalog.CheckAsync(default));
            f.Http.Edit = row => row["id"] = "not a number"; await Refuses(() => f.Catalog.CheckAsync(default));
            Check(f.Device.Commands.IsEmpty && f.Uploader.Uploads == 0, "Malformed metadata cannot affect a device.");
        });
        foreach (var kind in new[] { "digest", "size", "malformedhex", "changedrelease", "checksumformat", "unsafeRedirect", "interrupted", "cancelled" })
            await run("Firmware: download refuses " + kind + " and removes its temporary file", async () =>
            {
                using var f = new Fixture(); var release = (await f.Catalog.CheckAsync(default)).Single(); using var cancellation = new CancellationTokenSource();
                switch (kind)
                {
                    case "digest": f.Http.HexOverride = f.Http.Hex.Select((b, i) => i == 1 ? (byte)'9' : b).ToArray(); break;
                    case "size": f.Http.HexOverride = f.Http.Hex[..^1]; break;
                    case "malformedhex": f.Http.Hex = Encoding.ASCII.GetBytes(new string('x', f.Http.Hex.Length)); break;
                    case "changedrelease": f.Http.Edit = row => row["id"] = 99; break;
                    case "checksumformat": f.Http.ChecksumOverride = f.Http.Checksum.Select((b, i) => i == 0 ? (byte)'Z' : b).ToArray(); break;
                    default: f.Http.Override = (request, _) =>
                    {
                        if (!request.RequestUri!.AbsolutePath.EndsWith(".hex", StringComparison.Ordinal)) return Task.FromResult<HttpResponseMessage?>(null);
                        if (kind == "unsafeRedirect") { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://untrusted.example/file.hex"); return Task.FromResult<HttpResponseMessage?>(response); }
                        if (kind == "cancelled") cancellation.Cancel();
                        return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(f.Http.Hex)) });
                    }; break;
                }
                await Refuses(() => f.Catalog.DownloadAsync(release, _ => { }, cancellation.Token));
                Check(!Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories).Any(), "Failed download leaves no finalized or partial package.");
                Check(f.Device.Commands.IsEmpty && f.Uploader.Uploads == 0, "Download failure is device-independent.");
            });
        await run("Firmware: confirmation declined sends no ENTER/FWPREP", async () =>
        {
            using var f = new Fixture(); await f.Download(); await f.Connect();
            Check(!await f.Install(false), "Declined result."); Check(f.Device.Commands.All(c => c == "CQ1,HELLO") && f.Uploader.Uploads == 0, "Only identification sent.");
        });
        await run("Firmware: verified upload closes sole owner, checks version, no intermediate EXIT", async () =>
        {
            using var f = new Fixture(); await f.Download(); await f.Connect();
            f.Uploader.BeforeUpload = () => { Check(f.Transports.All(t => !t.IsOpen), "All app transports closed before uploader."); Check(f.Service.Snapshot.WorkflowActive && f.Service.Snapshot.Phase == ConnectionPhase.Updating, "Exclusive update state remains active."); Check(!f.Device.Commands.Contains("CQ1,EXIT"), "No EXIT between FWPREP and upload."); return Task.CompletedTask; };
            Check(await f.Install(), "Successful installation verified.");
            Check(f.Uploader.Uploads == 1 && f.Uploader.SelectedPort == "COM7" && f.Service.Snapshot.FirmwareVersion == "1.1.0" && f.Service.Snapshot.Mode == DeviceMode.Idle, "Same explicit port and expected version.");
            Check(f.Transports.Count == 2 && f.Transports.All(t => t.Configuration is { DtrEnabled: false, RtsEnabled: false, BaudRate: 250000 }), "Normal and reconnect transport flags preserved.");
            Check(!f.Service.Snapshot.WorkflowActive && !f.Service.MaintenanceMayRemain, "Workflow lease and uncertainty cleared.");
        });
        foreach (var kind in new[] { "test", "manual", "racedtest", "sameversion", "older", "unknownversion", "bundle" })
            await run("Firmware: installation refused before uploader — " + kind, async () =>
            {
                using var f = new Fixture(); await f.Download();
                switch (kind) { case "test": f.Device.Mode = "TEST"; break; case "manual": f.Device.Mode = "MANUAL"; f.Device.ManualValveActive = true; break; case "racedtest": f.Device.BeginTestOnEnter = true; break; case "sameversion": f.Device.FirmwareVersion = "1.1.0"; break; case "older": f.Device.FirmwareVersion = "2.0.0"; break; case "unknownversion": f.Device.FirmwareVersion = "unknown"; break; case "bundle": f.Uploader.ValidationError = new IOException("Missing uploader"); break; }
                await f.Connect(); await Refuses(() => f.Install());
                Check(f.Uploader.Uploads == 0 && !f.Device.Commands.Contains("CQ1,FWPREP"), "Refused target never reaches FWPREP or uploader.");
                if (kind is "test" or "racedtest") Check(f.Device.Mode == "TEST", "No test stop.");
            });
        await run("Firmware: FWPREP rejection safely EXITs; FWPREP timeout stays uncertain", async () =>
        {
            using (var f = new Fixture()) { await f.Download(); await f.Connect(); f.Device.DenyFwPrep = true; await Refuses(() => f.Install()); Check(f.Device.Mode == "IDLE" && f.Device.Commands.Contains("CQ1,EXIT") && f.Uploader.Uploads == 0, "Denied preparation exits without upload."); }
            using (var f = new Fixture()) { await f.Download(); await f.Connect(); f.Device.DropFwPrep = true; await Refuses(() => f.Install()); Check(f.Service.MaintenanceMayRemain && f.Uploader.Uploads == 0 && f.Transports.All(t => !t.IsOpen), "Ambiguous timeout closes without reset/upload."); }
        });
        await run("Firmware: cancellation while ENTER/FWPREP awaits ACK cleans up", async () =>
        {
            foreach (bool atPrep in new[] { false, true })
            {
                using var f = new Fixture(); await f.Download(); await f.Connect(); using var cts = new CancellationTokenSource();
                Func<Task> cancel = () => { cts.Cancel(); return Task.CompletedTask; };
                if (atPrep) f.Device.BeforeFwPrepReply = cancel; else f.Device.BeforeEnterReply = cancel;
                await Refuses(() => f.Install(token: cts.Token));
                Check(f.Device.Mode == "IDLE" && f.Device.Commands.Contains("CQ1,EXIT") && f.Uploader.Uploads == 0, "ACK resolved, EXIT sent, no uploader on cancellation.");
            }
        });
        await run("Firmware: existing MAINT/FWPREP recovered only after confirmation", async () =>
        {
            using var f = new Fixture(); f.Device.Mode = "MAINT"; await f.Download(); await f.Connect(); Check(!await f.Install(false) && f.Device.Mode == "MAINT", "Declining does not silently adopt maintenance.");
            Check(await f.Install(), "Explicit existing maintenance update succeeds.");
            var commands = f.Device.Commands.ToArray(); Check(Array.IndexOf(commands, "CQ1,EXIT") < Array.IndexOf(commands, "CQ1,ENTER"), "Old session exited before new preparation.");
        });
        foreach (var kind in new[] { "launch", "write", "readback", "timeout", "disconnect", "version", "returnMAINT", "close" })
            await run("Firmware: handoff/reconnection behavior — " + kind, async () =>
            {
                using var f = new Fixture(); await f.Download(); await f.Connect();
                bool success = kind == "returnMAINT";
                if (kind is "launch" or "write" or "readback" or "timeout") f.Uploader.UploadError = kind == "timeout" ? new TimeoutException("Simulated stopped uploader timeout") : new IOException("Simulated " + kind);
                if (kind == "disconnect") f.FailReconnect = true;
                if (kind == "version") f.Uploader.ReturningVersion = "1.0.0";
                if (kind == "returnMAINT") f.Uploader.ReturningMode = "MAINT";
                if (kind == "close") f.Transports[0].FailClose = true;
                if (success) Check(await f.Install(), "Post-upload MAINT exited and expected version confirmed."); else await Refuses(() => f.Install());
                Check(f.Uploader.Uploads == (kind == "close" ? 0 : 1), "No automatic second upload.");
                if (kind is "launch" or "write" or "readback" or "timeout" or "returnMAINT") Check(f.Device.Mode == "IDLE" && f.Device.Commands.Contains("CQ1,EXIT"), "Recovery EXIT after released uploader.");
                if (kind == "version") Check(f.Service.Snapshot.FirmwareVersion == "1.0.0", "Observed wrong version never relabeled.");
                if (kind == "close") { Check(f.Transports.Count == 1, "Failed close cannot create second owner."); f.Transports[0].FailClose = false; }
            });
        await run("Firmware: cancel/disconnect waits for active uploader and shared lease", async () =>
        {
            using var f = new Fixture(); await f.Download(); await f.Connect(); using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Uploader.BeforeUpload = async () => { started.SetResult(); await finish.Task; };
            var install = f.Install(token: cts.Token); await started.Task; cts.Cancel();
            var refresh = f.Service.RefreshHelloAsync(); await Task.Delay(20); Check(!refresh.IsCompleted, "Refresh waits for complete update lease.");
            var disconnect = f.Service.DisconnectAsync(); await Task.Delay(20); Check(!disconnect.IsCompleted && !install.IsCompleted, "Disconnect/cancel cannot abandon active flash.");
            finish.SetResult(); Check(await install, "Active flash still verified after ordinary cancellation."); await refresh; await disconnect;
            Check(f.Service.Snapshot.Phase == ConnectionPhase.Disconnected, "Queued disconnect occurs after verified update.");
        });
        await run("Firmware: package remains locked through confirmation and device state is rechecked", async () =>
        {
            using var f = new Fixture(); var package = await f.Download(); await f.Connect();
            await Refuses(() => f.Service.InstallFirmwareAsync(package, f.Uploader, async (_, _) =>
            {
                await Refuses(() => Task.Run(() => File.WriteAllText(package.Path, "tampered during confirmation")));
                f.Device.Mode = "TEST"; return true;
            }, _ => { }, options: Fast));
            Check(f.Uploader.Uploads == 0 && f.Device.Commands.All(c => c == "CQ1,HELLO"), "Fresh status after confirmation blocks raced test without ENTER.");
        });
        await run("Firmware: USB loss while preparing cannot launch uploader", async () =>
        {
            using var f = new Fixture(); await f.Download(); await f.Connect();
            f.Device.BeforeFwPrepReply = () => { f.Transports[0].LoseConnection("Simulated preparation unplug"); return Task.CompletedTask; };
            await Refuses(() => f.Install()); Check(f.Uploader.Uploads == 0 && f.Service.MaintenanceMayRemain, "Unplugged preparation is uncertain, never uploaded.");
        });
        await run("Firmware: pinned bundled uploader and explicit verified-only arguments", async () =>
        {
            var bundlePath = Path.GetFullPath("src/CellQ.Desktop/Tools/avrdude/8.3");
            var fakeProcess = new ProcessFake(); var bundle = new BundledMegaUploader(bundlePath, fakeProcess); await bundle.ValidateAsync(default);
            await bundle.UploadAsync("COM7", "C:\\sample folder\\published.hex", _ => { });
            var args = fakeProcess.Invocation!.Arguments;
            Check(args.Contains("atmega2560") && args.Contains("wiring") && args.Contains("115200") && args.Contains(@"\\.\COM7") && args.Contains("-N"), "Fixed target/protocol/baud/port, no personal configuration.");
            Check(args.Contains("flash:v:C:\\sample folder\\published.hex:i") && !args.Contains("-F") && !args.Contains("-V"), "Explicit readback, no signature/verification bypass.");
            foreach (var port in new[] { "COM0", "usb", "COM7 -F", "COM7\n" }) await Refuses(() => Task.Run(() => BundledMegaUploader.Arguments("conf", port, "file")));
            fakeProcess.Exit = 1; await Refuses(() => bundle.UploadAsync("COM7", "published.hex", _ => { }));
            Check(fakeProcess.Runs == 2, "Only fake process ran; no physical uploader executed.");
        });
        await run("Firmware: native process runner reads help without any port or upload arguments", async () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            int exit = await new UploaderProcess().RunAsync(new(Path.GetFullPath("src/CellQ.Desktop/Tools/avrdude/8.3/avrdude.exe"), Path.GetFullPath("src/CellQ.Desktop/Tools/avrdude/8.3"), ["--help"]), line => { lines.Enqueue(line); throw new InvalidOperationException("Simulated display callback failure"); }, TimeSpan.FromSeconds(2));
            Check(exit == 0 && lines.Any(line => line.Contains("Usage", StringComparison.OrdinalIgnoreCase)), "Output callback faults cannot abandon the help process; no device options were passed.");
        });
    }
    private static async Task Refuses(Func<Task> action)
    {
        bool refused = false; try { await action(); } catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException) { refused = true; }
        Check(refused, "Unsafe or failed firmware operation refused.");
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "firmware-tests", Guid.NewGuid().ToString("N")));
        public FirmwareHttpSimulator Http { get; } = new();
        public LogDeviceSimulator Device { get; } = new() { FirmwareVersion = "1.0.0" };
        public List<FakeDevice> Transports { get; } = [];
        public GitHubFirmwareReleases Catalog { get; }
        public FirmwareUploaderSimulator Uploader { get; }
        public DeviceConnectionService Service { get; }
        public bool FailReconnect { get; set; }
        private readonly HttpClient _client;
        private VerifiedFirmware? _package;
        public Fixture()
        {
            Directory.CreateDirectory(Root); _client = new(Http); Catalog = new(_client, Root); Uploader = new(Device);
            Service = new(() => { var fake = Device.CreateTransport(); Transports.Add(fake); if (FailReconnect && Transports.Count > 1) fake.OnOpen = _ => Task.FromException(new IOException("Simulated disconnected board")); return fake; }, TimeSpan.FromMilliseconds(100));
        }
        public async Task<VerifiedFirmware> Download() { _package = await Catalog.DownloadAsync((await Catalog.CheckAsync(default)).Single(), _ => { }, default); return _package; }
        public Task Connect() => Service.ConnectAsync("COM7");
        public Task<bool> Install(bool confirmed = true, CancellationToken token = default) => Service.InstallFirmwareAsync(_package!, Uploader, (_, _) => Task.FromResult(confirmed), _ => { }, token, Fast);
        public void Dispose() { foreach (var transport in Transports) transport.FailClose = false; Service.DisposeAsync().AsTask().GetAwaiter().GetResult(); _package?.Dispose(); _client.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); if (_read) throw new IOException("Simulated interrupted asset download"); _read = true; return base.ReadAsync(buffer[..Math.Min(8, buffer.Length)], token); }
    }
    private sealed class ProcessFake : IUploaderProcess
    {
        public UploaderInvocation? Invocation { get; private set; }
        public int Exit { get; set; }
        public int Runs { get; private set; }
        public Task<int> RunAsync(UploaderInvocation invocation, Action<string> output, TimeSpan warningTimeout) { Runs++; Invocation = invocation; output("Writing | #### | 50%"); output("Reading | #### | 100%"); return Task.FromResult(Exit); }
    }
}
