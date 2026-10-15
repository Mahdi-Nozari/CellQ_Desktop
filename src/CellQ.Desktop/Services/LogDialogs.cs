using System.Windows;
using System.IO;
using System.Windows.Threading;
using CellQ.Protocol;
using Microsoft.Win32;

namespace CellQ.Desktop.Services;

public interface ILogDialogs
{
    string? ChooseDestination(DeviceLog log);
    string? ChooseSavedCopy(DeviceLog log);
    Task<bool> ConfirmDeletionAsync(DeletionConfirmation confirmation, CancellationToken token);
}

public sealed class LogDialogs(Func<Window?> owner, Dispatcher dispatcher, Func<string> downloadDirectory) : ILogDialogs
{
    public string? ChooseDestination(DeviceLog log)
    {
        var dialog = CreateDestinationDialog(log);
        Directory.CreateDirectory(dialog.InitialDirectory);
        return (owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }
    internal SaveFileDialog CreateDestinationDialog(DeviceLog log) => new()
        {
            Title = "Save device log — choose the same location to resume",
            FileName = LogFilename.Suggested(log),
            Filter = "Log files (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true, DefaultExt = ".csv", OverwritePrompt = false,
            InitialDirectory = downloadDirectory()
        };
    public async Task<bool> ConfirmDeletionAsync(DeletionConfirmation confirmation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            var message = $"Delete this log from the connected CellQ?\n\n{confirmation.RemotePath}\n\nYour verified copy will be kept at:\n{confirmation.LocalPath}\n\nThis cannot be undone on the device.";
            var result = owner() is { } window
                ? MessageBox.Show(window, message, "Delete device log", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(message, "Delete device log", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        });
    }
    public string? ChooseSavedCopy(DeviceLog log)
    {
        var dialog = new OpenFileDialog { Title = "Choose a previously verified log copy", FileName = LogFilename.Suggested(log), InitialDirectory = downloadDirectory(), Filter = "Log files (*.csv)|*.csv|All files (*.*)|*.*", CheckFileExists = true };
        return (owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }
}
