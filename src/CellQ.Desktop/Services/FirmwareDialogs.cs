using System.Windows;
using System.Windows.Threading;
using CellQ.Protocol;

namespace CellQ.Desktop.Services;

public interface IFirmwareDialogs
{
    Task<bool> ConfirmAsync(FirmwareConfirmation confirmation, CancellationToken token);
}
public sealed class FirmwareDialogs(Func<Window?> owner, Dispatcher dispatcher) : IFirmwareDialogs
{
    internal static string ConfirmationMessage(FirmwareConfirmation confirmation) =>
        $"Update your CellQ from {confirmation.InstalledVersion} to {confirmation.ReleaseVersion}?\n\nYour device will restart. Keep USB and power connected until the update finishes.";

    public async Task<bool> ConfirmAsync(FirmwareConfirmation confirmation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            string message = ConfirmationMessage(confirmation);
            var result = owner() is { } window ? MessageBox.Show(window, message, "Update firmware", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(message, "Update firmware", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        });
    }
}
