namespace CellQ.Protocol;

/// <summary>Protocol v1 reflected CRC32, polynomial EDB88320, initial/final XOR FFFFFFFF.</summary>
public sealed class Crc32
{
    private uint _state = uint.MaxValue;
    public uint Value => _state ^ uint.MaxValue;
    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            _state ^= value;
            for (int bit = 0; bit < 8; bit++) _state = (_state >> 1) ^ ((_state & 1) == 0 ? 0u : 0xEDB88320u);
        }
    }
    public static uint Compute(ReadOnlySpan<byte> bytes) { var crc = new Crc32(); crc.Append(bytes); return crc.Value; }
}
