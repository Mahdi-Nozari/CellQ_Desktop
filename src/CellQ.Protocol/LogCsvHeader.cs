using System.Text;

namespace CellQ.Protocol;

/// <summary>Bounded reader for the CellQ CSV header. Metadata does not certify a complete local copy.</summary>
public static class LogCsvHeader
{
    public const int MaximumBytes = 8192;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // True means the header is complete, absent, malformed or over the bound.
    // False means more bytes are needed, including when a UTF-8 character or quoted value is split.
    public static bool TryReadPresetName(ReadOnlySpan<byte> bytes, bool endOfFile, out string? presetName)
    {
        presetName = null;
        if (bytes.Length > MaximumBytes) { bytes = bytes[..MaximumBytes]; endOfFile = false; }
        int start = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        int row = 0;
        bool quoted = false, cellQ = false;
        for (int index = start; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            if (value == '"')
            {
                if (quoted && index + 1 < bytes.Length && bytes[index + 1] == '"') index++;
                else quoted = !quoted;
            }
            if (quoted || value != '\r' && value != '\n') continue;
            row++;
            if (row == 1) cellQ = Fields(bytes[start..index]) is { Count: > 0 } first && first[0] == "CELLQ_DATA";
            if (row == 7)
            {
                presetName = cellQ ? Preset(bytes[start..index]) : null;
                return true;
            }
            if (value == '\r' && index + 1 < bytes.Length && bytes[index + 1] == '\n') index++;
            start = index + 1;
        }
        if (endOfFile)
        {
            if (!quoted && row == 6 && start < bytes.Length && cellQ) presetName = Preset(bytes[start..]);
            return true;
        }
        return bytes.Length >= MaximumBytes;
    }

    private static string? Preset(ReadOnlySpan<byte> row)
    {
        var fields = Fields(row);
        return fields is { Count: >= 2 } && fields[0] == "PRESET_NAME" && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1].Trim() : null;
    }

    private static List<string>? Fields(ReadOnlySpan<byte> row)
    {
        string text;
        try { text = Utf8.GetString(row); } catch (DecoderFallbackException) { return null; }
        var fields = new List<string>();
        int index = 0;
        while (true)
        {
            var field = new StringBuilder();
            if (index < text.Length && text[index] == '"')
            {
                index++;
                bool closed = false;
                while (index < text.Length)
                {
                    char value = text[index++];
                    if (value != '"') { field.Append(value); continue; }
                    if (index < text.Length && text[index] == '"') { index++; field.Append('"'); continue; }
                    closed = true; break;
                }
                if (!closed || index < text.Length && text[index] != ',') return null;
            }
            else
            {
                while (index < text.Length && text[index] != ',')
                {
                    if (text[index] == '"') return null;
                    field.Append(text[index++]);
                }
            }
            fields.Add(field.ToString());
            if (index == text.Length) return fields;
            index++; // Comma; another iteration preserves a final empty column.
        }
    }

    public static async Task<string?> ReadPresetNameAsync(Stream stream, CancellationToken token = default)
    {
        long? previous = stream.CanSeek ? stream.Position : null;
        if (previous is not null) stream.Position = 0;
        try
        {
            var bytes = new byte[MaximumBytes];
            int count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (TryReadPresetName(bytes.AsSpan(0, count), false, out var name)) return name;
                int read = await stream.ReadAsync(bytes.AsMemory(count, Math.Min(512, bytes.Length - count)), token).ConfigureAwait(false);
                if (read == 0) { TryReadPresetName(bytes.AsSpan(0, count), true, out name); return name; }
                count += read;
            }
        }
        finally { if (previous is not null) stream.Position = previous.Value; }
    }
}
