namespace CellQ.Protocol;

public static class Crc16
{
    // CRC16-CCITT-FALSE: poly 1021, init FFFF, no reflection/final XOR.
    public static ushort Compute(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }
}
