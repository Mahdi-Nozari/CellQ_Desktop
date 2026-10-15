using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CellQ.Desktop.Services;

public interface IFolderPicker
{
    string? ChooseFolder(string initialDirectory);
}

public sealed class FolderPicker(Func<Window?> owner) : IFolderPicker
{
    public string? ChooseFolder(string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose log download folder", Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify)
        };
        return (owner() is { } window ? dialog.ShowDialog(window) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }
}
