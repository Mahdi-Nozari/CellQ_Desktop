using System.Globalization;

namespace CellQ.Protocol;

public sealed class GraphViewport
{
    public GraphViewport(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum < 0 || maximum < minimum) throw new ArgumentOutOfRangeException(nameof(minimum));
        Minimum = minimum; Maximum = Math.Max(minimum + 1, maximum); Span = Math.Min(30000, Range); Start = minimum;
    }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Range => Maximum - Minimum;
    public double MinimumSpan => Math.Min(1000, Range);
    public double Span { get; private set; }
    public double Start { get; private set; }
    public double End => Start + Span;
    public double MaximumStart => Maximum - Span;
    public double ZoomPosition => Range <= MinimumSpan ? 0 : 100 * Math.Log(Range / Span) / Math.Log(Range / MinimumSpan);
    public void PanTo(double start) { if (double.IsFinite(start)) Start = Math.Clamp(start, Minimum, MaximumStart); }
    public void SetSpan(double span, double anchorFraction = .5)
    {
        if (!double.IsFinite(span) || span <= 0) throw new ArgumentOutOfRangeException(nameof(span));
        double anchor = Start + Math.Clamp(anchorFraction, 0, 1) * Span;
        Span = Math.Clamp(span, MinimumSpan, Range);
        PanTo(anchor - Math.Clamp(anchorFraction, 0, 1) * Span);
    }
    public void SetZoom(double position) => SetSpan(Range * Math.Pow(MinimumSpan / Range, Math.Clamp(position, 0, 100) / 100));
    public void JumpTo(double time)
    {
        if (!double.IsFinite(time) || time < Minimum || time > Maximum) throw new ArgumentOutOfRangeException(nameof(time));
        PanTo(time - Span / 2);
    }
    public void ShowAll() { Span = Range; Start = Minimum; }

    public static string FormatTime(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        string seconds = time.Seconds.ToString("00", CultureInfo.InvariantCulture) + (time.Milliseconds == 0 ? "" : "." + time.Milliseconds.ToString("000", CultureInfo.InvariantCulture));
        return ((long)time.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + time.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + seconds;
    }
    public static bool TryTime(string text, out double milliseconds)
    {
        milliseconds = 0; string[] parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        double seconds = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && !double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out value)) return false;
            if (!double.IsFinite(value) || value < 0 || index > 0 && value >= 60 || index < parts.Length - 1 && value % 1 != 0) return false;
            seconds = seconds * 60 + value;
        }
        milliseconds = seconds * 1000;
        return double.IsFinite(milliseconds) && milliseconds <= TimeSpan.MaxValue.TotalMilliseconds;
    }
}
