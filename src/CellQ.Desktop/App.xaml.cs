using System.Windows;
using System.IO;
using CellQ.Desktop.Services;

namespace CellQ.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if ((e.Args.Length is 2 or 3 && e.Args[0] == "--verify-package") || (e.Args.Length == 2 && e.Args[0] == "--export-tools"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string output = e.Args[1];
            try
            {
                output = Path.GetFullPath(output);
                Directory.CreateDirectory(output);
                if (e.Args[0] == "--verify-package") await PackageVerification.RunAsync(this, output, e.Args.Length == 3 ? e.Args[2] : null);
                else
                {
                    string tools = await new EmbeddedUploaderFiles(output).GetDirectoryAsync(default);
                    await File.WriteAllTextAsync(Path.Combine(output, "tools-location.txt"), tools);
                }
                Shutdown(0);
            }
            catch (Exception error)
            {
                try { await File.WriteAllTextAsync(Path.Combine(output, "FAILED.txt"), error.ToString()); }
                catch (Exception) { /* A read-only diagnostic directory must still exit with failure. */ }
                Shutdown(1);
            }
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
