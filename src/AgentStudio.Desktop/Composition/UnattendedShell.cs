using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Desktop.Composition;

/// <summary>
/// The unattended work of the app put together on the shell (docs/16 §4.5, «Integración»; ADR-0036 and ADR-0037): the
/// background (the tray with its menu and its panel, the managed exit and «mantener despierto») over the main window and its
/// view model; the tray following the lock; «Impedir la suspensión mientras hay trabajo» read again when the settings are
/// saved; «Ajustes» shown when the save asked on leaving could not be done; the consented resumes suspended by the managed exit
/// while the app leaves (<c>ResumeScheduler.Suspend</c> and
/// <c>WaitForIdleAsync</c>); and the idle lock. App starts it on the UI thread once the window is shown, and disposes it when
/// the app ends.
/// </summary>
public sealed class UnattendedShell : IDisposable
{
    /// <summary>How long the end of the session may take to write the exit in the logs (ADR-0036, decision 8).</summary>
    public static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(2);

    private readonly Action _unsubscribe;
    private bool _disposed;

    private UnattendedShell(BackgroundHost background, Action unsubscribe)
    {
        Background = background;
        _unsubscribe = unsubscribe;
    }

    /// <summary>The background: its registry, its managed exit, its tray and its panel.</summary>
    public BackgroundHost Background { get; }

    /// <summary>
    /// Puts the background together over <paramref name="window"/> and the services of the app, and starts it (the icon goes to
    /// the notification area) and the idle lock; then the consented resumes may run (<see cref="ConsentedResumes.Enable"/>): the
    /// managed exit exists. On the UI thread. If any of it fails, nothing is left behind: the icon goes, the window closes the
    /// app as it does before the tray exists, no resume runs by itself, and the exception goes to the caller.
    /// </summary>
    /// <remarks>
    /// The menu of the tray opens a recent project or a folder with the shell's commands, and «Bloquear» runs the command of the
    /// lock screen (the lock of the interface with <see cref="LockReason.Manual"/>, which the screen then explains). Leaving with
    /// unsaved changes in the editor asks about them first (<see cref="MainViewModel.ConfirmDiscardOrSaveAsync"/>), a question the
    /// lock withdraws.
    /// </remarks>
    public static UnattendedShell Start(IServiceProvider services, MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(window);
        var main = services.GetRequiredService<MainViewModel>();
        var settings = services.GetRequiredService<IAppSettingsStore>();
        var resumes = services.GetService<ConsentedResumes>();
        var background = BackgroundHost.Create(services, new BackgroundShell(
            ConfirmLeaving: withdrawn => main.ConfirmDiscardOrSaveAsync(main.Documents.ToList(), withdrawn),
            Tray: new TrayShellActions(
                OpenProject: main.OpenProjectAsync,
                OpenFolder: () => main.OpenFolderCommand.ExecuteAsync(null),
                Lock: LockAction(services),
                RecentProjects: () => settings.Load().RecentProjects),
            Resumes: resumes));

        // Saved settings: «Impedir la suspensión mientras hay trabajo» is read again (the others are read where they are used).
        void OnSettingsSaved(object? sender, EventArgs e) => background.SettingsChanged();

        // «Guardar» of «Ajustes» chosen in the question about unsaved changes and not done (a section is wrong, the file could
        // not be written): the window shows «Ajustes», whose sections say why, through the managed exit (which stops an exit
        // that waits), instead of the app staying in the tray without a word. The change is kept.
        void OnSettingsShowRequested(object? sender, EventArgs e)
        {
            if (sender is SettingsDocumentViewModel document)
            {
                main.Show(document);
                background.Exit.ShowWindow();
            }
        }

        UnattendedSettingsViewModel? unattended = null;
        LockSettingsViewModel? lockSettings = null;
        ObservabilitySettingsViewModel? panels = null;
        SettingsDocumentViewModel? settingsDocument = null;
        void Unsubscribe()
        {
            if (unattended is not null)
            {
                unattended.Saved -= OnSettingsSaved;
            }

            if (lockSettings is not null)
            {
                lockSettings.Saved -= OnSettingsSaved;
            }

            if (panels is not null)
            {
                panels.Saved -= OnSettingsSaved;
            }

            if (settingsDocument is not null)
            {
                settingsDocument.ShowRequested -= OnSettingsShowRequested;
            }
        }

        try
        {
            // The icon first: only a background that is up gets the close of the window.
            background.Start();
            window.AttachBackground(background, settings);

            unattended = services.GetService<UnattendedSettingsViewModel>();
            if (unattended is not null)
            {
                unattended.Saved += OnSettingsSaved;
            }

            lockSettings = services.GetService<LockSettingsViewModel>();
            if (lockSettings is not null)
            {
                lockSettings.Saved += OnSettingsSaved;
            }

            panels = services.GetService<ObservabilitySettingsViewModel>();
            if (panels is not null)
            {
                panels.Saved += OnSettingsSaved;
            }

            settingsDocument = services.GetService<SettingsDocumentViewModel>();
            if (settingsDocument is not null)
            {
                settingsDocument.ShowRequested += OnSettingsShowRequested;
            }

            services.GetService<IdleLockMonitor>()?.Start();
        }
        catch
        {
            // Back to the app without its tray: the window closes the app, the icon goes, and the resumes stay held back.
            Unsubscribe();
            window.DetachBackground();
            background.Dispose();
            throw;
        }

        // Last, once everything is up: the managed exit exists, and the consented resumes may run (ADR-0036).
        resumes?.Enable();
        return new UnattendedShell(background, Unsubscribe);
    }

    /// <summary>
    /// Windows is ending the session (<c>Application.SessionEnding</c>): the managed exit suspends the consented resumes for good
    /// and writes the exit in the log of the work nobody decided about (<see cref="ManagedExit.RecordSessionEnded"/>).
    /// </summary>
    public void SessionEnding() => Background.Exit.RecordSessionEnded(SessionEndBudget);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _unsubscribe();
        Background.Dispose();
    }

    /// <summary>«Bloquear» of the tray: the command of the lock screen, or the lock itself; null when no lock was composed.</summary>
    private static Func<Task>? LockAction(IServiceProvider services)
    {
        if (services.GetService<LockScreenViewModel>() is { } screen)
        {
            return () => screen.LockCommand.ExecuteAsync(null);
        }

        if (services.GetService<InterfaceLock>() is { } interfaceLock)
        {
            return async () => await interfaceLock.LockAsync(LockReason.Manual, CancellationToken.None).ConfigureAwait(true);
        }

        return null;
    }
}
