using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;

namespace CellQ.Desktop.Controls;

public sealed class PressurePlot : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(LogGraphViewModel), typeof(PressurePlot), new FrameworkPropertyMetadata(null, OnModel));
    public static readonly DependencyProperty TextBrushProperty = BrushProperty(nameof(TextBrush), Brushes.Black);
    public static readonly DependencyProperty SurfaceBrushProperty = BrushProperty(nameof(SurfaceBrush), Brushes.White);
    public static readonly DependencyProperty GridBrushProperty = BrushProperty(nameof(GridBrush), Brushes.Gray);
    public static readonly DependencyProperty SecondaryBrushProperty = BrushProperty(nameof(SecondaryBrush), Brushes.Gray);
    private static DependencyProperty BrushProperty(string name, Brush value) => DependencyProperty.Register(name, typeof(Brush), typeof(PressurePlot), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
    public LogGraphViewModel? Model { get => (LogGraphViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush SurfaceBrush { get => (Brush)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush SecondaryBrush { get => (Brush)GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _rendering;
    private int _revision;
    private bool _dirty, _stopped, _dragging;
    private double _dragX, _dragStart;
    private Frame? _frame;
    private sealed record Frame(StreamGeometry[] Traces, double Start, double End, double Min, double Max, double Step, bool HasChannels, double Width, double Height);
    public PressurePlot()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => RenderPending(), Dispatcher);
        _timer.Start(); SizeChanged += (_, _) => RequestRender();
        Cursor = Cursors.Hand;
        Unloaded += (_, _) => { _timer.Stop(); _rendering?.Cancel(); };
        Loaded += (_, _) => { if (!_stopped) { _timer.Start(); RequestRender(); } };
    }
    internal bool HasFrame => _frame is not null;
    private Rect PlotBounds => new(64, 30, Math.Max(1, ActualWidth - 84), Math.Max(1, ActualHeight - 82));
    private static void OnModel(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var plot = (PressurePlot)sender;
        if (e.OldValue is LogGraphViewModel old) { old.ViewChanged -= plot.RequestRender; old.CursorChanged -= plot.InvalidateVisual; }
        if (e.NewValue is LogGraphViewModel current) { current.ViewChanged += plot.RequestRender; current.CursorChanged += plot.InvalidateVisual; }
        plot.RequestRender();
    }
    private void RequestRender()
    {
        if (_stopped) return;
        _revision++; _dirty = true; _frame = null; _rendering?.Cancel(); InvalidateVisual();
    }
    private async void RenderPending()
    {
        if (!_dirty || _stopped || Model?.Data is not { } data || Model.Viewport is not { } viewport || ActualWidth < 100 || ActualHeight < 100) return;
        _dirty = false;
        int revision = _revision;
        double width = ActualWidth, height = ActualHeight, start = viewport.Start, end = viewport.End;
        Rect bounds = PlotBounds;
        bool[] visible = Model.Channels.Select(channel => channel.IsVisible).ToArray();
        int pixels = Math.Clamp((int)(bounds.Width * VisualTreeHelper.GetDpi(this).DpiScaleX), 1, 10000);
        var cancellation = new CancellationTokenSource(); _rendering = cancellation;
        try
        {
            var frame = await Task.Run(() => Build(data, visible, start, end, pixels, bounds, width, height, cancellation.Token));
            if (!_stopped && revision == _revision && !cancellation.IsCancellationRequested) { _frame = frame; InvalidateVisual(); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_rendering, cancellation)) _rendering = null; cancellation.Dispose(); }
    }
    private static Frame Build(PressureLog data, bool[] visible, double start, double end, int pixels, Rect bounds, double width, double height, CancellationToken token)
    {
        double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        for (int channel = 0; channel < 6; channel++)
            if (visible[channel] && double.IsFinite(data.MinimumPressures[channel])) { minimum = Math.Min(minimum, data.MinimumPressures[channel]); maximum = Math.Max(maximum, data.MaximumPressures[channel]); }
        bool any = double.IsFinite(minimum);
        if (!any) { minimum = -1; maximum = 1; }
        double padding = Math.Max(1, (maximum - minimum) * .05);
        double step = NiceStep((maximum - minimum + 2 * padding) / 6);
        minimum = Math.Floor((minimum - padding) / step) * step;
        maximum = Math.Ceiling((maximum + padding) / step) * step;
        var traces = new StreamGeometry[6];
        for (int channel = 0; channel < 6; channel++)
        {
            token.ThrowIfCancellationRequested();
            var points = visible[channel] ? PressureTraceReducer.Reduce(data, channel, start, end, pixels, token) : [];
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
                for (int index = 0; index < points.Count; index++)
                {
                    var sample = points[index];
                    var point = new Point(bounds.Left + (sample.TimeMs - start) / (end - start) * bounds.Width, bounds.Bottom - (sample.Kpa - minimum) / (maximum - minimum) * bounds.Height);
                    bool begins = index == 0 || sample.BreakBefore;
                    bool isolated = begins && (index == points.Count - 1 || points[index + 1].BreakBefore);
                    if (isolated)
                    {
                        drawing.BeginFigure(new(point.X - 1.8, point.Y), true, true);
                        drawing.LineTo(new(point.X, point.Y - 1.8), true, false); drawing.LineTo(new(point.X + 1.8, point.Y), true, false); drawing.LineTo(new(point.X, point.Y + 1.8), true, false);
                    }
                    else if (begins) drawing.BeginFigure(point, false, false);
                    else drawing.LineTo(point, true, false);
                }
            geometry.Freeze(); traces[channel] = geometry;
        }
        return new(traces, start, end, minimum, maximum, step, any, width, height);
    }
    private static double NiceStep(double value)
    {
        double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(.0001, value))));
        double fraction = value / power;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(SurfaceBrush, null, new Rect(RenderSize));
        var model = Model;
        var frame = _frame;
        if (model?.Data is null) { Label(drawing, model?.IsLoading == true ? "Loading pressure readings…" : "No readings to show.", new(80, 60), TextBrush, 14); return; }
        if (frame is null || frame.Width != ActualWidth || frame.Height != ActualHeight) { Label(drawing, "Drawing graph…", new(80, 60), SecondaryBrush, 13); return; }
        Rect bounds = PlotBounds;
        Label(drawing, "Pressure (kPa)", new(7, 3), TextBrush, 12);
        var grid = new Pen(GridBrush, .5);
        for (double value = frame.Min; value <= frame.Max + frame.Step / 100; value += frame.Step)
        {
            double y = bounds.Bottom - (value - frame.Min) / (frame.Max - frame.Min) * bounds.Height;
            drawing.DrawLine(value == 0 ? new Pen(SecondaryBrush, 1) : grid, new(bounds.Left, y), new(bounds.Right, y));
            Label(drawing, value.ToString("0.#", CultureInfo.CurrentCulture), new(5, y - 7), SecondaryBrush, 11);
        }
        double timeStep = TimeStep((frame.End - frame.Start) / 6);
        for (double time = Math.Ceiling(frame.Start / timeStep) * timeStep; time <= frame.End; time += timeStep)
        {
            double x = bounds.Left + (time - frame.Start) / (frame.End - frame.Start) * bounds.Width;
            drawing.DrawLine(grid, new(x, bounds.Top), new(x, bounds.Bottom));
            string label = GraphViewport.FormatTime(time);
            if (frame.End < 3600000) label = label[3..];
            Label(drawing, label, new(x - 26, bounds.Bottom + 7), SecondaryBrush, 11);
        }
        Label(drawing, "Elapsed time (hours:minutes:seconds)", new(bounds.Left + bounds.Width / 2 - 110, ActualHeight - 20), TextBrush, 11);
        if (!frame.HasChannels) { Label(drawing, model.Channels.Any(channel => channel.IsVisible) ? "No pressure readings for the selected channels." : "Select a channel above to show its pressure.", new(bounds.Left + 25, bounds.Top + 35), TextBrush, 14); return; }
        drawing.PushClip(new RectangleGeometry(bounds));
        for (int channel = 0; channel < 6; channel++) drawing.DrawGeometry(model.Channels[channel].Brush, new Pen(model.Channels[channel].Brush, 1.4), frame.Traces[channel]);
        if (model.CursorTime is { } cursor && cursor >= frame.Start && cursor <= frame.End)
        {
            double x = bounds.Left + (cursor - frame.Start) / (frame.End - frame.Start) * bounds.Width;
            var marker = new Pen(SecondaryBrush, 1) { DashStyle = DashStyles.Dash };
            drawing.DrawLine(marker, new(x, bounds.Top), new(x, bounds.Bottom));
        }
        drawing.Pop();
    }
    private void Label(DrawingContext drawing, string text, Point at, Brush brush, double size) => drawing.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
    private static double TimeStep(double target)
    {
        double[] steps = [100, 200, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000, 900000, 1800000, 3600000, 7200000, 14400000, 21600000, 43200000, 86400000];
        return steps.FirstOrDefault(step => step >= target, NiceStep(target));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Model?.HasData != true || !PlotBounds.Contains(e.GetPosition(this))) return;
        Focus(); _dragging = CaptureMouse(); _dragX = e.GetPosition(this).X; _dragStart = Model.Start; e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (Model?.HasData != true) return;
        var point = e.GetPosition(this);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed) Model.PanTo(_dragStart - (point.X - _dragX) / PlotBounds.Width * Model.Span);
        if (PlotBounds.Contains(point)) Model.SetCursor(Model.Start + (point.X - PlotBounds.Left) / PlotBounds.Width * Model.Span);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); _dragging = false; if (IsMouseCaptured) ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragging = false; }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); if (Model?.HasData != true || !PlotBounds.Contains(e.GetPosition(this))) return;
        Model.ZoomAt((e.GetPosition(this).X - PlotBounds.Left) / PlotBounds.Width, e.Delta > 0 ? .8 : 1.25); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (Model?.HasData != true) return;
        if (e.Key == Key.Left) Model.PanTo(Model.Start - Model.Span / 5);
        else if (e.Key == Key.Right) Model.PanTo(Model.Start + Model.Span / 5);
        else if (e.Key is Key.Add or Key.OemPlus) Model.ZoomAt(.5, .8);
        else if (e.Key is Key.Subtract or Key.OemMinus) Model.ZoomAt(.5, 1.25);
        else if (e.Key == Key.Home) Model.PanTo(Model.MinimumStart);
        else if (e.Key == Key.End) Model.PanTo(Model.MaximumStart);
        else return;
        e.Handled = true;
    }
    public void Stop()
    {
        if (_stopped) return; _stopped = true; _timer.Stop(); _rendering?.Cancel();
        if (Model is { } model) { model.ViewChanged -= RequestRender; model.CursorChanged -= InvalidateVisual; }
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
