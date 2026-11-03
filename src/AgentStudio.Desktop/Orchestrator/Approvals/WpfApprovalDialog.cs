using System.Windows;
using AgentStudio.Desktop.Views.Orchestrator;

namespace AgentStudio.Desktop.Orchestrator.Approvals;

/// <summary>
/// The approval question as a themed modal window over the main window. Runs on the UI thread
/// (<see cref="ApprovalPromptService"/> posts it there). The window closes when a button answers or when the caller's
/// token is cancelled; closing it any other way leaves no answer, which the prompt counts as a denial.
/// </summary>
public sealed class WpfApprovalDialog : IApprovalDialog
{
    public Task<ApprovalAnswer?> ShowAsync(ApprovalDialogViewModel question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        var window = new ApprovalWindow { DataContext = question };
        if (System.Windows.Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        void Close(object? sender, EventArgs e) => window.Close();
        question.CloseRequested += Close;
        using var expiry = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(window.Close));
        try
        {
            window.ShowDialog();
        }
        finally
        {
            question.CloseRequested -= Close;
        }

        return Task.FromResult(question.Answer);
    }
}
