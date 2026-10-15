using System.Windows.Input;

namespace CellQ.Desktop.Infrastructure;

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    public bool IsExecuting { get; private set; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !IsExecuting && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        IsExecuting = true; Notify();
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { onError(error); }
        finally { IsExecuting = false; Notify(); }
    }
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
