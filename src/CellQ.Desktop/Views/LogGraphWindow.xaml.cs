using System.Windows;
using CellQ.Desktop.Services;
using CellQ.Desktop.ViewModels;

namespace CellQ.Desktop.Views;

public partial class LogGraphWindow : Window
{
    public LogGraphViewModel ViewModel { get; }
    public LogGraphWindow(GraphSource source)
    {
        InitializeComponent(); ViewModel = new(source); DataContext = ViewModel;
        Loaded += async (_, _) => await ViewModel.LoadAsync();
        Closed += (_, _) => { Plot.Stop(); ViewModel.Dispose(); };
    }
}
