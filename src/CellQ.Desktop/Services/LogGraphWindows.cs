using System.IO;
using System.Windows;
using CellQ.Desktop.Views;
using Microsoft.Win32;

namespace CellQ.Desktop.Services;

public sealed record GraphSource(string Name, Func<Stream> Open)
{
    public static GraphSource Saved(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return new(Path.GetFileName(fullPath), () => new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
}

public interface ILogGraphWindows
{
    void ShowFile(string path);
    void ChooseFile();
}

public sealed class LogGraphWindows(Func<Window?> owner, Func<string> directory) : ILogGraphWindows
{
    public void ShowFile(string path) => Show(GraphSource.Saved(path));
    public void ChooseFile()
    {
        var dialog = new OpenFileDialog { Title = "Open a CellQ log graph", Filter = "CellQ logs (*.csv)|*.csv", InitialDirectory = directory(), CheckFileExists = true, Multiselect = false };
        if ((owner() is { } parent ? dialog.ShowDialog(parent) : dialog.ShowDialog()) == true) ShowFile(dialog.FileName);
    }
    private void Show(GraphSource source)
    {
        var window = new LogGraphWindow(source);
        if (owner() is { IsVisible: true } parent) window.Owner = parent;
        window.Show();
    }
}
