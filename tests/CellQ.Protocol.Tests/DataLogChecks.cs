using System.Text;
using System.Text.Json;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.Protocol.Tests;

internal static class DataLogChecks
{
    private const string Remote = "/DATA/20261009/log_001.csv";
    private static readonly byte[] Data = Enumerable.Range(0, 207).Select(value => (byte)(value % 256)).ToArray();
    private static readonly LogProtocolOptions Options = new() { CommandTimeout = TimeSpan.FromMilliseconds(500), NextTimeout = TimeSpan.FromMilliseconds(150), HashTimeout = TimeSpan.FromMilliseconds(500), TransferRetries = 2 };
    private static Action<bool, string> Check = null!;
    private static string Target()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "protocol-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory); return Path.Combine(directory, "log.csv");
    }
    private static DeviceLog Log(byte[]? bytes = null) => new(Remote, "log_001.csv", "20261009", (uint)(bytes ?? Data).Length);
    private static LogDeviceSimulator Simulator() { var sim = new LogDeviceSimulator(); sim.Files.Add(Remote, Data.ToArray()); return sim; }
    private static async Task<DeviceConnectionService> Connect(LogDeviceSimulator sim)
    {
        var connection = new DeviceConnectionService(sim.CreateTransport);
        await connection.ConnectAsync("COM_SIMULATOR"); return connection;
    }
    private static async Task<Exception> Fails(Func<Task> action)
    {
        try { await action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected the operation to fail.");
    }
    private static void Normal(LogDeviceSimulator sim, DeviceConnectionService connection)
    {
        Check(sim.Mode == "IDLE" && connection.Snapshot.Mode == DeviceMode.Idle && !connection.Snapshot.WorkflowActive, "Finished workflow returns to Form 0 and clears its exclusive state.");
        Check(sim.Commands.Contains("CQ1,EXIT"), "EXIT acknowledged after the workflow.");
    }
    public static async Task RunAllAsync(Action<bool, string> check, Func<string, Func<Task>, Task> run)
    {
        Check = check;
        await run("CRC32 independent vector and fragmented root/folder listing", async () =>
        {
            Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926 && Crc32.Compute([]) == 0, "CRC32 matches its independent check vector and empty stream.");
            var sim = Simulator(); sim.Files.Add("/DATA/NO_RTC/log_002.csv", [1, 2]);
            await using var connection = await Connect(sim);
            Check(sim.Commands.All(command => command == "CQ1,HELLO"), "Connect alone never enters maintenance.");
            var files = await new DataLogService(connection, Options).ListAsync();
            Check(files.Count == 2 && files.Any(file => file.Folder == "NO_RTC" && file.Size == 2), "List /DATA, then generated folders with exact item sizes.");
            Normal(sim, connection);
        });
        await run("empty card and empty dated folder", async () =>
        {
            var sim = new LogDeviceSimulator(); await using var connection = await Connect(sim);
            Check((await new DataLogService(connection, Options).ListAsync()).Count == 0, "An empty card is a valid empty list.");
            sim.EmptyFolders.Add("20261009");
            Check((await new DataLogService(connection, Options).ListAsync()).Count == 0, "An empty dated folder is a valid empty list."); Normal(sim, connection);
        });
        await run("download checks CRC16, byte positions, EOF, stream CRC32 and both whole-file hashes", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); string target = Target();
            var verified = await new DataLogService(connection, Options).DownloadAsync(Log(), target);
            Check((await File.ReadAllBytesAsync(target)).SequenceEqual(Data) && verified.Crc32 == Crc32.Compute(Data), "Final bytes are exact and fully verified.");
            Check(File.Exists(target + ".verified.json") && !File.Exists(target + ".part") && !File.Exists(target + ".part.json"), "Finalization consumes only the temporary copy and its owned progress record.");
            Check(sim.HashCount == 2 && sim.Commands.Count(command => command == "CQ1,EXIT") == 1, "HASH before/after and EXIT only after the entire file workflow."); Normal(sim, connection);
        });
        await run("zero-byte log and empty transfer checksum", async () =>
        {
            var sim = Simulator(); sim.Files[Remote] = []; sim.MinimalCrc = true; await using var connection = await Connect(sim);
            string target = Target(); await new DataLogService(connection, Options).DownloadAsync(Log([]), target);
            Check(new FileInfo(target).Length == 0, "Empty file completes after size/CRC/hash verification."); Normal(sim, connection);
        });
        await run("interruption, reconnect to existing MAINT, and resumed segment CRC32", async () =>
        {
            var sim = Simulator(); sim.DisconnectAtNext = 4; sim.TruncateOnDisconnect = true;
            await using var connection = await Connect(sim); string target = Target(); var logs = new DataLogService(connection, Options);
            await Fails(() => logs.DownloadAsync(Log(), target));
            Check(!File.Exists(target) && File.Exists(target + ".part") && new FileInfo(target + ".part").Length == 144, "USB loss keeps only checked frame bytes in .part.");
            Check(connection.Snapshot.Phase == ConnectionPhase.Error && sim.Mode == "MAINT", "Lost connection does not falsely claim EXIT.");
            int commands = sim.Commands.Count; await connection.ConnectAsync("COM_SIMULATOR");
            Check(sim.Commands.Skip(commands).SequenceEqual(["CQ1,HELLO"]), "Reconnecting to MAINT still only identifies the device.");
            await logs.DownloadAsync(Log(), target);
            Check(sim.Commands.Contains($"CQ1,GET,{Remote},144") && (await File.ReadAllBytesAsync(target)).SequenceEqual(Data), "Resume restarts at the durable verified offset and checks only the resumed stream CRC.");
            Check(sim.Commands.Count(command => command == "CQ1,ENTER") == 1, "Explicit resume recovers existing maintenance without a second ENTER."); Normal(sim, connection);
        });
        await run("user cancellation checkpoints and exits maintenance", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); using var cancel = new CancellationTokenSource();
            var logs = new DataLogService(connection, Options); logs.Progress += state => { if (state.Bytes >= 48 && state.Phase == LogPhase.Downloading) cancel.Cancel(); };
            string target = Target(); var failure = await Fails(() => logs.DownloadAsync(Log(), target, cancel.Token));
            Check(failure is OperationCanceledException && new FileInfo(target + ".part").Length == 48 && !File.Exists(target), "Cancel preserves a checked incomplete prefix."); Normal(sim, connection);
            await new DataLogService(connection, Options).DownloadAsync(Log(), target);
            Check((await File.ReadAllBytesAsync(target)).SequenceEqual(Data), "Cancelled download resumes without duplicated bytes.");
        });
        await run("cancellation while ENTER is awaiting its ACK still exits accepted maintenance", async () =>
        {
            var sim = Simulator(); using var cancel = new CancellationTokenSource();
            sim.BeforeEnterReply = () => { cancel.Cancel(); return Task.CompletedTask; };
            await using var connection = await Connect(sim);
            await Fails(() => new DataLogService(connection, Options).ListAsync(cancel.Token));
            Check(!sim.Commands.Any(command => command.StartsWith("CQ1,LIST,")), "Cancelled entry does not start listing."); Normal(sim, connection);
        });
        await run("disconnect during download cancels, checkpoints, exits, then closes", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); string target = Target();
            var logs = new DataLogService(connection, Options); Task? disconnect = null;
            logs.Progress += progress => { if (progress.Bytes >= 48 && disconnect is null) disconnect = connection.DisconnectAsync(); };
            await Fails(() => logs.DownloadAsync(Log(), target)); if (disconnect is not null) await disconnect;
            Check(connection.Snapshot.Phase == ConnectionPhase.Disconnected && sim.Mode == "IDLE" && sim.Commands.Contains("CQ1,EXIT"), "Explicit disconnect waits for safe maintenance exit before closing the shared port.");
            Check(!File.Exists(target) && new FileInfo(target + ".part").Length == 48, "Disconnect keeps resumable checked progress.");
        });
        await run("app disposal during download completes safe cleanup before closing", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); string target = Target();
            var logs = new DataLogService(connection, Options); Task? disposal = null;
            logs.Progress += progress => { if (progress.Bytes >= 48 && disposal is null) disposal = connection.DisposeAsync().AsTask(); };
            await Fails(() => logs.DownloadAsync(Log(), target)); if (disposal is not null) await disposal;
            Check(sim.Mode == "IDLE" && connection.Snapshot.Phase == ConnectionPhase.Disconnected && !connection.MaintenanceMayRemain, "Closing the owner during a download confirms EXIT before port disposal.");
        });
        foreach (string corruption in new[] { "frame CRC", "frame offset", "GET offset", "stream CRC", "EOF size", "NEXT timeout" })
        {
            await run("CANCEL and resume after " + corruption, async () =>
            {
                var sim = Simulator();
                switch (corruption) { case "frame CRC": sim.CorruptFrames = 1; break; case "frame offset": sim.WrongOffsets = 1; break; case "GET offset": sim.WrongGetOffset = 1; break; case "stream CRC": sim.BadStreamCrc = 1; break; case "EOF size": sim.BadEofSize = 1; break; case "NEXT timeout": sim.DropNext = 1; break; }
                await using var connection = await Connect(sim); string target = Target();
                await new DataLogService(connection, Options).DownloadAsync(Log(), target);
                Check((await File.ReadAllBytesAsync(target)).SequenceEqual(Data), "Recovery does not accept or duplicate corrupt bytes.");
                Check(sim.Commands.Count(command => command.StartsWith("CQ1,GET,")) == 2 && sim.Commands.Count(command => command == "CQ1,CANCEL") >= 2, "Failure sends CANCEL before GET from the last checked position."); Normal(sim, connection);
            });
        }
        await run("exhausted CRC retries remain incomplete", async () =>
        {
            var sim = Simulator(); sim.CorruptFrames = 20; await using var connection = await Connect(sim); string target = Target();
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            Check(!File.Exists(target) && new FileInfo(target + ".part").Length == 0, "Repeated CRC errors never finalize unchecked bytes."); Normal(sim, connection);
        });
        await run("whole-file HASH mismatch never finalizes", async () =>
        {
            var sim = Simulator(); sim.BadFinalHash = true; await using var connection = await Connect(sim); string target = Target();
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            Check(!File.Exists(target) && File.Exists(target + ".part"), "Whole-file verification failure leaves .part, not a completed log."); Normal(sim, connection);
        });
        await run("changed remote file and tampered resume prefix are refused", async () =>
        {
            var sim = Simulator(); sim.DisconnectAtNext = 2; await using var connection = await Connect(sim); string target = Target();
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            byte[] prefix = await File.ReadAllBytesAsync(target + ".part");
            await connection.ConnectAsync("COM_SIMULATOR"); sim.Files[Remote][0] ^= 1;
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            Check((await File.ReadAllBytesAsync(target + ".part")).SequenceEqual(prefix), "Changed remote identity leaves the saved prefix untouched.");
            sim.Files[Remote] = Data.ToArray(); prefix[0] ^= 1; await File.WriteAllBytesAsync(target + ".part", prefix);
            int gets = sim.Commands.Count(command => command.StartsWith("CQ1,GET,"));
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            Check(sim.Commands.Count(command => command.StartsWith("CQ1,GET,")) == gets, "Corrupt local prefix fails CRC/SHA checks before GET."); Normal(sim, connection);
        });
        await run("crash tail is truncated only after a validated checkpoint", async () =>
        {
            var sim = Simulator(); sim.DisconnectAtNext = 2; await using var connection = await Connect(sim); string target = Target();
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            await using (var tail = new FileStream(target + ".part", FileMode.Append)) await tail.WriteAsync(new byte[] { 255, 254 });
            await connection.ConnectAsync("COM_SIMULATOR");
            await new DataLogService(connection, Options).DownloadAsync(Log(), target);
            Check((await File.ReadAllBytesAsync(target)).SequenceEqual(Data) && sim.Commands.Contains($"CQ1,GET,{Remote},48"), "Uncheckpointed crash tail is replaced, not trusted as resume progress.");
        });
        foreach (string mode in new[] { "TEST", "MANUAL" })
        {
            await run("maintenance denial in " + mode, async () =>
            {
                var sim = Simulator(); sim.Mode = mode; sim.ManualValveActive = mode == "MANUAL"; await using var connection = await Connect(sim);
                var error = await Fails(() => new DataLogService(connection, Options).ListAsync());
                Check(error is MaintenanceDeniedException or DeviceReplyException && sim.Mode == mode, "Busy device refuses log access without changing test/output state.");
                Check(!sim.Commands.Contains("CQ1,EXIT") && !sim.Commands.Any(command => command.Contains("STOP") || command.Contains("VALVE")), "Denied entry does not stop a test or issue EXIT outside maintenance.");
                if (mode == "TEST") Check(!sim.Commands.Contains("CQ1,ENTER"), "Fresh TEST observation blocks ENTER locally.");
            });
        }
        await run("entry denial after a stale Idle observation", async () =>
        {
            var sim = Simulator(); sim.DenyEnter = true; await using var connection = await Connect(sim);
            await Fails(() => new DataLogService(connection, Options).ListAsync());
            Check(!sim.Commands.Any(command => command.StartsWith("CQ1,LIST")) && sim.Mode == "IDLE", "Firmware entry refusal is honored even after Idle HELLO.");
        });
        await run("test starts between HELLO and ENTER and is reported without being stopped", async () =>
        {
            var sim = Simulator(); sim.BeginTestOnEnter = true; await using var connection = await Connect(sim);
            await Fails(() => new DataLogService(connection, Options).ListAsync());
            Check(connection.Snapshot.Mode == DeviceMode.Test && sim.Mode == "TEST", "After an entry refusal, fresh HELLO reports the actual busy test state.");
            Check(!sim.Commands.Contains("CQ1,EXIT") && !sim.Commands.Any(command => command.StartsWith("CQ1,LIST,")), "A raced test start is not stopped or overridden.");
        });
        await run("existing completed filename and orphan .part are never overwritten", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); string target = Target(); await File.WriteAllTextAsync(target, "keep me");
            int commands = sim.Commands.Count; await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), target));
            Check(await File.ReadAllTextAsync(target) == "keep me" && sim.Commands.Count == commands, "Existing local file is preserved before maintenance.");
            string orphan = Target(); await File.WriteAllTextAsync(orphan + ".part", "unknown");
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(Log(), orphan));
            Check(await File.ReadAllTextAsync(orphan + ".part") == "unknown", "Orphan temporary file is preserved, not assumed verified.");
        });
        await run("deletion confirmation declined sends no maintenance or delete command", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var verified = await service.DownloadAsync(Log(), Target()); int commands = sim.Commands.Count;
            Check(!await service.DeleteVerifiedAsync(verified, (_, _) => Task.FromResult(false)) && sim.Commands.Count == commands && sim.Files.ContainsKey(Remote), "Declining deletion sends no commands and keeps both copies.");
        });
        await run("local copy stays locked throughout deletion confirmation", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var verified = await service.DownloadAsync(Log(), Target()); bool blocked = false;
            await service.DeleteVerifiedAsync(verified, async (_, _) =>
            {
                try { await File.WriteAllTextAsync(verified.LocalPath, "tamper"); } catch (IOException) { blocked = true; }
                return false;
            });
            Check(blocked && (await File.ReadAllBytesAsync(verified.LocalPath)).SequenceEqual(Data), "The checked local bytes cannot be replaced while the confirmation is open.");
        });
        await run("confirmed conditional deletion keeps verified local copy", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var verified = await service.DownloadAsync(Log(), Target());
            Check(await service.DeleteVerifiedAsync(verified, (confirmation, _) => Task.FromResult(confirmation.RemotePath == Remote && confirmation.LocalPath == verified.LocalPath)), "Deletion requires explicit confirmation of the exact device file/local copy.");
            Check(sim.Commands.Contains($"CQ1,DELETE,{Remote},{Data.Length},{Crc32.Compute(Data):X8}") && !sim.Files.ContainsKey(Remote) && File.Exists(verified.LocalPath), "DELETE contains matching size and uppercase CRC32, and local copy survives."); Normal(sim, connection);
        });
        await run("saved receipt is reverified after restarting the log service", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); string target = Target();
            await new DataLogService(connection, Options).DownloadAsync(Log(), target); int commands = sim.Commands.Count;
            var restarted = new DataLogService(connection, Options);
            var verified = await restarted.UseSavedCopyAsync(Log(), target);
            Check(sim.Commands.Count == commands && verified.LocalPath == target, "Reusing a saved copy checks only local bytes and does not enter maintenance.");
            Check(await restarted.DeleteVerifiedAsync(verified, (_, _) => Task.FromResult(true)), "A restored verified copy remains eligible for freshly checked conditional deletion.");
            await File.WriteAllBytesAsync(target, new byte[Data.Length]);
            await Fails(() => restarted.UseSavedCopyAsync(Log(), target));
            Check(restarted.LastVerified is null, "Receipt reuse never silently marks altered local bytes verified.");
        });
        await run("changed local or remote copy prevents deletion", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var verified = await service.DownloadAsync(Log(), Target()); sim.Files[Remote][0] ^= 1;
            await Fails(() => service.DeleteVerifiedAsync(verified, (_, _) => Task.FromResult(true)));
            Check(!sim.Commands.Any(command => command.StartsWith("CQ1,DELETE,")) && sim.Files.ContainsKey(Remote), "Fresh remote HASH mismatch prevents DELETE.");
            sim.Files[Remote] = Data.ToArray(); await File.WriteAllBytesAsync(verified.LocalPath, new byte[Data.Length]);
            bool asked = false; await Fails(() => service.DeleteVerifiedAsync(verified, (_, _) => { asked = true; return Task.FromResult(true); }));
            Check(!asked && !sim.Commands.Any(command => command.StartsWith("CQ1,DELETE,")), "Tampered local copy fails re-verification before confirmation.");
        });
        await run("firmware conditional deletion denies a file changed after HASH", async () =>
        {
            var sim = Simulator(); await using var connection = await Connect(sim); var service = new DataLogService(connection, Options);
            var verified = await service.DownloadAsync(Log(), Target()); sim.BeforeDelete = () => sim.Files[Remote][0] ^= 1;
            await Fails(() => service.DeleteVerifiedAsync(verified, (_, _) => Task.FromResult(true)));
            Check(sim.Files.ContainsKey(Remote) && File.Exists(verified.LocalPath), "Conditional firmware refusal preserves both copies."); Normal(sim, connection);
        });
        await run("unsafe listing and /PRESETS paths are rejected", async () =>
        {
            var sim = Simulator(); sim.ListingOverride = "CQ1,LIST,BEGIN\nCQ1,ITEM,..,D,0\nCQ1,LIST,END"; await using var connection = await Connect(sim);
            await Fails(() => new DataLogService(connection, Options).ListAsync()); Normal(sim, connection);
            int commands = sim.Commands.Count;
            await Fails(() => new DataLogService(connection, Options).DownloadAsync(new("/PRESETS/test.csv", "test.csv", "PRESETS", 0), Target()));
            Check(commands == sim.Commands.Count, "Non-log path never reaches the serial stream.");
        });
        await run("failed EXIT preserves verified copy and explicit reconnect recovery", async () =>
        {
            var sim = Simulator(); sim.DropExit = true; await using var connection = await Connect(sim); var service = new DataLogService(connection, Options); string target = Target();
            await Fails(() => service.DownloadAsync(Log(), target));
            Check(File.Exists(target) && service.LastVerified is not null && connection.Snapshot.Phase == ConnectionPhase.Error && sim.Mode == "MAINT", "Missing EXIT ACK does not pretend the device returned to normal or discard a verified local copy.");
            sim.DropExit = false; int commands = sim.Commands.Count; await connection.ConnectAsync("COM_SIMULATOR");
            Check(sim.Commands.Skip(commands).SequenceEqual(["CQ1,HELLO"]), "Recovery reconnect still performs no automatic mode change.");
            await connection.ReturnToNormalAsync(); Normal(sim, connection);
            int entries = sim.Commands.Count(command => command == "CQ1,ENTER"); await Fails(() => connection.ReturnToNormalAsync());
            Check(sim.Commands.Count(command => command == "CQ1,ENTER") == entries, "Return to normal never enters maintenance from Idle.");
        });
        await run("refresh queued behind the full log workflow and all command lines bounded", async () =>
        {
            var sim = Simulator(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sim.BeforeNext = async number => { if (number == 1) { reached.SetResult(); await gate.Task; } };
            await using var connection = await Connect(sim); var download = new DataLogService(connection, Options).DownloadAsync(Log(), Target());
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3)); int count = sim.Commands.Count;
            var refresh = connection.RefreshHelloAsync(); Check(!refresh.IsCompleted && sim.Commands.Count == count, "HELLO cannot interleave with transfer requests.");
            gate.SetResult(); await download; await refresh;
            Check(sim.Commands.All(command => Encoding.ASCII.GetByteCount(command) + 1 <= 79), "Every transmitted command fits the v1 line limit."); Normal(sim, connection);
        });
    }
}
