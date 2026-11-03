using System.Windows;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Views;

namespace AgentStudio.Desktop.Services;

/// <summary>The user's answer to the unsaved-changes question.</summary>
public enum UnsavedChangesChoice
{
    /// <summary>Save the documents, then continue.</summary>
    Save,

    /// <summary>Throw the changes away and continue.</summary>
    Discard,

    /// <summary>Do not continue; everything stays as it was.</summary>
    Cancel,
}

/// <summary>Asks what to do with unsaved edits. An interface so ViewModels stay testable without dialogs.</summary>
public interface IUnsavedChangesPrompt
{
    /// <summary>Asks once for all the documents in <paramref name="titles"/>.</summary>
    /// <param name="titles">Titles of the dirty documents.</param>
    /// <param name="canSave">False when at least one of them cannot be saved from here; then <see cref="UnsavedChangesChoice.Save"/> is never offered.</param>
    UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave);

    /// <summary>
    /// Asks once, as <see cref="Ask(IReadOnlyList{string}, bool)"/>, withdrawn when <paramref name="cancellationToken"/> is
    /// cancelled (the interface locked, docs/16 D-13): a withdrawn question is <see cref="UnsavedChangesChoice.Cancel"/>.
    /// </summary>
    UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? UnsavedChangesChoice.Cancel : Ask(titles, canSave);
}

/// <summary>
/// The question as a themed modal window: over the main window when it is visible, in the middle of the screen otherwise. Runs
/// on the UI thread. With the lock of the interface (docs/16 D-13, ADR-0037) it is never shown while locked, and one open when
/// it locks is withdrawn (its window closes) as <see cref="UnsavedChangesChoice.Cancel"/>: the names of the files never show over
/// the lock screen, and an answer given as it locked is not taken. The caller's token withdraws it the same way.
/// </summary>
public sealed class WpfUnsavedChangesPrompt : IUnsavedChangesPrompt
{
    private readonly IInterfaceLock? _lock;

    /// <param name="interfaceLock">The lock of the interface that withdraws the question; without it, only the caller's token does.</param>
    public WpfUnsavedChangesPrompt(IInterfaceLock? interfaceLock = null) => _lock = interfaceLock;

    public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave) => Ask(titles, canSave, CancellationToken.None);

    public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(titles);
        using var withdrawal = new LockWithdrawal(_lock, cancellationToken);
        var withdrawn = withdrawal.Token;
        if (withdrawn.IsCancellationRequested)
        {
            return UnsavedChangesChoice.Cancel;
        }

        var model = new UnsavedChangesPromptViewModel(titles, canSave);
        var window = new UnsavedChangesWindow { DataContext = model };
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        void Close(object? sender, EventArgs e) => window.Close();
        model.CloseRequested += Close;
        using var withdraw = withdrawn.Register(() => window.Dispatcher.BeginInvoke(window.Close));
        try
        {
            window.ShowDialog();
        }
        finally
        {
            model.CloseRequested -= Close;
        }

        return withdrawn.IsCancellationRequested ? UnsavedChangesChoice.Cancel : model.Answer ?? UnsavedChangesChoice.Cancel;
    }
}
