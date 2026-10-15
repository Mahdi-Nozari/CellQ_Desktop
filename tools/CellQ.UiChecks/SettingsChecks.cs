using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CellQ.Desktop;
using CellQ.Desktop.Services;
using CellQ.Desktop.ViewModels;
using CellQ.Protocol;

namespace CellQ.UiChecks;

internal static partial class Program
{
    private static SettingsViewModel TestSettings(string output) => new(new AppSettingsStore(Path.Combine(output, "settings", Guid.NewGuid().ToString("N"), "settings.json")), new TestFolderPicker());

    private sealed class TestFolderPicker : IFolderPicker
    {
        public string? Next { get; set; }
        public string? InitialDirectory { get; private set; }
        public string? ChooseFolder(string initialDirectory) { InitialDirectory = initialDirectory; return Next; }
    }
    private static void SettingsChecks(App app, FrameworkElement root, ShellViewModel shell, AppSettingsStore store, TestFolderPicker picker, string output)
    {
        var settings = (SettingsViewModel)shell.Pages[Destination.Settings];
        shell.Select(Destination.Settings); root.DataContext = shell; Layout(root, 1040, 700);
        string defaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "CellQ");
        Require(settings.DownloadDirectory == defaultDirectory && settings.SelectedTheme == "Light", "Default preferences use Light and CellQ inside the Windows Documents folder");
        Require(settings.Themes.SequenceEqual(new[] { "Light", "Dark" }) && Descendants<ComboBox>(root).Count(c => ReferenceEquals(c.DataContext, settings)) == 1 && Descendants<TextBox>(root).Any(t => t.IsReadOnly && t.Text == defaultDirectory), "Settings contains only the theme choice and download folder");
        Require(Descendants<Button>(root).Where(IsPresented).All(b => !Equals(b.Content, "Save preferences") && !Equals(b.Content, "")), "Autosaved Settings has no disabled placeholder ribbon actions");
        picker.Next = null; settings.ChooseFolderCommand.Execute(null);
        Require(settings.DownloadDirectory == defaultDirectory && picker.InitialDirectory == defaultDirectory, "Cancelling the folder picker preserves the current preference");
        // Explicit TextBlock styles bypass the implicit theme style. A black ancestor
        // reproduces the default foreground of a live UserControl, unlike our preview root.
        var textProbe = new StackPanel();
        TextBlock.SetForeground(textProbe, Brushes.Black);
        textProbe.Resources.MergedDictionaries.Add(app.Resources);
        foreach (string key in new[] { "PanelTitle", "MonoCell" })
            textProbe.Children.Add(new TextBlock { Text = key, Style = (Style)app.Resources[key] });
        settings.SelectedTheme = "Dark"; UiUntil(() => settings.CanChange, root); settings.FlushAsync().GetAwaiter().GetResult();
        Require(store.Load().Theme == "Dark" && settings.SaveStatus == "Settings saved.", "Theme selection persists for the next app launch");
        Layout(root, 1040, 700);
        var canvasColor = ((SolidColorBrush)root.FindResource("CanvasBrush")).Color;
        var titleColor = ((SolidColorBrush)Descendants<TextBlock>(root).Single(t => t.Text == "Settings").Foreground).Color;
        Require(canvasColor == Color.FromRgb(0, 29, 80) && titleColor == Color.FromRgb(248, 252, 248), $"Changing theme updates the existing canvas and page text immediately (canvas: {canvasColor}; text: {titleColor})");
        Layout(textProbe, 220, 80);
        Require(textProbe.Children.OfType<TextBlock>().All(t => ((SolidColorBrush)t.Foreground).Color == Color.FromRgb(248, 252, 248)), "Section headings and monospace cells override inherited black text in Dark");
        var themePicker = Descendants<ComboBox>(root).Single(c => ReferenceEquals(c.DataContext, settings));
        Require(Equals(themePicker.SelectedItem, "Dark") && ((SolidColorBrush)themePicker.Background).Color != ((SolidColorBrush)themePicker.Foreground).Color, "The theme picker remains readable in Dark");
        Require(Descendants<Button>(root).Where(b => b.FontFamily.Source == "Segoe MDL2 Assets").SelectMany(Descendants<TextBlock>).All(t => ((SolidColorBrush)t.Foreground).Color == Color.FromRgb(0, 29, 80)), "Caption controls remain readable on the preserved light logo strip in Dark");
        CheckMenuSelection(app, "Dark");
        Save(root, Path.Combine(output, "Settings-dark-minimum.png"), 1040, 700);
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 }) Save(root, Path.Combine(output, $"Settings-dark-{(int)(scale * 100)}percent.png"), 1120, 760, scale);
        foreach (var destination in Enum.GetValues<Destination>())
        {
            shell.Select(destination); Layout(root, 1120, 760);
            Save(root, Path.Combine(output, $"{destination}-dark.png"), 1120, 760);
            if (destination == Destination.Help) SaveHelpPreviews(root, output, "Dark");
            Require(Descendants<TextBlock>(root).Any(t => t.Text == shell.CurrentPage.Title && ((SolidColorBrush)t.Foreground).Color == Color.FromRgb(248, 252, 248)), "Dark theme is applied to " + destination);
            var headings = Descendants<TextBlock>(root).Where(t => ReferenceEquals(t.Style, app.Resources["PanelTitle"])).ToArray();
            Require(headings.Length > 0 && headings.All(t => ((SolidColorBrush)t.Foreground).Color == Color.FromRgb(248, 252, 248)), "All section headings remain readable in Dark on " + destination);
            foreach (var table in Descendants<DataGrid>(root)) Require(((SolidColorBrush)table.Foreground).Color == Color.FromRgb(248, 252, 248) && ((SolidColorBrush)table.Background).Color == Color.FromRgb(0, 51, 120), "Dark tables retain readable text and surfaces on " + destination);
        }
        shell.Select(Destination.Settings); Layout(root, 1040, 700);
        string chosenDirectory = Path.Combine(output, "settings-folders", Guid.NewGuid().ToString("N"), "Logs with spaces آزمون");
        Directory.CreateDirectory(chosenDirectory); picker.Next = chosenDirectory;
        settings.ChooseFolderCommand.Execute(null); UiUntil(() => settings.CanChange, root);
        var saved = store.Load();
        Require(saved.Theme == "Dark" && saved.DownloadDirectory == chosenDirectory && settings.DownloadDirectory == chosenDirectory, "Chosen download folder persists alongside the theme, including spaces and Unicode");
        var logDialogs = new LogDialogs(() => null, app.Dispatcher, () => settings.DownloadDirectory);
        var log = new DeviceLog("/DATA/20261009/log_001.csv", "log_001.csv", "20261009", 24);
        var saveDialog = logDialogs.CreateDestinationDialog(log);
        Require(saveDialog.InitialDirectory == chosenDirectory && saveDialog.FileName == "20261009_log_001.csv" && !saveDialog.OverwritePrompt, "Real log-save dialog starts in the configured directory without changing resume or overwrite handling");
        var namedSaveDialog = logDialogs.CreateDestinationDialog(log with { PresetName = "Heart Test" });
        Require(namedSaveDialog.FileName == "Heart Test_20261009_log_001.csv" && namedSaveDialog.InitialDirectory == chosenDirectory && !namedSaveDialog.OverwritePrompt, "The real save dialog prefixes the preset and keeps the configured folder and overwrite/resume policy");
        var reloaded = new SettingsViewModel(store, new TestFolderPicker());
        Require(reloaded.SelectedTheme == "Dark" && reloaded.DownloadDirectory == chosenDirectory, "A new settings view model restores both persisted preferences");
        settings.DefaultFolderCommand.Execute(null); UiUntil(() => settings.CanChange, root);
        Require(settings.DownloadDirectory == defaultDirectory && store.Load() == new AppPreferences("Dark", defaultDirectory) && logDialogs.CreateDestinationDialog(log).InitialDirectory == defaultDirectory, "Use default folder restores Documents/CellQ and immediately updates the existing log dialog service");
        picker.Next = "relative-folder"; settings.ChooseFolderCommand.Execute(null);
        Require(settings.DownloadDirectory == defaultDirectory && settings.SaveStatus.Contains("another folder"), "Invalid folder input is refused without replacing the preference");
        var fixtureDirectory = Path.Combine(output, "settings-failures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtureDirectory);
        string malformedPath = Path.Combine(fixtureDirectory, "malformed.json"); File.WriteAllText(malformedPath, "{broken");
        var malformedStore = new AppSettingsStore(malformedPath); var malformed = new SettingsViewModel(malformedStore, new TestFolderPicker());
        Require(malformed.SelectedTheme == "Light" && malformed.DownloadDirectory == defaultDirectory && malformed.SaveStatus.Contains("could not be read"), "Corrupted settings fall back to defaults with an honest notice");
        string invalidPath = Path.Combine(fixtureDirectory, "invalid.json");
        File.WriteAllText(invalidPath, "{\"Version\":1,\"Theme\":\"Unknown\",\"DownloadDirectory\":\"relative-folder\"}");
        var invalidStore = new AppSettingsStore(invalidPath);
        Require(invalidStore.Load() == invalidStore.Defaults && invalidStore.LoadNotice is not null, "Unsupported themes and relative directories cannot authorize invalid stored settings");
        string blockedPath = Path.Combine(fixtureDirectory, "blocked.json"); Directory.CreateDirectory(blockedPath);
        var blocked = new SettingsViewModel(new AppSettingsStore(blockedPath), new TestFolderPicker());
        blocked.SelectedTheme = "Dark"; UiUntil(() => blocked.CanChange, root); blocked.FlushAsync().GetAwaiter().GetResult();
        Require(blocked.SelectedTheme == "Light" && blocked.SaveStatus.Contains("could not be saved") && ((SolidColorBrush)root.FindResource("CanvasBrush")).Color == Color.FromRgb(230, 230, 230), "A save failure rolls back the theme and reports that previous settings remain in use");
        settings.SelectedTheme = "Light"; UiUntil(() => settings.CanChange, root); Layout(root, 1040, 700);
        Require(store.Load() == store.Defaults, "Changing back to Light is persisted without changing the default folder");
        Layout(textProbe, 220, 80);
        Require(textProbe.Children.OfType<TextBlock>().All(t => ((SolidColorBrush)t.Foreground).Color == Color.FromRgb(0, 29, 80)), "Existing section headings and monospace cells switch back to navy in Light");
        CheckMenuSelection(app, "Light");
        Save(root, Path.Combine(output, "Settings.png"), 1120, 760);
        Save(root, Path.Combine(output, "Settings-minimum.png"), 1040, 700);
        var buttons = Descendants<Button>(root).Where(IsPresented).Where(b => ReferenceEquals(b.DataContext, settings));
        Require(buttons.All(b => b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize)).Bottom < 660), "Settings controls fit at the minimum window size");
        foreach (var destination in Enum.GetValues<Destination>()) { shell.Select(destination); Layout(root, 1120, 760); }
        Require(shell.Connection.State.Phase == ConnectionPhase.Disconnected, "Settings changes do not open the shared device connection");
    }
    private static void CheckMenuSelection(App app, string theme)
    {
        var item = new ComboBoxItem { Content = "Selected option", IsSelected = true };
        item.Resources.MergedDictionaries.Add(app.Resources); Layout(item, 220, 32);
        Require(((SolidColorBrush)item.Background).Color == Color.FromRgb(0, 103, 191) && Descendants<TextBlock>(item).Any(t => t.Text == "Selected option" && ((SolidColorBrush)t.Foreground).Color == Colors.White), "Selected dropdown options are readable in " + theme);
    }
}
