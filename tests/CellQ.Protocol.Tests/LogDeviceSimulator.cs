using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CellQ.Protocol;

namespace CellQ.Testing;

/// <summary>Test-only protocol v1 card/maintenance model, shared across transport reconnects.</summary>
public sealed class LogDeviceSimulator
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public HashSet<string> EmptyFolders { get; } = [];
    public ConcurrentQueue<string> Commands { get; } = new();
    public string Mode { get; set; } = "IDLE";
    public string FirmwareVersion { get; set; } = "simulator-only";
    public bool DropFwPrep { get; set; }
    public bool DenyFwPrep { get; set; }
    public bool FirmwarePrepared { get; private set; }
    public Func<Task>? BeforeFwPrepReply { get; set; }
    public bool ManualValveActive { get; set; }
    public bool DenyEnter { get; set; }
    public bool BeginTestOnEnter { get; set; }
    public bool DropExit { get; set; }
    public bool BadFinalHash { get; set; }
    public bool MinimalCrc { get; set; }
    public Func<Task>? BeforeEnterReply { get; set; }
    public int CorruptFrames { get; set; }
    public int WrongOffsets { get; set; }
    public int BadStreamCrc { get; set; }
    public int BadEofSize { get; set; }
    public int WrongGetOffset { get; set; }
    public int DropNext { get; set; }
    public int DisconnectAtNext { get; set; }
    public bool TruncateOnDisconnect { get; set; }
    public string? ListingOverride { get; set; }
    public Func<int, Task>? BeforeNext { get; set; }
    public Action? BeforeDelete { get; set; }
    public int NextCount { get; private set; }
    public int HashCount { get; private set; }
    private string? _path;
    private uint _offset, _getStart;

    public FakeDevice CreateTransport()
    {
        var device = new FakeDevice();
        device.OnWrite = command => HandleAsync(device, command.TrimEnd('\n'));
        return device;
    }
    private static void Reply(FakeDevice device, string text) => Feed(device, Encoding.ASCII.GetBytes("D,simulator-only\n" + text + "\n"));
    private static void Feed(FakeDevice device, byte[] data)
    {
        for (int offset = 0; offset < data.Length; offset += 5) device.Feed(data.AsSpan(offset, Math.Min(5, data.Length - offset)).ToArray());
    }
    private async Task HandleAsync(FakeDevice device, string command)
    {
        Commands.Enqueue(command); var fields = command.Split(',');
        if (command == "CQ1,HELLO") { Reply(device, "CQ1,HELLO," + FirmwareVersion + "," + Mode); return; }
        if (command == "CQ1,ENTER")
        {
            if (BeginTestOnEnter) Mode = "TEST";
            if (Mode == "TEST" || ManualValveActive || DenyEnter) Reply(device, "CQ1,ERR,SIMULATED_DENIAL");
            else { Mode = "MAINT"; if (BeforeEnterReply is not null) await BeforeEnterReply(); Reply(device, "CQ1,OK,ENTER"); }
            return;
        }
        if (Mode != "MAINT") { Reply(device, "CQ1,ERR,SIMULATED_NOT_MAINT"); return; }
        switch (fields[1])
        {
            case "CANCEL": _path = null; Reply(device, "CQ1,OK,CANCEL"); return;
            case "EXIT": if (DropExit) return; Mode = "IDLE"; FirmwarePrepared = false; _path = null; Reply(device, "CQ1,OK,EXIT"); return;
            case "FWPREP":
                if (DropFwPrep) return;
                if (DenyFwPrep) { Reply(device, "CQ1,ERR,SIMULATED_PREP_DENIAL"); return; }
                FirmwarePrepared = true;
                if (BeforeFwPrepReply is not null) await BeforeFwPrepReply();
                Reply(device, "CQ1,OK,FWPREP"); return;
            case "LIST":
                if (ListingOverride is not null) { Reply(device, ListingOverride); return; }
                var rows = new List<string> { "CQ1,LIST,BEGIN" };
                if (fields[2] == "/DATA")
                {
                    foreach (var folder in Files.Keys.Select(path => path.Split('/')[2]).Concat(EmptyFolders).Distinct().Order()) rows.Add($"CQ1,ITEM,{folder},D,0");
                }
                else
                {
                    foreach (var pair in Files.Where(pair => pair.Key.StartsWith(fields[2] + "/", StringComparison.Ordinal))) rows.Add($"CQ1,ITEM,{pair.Key.Split('/')[3]},F,{pair.Value.Length}");
                }
                rows.Add("CQ1,LIST,END"); Reply(device, string.Join('\n', rows)); return;
            case "HASH":
                HashCount++;
                if (!Files.TryGetValue(fields[2], out var hashBytes)) { Reply(device, "CQ1,ERR,SIMULATED_MISSING"); return; }
                uint hash = Crc32.Compute(hashBytes); if (BadFinalHash && HashCount % 2 == 0) hash ^= 1;
                Reply(device, $"CQ1,OK,HASHING\nCQ1,HASH,{hashBytes.Length},{hash.ToString(MinimalCrc ? "X" : "X8")}"); return;
            case "GET":
                _path = fields[2]; _offset = _getStart = uint.Parse(fields[3], CultureInfo.InvariantCulture);
                uint echo = _offset; if (WrongGetOffset > 0) { WrongGetOffset--; echo++; }
                Reply(device, $"CQ1,GET,{Files[_path].Length},{echo}"); return;
            case "NEXT":
                NextCount++; if (BeforeNext is not null) await BeforeNext(NextCount);
                if (_path is null) { Reply(device, "CQ1,ERR,SIMULATED_NO_GET"); return; }
                byte[] data = Files[_path];
                if (_offset == data.Length)
                {
                    uint crc = Crc32.Compute(data.AsSpan((int)_getStart)); if (BadStreamCrc > 0) { BadStreamCrc--; crc ^= 1; }
                    int size = data.Length; if (BadEofSize > 0) { BadEofSize--; size++; }
                    Reply(device, $"CQ1,EOF,{size},{crc.ToString(MinimalCrc ? "X" : "X8")}"); return;
                }
                var payload = data.AsSpan((int)_offset, Math.Min(48, data.Length - (int)_offset)).ToArray();
                uint frameOffset = _offset; if (WrongOffsets > 0) { WrongOffsets--; frameOffset++; }
                var frame = Frame(payload, frameOffset); _offset += (uint)payload.Length;
                if (CorruptFrames > 0) { CorruptFrames--; frame[^1] ^= 1; }
                if (DisconnectAtNext == NextCount)
                {
                    if (TruncateOnDisconnect) Feed(device, frame[..6]);
                    device.LoseConnection("Simulated USB removal"); return;
                }
                if (DropNext > 0) { DropNext--; return; }
                Feed(device, Encoding.ASCII.GetBytes("D,simulated-transfer\n").Concat(frame).ToArray()); return;
            case "DELETE":
                BeforeDelete?.Invoke();
                if (Files.TryGetValue(fields[2], out var bytes) && bytes.Length == uint.Parse(fields[3], CultureInfo.InvariantCulture) && Crc32.Compute(bytes).ToString("X8") == fields[4])
                { Reply(device, "CQ1,OK,CHECKING\nCQ1,OK,DELETE"); Files.Remove(fields[2]); }
                else Reply(device, "CQ1,OK,CHECKING\nCQ1,ERR,SIMULATED_CHANGED");
                return;
            default: throw new InvalidOperationException("Simulator received unsupported command: " + command);
        }
    }
    public static byte[] Frame(byte[] payload, uint offset)
    {
        var frame = new byte[10 + payload.Length]; frame[0] = 0xA5; frame[1] = 0x5A; frame[2] = 1; frame[3] = (byte)payload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), offset); payload.CopyTo(frame, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Crc16.Compute(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }
}
