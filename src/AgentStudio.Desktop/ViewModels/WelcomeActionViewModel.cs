using System.Windows.Input;

namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// A way into the open project from the welcome tab: what it opens, what it is for, its keyboard shortcut (null when it has
/// none) and the command of the shell that opens it.
/// </summary>
public sealed record WelcomeActionViewModel(string Title, string Description, string? Shortcut, ICommand Command)
{
    /// <summary>The title: what a screen reader says for the entry, instead of every field of the record.</summary>
    public override string ToString() => Title;
}
