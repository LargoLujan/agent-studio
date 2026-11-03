using System.Windows;
using AgentStudio.Desktop.Views.Background;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// The question of the managed exit as a themed modal window: over the main window when it is visible, and in the middle
/// of the screen, on top, when the exit was asked from the tray. Runs on the UI thread. Closing the window any other way
/// than with its buttons, or the caller's token, is «Cancelar».
/// </summary>
public sealed class WpfExitPrompt : IExitPrompt
{
    public Task<ExitChoice> AskAsync(ExitQuestion question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        var model = new ExitPromptViewModel(question);
        var window = new ExitPromptWindow { DataContext = model };
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
        using var expiry = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(window.Close));
        try
        {
            window.ShowDialog();
        }
        finally
        {
            model.CloseRequested -= Close;
        }

        return Task.FromResult(model.Answer ?? ExitChoice.Cancel);
    }
}
