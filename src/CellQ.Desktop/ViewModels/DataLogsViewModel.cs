using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CellQ.Desktop.Infrastructure;
using CellQ.Desktop.Services;
using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

public sealed class LogRow(DeviceLog file) : ObservableObject
{
    private VerifiedLog? _verified;
    public DeviceLog File { get; } = file;
    public string Filename => File.Filename;
    public string PresetName => File.PresetName ?? Verified?.PresetName ?? "—";
    public string Folder => File.Folder == "NO_RTC" ? "No date" : File.Folder.Length == 8 ? $"{File.Folder[..4]}-{File.Folder[4..6]}-{File.Folder[6..]}" : File.Folder;
    public string Size => File.Size < 1024 ? $"{File.Size} bytes" : $"{File.Size / 1024d:0.#} KB";
    public VerifiedLog? Verified { get => _verified; set { if (Set(ref _verified, value)) { Raise(nameof(Verification)); Raise(nameof(Tone)); Raise(nameof(LocalPath)); Raise(nameof(PresetName)); } } }
    public string Verification => Verified is null ? "No verified copy" : "Verified copy";
    public string Tone => Verified is null ? "Neutral" : "Success";
    public string LocalPath => Verified?.LocalPath ?? "Choose Download / Resume to save a checked copy.";
}

public sealed class DataLogsViewModel : PageViewModel, IAsyncDisposable
{
    private readonly DataLogService _logs;
    private readonly ILogDialogs _dialogs;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, VerifiedLog> _verified = new(StringComparer.Ordinal);
    private LogProgress? _pending;
    private CancellationTokenSource? _operation;
    private Task? _running;
    private bool _busy, _listed, _disposed;
    private string _filter = "", _message = "Connect your CellQ, then choose Browse device logs.", _details = "";
    private LogRow? _selected;
    private LogPhase _phase;
    private uint _bytes, _total;
    public ConnectionViewModel Connection { get; }
    public ObservableCollection<LogRow> Logs { get; } = [];
    public ICollectionView FilteredLogs { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand UseCopyCommand { get; }
    public AsyncCommand ReturnNormalCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenGraphFileCommand { get; }
    public RelayCommand ViewGraphCommand { get; }
    public override string ContentLabel => "Device logs";
    public override ICommand FirstActionCommand => BrowseCommand;
    public override ICommand SecondActionCommand => DownloadCommand;
    public override bool FirstActionAvailable => true;
    public override bool SecondActionAvailable => true;
    public bool IsBusy => _busy;
    public bool CanAccess => !_busy && !Connection.State.WorkflowActive && Connection.State.Phase == ConnectionPhase.Connected && Connection.State.Mode != DeviceMode.Test;
    public bool ShowReturnNormal => !_busy && Connection.State.Phase == ConnectionPhase.Connected && Connection.State.Mode == DeviceMode.Maintenance;
    public string AccessHint => Connection.State.Phase != ConnectionPhase.Connected ? "Connect to download device logs, or open a saved log below." : Connection.State.Mode == DeviceMode.Test
        ? "A test is running or paused. Finish it on the device before accessing logs."
        : "Log access temporarily puts the device in Maintenance mode. Manual pressure outputs are idled if entry is allowed. The device returns to its normal screen when finished.";
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) { FilteredLogs.Refresh(); Raise(nameof(VisibleCount)); } } }
    public LogRow? SelectedLog { get => _selected; set { if (Set(ref _selected, value)) NotifyCommands(); } }
    public string Message => _message;
    public string TechnicalError => _details;
    public bool HasTechnicalError => _details.Length > 0;
    public string StatusLabel => _phase switch { LogPhase.Listing => "Reading logs", LogPhase.Downloading => "Downloading", LogPhase.Verifying => "Checking copy", LogPhase.Complete => "Complete", LogPhase.Cancelled => "Stopped", LogPhase.Denied => "Access unavailable", LogPhase.Error => "Needs attention", _ => "Ready" };
    public string Tone => _phase is LogPhase.Error or LogPhase.Denied ? "Error" : _phase == LogPhase.Complete ? "Success" : _busy ? "Info" : "Neutral";
    public double Percentage => _total == 0 ? 0 : (double)_bytes / _total * 100;
    public string ProgressLabel => _total == 0 ? "" : $"{_bytes:N0} of {_total:N0} bytes checked";
    public string VisibleCount => $"{FilteredLogs.Cast<LogRow>().Count()} logs shown";
    public string EmptyMessage => !_listed ? "Choose Browse device logs to read the device." : Logs.Count == 0 ? "No logs found on the device." : "No logs match your search.";

    public DataLogsViewModel(ConnectionViewModel connection, ILogDialogs dialogs, ILogGraphWindows graphs) : base("Data Logs", "Save a verified copy of your device logs.", "DATA LOGS", "Browse device logs", "↓ Download / Resume")
    {
        Connection = connection; _dialogs = dialogs; _logs = new(connection.Service);
        FilteredLogs = CollectionViewSource.GetDefaultView(Logs);
        FilteredLogs.Filter = item => item is LogRow row && (row.PresetName.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase) || row.Filename.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase) || row.Folder.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase) || row.File.Folder.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase));
        BrowseCommand = new(() => RunAsync(BrowseAsync), () => CanAccess, OnError);
        DownloadCommand = new(() =>
        {
            var row = SelectedLog!; string? target = _dialogs.ChooseDestination(row.File);
            return target is null ? Task.CompletedTask : RunAsync(async ct => { var copy = await _logs.DownloadAsync(row.File, target, ct); Remember(copy); });
        }, () => CanAccess && SelectedLog is not null, OnError);
        DeleteCommand = new(() => RunAsync(async ct =>
        {
            var row = SelectedLog!;
            if (await _logs.DeleteVerifiedAsync(row.Verified!, _dialogs.ConfirmDeletionAsync, ct))
            { Logs.Remove(row); SelectedLog = null; Raise(nameof(VisibleCount)); Raise(nameof(EmptyMessage)); }
        }), () => CanAccess && SelectedLog?.Verified is not null, OnError);
        UseCopyCommand = new(() =>
        {
            var row = SelectedLog!; string? path = _dialogs.ChooseSavedCopy(row.File);
            return path is null ? Task.CompletedTask : RunAsync(async ct =>
            {
                row.Verified = null; _verified.Remove(row.File.RemotePath);
                Remember(await _logs.UseSavedCopyAsync(row.File, path, ct));
            });
        }, () => !_busy && SelectedLog is not null, OnError);
        ReturnNormalCommand = new(() => RunAsync(async ct => { await connection.Service.ReturnToNormalAsync(ct); _message = "The device has returned to its normal screen."; _phase = LogPhase.Complete; }), () => ShowReturnNormal, OnError);
        CancelCommand = new(_ => _operation?.Cancel(), _ => _busy);
        OpenGraphFileCommand = new(_ => graphs.ChooseFile());
        ViewGraphCommand = new(_ => graphs.ShowFile(SelectedLog!.Verified!.LocalPath), _ => SelectedLog?.Verified is { } copy && File.Exists(copy.LocalPath));
        connection.PropertyChanged += OnConnection;
        _logs.Progress += OnProgress;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => DrainProgress(), System.Windows.Application.Current.Dispatcher);
        _timer.Start();
    }
    private async Task BrowseAsync(CancellationToken token)
    {
        var files = await _logs.ListAsync(token, includePresetNames: true);
        Logs.Clear(); SelectedLog = null;
        foreach (var file in files)
        {
            var row = new LogRow(file);
            if (_verified.TryGetValue(file.RemotePath, out var copy) && copy.Size == file.Size) row.Verified = copy;
            Logs.Add(row);
        }
        _listed = true; SelectedLog = Logs.FirstOrDefault(); Raise(nameof(EmptyMessage)); Raise(nameof(VisibleCount));
    }
    private Task RunAsync(Func<CancellationToken, Task> action) => _running = PerformAsync(action);
    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || _busy) return;
        _busy = true; _details = ""; _operation = new(); NotifyAll();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { if (Connection.State.Phase == ConnectionPhase.Error) OnError(new IOException("The USB connection was lost. Reconnect and choose the same save location to resume.")); }
        finally { DrainProgress(); _operation.Dispose(); _operation = null; _busy = false; NotifyAll(); }
    }
    private void OnProgress(LogProgress state) => Interlocked.Exchange(ref _pending, state);
    internal void DrainProgress()
    {
        var state = Interlocked.Exchange(ref _pending, null); if (state is null || _disposed) return;
        _phase = state.Phase; _message = state.Message; _bytes = state.Bytes; _total = state.Total;
        if (state.Verified is not null) Remember(state.Verified);
        NotifyAll();
    }
    private void Remember(VerifiedLog copy)
    {
        _verified[copy.RemotePath] = copy;
        foreach (var row in Logs.Where(row => row.File.RemotePath == copy.RemotePath)) row.Verified = copy;
        FilteredLogs.Refresh(); Raise(nameof(VisibleCount));
        NotifyCommands();
    }
    private void OnError(Exception error)
    {
        _details = error.Message; _phase = error is MaintenanceDeniedException or DeviceReplyException ? LogPhase.Denied : LogPhase.Error;
        _message = error switch
        {
            MaintenanceDeniedException => error.Message,
            DeviceReplyException => "The device could not complete this request. Check it, then browse device logs again.",
            ProtocolException => "The device sent an unexpected reply. Reconnect and try again.",
            _ => error.Message
        };
        NotifyAll();
    }
    private void OnConnection(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not ("" or nameof(ConnectionViewModel.State))) return;
        if (Connection.State.Phase != ConnectionPhase.Connected && !_busy) { Logs.Clear(); _listed = false; SelectedLog = null; Raise(nameof(EmptyMessage)); Raise(nameof(VisibleCount)); }
        Raise(nameof(AccessHint)); Raise(nameof(ShowReturnNormal)); NotifyCommands();
    }
    private void NotifyAll() { Raise(""); NotifyCommands(); }
    private void NotifyCommands() { BrowseCommand.Notify(); DownloadCommand.Notify(); DeleteCommand.Notify(); UseCopyCommand.Notify(); ReturnNormalCommand.Notify(); CancelCommand.Notify(); ViewGraphCommand.Notify(); }
    public async ValueTask DisposeAsync()
    {
        _operation?.Cancel(); if (_running is not null) try { await _running; } catch (Exception) { /* The command reports its error; shutdown still closes the port. */ }
        _disposed = true; _timer.Stop(); _logs.Progress -= OnProgress; Connection.PropertyChanged -= OnConnection;
    }
}
