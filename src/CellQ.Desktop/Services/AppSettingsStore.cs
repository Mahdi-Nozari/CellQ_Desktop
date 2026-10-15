using System.IO;
using System.Text.Json;

namespace CellQ.Desktop.Services;

public sealed record AppPreferences(string Theme, string DownloadDirectory);

public sealed class AppSettingsStore
{
    private sealed record StoredSettings(int Version, string Theme, string DownloadDirectory);
    private readonly string _path;
    public string DefaultDownloadDirectory { get; }
    public AppPreferences Defaults => new("Light", DefaultDownloadDirectory);
    public string? LoadNotice { get; private set; }

    public AppSettingsStore(string? path = null, string? documentsDirectory = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellQ", "settings.json");
        DefaultDownloadDirectory = Path.Combine(documentsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "CellQ");
    }
    public AppPreferences Load()
    {
        LoadNotice = null;
        try
        {
            if (!File.Exists(_path)) return Defaults;
            if (new FileInfo(_path).Length > 65536) throw new IOException("Settings are too large.");
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_path));
            if (stored is null || stored.Version != 1 || !Valid(stored.Theme, stored.DownloadDirectory)) throw new IOException("Invalid settings.");
            return new(stored.Theme, stored.DownloadDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LoadNotice = "Saved settings could not be read. Default settings are in use.";
            return Defaults;
        }
    }
    internal static bool Valid(string? theme, string? directory) => theme is "Light" or "Dark" &&
        !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory) && directory.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    public async Task SaveAsync(AppPreferences preferences)
    {
        if (!Valid(preferences.Theme, preferences.DownloadDirectory)) throw new ArgumentException("Invalid app settings.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, new StoredSettings(1, preferences.Theme, preferences.DownloadDirectory)).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); } // Only our own unique settings scratch file.
    }
}
