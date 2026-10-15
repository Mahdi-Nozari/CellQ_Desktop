using System.Globalization;
using System.Text;

namespace CellQ.Protocol;

public sealed class PressureLog
{
    internal PressureLog(string? preset, long[] times, int[][] pressures, byte[] masks, long end, long interval, int missing)
    {
        PresetName = preset; Times = times; Values = pressures; Masks = masks; MissingReadings = missing;
        MinimumTimeMs = Math.Floor(times[0] / 1000d) * 1000;
        MaximumTimeMs = Math.Max(MinimumTimeMs + 1, Math.Max(end, times[^1]));
        GapThresholdMs = Math.Max(1000d, interval * 5d);
        var minima = new double[6]; var maxima = new double[6];
        for (int channel = 0; channel < 6; channel++)
        {
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            for (int index = 0; index < times.Length; index++)
                if (HasReading(index, channel)) { minimum = Math.Min(minimum, pressures[channel][index] / 10d); maximum = Math.Max(maximum, pressures[channel][index] / 10d); }
            minima[channel] = minimum; maxima[channel] = maximum;
        }
        MinimumPressures = minima; MaximumPressures = maxima;
    }
    internal long[] Times { get; }
    internal int[][] Values { get; }
    internal byte[] Masks { get; }
    public string? PresetName { get; }
    public int Count => Times.Length;
    public int MissingReadings { get; }
    public double MinimumTimeMs { get; }
    public double MaximumTimeMs { get; }
    public double GapThresholdMs { get; }
    public IReadOnlyList<double> MinimumPressures { get; }
    public IReadOnlyList<double> MaximumPressures { get; }
    public long TimeAt(int index) => Times[index];
    public bool HasReading(int index, int channel) => (Masks[index] & (1 << channel)) != 0;
    public double PressureAt(int index, int channel) => HasReading(index, channel) ? Values[channel][index] / 10d : double.NaN;
    public int LowerBound(double time)
    {
        int low = 0, high = Count;
        while (low < high) { int mid = low + (high - low) / 2; if (Times[mid] < time) low = mid + 1; else high = mid; }
        return low;
    }
    public int Nearest(double time)
    {
        int index = LowerBound(time);
        if (index == Count) index--;
        else if (index > 0 && time - Times[index - 1] <= Times[index] - time) index--;
        return Math.Abs(Times[index] - time) > GapThresholdMs ? -1 : index;
    }
}

public static class PressureLogReader
{
    public const int MaximumSamples = 5_000_000;
    private static readonly string[] Required = ["TYPE", "ELAPSED_MS", "STATE", "G1_CYCLE", "G2_CYCLE", "V1", "V2", "V3", "V4", "V5", "V6", "P1_X10_KPA", "P2_X10_KPA", "P3_X10_KPA", "P4_X10_KPA", "P5_X10_KPA", "P6_X10_KPA"];

    public static async Task<PressureLog> ReadAsync(Stream stream, CancellationToken token = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 65536, leaveOpen: true);
        var times = new List<long>();
        var values = Enumerable.Range(0, 6).Select(_ => new List<int>()).ToArray();
        var masks = new List<byte>();
        Dictionary<string, int>? columns = null;
        int typeColumn = 0, timeColumn = 1;
        int[] pressureColumns = [];
        string? preset = null; bool cellQ = false;
        long end = 0, interval = 0; int record = 0, missing = 0;
        while (await ReadRecordAsync(reader, token).ConfigureAwait(false) is { } fields)
        {
            record++; token.ThrowIfCancellationRequested();
            if (fields.Count == 0 || fields.All(string.IsNullOrEmpty)) continue;
            if (columns is null)
            {
                if (fields[0] == "CELLQ_DATA")
                {
                    if (fields.Count < 2 || fields[1] != "1") throw new InvalidDataException("This log uses an unsupported CellQ CSV version.");
                    cellQ = true;
                }
                else if (fields[0] == "PRESET_NAME" && fields.Count > 1) preset = fields[1].Trim();
                else if (fields[0] == "SAMPLE_INTERVAL_MS" && fields.Count > 1) long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out interval);
                else if (fields[0] == "TYPE")
                {
                    if (!cellQ || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count || !Required.All(fields.Contains))
                        throw new InvalidDataException("Choose a CellQ log containing time and all six pressure columns.");
                    columns = fields.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
                    typeColumn = columns["TYPE"]; timeColumn = columns["ELAPSED_MS"];
                    pressureColumns = Enumerable.Range(1, 6).Select(channel => columns[$"P{channel}_X10_KPA"]).ToArray();
                }
                continue;
            }
            if (fields.Count != columns.Count) throw new InvalidDataException($"CSV row {record} has an unexpected number of columns.");
            string type = fields[typeColumn];
            if (type is "END" or "EVENT")
            {
                if (long.TryParse(fields[timeColumn], NumberStyles.None, CultureInfo.InvariantCulture, out long eventTime) && eventTime < TimeSpan.MaxValue.TotalMilliseconds - 1000) end = Math.Max(end, eventTime);
                continue;
            }
            if (type == "DATA") continue;
            if (type != "SAMPLE") throw new InvalidDataException($"CSV row {record} contains an unsupported record type.");
            if (!long.TryParse(fields[timeColumn], NumberStyles.None, CultureInfo.InvariantCulture, out long time) || time >= TimeSpan.MaxValue.TotalMilliseconds - 1000)
                throw new InvalidDataException($"CSV row {record} has an invalid elapsed time.");
            if (times.Count > 0 && time < times[^1]) throw new InvalidDataException($"Elapsed time goes backwards at CSV row {record}. Separate recordings cannot be combined automatically.");
            if (times.Count >= MaximumSamples) throw new InvalidDataException("This log exceeds the viewer's 5,000,000-reading limit.");
            byte mask = 0;
            for (int channel = 0; channel < 6; channel++)
            {
                string text = fields[pressureColumns[channel]];
                int pressure = 0;
                if (text.Length == 0) missing++;
                else
                {
                    if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out pressure))
                        throw new InvalidDataException($"CSV row {record} has an invalid pressure for Channel {channel + 1}.");
                    mask |= (byte)(1 << channel);
                }
                values[channel].Add(pressure);
            }
            times.Add(time); masks.Add(mask);
        }
        if (columns is null || times.Count == 0 || masks.All(mask => mask == 0)) throw new InvalidDataException("This file has no CellQ pressure readings to graph.");
        if (interval <= 0)
            interval = times.Take(101).Zip(times.Skip(1).Take(100), (first, second) => second - first).Where(delta => delta > 0).DefaultIfEmpty(20).Min();
        return new(preset, times.ToArray(), values.Select(channel => channel.ToArray()).ToArray(), masks.ToArray(), end, interval, missing);
    }

    private static async Task<List<string>?> ReadRecordAsync(StreamReader reader, CancellationToken token)
    {
        string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null) return null;
        if (!line.Contains('"'))
        {
            if (line.Length > 16384) throw new InvalidDataException("A CSV row is too long to display safely.");
            return line.Split(',').ToList();
        }
        var record = new StringBuilder(line);
        bool quoted = OpenQuote(line, false);
        while (quoted)
        {
            if (record.Length > 16384) throw new InvalidDataException("A CSV row is too long to display safely.");
            line = await reader.ReadLineAsync(token).ConfigureAwait(false) ?? throw new InvalidDataException("The CSV ends inside a quoted field.");
            record.Append('\n').Append(line); quoted = OpenQuote(line, quoted);
        }
        if (record.Length > 16384) throw new InvalidDataException("A CSV row is too long to display safely.");
        var result = new List<string>(); var field = new StringBuilder(); string text = record.ToString(); int index = 0;
        while (true)
        {
            field.Clear();
            if (index < text.Length && text[index] == '"')
            {
                index++; bool closed = false;
                while (index < text.Length)
                {
                    char value = text[index++];
                    if (value != '"') { field.Append(value); continue; }
                    if (index < text.Length && text[index] == '"') { index++; field.Append('"'); continue; }
                    closed = true; break;
                }
                if (!closed || index < text.Length && text[index] != ',') throw new InvalidDataException("The CSV contains a malformed quoted field.");
            }
            else while (index < text.Length && text[index] != ',') { if (text[index] == '"') throw new InvalidDataException("The CSV contains a misplaced quote."); field.Append(text[index++]); }
            result.Add(field.ToString()); if (index == text.Length) return result; index++;
        }
    }
    private static bool OpenQuote(string text, bool quoted)
    {
        for (int index = 0; index < text.Length; index++)
            if (text[index] == '"') { if (quoted && index + 1 < text.Length && text[index + 1] == '"') index++; else quoted = !quoted; }
        return quoted;
    }
}
