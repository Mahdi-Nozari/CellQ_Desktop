using System.Windows.Input;
using CellQ.Desktop.Infrastructure;

namespace CellQ.Desktop.ViewModels;

public abstract class PageViewModel(string title, string subtitle, string actionGroup, string firstAction, string secondAction) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string ActionGroup { get; } = actionGroup;
    public string FirstAction { get; } = firstAction;
    public string SecondAction { get; } = secondAction;
    public abstract string ContentLabel { get; }
    public virtual ICommand? FirstActionCommand => null;
    public virtual ICommand? SecondActionCommand => null;
    public virtual bool FirstActionAvailable => false;
    public virtual bool SecondActionAvailable => false;
}

public sealed class HelpViewModel() : PageViewModel("Help", "Connect CellQ, save your logs, and install updates step by step.", "HELP", "", "")
{
    public override string ContentLabel => "User guide";
}
