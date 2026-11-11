using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Desktop.Background;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Desktop.Phase8;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>
/// «Ajustes» in the shell (docs/03 §5.2): the last entry of the activity bar opens one tab for the session; closing it with a
/// change that was not saved asks, as a file does; and what it saves reaches every orchestrator, «Consumo» and «mantener
/// despierto». Nothing is shown and no CLI is launched: the window of the composition is built and never shown.
/// </summary>
public sealed class SettingsShellTests
{
    private static MainViewModel Shell(
        SettingsDocumentViewModel? settings,
        IUnsavedChangesPrompt? prompt = null,
        UsageDashboardViewModel? usage = null,
        IOrchestratorDocumentFactory? orchestrators = null) =>
        new(
            new InMemorySettingsStore(),
            new FakeFolderPicker(),
            prompt ?? new FakeUnsavedChangesPrompt(),
            new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
            new ExplorerViewModel(),
            new JournalViewModel(TimeProvider.System),
            new QuickOpenViewModel(),
            orchestrators: orchestrators,
            usage: usage,
            settingsDocument: settings);

    private static SettingsDocumentViewModel Settings(IAppSettingsStore store) =>
        new(store, new ObservabilitySettingsViewModel(store), new UnattendedSettingsViewModel(store, new LockSettingsViewModel(store)), time: new FakeTimeProvider(T0));

    [Fact]
    public void The_activity_bar_ends_with_ajustes_which_opens_one_tab_for_the_session()
    {
        var settings = Settings(new InMemorySettingsStore());
        var main = Shell(settings);

        var activity = main.Activities[^1];
        Assert.Equal(("settings", "Ajustes", ""), (activity.Id, activity.Title, activity.Glyph));
        Assert.True(activity.IsEnabled);
        Assert.Null(activity.PanelId);
        Assert.Equal("Ajustes", activity.ToolTip);

        activity.Command.Execute(activity);
        activity.Command.Execute(activity);

        Assert.Same(settings, Assert.Single(main.Documents.OfType<SettingsDocumentViewModel>()));
        Assert.Same(settings, main.ActiveContent);
    }

    [Fact]
    public void Without_its_document_ajustes_is_not_offered()
    {
        Assert.DoesNotContain(ShellFactory.CreateMain().Activities, a => a.Id == "settings");
    }

    [Fact]
    public async Task Closing_ajustes_with_a_change_asks_and_discarding_drops_the_change()
    {
        var store = new InMemorySettingsStore();
        var settings = Settings(store);
        var prompt = new FakeUnsavedChangesPrompt(UnsavedChangesChoice.Discard);
        var main = Shell(settings, prompt);
        main.Activities[^1].Command.Execute(null);
        settings.Context.WarningText = "50";
        settings.Unattended!.Choose(CloseWindowAction.Exit);

        Assert.True(await main.CloseDocumentAsync(settings));

        var (titles, canSave) = Assert.Single(prompt.Calls);
        Assert.Equal(["Ajustes"], titles);
        Assert.True(canSave);
        Assert.DoesNotContain(settings, main.Documents);
        Assert.Equal(0, store.Saves);

        // Opening it again shows what is in force.
        Assert.Equal("70", settings.Context.WarningText);
        Assert.Equal(CloseWindowAction.Ask, settings.Unattended.CloseWindow);
        Assert.False(settings.IsDirty);
    }

    [Fact]
    public async Task Closing_ajustes_and_choosing_guardar_saves_every_section_once()
    {
        var store = new InMemorySettingsStore();
        var settings = Settings(store);
        var main = Shell(settings, new FakeUnsavedChangesPrompt(UnsavedChangesChoice.Save));
        main.Activities[^1].Command.Execute(null);
        settings.Context.WarningText = "50";
        settings.Lock!.IdleLockEnabled = true;

        Assert.True(await main.CloseDocumentAsync(settings));

        Assert.Equal(1, store.Saves);
        Assert.Equal((50, true), (store.Current.ContextWarningPercent, store.Current.IdleLockEnabled));
        Assert.DoesNotContain(settings, main.Documents);
    }

    [Fact]
    public async Task Closing_ajustes_with_a_wrong_section_and_choosing_guardar_keeps_the_tab_and_says_why()
    {
        var store = new InMemorySettingsStore();
        var settings = Settings(store);
        var main = Shell(settings, new FakeUnsavedChangesPrompt(UnsavedChangesChoice.Save));
        main.Activities[^1].Command.Execute(null);
        settings.Context.WarningText = "cincuenta";

        Assert.False(await main.CloseDocumentAsync(settings));

        Assert.Equal(0, store.Saves);
        Assert.Contains(settings, main.Documents);
        Assert.Equal("No se pudo guardar Ajustes: los cambios siguen pendientes.", main.StatusMessage);
        Assert.Equal("No se guardó nada: corrige «Contexto y límite de uso».", settings.ErrorMessage);
    }

    [Fact]
    public async Task What_ajustes_saves_reaches_every_orchestrator_and_consumo()
    {
        using var temp = new TempDirectory();
        var clock = new FakeTimeProvider(T0);
        var store = new InMemorySettingsStore();
        var preferences = new ObservabilitySettingsViewModel(store);
        var usage = new UsageDashboardViewModel(
            new FakeUsageReport(),
            new FakeUsageExporter(),
            new FakeSaveFilePicker(null),
            store,
            clock,
            new QuotaViewModel(new FakeQuotaObservations(), new FakeQuotaReader(), clock),
            preferences);
        var settings = new SettingsDocumentViewModel(store, preferences, time: clock);
        var main = Shell(settings, usage: usage, orchestrators: new InlineOrchestrators(clock));
        await main.OpenProjectAsync(temp.CreateDirectory("calc"));
        await main.OpenOrchestratorCommand.ExecuteAsync(null);
        var orchestrator = Assert.Single(main.Documents.OfType<OrchestratorDocumentViewModel>());

        settings.Context.WarningText = "50";
        settings.Context.CompressText = "75";
        settings.Context.CriticalText = "92";
        settings.Context.CodexQuotaReadEnabled = true;
        settings.SaveCommand.Execute(null);

        var thresholds = orchestrator.Monitor.ContextThresholds;
        Assert.Equal((0.50, 0.75, 0.92), (thresholds.Warning, thresholds.Compress, thresholds.Critical));
        Assert.True(usage.Quota.CodexOptedIn);
        Assert.Same(preferences, usage.Preferences);
    }

    [Fact]
    public void The_app_puts_ajustes_last_over_the_sections_it_composed()
    {
        using var temp = new TempDirectory();
        using var provider = new Phase8Doubles().BuildApp(temp);

        var main = provider.GetRequiredService<MainViewModel>();
        var settings = provider.GetRequiredService<SettingsDocumentViewModel>();
        Assert.Equal("settings", main.Activities[^1].Id);
        main.Activities[^1].Command.Execute(null);

        Assert.Same(settings, Assert.Single(main.Documents.OfType<SettingsDocumentViewModel>()));
        Assert.Same(provider.GetRequiredService<ObservabilitySettingsViewModel>(), settings.Context);
        Assert.Same(provider.GetRequiredService<UnattendedSettingsViewModel>(), settings.Unattended);
        Assert.Same(provider.GetRequiredService<LockSettingsViewModel>(), settings.Lock);
    }

    [Fact]
    public async Task Leaving_from_the_tray_with_a_wrong_change_in_ajustes_shows_it_with_why_and_keeps_the_change()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        var unsaved = new FakeUnsavedChangesPrompt(UnsavedChangesChoice.Save);
        await using var provider = App(doubles, temp, _ => unsaved);

        var (outcome, shown, inFront, why, sectionWhy, kept, dirty, status, stored) = await WpfHost.RunAsync(async () =>
        {
            using var shell = UnattendedShell.Start(provider, Window(provider, temp));
            var main = provider.GetRequiredService<MainViewModel>();
            var settings = provider.GetRequiredService<SettingsDocumentViewModel>();
            main.Activities[^1].Command.Execute(null);
            settings.Context.WarningText = "x";

            // Another tab in front, and the window hidden in the tray: «Salir» from the menu of the tray, «Guardar» in the question.
            main.ShowWelcomeCommand.Execute(null);
            doubles.Window.Hide();
            var shownBefore = doubles.Window.Shown;
            var left = await shell.Background.Exit.ExitAsync();

            return (
                left,
                doubles.Window.Shown - shownBefore,
                ReferenceEquals(main.ActiveContent, settings),
                settings.ErrorMessage,
                settings.Context.ErrorMessage,
                settings.Context.WarningText,
                settings.IsDirty,
                main.StatusMessage,
                provider.GetRequiredService<IAppSettingsStore>().Load().ContextWarningPercent);
        });

        var (titles, canSave) = Assert.Single(unsaved.Calls);
        Assert.Equal(["Ajustes"], titles);
        Assert.True(canSave);

        // The app does not leave, and does not stay hidden either: the window shows «Ajustes», with why, and the change.
        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(0, doubles.Shutdown.Calls);
        Assert.Equal(1, shown);
        Assert.True(inFront, "«Ajustes» is the tab in front.");
        Assert.Equal("No se guardó nada: corrige «Contexto y límite de uso».", why);
        Assert.Equal("No se guardaron los umbrales. «x» no es un número entero de 1 a 100: corrígelo.", sectionWhy);
        Assert.Equal("x", kept);
        Assert.True(dirty);
        Assert.Equal("No se pudo guardar Ajustes: los cambios siguen pendientes.", status);
        Assert.Equal(70, stored);
    }

    [Fact]
    public async Task Locked_while_the_question_about_ajustes_is_open_withdraws_it_saves_nothing_and_shows_nothing()
    {
        using var temp = new TempDirectory();
        var doubles = new Phase8Doubles();
        LockingSave? unsaved = null;
        await using var provider = App(doubles, temp, services => unsaved = new LockingSave(services.GetRequiredService<InterfaceLock>()));

        var (outcome, shown, dirty, stored) = await WpfHost.RunAsync(async () =>
        {
            using var shell = UnattendedShell.Start(provider, Window(provider, temp));
            var main = provider.GetRequiredService<MainViewModel>();
            var settings = provider.GetRequiredService<SettingsDocumentViewModel>();
            main.Activities[^1].Command.Execute(null);
            settings.Context.WarningText = "60";
            doubles.Window.Hide();
            var shownBefore = doubles.Window.Shown;

            // «Salir» from the tray; the idle lock fires while the question is open, and «Guardar» is answered as it locks.
            var left = await shell.Background.Exit.ExitAsync();
            return (left, doubles.Window.Shown - shownBefore, settings.IsDirty, provider.GetRequiredService<IAppSettingsStore>().Load().ContextWarningPercent);
        });

        Assert.Equal(1, unsaved!.Asked);
        Assert.True(unsaved.Withdrawn, "The lock withdrew the question.");
        Assert.Equal(CloseOutcome.StayOpen, outcome);
        Assert.Equal(0, doubles.Shutdown.Calls);
        Assert.Equal(0, shown);
        Assert.True(dirty, "The change is still there, not saved.");
        Assert.Equal(70, stored);
    }

    /// <summary>
    /// The app as it composes itself over temporary folders, with the doubles of Phase 8 first and, after it, the question about
    /// unsaved changes (the app's is a window): what is registered last is what the shell gets.
    /// </summary>
    private static ServiceProvider App(Phase8Doubles doubles, TempDirectory temp, Func<IServiceProvider, IUnsavedChangesPrompt> unsaved)
    {
        var services = new ServiceCollection();
        doubles.Register(services);
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
        services.AddSingleton(unsaved);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static MainWindow Window(IServiceProvider provider, TempDirectory temp) => new(
        provider.GetRequiredService<MainViewModel>(),
        new FileLayoutStore(new AppDataPaths(temp.Combine("layouts"))),
        quickOpenPresenter: new MainWindowLockTests.RecordingPresenter(),
        lockScreen: provider.GetRequiredService<LockScreenViewModel>(),
        interfaceLock: provider.GetRequiredService<IInterfaceLock>(),
        floatingPanes: new MainWindowLockTests.CountingPanes());

    /// <summary>«Orquestador» documents without an engine whose monitor applies what it reads at once, on the test's thread.</summary>
    private sealed class InlineOrchestrators(TimeProvider time) : IOrchestratorDocumentFactory
    {
        public OrchestratorDocumentViewModel Create(string projectRoot) =>
            new(projectRoot, new OrchestrationSession(OrchestrationBackend.None), new FileCanvasLayoutStore(new ProjectStorageFiles()), time, new InlineSynchronizationContext());
    }

    /// <summary>The question about unsaved changes, during which the idle lock fires: it answers «Guardar» as it locks.</summary>
    private sealed class LockingSave(InterfaceLock interfaceLock) : IUnsavedChangesPrompt
    {
        public int Asked { get; private set; }

        public bool Withdrawn { get; private set; }

        public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave) => Ask(titles, canSave, CancellationToken.None);

        public UnsavedChangesChoice Ask(IReadOnlyList<string> titles, bool canSave, CancellationToken cancellationToken)
        {
            Asked++;

            // The lock applies at once, on this thread: the window hides its content and the background withdraws the question.
            _ = interfaceLock.LockAsync(LockReason.Idle, TestContext.Current.CancellationToken);
            Withdrawn = cancellationToken.IsCancellationRequested;
            return UnsavedChangesChoice.Save;
        }
    }
}
