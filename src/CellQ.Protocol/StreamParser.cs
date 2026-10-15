using System.Buffers.Binary;
using System.Text;

namespace CellQ.Protocol;

/// <summary>One incremental parser for ASCII lines and CQ1 binary frames. Not thread safe; owned by one reader.</summary>
public sealed class StreamParser(int maximumLineLength = 8192)
{
    private readonly List<byte> _line = [];
    private readonly List<byte> _frame = [];
    private bool _discardLine;

    public IReadOnlyList<ProtocolMessage> Feed(ReadOnlySpan<byte> bytes)
    {
        var result = new List<ProtocolMessage>();
        foreach (var value in bytes) Accept(value, result);
        return result;
    }

    private void Accept(byte value, List<ProtocolMessage> result)
    {
        if (_frame.Count > 0)
        {
            if (_frame.Count == 1 && value != 0x5A)
            {
                _frame.Clear();
                result.Add(new FramingError(FramingErrorKind.InvalidMarker, "A5 marker was not followed by 5A."));
                Accept(value, result);
                return;
            }
            _frame.Add(value);
            if (_frame.Count == 4 && (_frame[3] < 1 || _frame[3] > 48))
            {
                result.Add(new FramingError(FramingErrorKind.InvalidHeader, "File data payload length must be 1–48 bytes."));
                _frame.Clear();
                _discardLine = true; // Unknown frame size: recover at LF or the next binary marker.
                return;
            }
            if (_frame.Count >= 4 && _frame.Count == 10 + _frame[3])
            {
                var frame = _frame.ToArray();
                _frame.Clear();
                if (frame[2] != 0x01) result.Add(new FramingError(FramingErrorKind.InvalidHeader, "Unsupported binary frame type; expected 01."));
                else if (Crc16.Compute(frame.AsSpan(0, frame.Length - 2)) != BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(frame.Length - 2)))
                    result.Add(new FramingError(FramingErrorKind.CrcMismatch, "Binary frame CRC16 mismatch; payload discarded."));
                else result.Add(new DataFrame(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)), frame.AsSpan(8, frame[3]).ToArray()));
            }
            return;
        }
        if (value == 0xA5)
        {
            if (_line.Count != 0) result.Add(new FramingError(FramingErrorKind.InvalidText, "Binary marker interrupted an incomplete ASCII line."));
            _line.Clear();
            _discardLine = false;
            _frame.Add(value);
            return;
        }
        if (_discardLine) { if (value == (byte)'\n') _discardLine = false; return; }
        if (value == (byte)'\n')
        {
            if (_line.Count > 0 && _line[^1] == (byte)'\r') _line.RemoveAt(_line.Count - 1);
            var text = Encoding.ASCII.GetString(_line.ToArray());
            _line.Clear();
            if (text.Contains('\r')) result.Add(new FramingError(FramingErrorKind.InvalidText, "Unexpected carriage return inside an ASCII line."));
            else if (text.StartsWith("D,", StringComparison.Ordinal)) result.Add(new DebugLine(text));
            else if (text.StartsWith("CQ1,", StringComparison.Ordinal)) result.Add(new ResponseLine(text));
            else if (text.Length > 0) result.Add(new OtherLine(text));
            return;
        }
        if (value > 127 || (value < 32 && value != (byte)'\r' && value != (byte)'\t'))
        {
            _line.Clear();
            _discardLine = true;
            result.Add(new FramingError(FramingErrorKind.InvalidText, "Non-ASCII byte outside a binary frame."));
            return;
        }
        _line.Add(value);
        if (_line.Count > maximumLineLength)
        {
            _line.Clear();
            _discardLine = true;
            result.Add(new FramingError(FramingErrorKind.LineTooLong, $"ASCII line exceeded the host buffer limit ({maximumLineLength} bytes)."));
        }
    }

    public IReadOnlyList<ProtocolMessage> Complete()
    {
        bool incomplete = _frame.Count > 0 || _line.Count > 0 || _discardLine;
        _frame.Clear(); _line.Clear(); _discardLine = false;
        return incomplete ? [new FramingError(FramingErrorKind.Truncated, "Connection ended with an incomplete line or binary frame.")] : [];
    }
}
