namespace CellQ.Protocol;

public readonly record struct PressurePoint(double TimeMs, double Kpa, bool BreakBefore);

/// <summary>Preserves first/min/max/last readings in time order per screen column; never invents zeros across gaps.</summary>
public static class PressureTraceReducer
{
    public static IReadOnlyList<PressurePoint> Reduce(PressureLog log, int channel, double start, double end, int pixels, CancellationToken token = default)
    {
        if (channel is < 0 or > 5 || pixels < 1 || !double.IsFinite(start) || !double.IsFinite(end) || end <= start) throw new ArgumentOutOfRangeException(nameof(channel));
        int lower = Math.Max(0, log.LowerBound(start) - 1), upper = Math.Min(log.Count, log.LowerBound(end) + 1);
        var result = new List<PressurePoint>(Math.Min(log.Count, pixels * 4));
        int bucket = -1, first = -1, last = -1, min = -1, max = -1;
        bool gap = true, bucketGap = false, firstGap = true;
        for (int index = lower; index < upper; index++)
        {
            if ((index & 2047) == 0) token.ThrowIfCancellationRequested();
            double position = Math.Clamp((log.TimeAt(index) - start) / (end - start), 0, 1);
            int nextBucket = Math.Min((int)Math.Floor(position * pixels), pixels - 1);
            if (nextBucket != bucket) { Flush(); bucket = nextBucket; first = last = min = max = -1; bucketGap = false; firstGap = gap; }
            if (index > lower && log.TimeAt(index) - log.TimeAt(index - 1) > log.GapThresholdMs) { gap = true; bucketGap = true; }
            if (!log.HasReading(index, channel)) { gap = true; bucketGap = true; continue; }
            if (first < 0) { first = min = max = index; firstGap = gap; }
            last = index;
            if (log.PressureAt(index, channel) < log.PressureAt(min, channel)) min = index;
            if (log.PressureAt(index, channel) > log.PressureAt(max, channel)) max = index;
            gap = false;
        }
        Flush(); token.ThrowIfCancellationRequested(); return result;

        void Flush()
        {
            if (first < 0) return;
            bool firstPoint = true;
            foreach (int index in new[] { first, min, max, last }.Distinct().Order())
            {
                result.Add(new(log.TimeAt(index), log.PressureAt(index, channel), bucketGap || firstPoint && firstGap)); firstPoint = false;
            }
        }
    }
}
