using System.IO;
using System.Windows;
using CellQ.Desktop.Infrastructure;
using CellQ.Desktop.Services;

namespace CellQ.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppSettingsStore _store;
    private readonly IFolderPicker _picker;
    private AppPreferences _saved;
    private string _theme, _directory, _status;
    private bool _saving;
    private Task _save = Task.CompletedTask;
    public IReadOnlyList<string> Themes { get; } = ["Light", "Dark"];
    public override string ContentLabel => "App preferences";
    public bool CanChange => !_saving;
    public string DownloadDirectory => _directory;
    public string SaveStatus => _status;
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand DefaultFolderCommand { get; }
    public string SelectedTheme
    {
        get => _theme;
        set
        {
            if (!CanChange || !Themes.Contains(value) || !Set(ref _theme, value)) return;
            ThemeService.Apply(value);
            _save = PersistAsync();
        }
    }
    public SettingsViewModel(AppSettingsStore? store = null, IFolderPicker? picker = null)
        : base("Settings", "Choose your appearance and log download folder.", "SETTINGS", "", "")
    {
        _store = store ?? new(); _picker = picker ?? new FolderPicker(() => Application.Current.MainWindow);
        _saved = _store.Load(); _theme = _saved.Theme; _directory = _saved.DownloadDirectory;
        _status = _store.LoadNotice ?? "Changes are saved automatically.";
        ThemeService.Apply(_theme);
        ChooseFolderCommand = new(_ =>
        {
            try { if (_picker.ChooseFolder(_directory) is { } directory) ChangeDirectory(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { _status = "Could not choose that folder. Please try another folder."; Raise(nameof(SaveStatus)); }
        }, _ => CanChange);
        DefaultFolderCommand = new(_ => ChangeDirectory(_store.DefaultDownloadDirectory), _ => CanChange && _directory != _store.DefaultDownloadDirectory);
    }
    private void ChangeDirectory(string directory)
    {
        if (!CanChange || _directory == directory) return;
        if (!AppSettingsStore.Valid(_theme, directory)) throw new ArgumentException("Choose a valid download folder.");
        _directory = directory; Raise(nameof(DownloadDirectory));
        _save = PersistAsync();
    }
    private async Task PersistAsync()
    {
        _saving = true; _status = "Saving…"; Notify();
        var proposed = new AppPreferences(_theme, _directory);
        try { await _store.SaveAsync(proposed); _saved = proposed; _status = "Settings saved."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _theme = _saved.Theme; _directory = _saved.DownloadDirectory; ThemeService.Apply(_theme);
            _status = "Settings could not be saved. Your previous settings are still in use.";
        }
        finally { _saving = false; Notify(); }
    }
    private void Notify() { Raise(""); ChooseFolderCommand.Notify(); DefaultFolderCommand.Notify(); }
    public Task FlushAsync() => _save;
}
