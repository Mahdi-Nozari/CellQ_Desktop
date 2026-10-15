using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CellQ.Desktop.Infrastructure;
using CellQ.Desktop.Services;
using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

public sealed class GraphChannel(int index) : ObservableObject
{
    // Straight borders sampled from squareWaveMonitor.png at y=200, x=19/148/277/407/536/665.
    private static readonly string[] Colors = ["#000088", "#008000", "#C80000", "#008080", "#800080", "#F84400"];
    private bool _visible = true;
    private Brush _brush = Brushes.Transparent;
    private string _reading = "—";
    public int Index { get; } = index;
    public string Name => "Channel " + (Index + 1);
    public bool IsVisible { get => _visible; set => Set(ref _visible, value); }
    public Brush Brush { get => _brush; private set => Set(ref _brush, value); }
    public string Reading { get => _reading; internal set => Set(ref _reading, value); }
    internal void UpdateTheme(bool dark)
    {
        var color = (Color)ColorConverter.ConvertFromString(Colors[Index]);
        if (dark) color = Color.FromRgb((byte)(color.R + (255 - color.R) * .55), (byte)(color.G + (255 - color.G) * .55), (byte)(color.B + (255 - color.B) * .55));
        var brush = new SolidColorBrush(color); brush.Freeze(); Brush = brush;
    }
}

public sealed class LogGraphViewModel : ObservableObject, IDisposable
{
    private readonly GraphSource _source;
    private readonly CancellationTokenSource _loading = new();
    private bool _disposed;
    private double _detailSpan = 30000;
    private Task? _load;
    private string _spanText = "00:00:30", _jumpText = "00:00:00", _message = "Loading pressure readings…", _reading = "Move over the graph to read pressures.";
    public LogGraphViewModel(GraphSource source)
    {
        _source = source;
        Channels = Enumerable.Range(0, 6).Select(index => new GraphChannel(index)).ToArray();
        foreach (var channel in Channels) { channel.UpdateTheme(ThemeService.IsDark); channel.PropertyChanged += OnChannel; }
        ThemeService.ThemeChanged += OnTheme;
        ApplySpanCommand = new(_ => ApplySpan(), _ => HasData);
        JumpCommand = new(_ => Jump(), _ => HasData);
        ShowAllCommand = new(_ => { Viewport!.ShowAll(); ReadyMessage(); Changed(); }, _ => HasData);
        EarlierCommand = new(_ => PanTo(Start - Span / 2), _ => HasData && Start > Viewport!.Minimum);
        LaterCommand = new(_ => PanTo(Start + Span / 2), _ => HasData && Start < Viewport!.MaximumStart);
    }
    public PressureLog? Data { get; private set; }
    public GraphViewport? Viewport { get; private set; }
    public IReadOnlyList<GraphChannel> Channels { get; }
    public bool HasData => Data is not null;
    public bool IsLoading => !HasData && _message.StartsWith("Loading", StringComparison.Ordinal);
    public string Title => Data?.PresetName is { Length: > 0 } name ? name : "Pressure graph";
    public string SourceName => _source.Name;
    public string SourceKind => "Saved log";
    public string SourceNotice => "Local CSV · readings shown from this file";
    public string Summary => Data is null ? "" : $"{Data.Count:N0} readings · Recorded time {GraphViewport.FormatTime(Data.MinimumTimeMs)} – {GraphViewport.FormatTime(Data.MaximumTimeMs)}";
    public string StatusMessage => _message;
    public string ReadingTime => _reading;
    public string MissingNotice => Data?.MissingReadings > 0 ? "Some pressure readings are missing. Gaps are shown." : "";
    public string SpanText { get => _spanText; set => Set(ref _spanText, value); }
    public string JumpText { get => _jumpText; set => Set(ref _jumpText, value); }
    public double Span => Viewport?.Span ?? 1;
    public double Start { get => Viewport?.Start ?? 0; set => PanTo(value); }
    public double MinimumStart => Viewport?.Minimum ?? 0;
    public double MaximumStart => Viewport?.MaximumStart ?? 0;
    public double ZoomPosition { get => Viewport?.ZoomPosition ?? 0; set { if (Viewport is not null && Math.Abs(value - Viewport.ZoomPosition) > .000001) { Viewport.SetZoom(value); ReadyMessage(); Changed(); } } }
    public string ViewRange => Viewport is null ? "" : $"Showing {GraphViewport.FormatTime(Viewport.Start)} – {GraphViewport.FormatTime(Viewport.End)}";
    public double? CursorTime { get; private set; }
    public RelayCommand ApplySpanCommand { get; }
    public RelayCommand JumpCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand EarlierCommand { get; }
    public RelayCommand LaterCommand { get; }
    public event Action? ViewChanged;
    public event Action? CursorChanged;
    public Task LoadAsync() => _load ??= LoadCoreAsync();
    private async Task LoadCoreAsync()
    {
        var token = _loading.Token;
        try
        {
            var data = await Task.Run(async () => { await using var file = _source.Open(); return await PressureLogReader.ReadAsync(file, token).ConfigureAwait(false); }, token);
            if (_disposed) return;
            Data = data; Viewport = new(data.MinimumTimeMs, data.MaximumTimeMs);
            JumpText = GraphViewport.FormatTime(data.MinimumTimeMs); ReadyMessage(); Changed();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) { _message = "Could not show this log. " + error.Message; Raise(""); } }
    }
    public void PanTo(double time)
    {
        if (Viewport is null) return;
        double previous = Viewport.Start; Viewport.PanTo(time);
        if (Viewport.Start != previous) { ReadyMessage(); Changed(); }
    }
    public void ZoomAt(double fraction, double factor)
    {
        if (Viewport is null) return;
        Viewport.SetSpan(Viewport.Span * factor, fraction); ReadyMessage(); Changed();
    }
    private void ApplySpan()
    {
        if (!GraphViewport.TryTime(SpanText, out double span) || span <= 0) { _message = "Enter a time greater than zero: seconds (30) or hours:minutes:seconds (00:00:30)."; Raise(nameof(StatusMessage)); return; }
        Viewport!.SetSpan(span); ReadyMessage(); Changed();
    }
    private void Jump()
    {
        if (!GraphViewport.TryTime(JumpText, out double time) || time < Viewport!.Minimum || time > Viewport.Maximum)
        { _message = "Enter a time within the recorded range shown above."; Raise(nameof(StatusMessage)); return; }
        // A whole-recording view cannot pan. Restore detail so Go actually locates the requested moment.
        if (Span >= Viewport.Range && Viewport.Range > Viewport.MinimumSpan)
            Viewport.SetSpan(Math.Min(_detailSpan, Viewport.Range / 3));
        Viewport.JumpTo(time); JumpText = GraphViewport.FormatTime(time); ReadyMessage(); Changed(); SetCursor(time);
    }
    public void SetCursor(double time)
    {
        if (Data is null || Viewport is null) return;
        CursorTime = Math.Clamp(time, Viewport.Start, Viewport.End);
        int index = Data.Nearest(CursorTime.Value);
        _reading = index < 0 ? "No readings near this time." : "Reading at " + GraphViewport.FormatTime(Data.TimeAt(index));
        foreach (var channel in Channels)
            channel.Reading = index < 0 || !Data.HasReading(index, channel.Index) ? "—" : Data.PressureAt(index, channel.Index).ToString("0.0", CultureInfo.CurrentCulture) + " kPa";
        Raise(nameof(ReadingTime)); CursorChanged?.Invoke();
    }
    private void ReadyMessage() => _message = "Drag left or right to move through time. Mouse wheel zooms. Arrow keys move earlier/later.";
    private void Changed()
    {
        if (Viewport is not null && Span < Viewport.Range) _detailSpan = Span;
        SpanText = GraphViewport.FormatTime(Span); Raise("");
        ApplySpanCommand.Notify(); JumpCommand.Notify(); ShowAllCommand.Notify(); EarlierCommand.Notify(); LaterCommand.Notify();
        ViewChanged?.Invoke();
    }
    private void OnChannel(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(GraphChannel.IsVisible) or nameof(GraphChannel.Brush)) ViewChanged?.Invoke(); }
    private void OnTheme(bool dark) { foreach (var channel in Channels) channel.UpdateTheme(dark); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ThemeService.ThemeChanged -= OnTheme;
        foreach (var channel in Channels) channel.PropertyChanged -= OnChannel;
        _loading.Cancel();
        if (_load is null || _load.IsCompleted) _loading.Dispose(); else _ = _load.ContinueWith(_ => _loading.Dispose(), TaskScheduler.Default);
    }
}
