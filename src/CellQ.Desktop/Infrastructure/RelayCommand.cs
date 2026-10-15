using System.Windows.Input;

namespace CellQ.Desktop.Infrastructure;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public event EventHandler? CanExecuteChanged;
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
