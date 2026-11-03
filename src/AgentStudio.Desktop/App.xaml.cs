using System.Windows;
using System.Windows.Threading;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Theming;
using AgentStudio.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    /// <summary>The tray, the managed exit and the idle lock (Phase 8), once started after the window was shown; null before.</summary>
    private UnattendedShell? _unattended;

    /// <summary>The suspension of the consented resumes the end of the session took before the tray existed: never released.</summary>
    private IDisposable? _resumesHeldForGood;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;

        // Before any window exists: the dark title bar and the first keyboard focus of every window, MainWindow included, and
        // the menu of the text boxes in the theme.
        ThemedWindows.Register();
        TextBoxMenuTheme.Apply(Resources);

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);

            // Only the redacting file logger: the host's defaults (Console, Debug, Windows Event Log) would write
            // unredacted text, and the Event Log keeps it outside the app's control.
            builder.Logging.ClearProviders();
            builder.Logging.AddFileLogging(builder.Configuration);
            builder.Services.AddAgentStudioApp();
            _host = builder.Build();

            await _host.StartAsync();
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.Show();

            // The background once the window has painted (ADR-0036: creating the icon costs a few hundred milliseconds).
            _ = Dispatcher.InvokeAsync(() => StartUnattended(window), DispatcherPriority.ApplicationIdle);
        }
#pragma warning disable CA1031 // Startup failures must be shown, not swallowed by async void.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportFatal(ex, "No se pudo iniciar Agent Studio.");
            Shutdown(1);
        }
    }

    /// <summary>
    /// Starts the tray, the managed exit and the idle lock over the window (docs/16 §4.5, ADR-0036 and ADR-0037). From then on
    /// the app lives in the tray: closing the window no longer ends it (<see cref="ShutdownMode.OnExplicitShutdown"/>), only the
    /// managed exit or the end of the session do. Until then, and if it cannot start, closing the window ends the app as before.
    /// </summary>
    private void StartUnattended(MainWindow window)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            _unattended = UnattendedShell.Start(_host.Services, window);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
#pragma warning disable CA1031 // The app goes on without its tray: closing the window ends it, as before Phase 8.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportFatal(ex, "No se pudo iniciar la bandeja de Agent Studio: cerrar la ventana sale de la aplicación.");
        }
    }

    /// <summary>Windows ends the session: the exit is written in the log of what nobody decided about, and nothing resumes by itself any more.</summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (_unattended is { } unattended)
        {
            unattended.SessionEnding();
        }
        else if (_host?.Services.GetService<ConsentedResumes>() is { } resumes)
        {
            // Before the tray exists: the consented resumes are suspended for good all the same, at once. Nothing is written
            // here, so there is no pass to wait for.
            _resumesHeldForGood ??= resumes.Suspend();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // The icon leaves the notification area, «mantener despierto» lets go, and no consented resume starts any more.
            _unattended?.Dispose();
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
#pragma warning disable CA1031 // Nothing useful can be done while exiting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _host?.Services.GetService<ILogger<App>>()?.LogError(ex, "Error al detener el host.");
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatal(e.Exception, "Error inesperado en la interfaz.");
    }

    private void ReportFatal(Exception ex, string title)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(ex, "{Title}", title);
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
