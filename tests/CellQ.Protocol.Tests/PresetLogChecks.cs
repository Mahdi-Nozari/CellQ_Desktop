using System.Text;
using System.Text.Json;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.Protocol.Tests;

internal static class PresetLogChecks
{
    private const string Remote = "/DATA/20261011/log_001.csv";
    private const string FirstSix = "CELLQ_DATA,,1\nTEST_START,20261011T210217\nLOG_CREATED,20261011T210217\nFIRST_LOG_ELAPSED,0\nPRESET_ID,5\nPRESET_REV,35\n";
    private static readonly LogProtocolOptions Options = new() { CommandTimeout = TimeSpan.FromMilliseconds(100), NextTimeout = TimeSpan.FromMilliseconds(100), HashTimeout = TimeSpan.FromMilliseconds(100), TransferRetries = 2 };
    private static byte[] Csv(string name, bool tail = true, bool finalLf = true) => Encoding.UTF8.GetBytes(FirstSix + "PRESET_NAME,\"" + name.Replace("\"", "\"\"") + "\"" + (finalLf ? "\n" : "") + (tail ? "SOURCE,ANALOG\nSAMPLE_INTERVAL_MS,20\n" + string.Concat(Enumerable.Repeat("1,20,30,40\n", 800)) : ""));
    private static DeviceLog Log(byte[] data, string? preset = null) => new(Remote, "log_001.csv", "20261011", (uint)data.Length, preset);
    private static string Target(string filename = "checked.csv") { string folder = Path.Combine("artifacts", "preset-log-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); return Path.GetFullPath(Path.Combine(folder, filename)); }
    private static async Task<DeviceConnectionService> Connect(LogDeviceSimulator sim) { var connection = new DeviceConnectionService(sim.CreateTransport, TimeSpan.FromMilliseconds(100)); await connection.ConnectAsync("COM_SIMULATOR"); return connection; }
    private static async Task<Exception> Failure(Func<Task> operation) { try { await operation(); } catch (Exception error) { return error; } throw new InvalidOperationException("Expected a failure."); }
    private static void Normal(LogDeviceSimulator sim, DeviceConnectionService connection, Action<bool, string> check) => check(sim.Mode == "IDLE" && connection.Snapshot.Mode == DeviceMode.Idle && !connection.Snapshot.WorkflowActive, "Preset workflow confirms EXIT and reports normal state.");

    internal static async Task RunAllAsync(Action<bool, string> check, Func<string, Func<Task>, Task> run)
    {
        await run("CSV preset parsing across every byte boundary, UTF-8/BOM and quoted fields", async () =>
        {
            foreach (string name in new[] { "Heart Test", "Heart, \"slow\" test", "آزمون قلب α", "Heart\nTest" })
            {
                byte[] header = Encoding.UTF8.GetBytes("\uFEFF" + Encoding.UTF8.GetString(Csv(name, tail: false)).Replace("\n", "\r\n"));
                string expected = name.Replace("\n", "\r\n");
                for (int length = 0; length < header.Length - 1; length++)
                    check(!LogCsvHeader.TryReadPresetName(header.AsSpan(0, length), false, out _), "A split header/quoted name cannot become an incomplete preset name.");
                check(LogCsvHeader.TryReadPresetName(header, false, out var parsed) && parsed == expected, "The seventh CSV record's second field is decoded exactly, including quoted commas/quotes and Unicode.");
                await using var stream = new MemoryStream(header);
                stream.Position = 2;
                check(await LogCsvHeader.ReadPresetNameAsync(stream) == expected && stream.Position == 2, "Local header reads use the same parser and restore the caller's stream position.");
            }
            byte[] noLf = Csv("No final newline", tail: false, finalLf: false);
            check(!LogCsvHeader.TryReadPresetName(noLf, false, out _) && LogCsvHeader.TryReadPresetName(noLf, true, out var eofName) && eofName == "No final newline", "A seventh record without a newline is accepted only at confirmed EOF.");
        });

        await run("Missing, malformed and oversized CSV metadata remains unknown", () =>
        {
            byte[][] missing = [[], Encoding.UTF8.GetBytes(FirstSix), Csv("", tail: false), Encoding.UTF8.GetBytes(FirstSix + "OTHER,Heart Test\n"), Encoding.UTF8.GetBytes(FirstSix.Replace("CELLQ_DATA", "OTHER_DATA") + "PRESET_NAME,Heart Test\n"), Encoding.UTF8.GetBytes(FirstSix + "PRESET_NAME,\"unterminated\n"), Encoding.UTF8.GetBytes(FirstSix + "PRESET_NAME,\"bad\"extra\n"), Encoding.UTF8.GetBytes(FirstSix + "PRESET_NAME,").Concat(new byte[] { 0xFF, 10 }).ToArray()];
            foreach (var bytes in missing) check(LogCsvHeader.TryReadPresetName(bytes, true, out var name) && name is null, "Unknown metadata is never guessed from another record, invalid quotes or invalid UTF-8.");
            byte[] oversized = Encoding.UTF8.GetBytes("CELLQ_DATA," + new string('x', LogCsvHeader.MaximumBytes) + "\n" + FirstSix + "PRESET_NAME,too late\n");
            check(LogCsvHeader.TryReadPresetName(oversized, true, out var unavailable) && unavailable is null, "Metadata beyond the bounded prefix is ignored.");
            return Task.CompletedTask;
        });

        await run("Preset prefixes preserve the old filename suffix and stay safe on Windows", () =>
        {
            var log = Log(Csv("Heart Test"), "Heart Test");
            check(LogFilename.Suggested(log) == "Heart Test_20261011_log_001.csv", "Preset name precedes the unchanged folder and device filename.");
            check(LogFilename.Suggested(log with { PresetName = null }) == "20261011_log_001.csv", "Missing metadata preserves the original naming convention.");
            string safe = LogFilename.Suggested(log with { PresetName = "../Heart:Test\\?*\"|<>\n" });
            check(!safe.Any(value => char.IsControl(value) || "<>:\"/\\|?*".Contains(value)) && Path.GetFileName(safe) == safe && safe.EndsWith("_20261011_log_001.csv", StringComparison.Ordinal), "A CSV value cannot create a directory or an invalid filename.");
            check(LogFilename.Suggested(log with { PresetName = "<>:/\\*?\"|... " }) == "20261011_log_001.csv", "A prefix with no usable characters falls back cleanly.");
            string unicode = LogFilename.Suggested(log with { PresetName = new string('α', 79) + "😀tail" });
            check(!unicode.Any(char.IsSurrogate) && unicode.Length < 120, "Long prefixes are bounded without splitting a Unicode surrogate pair.");
            return Task.CompletedTask;
        });

        await run("Explicit browse reads just the CSV headers under one maintenance lease", async () =>
        {
            var sim = new LogDeviceSimulator(); byte[] data = Csv("Heart Test"); sim.Files.Add(Remote, data);
            sim.Files.Add("/DATA/NO_RTC/log_002.csv", Csv("Lung, \"slow\" test"));
            sim.Files.Add("/DATA/20261011/log_003.csv", []);
            await using var connection = await Connect(sim);
            var files = await new DataLogService(connection, Options).ListAsync(includePresetNames: true);
            check(files.Single(file => file.RemotePath == Remote).PresetName == "Heart Test" && files.Single(file => file.Folder == "NO_RTC").PresetName == "Lung, \"slow\" test", "Browse joins each CRC-checked header to the original listed remote identity.");
            check(files.Single(file => file.Filename == "log_003.csv").PresetName is null, "Zero-length files remain valid logs without a preset name.");
            check(sim.NextCount * 48 < data.Length && sim.HashCount == 0, "Browse does not download or hash entire logs to populate preset names.");
            check(sim.Commands.Count(command => command == "CQ1,ENTER") == 1 && sim.Commands.Count(command => command == "CQ1,EXIT") == 1 && !sim.Commands.Any(command => command.StartsWith("CQ1,DELETE", StringComparison.Ordinal)), "The complete browse batch enters/exits once and never deletes files.");
            var commands = sim.Commands.ToArray();
            var starts = commands.Select((command, index) => (command, index)).Where(row => row.command.StartsWith("CQ1,GET,", StringComparison.Ordinal)).ToArray();
            check(starts.Length == 2 && commands[(starts[0].index + 1)..starts[1].index].Contains("CQ1,CANCEL"), "A header preview is cancelled before the next GET begins.");
            Normal(sim, connection, check);
        });

        foreach (string fault in new[] { "CRC16", "offset", "GET echo", "timeout", "EOF CRC32", "EOF size" })
        {
            await run("Preset header recovery after " + fault, async () =>
            {
                var sim = new LogDeviceSimulator(); sim.Files.Add(Remote, Csv("Heart Test", tail: false, finalLf: false));
                switch (fault) { case "CRC16": sim.CorruptFrames = 1; break; case "offset": sim.WrongOffsets = 1; break; case "GET echo": sim.WrongGetOffset = 1; break; case "timeout": sim.DropNext = 1; break; case "EOF CRC32": sim.BadStreamCrc = 1; break; case "EOF size": sim.BadEofSize = 1; break; }
                await using var connection = await Connect(sim);
                var files = await new DataLogService(connection, Options).ListAsync(includePresetNames: true);
                check(files.Single().PresetName == "Heart Test" && sim.Commands.Count(command => command.StartsWith("CQ1,GET,", StringComparison.Ordinal)) == 2, "Untrusted header bytes are discarded; a bounded fresh GET recovers the name.");
                Normal(sim, connection, check);
            });
        }

        await run("Persistent header corruption fails without a partial listing or completed copy", async () =>
        {
            var sim = new LogDeviceSimulator { CorruptFrames = 20 }; byte[] bytes = Csv("Heart Test"); sim.Files.Add(Remote, bytes);
            await using var connection = await Connect(sim);
            var error = await Failure(() => new DataLogService(connection, Options).ListAsync(includePresetNames: true));
            check(error is TransferIntegrityException && sim.Commands.Count(command => command.StartsWith("CQ1,GET,", StringComparison.Ordinal)) == Options.TransferRetries + 1 && sim.Files[Remote].SequenceEqual(bytes), "A bad name is never accepted, retries are bounded and remote bytes stay unchanged.");
            Normal(sim, connection, check);
        });

        await run("Header browsing is bounded and supports empty/legacy cards", async () =>
        {
            var sim = new LogDeviceSimulator(); await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            check((await service.ListAsync(includePresetNames: true)).Count == 0 && !sim.Commands.Any(command => command.StartsWith("CQ1,GET,", StringComparison.Ordinal)), "An empty card needs no metadata GET.");
            sim.Files.Add(Remote, Csv(new string('x', LogCsvHeader.MaximumBytes * 2)));
            var files = await service.ListAsync(includePresetNames: true);
            check(files.Single().PresetName is null && sim.NextCount <= (LogCsvHeader.MaximumBytes + 47) / 48, "An oversized header has a bounded read and leaves normal download available.");
            Normal(sim, connection, check);
        });

        await run("Stopping a preset preview cancels and exits without changing files", async () =>
        {
            var sim = new LogDeviceSimulator(); byte[] data = Csv("Heart Test"); sim.Files.Add(Remote, data);
            using var stop = new CancellationTokenSource(); sim.BeforeNext = _ => { stop.Cancel(); return Task.CompletedTask; };
            await using var connection = await Connect(sim);
            var service = new DataLogService(connection, Options); LogProgress? latest = null; service.Progress += state => latest = state;
            check(await Failure(() => service.ListAsync(stop.Token, includePresetNames: true)) is OperationCanceledException && sim.Files[Remote].SequenceEqual(data), "Stopping a header read preserves every remote byte and returns no unverified name.");
            check(latest is { Phase: LogPhase.Cancelled, Message: "Stopped reading logs." }, "Stopping a preview does not falsely claim a resumable local download was saved.");
            Normal(sim, connection, check);
        });

        await run("USB loss during a header preview reconnects without automatic maintenance", async () =>
        {
            var sim = new LogDeviceSimulator { DisconnectAtNext = 1 }; sim.Files.Add(Remote, Csv("Heart Test"));
            await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            await Failure(() => service.ListAsync(includePresetNames: true));
            check(connection.Snapshot.Phase == ConnectionPhase.Error && sim.Mode == "MAINT", "A lost connection does not falsely claim maintenance was exited.");
            int before = sim.Commands.Count; await connection.ConnectAsync("COM_SIMULATOR");
            check(sim.Commands.Skip(before).SequenceEqual(["CQ1,HELLO"]), "Reconnect only identifies and does not enter maintenance or resume reads automatically.");
            check((await service.ListAsync(includePresetNames: true)).Single().PresetName == "Heart Test", "Explicit browsing recovers the old preview and reads a fresh header.");
            Normal(sim, connection, check);
        });

        foreach (bool test in new[] { true, false })
        {
            await run("Preset browsing denied during " + (test ? "a test" : "active Manual output"), async () =>
            {
                var sim = new LogDeviceSimulator { Mode = test ? "TEST" : "MANUAL", ManualValveActive = !test }; sim.Files.Add(Remote, Csv("Heart Test"));
                await using var connection = await Connect(sim);
                await Failure(() => new DataLogService(connection, Options).ListAsync(includePresetNames: true));
                check(!sim.Commands.Any(command => command.StartsWith("CQ1,GET,", StringComparison.Ordinal) || command.StartsWith("CQ1,LIST,", StringComparison.Ordinal)) && sim.Mode == (test ? "TEST" : "MANUAL"), "Metadata lookup never stops a test or bypasses maintenance denial.");
            });
        }

        await run("Named downloads preserve CSV bytes, original remote paths and old receipts", async () =>
        {
            var sim = new LogDeviceSimulator(); byte[] data = Csv("Heart Test"); sim.Files.Add(Remote, data);
            await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var log = (await service.ListAsync(includePresetNames: true)).Single();
            string target = Target(LogFilename.Suggested(log));
            var verified = await service.DownloadAsync(log, target);
            check(verified.PresetName == "Heart Test" && verified.RemotePath == Remote && (await File.ReadAllBytesAsync(target)).SequenceEqual(data), "The completed copy records the actual preset name without rewriting any CSV byte or remote identity.");
            var loaded = await service.UseSavedCopyAsync(log, target);
            check(loaded.PresetName == "Heart Test" && loaded.LocalPath == target, "A named saved copy reopens with its validated identity.");
            await File.WriteAllTextAsync(target + ".verified.json", JsonSerializer.Serialize(new { verified.RemotePath, verified.LocalPath, verified.Size, verified.Crc32, verified.Sha256 }));
            check((await service.UseSavedCopyAsync(log, target)).PresetName == "Heart Test", "Receipts from before preset metadata was introduced remain readable.");
            await File.WriteAllTextAsync(target + ".verified.json", JsonSerializer.Serialize(verified with { PresetName = "untrusted receipt name" }));
            check((await service.UseSavedCopyAsync(log, target)).PresetName == "Heart Test", "The name is re-read from verified bytes, not trusted from editable receipt metadata.");
            int before = sim.Commands.Count;
            check(!await service.DeleteVerifiedAsync(verified, (_, _) => Task.FromResult(false)) && before == sim.Commands.Count, "Declining deletion of a prefixed copy still sends no device command.");
            check(await service.DeleteVerifiedAsync(verified, (_, _) => Task.FromResult(true)) && sim.Commands.Any(command => command.StartsWith("CQ1,DELETE," + Remote + ",", StringComparison.Ordinal)) && File.Exists(target), "Confirmed conditional deletion targets the original remote file and retains the prefixed local copy.");
            Normal(sim, connection, check);
        });

        await run("Named download interruption resumes with the same prefix and verified offset", async () =>
        {
            var sim = new LogDeviceSimulator(); byte[] data = Csv("Heart Test"); sim.Files.Add(Remote, data);
            await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var log = (await service.ListAsync(includePresetNames: true)).Single(); string target = Target(LogFilename.Suggested(log));
            sim.DisconnectAtNext = sim.NextCount + 4;
            await Failure(() => service.DownloadAsync(log, target));
            check(!File.Exists(target) && new FileInfo(target + ".part").Length == 144, "Only checked frames remain under the same prefixed temporary name.");
            await connection.ConnectAsync("COM_SIMULATOR");
            var copy = await service.DownloadAsync(log, target);
            check(copy.PresetName == "Heart Test" && sim.Commands.Contains("CQ1,GET," + Remote + ",144") && (await File.ReadAllBytesAsync(target)).SequenceEqual(data), "Resume keeps the selected name and verifies complete CSV bytes without duplication.");
            Normal(sim, connection, check);
        });

        await run("Preset changes after browse and naming collisions never silently finalize or overwrite", async () =>
        {
            var sim = new LogDeviceSimulator(); sim.Files.Add(Remote, Csv("Heart Test"));
            await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var old = (await service.ListAsync(includePresetNames: true)).Single(); string oldTarget = Target(LogFilename.Suggested(old));
            sim.Files[Remote] = Csv("Lung Test "); // Same byte count; listing size alone cannot detect this change.
            check(await Failure(() => service.DownloadAsync(old, oldTarget)) is TransferIntegrityException && !File.Exists(oldTarget) && File.Exists(oldTarget + ".part"), "A changed preset cannot be finalized under the stale suggested name.");
            var current = (await service.ListAsync(includePresetNames: true)).Single(); string currentTarget = Path.Combine(Path.GetDirectoryName(oldTarget)!, LogFilename.Suggested(current));
            var verified = await service.DownloadAsync(current, currentTarget);
            byte[] saved = await File.ReadAllBytesAsync(currentTarget);
            int before = sim.Commands.Count;
            check(await Failure(() => service.DownloadAsync(current, currentTarget)) is IOException && sim.Commands.Count == before && (await File.ReadAllBytesAsync(currentTarget)).SequenceEqual(saved), "An existing named completed copy is refused before maintenance and remains untouched.");
            check(verified.PresetName == "Lung Test" && File.Exists(oldTarget + ".part"), "Fresh browsing uses the new name while preserving the old interrupted copy.");
            Normal(sim, connection, check);
        });
    }
}
