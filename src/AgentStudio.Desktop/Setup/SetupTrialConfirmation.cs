using System.Windows;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Views.Setup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Setup;

/// <summary>
/// Asks the person before «Probarla ahora» runs commands that are not the profile they already accepted (docs/24, wave 1): none
/// saved yet, or edited on the screen. It lists the exact commands and says what they can reach. No Windows Hello: nothing is
/// saved and the commands run confined (ADR-0017, addendum P50), but the person decides with the commands in front of them.
/// </summary>
public interface ISetupTrialConfirmation
{
    /// <summary>Whether the person chose «Ejecutar»; anything else, closing the window included, is no.</summary>
    /// <param name="commands">Each command that will run, «what it is for: program and arguments», and only those.</param>
    /// <param name="skipped">The line that says which commands do not run and why, or null when every one runs.</param>
    bool Confirm(IReadOnlyList<string> commands, string? skipped, CancellationToken cancellationToken);
}

/// <summary>The question of <see cref="ISetupTrialConfirmation"/>: the commands, and «Ejecutar» or «Cancelar» (the default).</summary>
public sealed partial class SetupTrialConfirmationViewModel(IReadOnlyList<string> commands, string? skipped = null) : ObservableObject
{
    /// <summary>Raised when the person answered, so the window closes.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The commands that will run, one per line.</summary>
    public IReadOnlyList<string> Commands { get; } = commands ?? throw new ArgumentNullException(nameof(commands));

    /// <summary>Which commands do not run and why, in one line; empty when every one runs.</summary>
    public string Skipped { get; } = skipped ?? string.Empty;

    /// <summary>True for «Ejecutar», false for «Cancelar»; null while unanswered (a closed window is a no).</summary>
    public bool? Answer { get; private set; }

    /// <summary>«Ejecutar».</summary>
    [RelayCommand]
    private void Run() => Close(true);

    /// <summary>«Cancelar», also Esc and Enter.</summary>
    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool answer)
    {
        Answer = answer;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The question as a themed modal window over the main window, on the UI thread. As every question of the app, the lock of the
/// interface withdraws it (docs/16 D-13): it is never shown while locked, and one open when it locks closes as a no.
/// </summary>
public sealed class WpfSetupTrialConfirmation(IInterfaceLock? interfaceLock = null) : ISetupTrialConfirmation
{
    public bool Confirm(IReadOnlyList<string> commands, string? skipped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        using var withdrawal = new LockWithdrawal(interfaceLock, cancellationToken);
        var withdrawn = withdrawal.Token;
        if (withdrawn.IsCancellationRequested)
        {
            return false;
        }

        var model = new SetupTrialConfirmationViewModel(commands, skipped);
        var window = new SetupTrialWindow { DataContext = model };
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

        return !withdrawn.IsCancellationRequested && model.Answer == true;
    }
}
