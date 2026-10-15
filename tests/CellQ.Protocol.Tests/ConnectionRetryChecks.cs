using System.Collections.Concurrent;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.Protocol.Tests;

internal static class ConnectionRetryChecks
{
    public static async Task RunAllAsync(Action<bool, string> check, Func<string, Func<Task>, Task> run)
    {
        foreach (string mode in new[] { "IDLE", "MANUAL", "TEST", "MAINT" })
        {
            await run("COM10: fragmented syntax rejection then valid identification in " + mode, async () =>
            {
                var device = new FakeDevice();
                var messages = new ConcurrentQueue<DiagnosticMessage>();
                var states = new ConcurrentQueue<ConnectionSnapshot>();
                device.OnWrite = command =>
                {
                    check(command == "CQ1,HELLO\n", "The exact protocol ASCII/LF command is unchanged on both attempts.");
                    if (device.Writes.Count == 1)
                    {
                        device.Feed("D,before-rejection\nCQ1,ERR,SYN");
                        device.Feed("TAX\nD,after-rejection\n");
                    }
                    else device.Feed("D,before-identity\nCQ1,HELLO,1.0.1," + mode + "\n");
                    return Task.CompletedTask;
                };
                await using var service = new DeviceConnectionService(() => device);
                service.MessageReceived += messages.Enqueue;
                service.StateChanged += states.Enqueue;
                await service.ConnectAsync("COM10");
                check(service.Snapshot.Phase == ConnectionPhase.Connected && service.Snapshot.FirmwareVersion == "1.0.1", "Only the verified second response establishes identity.");
                check(service.Snapshot.Mode.ToString() == (mode switch { "IDLE" => "Idle", "MANUAL" => "Manual", "TEST" => "Test", _ => "Maintenance" }), "The existing device mode is reported without changing it.");
                check(device.PortName == "COM10" && device.OpenCount == 1 && device.CloseCount == 0 && device.Writes.Count == 2, "The retry keeps the same COM10 owner and open port.");
                check(device.Configuration == new SerialConfiguration(), "250000 baud and DTR/RTS off are preserved.");
                check(messages.Count(m => m.Kind == "Notice" && m.Text.Contains("retrying CQ1,HELLO once", StringComparison.Ordinal)) == 1, "The controlled retry is visible in Diagnostics.");
                check(messages.Any(m => m.Text == "CQ1,ERR,SYNTAX") && messages.Count(m => m.Kind == "Debug") == 3, "The original rejection and mixed debug traffic remain visible.");
                check(states.Where(s => s.Phase == ConnectionPhase.Connecting).All(s => s.FirmwareVersion is null && s.Mode == DeviceMode.Unknown), "A rejected request never creates a live-looking identity.");
                await service.DisconnectAsync();
                check(device.CloseCount == 1 && device.Writes.All(w => w == "CQ1,HELLO\n"), "Completion/disconnect send no maintenance, reset or other commands.");
            });
        }

        await run("COM10: persistent syntax rejection stops after two requests", async () =>
        {
            var device = new FakeDevice { OnWrite = null };
            device.OnWrite = _ => { device.Feed("CQ1,ERR,SYNTAX\n"); return Task.CompletedTask; };
            await using var service = new DeviceConnectionService(() => device);
            await Fails<ProtocolException>(() => service.ConnectAsync("COM10"), check);
            check(service.Snapshot.Phase == ConnectionPhase.Error && service.Snapshot.Error!.EndsWith("CQ1,ERR,SYNTAX", StringComparison.Ordinal), "A repeated rejection preserves the actual firmware error.");
            check(service.Snapshot.FirmwareVersion is null && service.Snapshot.Mode == DeviceMode.Unknown && !service.Snapshot.RequestPending, "No identity or pending request remains after failure.");
            check(device.Writes.Count == 2 && device.CloseCount == 1 && !device.IsOpen, "The retry is bounded and the failed connection closes.");
        });

        foreach (string response in new[] { "CQ1,ERR,BUSY", "CQ1,ERR,syntax", "CQ1,ERR,SYNTAX,extra", "CQ1,HELLO,1.0.1,UNKNOWN" })
        {
            await run("Identification does not retry unexpected response " + response, async () =>
            {
                var device = new FakeDevice();
                device.OnWrite = _ => { device.Feed(response + "\n"); return Task.CompletedTask; };
                await using var service = new DeviceConnectionService(() => device);
                await Fails<ProtocolException>(() => service.ConnectAsync("COM10"), check);
                check(device.Writes.Count == 1 && !device.IsOpen && service.Snapshot.Mode == DeviceMode.Unknown, "Other replies are rejected strictly without a retry.");
            });
        }

        await run("Identification retry timeout closes without a third request", async () =>
        {
            var device = new FakeDevice();
            device.OnWrite = _ => { device.Feed(device.Writes.Count == 1 ? "CQ1,ERR,SYNTAX\n" : "D,no-response\n"); return Task.CompletedTask; };
            await using var service = new DeviceConnectionService(() => device, TimeSpan.FromMilliseconds(80));
            await Fails<TimeoutException>(() => service.ConnectAsync("COM10"), check);
            check(device.Writes.Count == 2 && !device.IsOpen && service.Snapshot.Phase == ConnectionPhase.Error, "A silent retry has a deadline and closes rather than looping.");
        });

        foreach (bool disconnect in new[] { false, true })
        {
            await run("Cancel identification between attempts by " + (disconnect ? "Disconnect" : "token"), async () =>
            {
                var device = new FakeDevice();
                device.OnWrite = _ => { device.Feed("CQ1,ERR,SYNTAX\n"); return Task.CompletedTask; };
                using var cancellation = new CancellationTokenSource();
                var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var service = new DeviceConnectionService(() => device);
                service.MessageReceived += m => { if (m.Kind == "Notice") announced.TrySetResult(); };
                var connection = service.ConnectAsync("COM10", cancellation.Token);
                await announced.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (disconnect) await service.DisconnectAsync(); else cancellation.Cancel();
                await Fails<OperationCanceledException>(() => connection, check);
                check(device.Writes.Count == 1 && device.CloseCount == 1 && !device.IsOpen, "Cancellation during the wait prevents any retry or maintenance command.");
                check(service.Snapshot.Phase == ConnectionPhase.Disconnected && service.Snapshot.FirmwareVersion is null, "Cancellation clears connection state.");
            });
        }

        await run("Late unsolicited identity between attempts cannot satisfy the retry", async () =>
        {
            var device = new FakeDevice();
            device.OnWrite = _ => { if (device.Writes.Count == 1) device.Feed("CQ1,ERR,SYNTAX\n"); return Task.CompletedTask; };
            var unsolicited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var service = new DeviceConnectionService(() => device, TimeSpan.FromMilliseconds(80));
            service.MessageReceived += m =>
            {
                if (m.Kind == "Notice" && m.Text.Contains("retrying", StringComparison.Ordinal)) device.Feed("CQ1,HELLO,stale,MAINT\n");
                if (m.Kind == "Notice" && m.Text.StartsWith("Unsolicited", StringComparison.Ordinal)) unsolicited.TrySetResult();
            };
            var connection = service.ConnectAsync("COM10");
            await unsolicited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(service.Snapshot.Phase == ConnectionPhase.Connecting && service.Snapshot.FirmwareVersion is null, "The gap has no pending request and cannot accept stale identity.");
            await Fails<TimeoutException>(() => connection, check);
            check(device.Writes.Count == 2 && !device.IsOpen && service.Snapshot.Mode == DeviceMode.Unknown, "The second request still needs its own valid response.");
        });

        await run("Refresh status syntax rejection keeps the existing fail-closed policy", async () =>
        {
            var device = FakeDevice.Responding();
            await using var service = new DeviceConnectionService(() => device);
            await service.ConnectAsync("COM10");
            device.OnWrite = _ => { device.Feed("CQ1,ERR,SYNTAX\n"); return Task.CompletedTask; };
            await Fails<ProtocolException>(() => service.RefreshHelloAsync(), check);
            check(device.Writes.Count == 2 && !device.IsOpen && service.Snapshot.FirmwareVersion is null, "Only initial connection can retry; refresh/workflow identification errors still close.");
        });
    }

    private static async Task Fails<T>(Func<Task> operation, Action<bool, string> check) where T : Exception
    {
        try { await operation(); }
        catch (T) { check(true, "Expected " + typeof(T).Name); return; }
        check(false, "Expected " + typeof(T).Name);
    }
}
