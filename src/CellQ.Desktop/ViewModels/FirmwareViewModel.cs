using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CellQ.Desktop.Infrastructure;
using CellQ.Desktop.Services;
using CellQ.Protocol;

namespace CellQ.Desktop.ViewModels;

public sealed class FirmwareViewModel : PageViewModel, IAsyncDisposable
{
    private readonly IFirmwareReleases _releases;
    private readonly IFirmwareUploader _uploader;
    private readonly IFirmwareDialogs _dialogs;
    private readonly HttpClient? _http;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly Action<string> _onOutput;
    private FirmwareProgress? _pending;
    private VerifiedFirmware? _package;
    private FirmwareRelease? _selected;
    private CancellationTokenSource? _operation;
    private Task? _running;
    private bool _busy, _disposed, _checked;
    private FirmwarePhase _phase;
    private string _message = "Check published releases to see whether a compatible update is available.", _details = "";
    private double? _percentage;
    public ConnectionViewModel Connection { get; }
    public ObservableCollection<FirmwareRelease> Releases { get; } = [];
    public AsyncCommand CheckCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public AsyncCommand InstallCommand { get; }
    public AsyncCommand ReturnNormalCommand { get; }
    public RelayCommand CancelCommand { get; }
    public override string ContentLabel => "Published firmware";
    public override ICommand FirstActionCommand => CheckCommand;
    public override ICommand SecondActionCommand => InstallCommand;
    public override bool FirstActionAvailable => true;
    public override bool SecondActionAvailable => true;
    public string RepositoryLabel => GitHubFirmwareReleases.Repository;
    public FirmwareRelease? SelectedRelease
    {
        get => _selected;
        set { if (_busy || !Set(ref _selected, value)) return; ClearPackage(); NotifyAll(); }
    }
    public bool IsBusy => _busy;
    public bool CanChoose => !_busy;
    public bool IsInstalling => _busy && _phase is FirmwarePhase.Installing or FirmwarePhase.Reconnecting;
    public bool CanCancel => _busy && !IsInstalling;
    public bool ShowReturnNormal => !_busy && !Connection.State.WorkflowActive && Connection.State.Phase == ConnectionPhase.Connected && Connection.State.Mode == DeviceMode.Maintenance;
    public bool CanInstall => !_busy && !Connection.State.WorkflowActive && _package is not null && Connection.State.Phase == ConnectionPhase.Connected && Connection.State.Mode != DeviceMode.Test && IsNewer;
    private bool IsNewer => SelectedRelease is not null && GitHubFirmwareReleases.TryVersion(Connection.State.FirmwareVersion ?? "", out var installed) && GitHubFirmwareReleases.TryVersion(SelectedRelease.Version, out var release) && release! > installed!;
    public string ReleaseStatus => !_checked ? "Not checked" : Releases.Count == 0 ? "No matching release" : Releases.Count == 1 ? "1 published release" : $"{Releases.Count} published releases";
    public bool ShowLatestStatus => _checked;
    private FirmwareRelease? LatestRelease => Releases.Where(release => GitHubFirmwareReleases.TryVersion(release.Version, out _)).MaxBy(release => Version.Parse(release.Version));
    public string LatestReleaseLabel => LatestRelease is { } release ? $"Latest release: {release.Version}" : "";
    public string LatestStatusLabel => LatestStatus.Label;
    public string LatestStatusTone => LatestStatus.Tone;
    private (string Label, string Tone) LatestStatus
    {
        get
        {
            if (!_checked) return ("", "Neutral");
            if (LatestRelease is not { } latest) return ("No published update available", "Neutral");
            if (IsInstalling) return ("Checking installed version…", "Info");
            if (Connection.State.Phase != ConnectionPhase.Connected) return ("Connect to check installed version", "Neutral");
            if (!GitHubFirmwareReleases.TryVersion(Connection.State.FirmwareVersion ?? "", out var installed)) return ("Version could not be compared", "Neutral");
            int comparison = installed!.CompareTo(Version.Parse(latest.Version));
            return comparison == 0 ? ("Latest version installed", "Success")
                : comparison < 0 ? ($"Update available: {latest.Version}", "Info")
                : ("Newer than latest release", "Info");
        }
    }
    public string PublishedLabel => SelectedRelease?.Published.ToLocalTime().ToString("yyyy-MM-dd") ?? "—";
    public string ReleaseNotes => SelectedRelease?.Notes is { Length: > 0 } notes ? notes : "No release notes selected.";
    public string ReleaseSource => SelectedRelease?.PageUrl ?? "https://github.com/" + GitHubFirmwareReleases.Repository + "/releases";
    public string AssetLabel => SelectedRelease is { } release ? $"Selected update: {release.Version}" : "No update selected";
    public string PackageLabel => _package is null ? "Not checked" : "Checked";
    public string PackageTone => _package is null ? "Neutral" : "Success";
    public string Message => !_busy && _phase == FirmwarePhase.Ready && _package is not null ? CanInstall ? "The update is ready. Choose Update firmware to continue." : InstallHint : _message;
    public string Details => _details;
    public string StatusLabel => _phase switch { FirmwarePhase.CheckingReleases => "Checking releases", FirmwarePhase.Downloading => "Downloading", FirmwarePhase.Verifying => "Checking package", FirmwarePhase.AwaitingConfirmation => "Review update", FirmwarePhase.Preparing => "Preparing device", FirmwarePhase.Installing => "Installing", FirmwarePhase.Reconnecting => "Checking device", FirmwarePhase.Complete => "Complete", FirmwarePhase.Cancelled => "Stopped", FirmwarePhase.Error => "Needs attention", _ => "Ready" };
    public string Tone => _phase == FirmwarePhase.Error ? "Error" : _phase == FirmwarePhase.Complete ? "Success" : _busy ? "Info" : "Neutral";
    public bool ShowProgress => _busy;
    public bool ShowActions => ShowProgress || ShowReturnNormal;
    public bool IsIndeterminate => !_percentage.HasValue;
    public double Percentage => _percentage ?? 0;
    public string ProgressLabel => _percentage is double value ? $"{value:0}% of this step" : _busy ? "Working…" : "";
    public string InstallHint => _busy ? IsInstalling ? "Keep USB and device power connected. Installation and version checks must finish before closing." : "You can cancel before installation starts."
        : _package is null ? "Download and check an update before installing."
        : Connection.State.Phase != ConnectionPhase.Connected ? "Connect your CellQ before installing."
        : Connection.State.WorkflowActive ? "Wait for the current device operation to finish."
        : Connection.State.Mode == DeviceMode.Test ? "A test is running or paused. Finish it on the device before updating."
        : !IsNewer ? "The selected version is already installed, older, or cannot be compared. Reinstallation and downgrades are unavailable."
        : "You will be asked to confirm before the update starts. Your CellQ will restart.";
    public string VerificationDetails => _package is null ? "The release must include the exact HEX and SHA-256 filenames. Local HEX files and source archives are not accepted." : $"File: {_package.Release.Hex.Name}\nSHA-256: {_package.Sha256}\nFirmware file checks passed.\nNo publisher signature is provided by this release policy.";

    public FirmwareViewModel(ConnectionViewModel connection, IFirmwareReleases? releases = null, IFirmwareUploader? uploader = null, IFirmwareDialogs? dialogs = null)
        : base("Firmware Update", "Keep your CellQ up to date.", "FIRMWARE UPDATE", "Check releases", "Update firmware")
    {
        Connection = connection;
        if (releases is null) { _http = GitHubFirmwareReleases.CreateHttpClient(); releases = new GitHubFirmwareReleases(_http, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellQ", "Firmware")); }
        _releases = releases; _uploader = uploader ?? new BundledMegaUploader(new EmbeddedUploaderFiles().GetDirectoryAsync);
        _dialogs = dialogs ?? new FirmwareDialogs(() => Application.Current.MainWindow, Application.Current.Dispatcher);
        _onOutput = line => { _output.Enqueue(line); while (_output.Count > 200) _output.TryDequeue(out _); };
        if (_uploader is BundledMegaUploader bundle) bundle.Output += _onOutput;
        CheckCommand = new(() => RunAsync(async ct =>
        {
            ClearPackage(); _selected = null; Releases.Clear(); _checked = false; NotifyAll();
            Report(new(FirmwarePhase.CheckingReleases, "Checking published CellQ firmware releases…"));
            var found = await _releases.CheckAsync(ct); foreach (var release in found) Releases.Add(release);
            _checked = true; _selected = Releases.FirstOrDefault();
            Report(new(FirmwarePhase.Ready, Releases.Count == 0 ? "No compatible published release is available. Installation remains unavailable." : LatestStatusTone == "Success" ? "Your CellQ already has the latest version." : "Select a release, review its notes, then download and verify it."));
        }), () => !_busy, OnError);
        DownloadCommand = new(() => RunAsync(async ct =>
        {
            ClearPackage(); var release = SelectedRelease!;
            Report(new(FirmwarePhase.Downloading, "Downloading the published firmware package…"));
            _package = await _releases.DownloadAsync(release, Report, ct);
            Report(new(FirmwarePhase.Ready, "The update has been checked and is ready to install."));
        }), () => !_busy && SelectedRelease is not null, OnError);
        InstallCommand = new(() => RunAsync(ct => connection.Service.InstallFirmwareAsync(_package!, _uploader, _dialogs.ConfirmAsync, Report, ct)), () => CanInstall, OnError);
        ReturnNormalCommand = new(() => RunAsync(async ct => { await connection.Service.ReturnToNormalAsync(ct); Report(new(FirmwarePhase.Ready, "The device has returned to its normal screen.")); }), () => ShowReturnNormal, OnError);
        CancelCommand = new(_ => _operation?.Cancel(), _ => CanCancel);
        connection.PropertyChanged += OnConnection;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => DrainProgress(), Application.Current.Dispatcher); _timer.Start();
    }
    private void Report(FirmwareProgress progress) => Interlocked.Exchange(ref _pending, progress);
    internal void DrainProgress()
    {
        var state = Interlocked.Exchange(ref _pending, null);
        if (state is not null) { _phase = state.Phase; _message = state.Message; _percentage = state.Percentage; }
        while (_output.TryDequeue(out var line)) { _details += line + "\n"; if (_details.Length > 24000) _details = _details[^24000..]; }
        NotifyAll();
    }
    private Task RunAsync(Func<CancellationToken, Task> action) => _running = PerformAsync(action);
    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _operation = new(); _details = ""; _percentage = null; NotifyAll();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested) { DrainProgress(); _phase = FirmwarePhase.Cancelled; _message = "Update stopped before installation. Check the device status before trying again."; }
        catch (OperationCanceledException error) { DrainProgress(); OnError(new TimeoutException("The release request timed out. No firmware was installed; check the connection and try again.", error)); }
        catch (Exception error) { DrainProgress(); OnError(error); }
        finally { DrainProgress(); _operation.Dispose(); _operation = null; _busy = false; NotifyAll(); }
    }
    private void OnError(Exception error)
    {
        _phase = FirmwarePhase.Error;
        _message = error switch { HttpRequestException => "Could not reach GitHub. Check your internet connection and try again. No firmware was installed.", System.Text.Json.JsonException => "The release information could not be read safely. Check releases again; no package was accepted.", _ => error.Message };
        _details += "\n" + error; if (_details.Length > 24000) _details = _details[^24000..]; NotifyAll();
    }
    private void ClearPackage()
    {
        var package = _package; _package = null; // Invalidate authorization even if cache cleanup fails.
        try { package?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _details += "\nTemporary package cleanup failed: " + error.Message; }
    }
    private void OnConnection(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not ("" or nameof(ConnectionViewModel.State))) return;
        NotifyAll();
    }
    private void NotifyAll() { Raise(""); CheckCommand.Notify(); DownloadCommand.Notify(); InstallCommand.Notify(); ReturnNormalCommand.Notify(); CancelCommand.Notify(); }
    public async ValueTask DisposeAsync()
    {
        _operation?.Cancel(); if (_running is not null) await _running;
        _disposed = true; _timer.Stop(); Connection.PropertyChanged -= OnConnection;
        if (_uploader is BundledMegaUploader bundle) bundle.Output -= _onOutput;
        ClearPackage(); _http?.Dispose();
    }
}
