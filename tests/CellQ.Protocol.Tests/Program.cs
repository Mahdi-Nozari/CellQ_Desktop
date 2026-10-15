using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using CellQ.Protocol;
using CellQ.Testing;

namespace CellQ.Protocol.Tests;

internal static class Program
{
    private static int _assertions, _cases;
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("\nD,payload-not-debug\nCQ1,HELLO,x,MAINT\n").Concat(new byte[] { 0xA5, 0x5A }).ToArray();
    private static async Task<int> Main()
    {
        try
        {
            ParserChecks();
            await ConnectionChecks();
            await ConnectionRetryChecks.RunAllAsync(Check, Run);
            await DataLogChecks.RunAllAsync(Check, Run);
            await PresetLogChecks.RunAllAsync(Check, Run);
            await PressureGraphChecks.RunAllAsync(Check, Run);
            await FirmwareChecks.RunAllAsync(Check, Run);
            Console.WriteLine($"PASS: {_assertions} protocol assertions across {_cases} cases. No physical ports opened.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException("FAIL: " + message); _assertions++; }
    private static async Task Run(string name, Func<Task> test) { await test(); _cases++; Console.WriteLine("PASS: " + name); }
    private static byte[] Frame(byte[] payload, uint offset = 0, byte type = 1)
    {
        var frame = new byte[10 + payload.Length]; frame[0] = 0xA5; frame[1] = 0x5A; frame[2] = type; frame[3] = (byte)payload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), offset); payload.CopyTo(frame, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Crc16.Compute(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }
    private static void VerifyMixed(IReadOnlyList<ProtocolMessage> messages)
    {
        Check(messages.Count == 5, "Mixed stream emits exactly five messages.");
        Check(messages[0] is DebugLine { Text: "D,before,1" }, "Raw CRLF debug preserved.");
        Check(messages[1] is ResponseLine { Text: "CQ1,HELLO,2.1.0,IDLE" }, "HELLO line preserved.");
        Check(messages[2] is DataFrame { Offset: 0xFEDCBA98 } data && data.Payload.SequenceEqual(Payload), "Binary offset/payload intact; embedded text and markers not dispatched.");
        Check(messages[3] is ResponseLine { Text: "CQ1,EOF,48,1234ABCD" }, "Other CQ1 responses preserved without reinterpretation.");
        Check(messages[4] is DebugLine { Text: "D,after" }, "Debug after frame recovered.");
    }
    private static void ParserChecks()
    {
        Check(Crc16.Compute(Encoding.ASCII.GetBytes("123456789")) == 0x29B1, "Independent CRC16-CCITT-FALSE check vector.");
        var bytes = Encoding.ASCII.GetBytes("D,before,1\r\nCQ1,HELLO,2.1.0,IDLE\n").Concat(Frame(Payload, 0xFEDCBA98)).Concat(Encoding.ASCII.GetBytes("CQ1,EOF,48,1234ABCD\nD,after\n")).ToArray();
        for (int split = 0; split <= bytes.Length; split++) { var parser = new StreamParser(); VerifyMixed(parser.Feed(bytes.AsSpan(0, split)).Concat(parser.Feed(bytes.AsSpan(split))).ToArray()); }
        var bytewise = new StreamParser(); var bytewiseMessages = new List<ProtocolMessage>();
        foreach (byte value in bytes) bytewiseMessages.AddRange(bytewise.Feed([value]));
        VerifyMixed(bytewiseMessages);
        for (int seed = 0; seed < 50; seed++)
        {
            var random = new Random(seed); var parser = new StreamParser(); var messages = new List<ProtocolMessage>();
            for (int offset = 0; offset < bytes.Length;) { int count = Math.Min(random.Next(1, 17), bytes.Length - offset); messages.AddRange(parser.Feed(bytes.AsSpan(offset, count))); offset += count; }
            VerifyMixed(messages);
        }
        _cases++; Console.WriteLine("PASS: every two-part split, bytewise input, and 50 randomized mixed-stream fragmentations");
        foreach (var length in new[] { 1, 48 })
        {
            var frame = Frame(Enumerable.Repeat((byte)0xA5, length).ToArray(), uint.MaxValue);
            Check(new StreamParser().Feed(frame).Single() is DataFrame { Offset: uint.MaxValue } data && data.Payload.Length == length, "Boundary payload size and unsigned little-endian offset.");
        }
        var corrupt = Frame(Payload); corrupt[8] ^= 1;
        var badMessages = new StreamParser().Feed(corrupt.Concat(Encoding.ASCII.GetBytes("D,recovered\n")).ToArray());
        Check(badMessages.Count == 2 && badMessages[0] is FramingError { Kind: FramingErrorKind.CrcMismatch } && badMessages[1] is DebugLine { Text: "D,recovered" }, "Bad CRC discards frame, never leaks payload text, and recovers next line.");
        var unsupported = new StreamParser().Feed(Frame(Payload, type: 2).Concat(Encoding.ASCII.GetBytes("D,recovered\n")).ToArray());
        Check(unsupported.Count == 2 && unsupported[0] is FramingError { Kind: FramingErrorKind.InvalidHeader } && unsupported[1] is DebugLine, "Unknown frame type does not leak payload.");
        foreach (byte length in new byte[] { 0, 49, 255 })
        {
            var parser = new StreamParser(); var invalid = parser.Feed(new byte[] { 0xA5, 0x5A, 1, length, 0x0A }.Concat(Encoding.ASCII.GetBytes("D,recovered\n")).ToArray());
            Check(invalid[0] is FramingError { Kind: FramingErrorKind.InvalidHeader } && invalid[^1] is DebugLine, "Invalid payload length rejected and line-boundary recovery works.");
        }
        var overlong = new StreamParser(16).Feed(Encoding.ASCII.GetBytes("D," + new string('x', 30) + "\nD,ok\n"));
        Check(overlong.Count == 2 && overlong[0] is FramingError { Kind: FramingErrorKind.LineTooLong } && overlong[1] is DebugLine, "Overlong ASCII input is bounded and recovers.");
        foreach (var partial in new[] { Encoding.ASCII.GetBytes("D,partial"), new byte[] { 0xA5 }, Frame(Payload)[..12] })
        {
            var parser = new StreamParser(); Check(parser.Feed(partial).Count == 0, "Partial input is not emitted.");
            Check(parser.Complete().Single() is FramingError { Kind: FramingErrorKind.Truncated }, "Interrupted input reported.");
            Check(parser.Feed(Encoding.ASCII.GetBytes("D,new\n")).Single() is DebugLine, "Completion resets stale parser bytes.");
        }
        Check(new StreamParser().Feed(new byte[] { 0xFF, 0x0A }.Concat(Encoding.ASCII.GetBytes("D,ok\n")).ToArray()).Count == 2, "Non-ASCII corruption reported and recovered.");
        Check(new StreamParser().Feed(new byte[] { 0xA5, 0xA5 }.Concat(Frame([1])[1..]).ToArray()).Count == 2, "Split/repeated invalid marker recovers a valid frame.");
        foreach (string mode in new[] { "IDLE", "MANUAL", "TEST", "MAINT" }) Check(HelloResponse.TryParse($"CQ1,HELLO,1.2-beta,{mode}", out _), "All documented modes accepted.");
        foreach (string line in new[] { "CQ1,HELLO,v,idle", "CQ1,HELLO,v,UNKNOWN", "CQ1,HELLO,,IDLE", "CQ1,HELLO,v,IDLE,extra", "CQ1,OK,ENTER" }) Check(!HelloResponse.TryParse(line, out _), "Malformed/unknown HELLO response is not reinterpreted.");
        _cases++; Console.WriteLine("PASS: CRC/header/length corruption, truncated input, bounded lines, resynchronization, and strict HELLO parsing");
    }
    private static async Task ConnectionChecks()
    {
        foreach (string mode in new[] { "IDLE", "MANUAL", "TEST", "MAINT" })
        {
            await Run("HELLO identification in " + mode + " without maintenance entry", async () =>
            {
                var fake = new FakeDevice(); var messages = new ConcurrentQueue<DiagnosticMessage>();
                fake.OnWrite = _ => { fake.Feed("D,before-hello\nCQ1,HEL"); fake.Feed("LO,test-firmware," + mode + "\nD,after-hello\n"); return Task.CompletedTask; };
                await using var service = new DeviceConnectionService(() => fake);
                service.MessageReceived += messages.Enqueue;
                Check(fake.OpenCount == 0, "Service creation opens no port.");
                await service.ConnectAsync("COM_TEST");
                Check(service.Snapshot.Phase == ConnectionPhase.Connected && service.Snapshot.FirmwareVersion == "test-firmware", "Valid HELLO identifies firmware.");
                Check(service.Snapshot.Mode.ToString() == (mode switch { "IDLE" => "Idle", "MANUAL" => "Manual", "TEST" => "Test", _ => "Maintenance" }), "Mode exactly follows HELLO.");
                Check(fake.Configuration == new SerialConfiguration() && fake.Configuration.Parity == "None" && fake.Configuration.StopBits == 1 && fake.Configuration.Handshake == "None", "250000, 8-N-1, DTR/RTS off, no handshake.");
                await Eventually(() => messages.Count(m => m.Kind == "Debug") == 2);
                Check(messages.Any(m => m.Text == "D,before-hello") && messages.Any(m => m.Text == "D,after-hello"), "Mixed debug does not satisfy or block HELLO.");
                await service.DisconnectAsync();
                Check(!fake.IsOpen && fake.CloseCount == 1, "Disconnect closes the owned port.");
                Check(service.Snapshot.FirmwareVersion is null && service.Snapshot.Mode == DeviceMode.Unknown, "Disconnected state clears stale firmware/mode.");
                Check(fake.Writes.SequenceEqual(new[] { "CQ1,HELLO\n" }), "Connect and disconnect send only HELLO, including existing MAINT.");
            });
        }
        await Run("one port owner and serialized HELLO requests", async () =>
        {
            var fake = FakeDevice.Responding(); int created = 0;
            await using var service = new DeviceConnectionService(() => { created++; return fake; });
            await service.ConnectAsync("COM_TEST");
            await Throws<InvalidOperationException>(() => service.ConnectAsync("COM_OTHER"));
            Check(created == 1 && fake.OpenCount == 1 && service.Snapshot.Phase == ConnectionPhase.Connected, "Second connection cannot open another port.");
            fake.OnWrite = _ => { if (fake.Writes.Count == 3) fake.Feed("CQ1,HELLO,test-firmware,TEST\n"); return Task.CompletedTask; };
            var first = service.RefreshHelloAsync(); await Eventually(() => fake.Writes.Count == 2);
            var second = service.RefreshHelloAsync();
            Check(fake.Writes.Count == 2 && service.Snapshot.RequestPending, "Second request waits for first response.");
            fake.Feed("D,while-pending\nCQ1,HELLO,test-firmware,MANUAL\n");
            await Task.WhenAll(first, second);
            Check(service.Snapshot.Mode == DeviceMode.Test && fake.Writes.Count == 3 && !service.Snapshot.RequestPending, "Serialized responses update state in order.");
            Check(fake.Writes.All(w => w == "CQ1,HELLO\n"), "No command outside HELLO emitted.");
        });
        await Run("HELLO timeout closes port without retry or stale identity", async () =>
        {
            var fake = new FakeDevice(); await using var service = new DeviceConnectionService(() => fake, TimeSpan.FromMilliseconds(80));
            await Throws<TimeoutException>(() => service.ConnectAsync("COM_TEST"));
            Check(!fake.IsOpen && service.Snapshot.Phase == ConnectionPhase.Error && service.Snapshot.Mode == DeviceMode.Unknown, "Timeout is an honest error with closed port.");
            Check(fake.Writes.Count == 1, "No automatic retry after uncertain response.");
        });
        await Run("refresh timeout invalidates previously reported state", async () =>
        {
            var fake = FakeDevice.Responding(); await using var service = new DeviceConnectionService(() => fake, TimeSpan.FromMilliseconds(80));
            await service.ConnectAsync("COM_TEST"); fake.OnWrite = null;
            await Throws<TimeoutException>(() => service.RefreshHelloAsync());
            Check(!fake.IsOpen && service.Snapshot.FirmwareVersion is null && service.Snapshot.Mode == DeviceMode.Unknown, "Failed re-identification closes port and clears state.");
            Check(fake.Writes.Count == 2, "No retry or CANCEL/EXIT during observational connection.");
        });
        await Run("cancel pending identification without sending CQ1,CANCEL", async () =>
        {
            var fake = new FakeDevice(); await using var service = new DeviceConnectionService(() => fake);
            var connect = service.ConnectAsync("COM_TEST"); await Eventually(() => fake.Writes.Count == 1);
            await service.DisconnectAsync(); await Throws<OperationCanceledException>(() => connect);
            Check(!fake.IsOpen && service.Snapshot.Phase == ConnectionPhase.Disconnected, "Cancel closes the port and reports disconnected.");
            Check(fake.Writes.SequenceEqual(new[] { "CQ1,HELLO\n" }), "No maintenance cancellation command sent.");
        });
        await Run("cancellation during open cleans up without a command", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var fake = new FakeDevice();
            fake.OnOpen = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
            await using var service = new DeviceConnectionService(() => fake);
            var connect = service.ConnectAsync("COM_TEST"); await started.Task;
            await service.DisconnectAsync(); await Throws<OperationCanceledException>(() => connect);
            Check(fake.Writes.IsEmpty && !fake.IsOpen && fake.CloseCount == 1, "Cancelled opening releases transport before any HELLO.");
        });
        foreach (string invalid in new[] { "CQ1,HELLO,test-firmware,UNKNOWN\n", "CQ1,HELLO,,IDLE\n", "CQ1,ERROR,DENIED\n" })
        {
            await Run("invalid/unexpected identification response", async () =>
            {
                var fake = new FakeDevice(); fake.OnWrite = _ => { fake.Feed(invalid); return Task.CompletedTask; };
                await using var service = new DeviceConnectionService(() => fake);
                await Throws<ProtocolException>(() => service.ConnectAsync("COM_TEST"));
                Check(service.Snapshot.Phase == ConnectionPhase.Error && !fake.IsOpen, "Unsupported response is not silently reinterpreted.");
                Check(fake.Writes.All(w => w == "CQ1,HELLO\n"), "Rejected response causes no mode-changing command.");
            });
        }
        await Run("binary frames and corrupt CRC share the diagnostic stream", async () =>
        {
            var fake = FakeDevice.Responding(); var messages = new ConcurrentQueue<DiagnosticMessage>();
            await using var service = new DeviceConnectionService(() => fake); service.MessageReceived += messages.Enqueue;
            await service.ConnectAsync("COM_TEST");
            var corrupt = Frame(Payload); corrupt[^1] ^= 1;
            fake.Feed(Frame(Payload, 100).Concat(corrupt).Concat(Encoding.ASCII.GetBytes("D,real-debug\n")).ToArray());
            await Eventually(() => messages.Any(m => m.Text == "D,real-debug"));
            Check(messages.Count(m => m.Kind == "Binary") == 1 && messages.Count(m => m.Kind == "Framing error") == 1, "Validated frames and CRC failures visible separately.");
            Check(messages.Where(m => m.Kind == "Debug").All(m => m.Text == "D,real-debug"), "No binary payload appears as debug.");
            Check(fake.Writes.Count == 1, "Receiving traffic never requests maintenance or transfer.");
        });
        await Run("framing failure during identification fails safely", async () =>
        {
            var fake = new FakeDevice(); fake.OnWrite = _ => { var bad = Frame(Payload); bad[^1] ^= 1; fake.Feed(bad); return Task.CompletedTask; };
            await using var service = new DeviceConnectionService(() => fake);
            await Throws<ProtocolException>(() => service.ConnectAsync("COM_TEST"));
            Check(!fake.IsOpen && service.Snapshot.Phase == ConnectionPhase.Error && fake.Writes.Count == 1, "Bad CRC cannot satisfy HELLO or trigger commands.");
        });
        await Run("unplug clears identity and reconnect drops stale fragments", async () =>
        {
            var first = FakeDevice.Responding(); var second = FakeDevice.Responding("MAINT", "reconnected-test-firmware");
            var devices = new Queue<FakeDevice>(new[] { first, second }); var messages = new ConcurrentQueue<DiagnosticMessage>();
            await using var service = new DeviceConnectionService(() => devices.Dequeue()); service.MessageReceived += messages.Enqueue;
            await service.ConnectAsync("COM_TEST"); first.Feed("D,unfinished"); first.LoseConnection("USB removed");
            await Eventually(() => service.Snapshot.Phase == ConnectionPhase.Error && !first.IsOpen);
            Check(service.Snapshot.Mode == DeviceMode.Unknown && service.Snapshot.FirmwareVersion is null, "Unplug never leaves live-looking identity.");
            await service.ConnectAsync("COM_TEST"); second.Feed("D,fresh\n"); await Eventually(() => messages.Any(m => m.Text == "D,fresh"));
            Check(!messages.Any(m => m.Text.StartsWith("D,unfinishedD,", StringComparison.Ordinal)), "Prior session's partial line cannot join fresh input.");
            Check(service.Snapshot.Mode == DeviceMode.Maintenance && service.Snapshot.FirmwareVersion == "reconnected-test-firmware", "Existing MAINT reported honestly on reconnect.");
            await service.DisconnectAsync(); Check(first.Writes.Concat(second.Writes).All(w => w == "CQ1,HELLO\n"), "Reconnect/disconnect do not enter or leave maintenance.");
        });
        await Run("EOF and port-open failures surface errors", async () =>
        {
            var fake = FakeDevice.Responding(); await using var service = new DeviceConnectionService(() => fake);
            await service.ConnectAsync("COM_TEST"); fake.LoseConnection();
            await Eventually(() => service.Snapshot.Phase == ConnectionPhase.Error && !fake.IsOpen);
            var denied = new FakeDevice { OnOpen = _ => throw new UnauthorizedAccessException("Port in use.") };
            await using var other = new DeviceConnectionService(() => denied);
            await Throws<UnauthorizedAccessException>(() => other.ConnectAsync("COM_TEST"));
            Check(other.Snapshot.Phase == ConnectionPhase.Error && denied.Writes.IsEmpty && denied.CloseCount == 1, "Open failure is visible and cleaned up.");
        });
        await Run("failed close retains port ownership", async () =>
        {
            var fake = FakeDevice.Responding(); int created = 0;
            await using var service = new DeviceConnectionService(() => { created++; return fake; }); await service.ConnectAsync("COM_TEST");
            fake.FailClose = true; await Throws<IOException>(() => service.DisconnectAsync());
            await Throws<IOException>(() => service.ConnectAsync("COM_OTHER"));
            Check(created == 1 && fake.IsOpen, "No second transport opened after close failed.");
            fake.FailClose = false; await service.DisconnectAsync(); Check(!fake.IsOpen, "Retry close succeeds.");
        });
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { _assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
        _assertions++;
    }
}
