using System.IO;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.Settings;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Settings;

/// <summary>
/// «Ajustes» (docs/03 §5.2): every preference of the app in one document, one section each, and one «Guardar» that checks every
/// section with its own rules and writes all of them at once (one <see cref="IAppSettingsStore.SavePreferences"/>), or nothing.
/// A change that is not saved marks the document as dirty. Over a store in memory: nothing touches the user's settings.
/// </summary>
public sealed class SettingsDocumentViewModelTests
{
    private static (SettingsDocumentViewModel Document, int[] Saved) Create(IAppSettingsStore store, bool phase8 = true)
    {
        var context = new ObservabilitySettingsViewModel(store);
        var unattended = phase8 ? new UnattendedSettingsViewModel(store, new LockSettingsViewModel(store)) : null;
        var document = new SettingsDocumentViewModel(store, context, unattended, time: new FakeTimeProvider(T0));
        var saved = new int[3];
        document.Context.Saved += (_, _) => saved[0]++;
        if (document.Unattended is { } section)
        {
            section.Saved += (_, _) => saved[1]++;
        }

        if (document.Lock is { } lockSection)
        {
            lockSection.Saved += (_, _) => saved[2]++;
        }

        return (document, saved);
    }

    [Fact]
    public void It_shows_every_section_with_what_is_in_force_and_nothing_to_save()
    {
        var store = new CountingStore(AppSettings.Default.WithContextThresholds(60, 80, 90) with { CloseWindow = CloseWindowAction.Exit, IdleLockEnabled = true, IdleLockMinutes = 30 });

        var (document, _) = Create(store);

        Assert.Equal("Ajustes", document.Title);
        Assert.Equal(("60", "80", "90"), (document.Context.WarningText, document.Context.CompressText, document.Context.CriticalText));
        Assert.Equal(CloseWindowAction.Exit, document.Unattended!.CloseWindow);
        Assert.Equal((true, "30"), (document.Lock!.IdleLockEnabled, document.Lock.IdleLockMinutesText));
        Assert.Same(document.Unattended.Lock, document.Lock);
        Assert.False(document.IsDirty);
        Assert.True(document.CanSave);
        Assert.Equal(0, store.PreferenceSaves);
    }

    [Fact]
    public void Guardar_writes_every_preference_at_once_keeps_the_rest_and_each_section_says_so()
    {
        var store = new CountingStore(AppSettings.Default.WithProjectOpened(@"C:\proyectos\calc"));
        var (document, saved) = Create(store);

        document.Context.WarningText = " 60 ";
        document.Context.CompressText = "80";
        document.Context.CriticalText = "90";
        document.Context.CodexQuotaReadEnabled = true;
        document.Unattended!.Choose(CloseWindowAction.KeepInTray);
        document.Unattended.PreventSleepWhileWorking = true;
        document.Lock!.HelloForCriticalApprovals = false;
        document.Lock.IdleLockEnabled = true;
        document.Lock.IdleLockMinutesText = "30";
        Assert.True(document.IsDirty);

        document.SaveCommand.Execute(null);

        // One write, with every section in it, and nothing else of the settings changed.
        Assert.Equal(1, store.PreferenceSaves);
        Assert.Equal(0, store.Saves);
        var stored = store.Current;
        Assert.Equal((60, 80, 90, true), (stored.ContextWarningPercent, stored.ContextCompressPercent, stored.ContextCriticalPercent, stored.CodexQuotaReadEnabled));
        Assert.Equal((CloseWindowAction.KeepInTray, true), (stored.CloseWindow, stored.PreventSleepWhileWorking));
        Assert.Equal((false, true, 30), (stored.WindowsHelloForImportantApprovals, stored.IdleLockEnabled, stored.IdleLockMinutes));
        Assert.Equal(@"C:\proyectos\calc", stored.LastProjectPath);

        // Each section takes what was saved and says so once: the orchestrators, «Consumo» and «mantener despierto» follow.
        Assert.Equal([1, 1, 1], saved);
        Assert.Equal((0.60, 0.80, 0.90), (document.Context.Thresholds.Warning, document.Context.Thresholds.Compress, document.Context.Thresholds.Critical));
        Assert.True(document.Context.CodexOptedIn);
        Assert.Equal("60", document.Context.WarningText);
        Assert.Equal("Ajustes guardados a las 10:00:00.", document.Notice);
        Assert.Null(document.ErrorMessage);
        Assert.False(document.IsDirty);
    }

    [Theory]
    [InlineData("80", "70", "95", "Los umbrales no se guardan: deben cumplir 0 < aviso < compresión < crítico ≤ 100.")]
    [InlineData("setenta", "85", "95", "Los umbrales no se guardan: «setenta» no es un número entero de 1 a 100.")]
    public void Wrong_thresholds_save_nothing_and_say_where(string warning, string compress, string critical, string why)
    {
        var store = new CountingStore();
        var (document, saved) = Create(store);
        document.Unattended!.PreventSleepWhileWorking = true;

        document.Context.WarningText = warning;
        document.Context.CompressText = compress;
        document.Context.CriticalText = critical;
        document.SaveCommand.Execute(null);

        Assert.Equal(0, store.PreferenceSaves);
        Assert.Equal([0, 0, 0], saved);
        Assert.Equal("No se guardó nada: corrige «Contexto y cuota».", document.ErrorMessage);
        Assert.Equal(why, document.Context.ErrorMessage);
        Assert.Null(document.Lock!.ErrorMessage);
        Assert.Null(document.Notice);

        // Nothing changed what is in force, and the change is still there to correct.
        Assert.Equal(0.70, document.Context.Thresholds.Warning);
        Assert.False(store.Current.PreventSleepWhileWorking);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void Two_wrong_sections_are_both_named_and_both_say_why()
    {
        var store = new CountingStore();
        var (document, _) = Create(store);

        document.Context.CriticalText = "101";
        document.Lock!.IdleLockMinutesText = "1441";
        document.SaveCommand.Execute(null);

        Assert.Equal(0, store.PreferenceSaves);
        Assert.Equal("No se guardó nada: corrige «Contexto y cuota» y «Windows Hello y bloqueo».", document.ErrorMessage);
        Assert.NotNull(document.Context.ErrorMessage);
        Assert.Equal("No se guardó el bloqueo: «1441» no es un número entero de minutos entre 1 y 1440. Escribe un número válido.", document.Lock.ErrorMessage);
    }

    [Fact]
    public void The_time_of_the_idle_lock_is_checked_even_while_the_lock_is_off()
    {
        var store = new CountingStore();
        var (document, _) = Create(store);

        document.Lock!.IdleLockEnabled = false;
        document.Lock.IdleLockMinutesText = "0";
        document.SaveCommand.Execute(null);

        Assert.Equal(0, store.PreferenceSaves);
        Assert.Equal("No se guardó nada: corrige «Windows Hello y bloqueo».", document.ErrorMessage);
    }

    [Fact]
    public void Correcting_the_section_and_saving_again_clears_the_messages()
    {
        var store = new CountingStore();
        var (document, _) = Create(store);
        document.Context.WarningText = "x";
        document.SaveCommand.Execute(null);

        document.Context.WarningText = "65";
        document.SaveCommand.Execute(null);

        Assert.Equal(1, store.PreferenceSaves);
        Assert.Equal(65, store.Current.ContextWarningPercent);
        Assert.Null(document.ErrorMessage);
        Assert.Null(document.Context.ErrorMessage);
        Assert.NotNull(document.Notice);
    }

    [Fact]
    public void A_store_that_cannot_write_saves_nothing_says_why_and_keeps_what_is_in_force()
    {
        var store = new FailingStore();
        var (document, saved) = Create(store);

        document.Context.WarningText = "60";
        document.Unattended!.Choose(CloseWindowAction.Exit);
        document.SaveCommand.Execute(null);

        Assert.Equal("No se pudieron guardar los ajustes: Disco lleno.", document.ErrorMessage);
        Assert.Null(document.Notice);
        Assert.Equal([0, 0, 0], saved);
        Assert.Equal(0.70, document.Context.Thresholds.Warning);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void A_change_on_screen_marks_the_document_dirty_and_undoing_it_clears_it()
    {
        var (document, _) = Create(new CountingStore());

        document.Unattended!.CloseWindowChoices[2].IsSelected = true;
        document.Unattended.CloseWindowChoices[0].IsSelected = false;
        Assert.True(document.IsDirty);

        document.Unattended.Choose(CloseWindowAction.Ask);
        Assert.False(document.IsDirty);

        document.Lock!.IdleLockMinutesText = "quince";
        Assert.True(document.IsDirty);

        document.Lock.IdleLockMinutesText = " 15 ";
        Assert.False(document.IsDirty);

        document.Context.CodexQuotaReadEnabled = true;
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void Without_phase_8_the_document_saves_context_and_quota_alone()
    {
        var store = new CountingStore();
        var (document, saved) = Create(store, phase8: false);

        document.Context.CodexQuotaReadEnabled = true;
        document.SaveCommand.Execute(null);

        Assert.Null(document.Unattended);
        Assert.Null(document.Lock);
        Assert.Equal(1, store.PreferenceSaves);
        Assert.True(store.Current.CodexQuotaReadEnabled);
        Assert.Equal([1, 0, 0], saved);
    }

    [Fact]
    public async Task Saving_from_the_question_about_unsaved_changes_is_the_same_as_guardar()
    {
        var store = new CountingStore();
        var (document, _) = Create(store);

        document.Unattended!.PreventSleepWhileWorking = true;
        await document.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(store.Current.PreventSleepWhileWorking);
        Assert.False(document.IsDirty);

        // A wrong section keeps the document dirty: the tab or the app stays open and the document says why.
        document.Lock!.IdleLockMinutesText = "0";
        await document.SaveAsync(TestContext.Current.CancellationToken);

        Assert.True(document.IsDirty);
        Assert.Equal("No se guardó nada: corrige «Windows Hello y bloqueo».", document.ErrorMessage);
    }

    [Fact]
    public async Task Only_a_save_asked_from_the_question_that_fails_asks_to_show_the_document()
    {
        var (document, _) = Create(new CountingStore());
        var asked = 0;
        document.ShowRequested += (_, _) => asked++;

        // Its own «Guardar» is on screen already: it says why there.
        document.Context.WarningText = "x";
        document.SaveCommand.Execute(null);
        Assert.Equal(0, asked);

        // «Guardar» chosen in the question about unsaved changes, and not done: the document asks to be shown.
        await document.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, asked);

        // Done: nothing to show.
        document.Context.WarningText = "60";
        await document.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, asked);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task A_save_from_the_question_that_cannot_write_asks_to_show_the_document_too()
    {
        var (document, _) = Create(new FailingStore());
        var asked = 0;
        document.ShowRequested += (_, _) => asked++;

        document.Context.CodexQuotaReadEnabled = true;
        await document.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, asked);
        Assert.Equal("No se pudieron guardar los ajustes: Disco lleno.", document.ErrorMessage);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void The_history_search_of_the_orchestrator_is_on_by_default_and_turning_it_off_is_saved_with_the_rest()
    {
        var store = new CountingStore(AppSettings.Default.WithProjectOpened(@"C:\proyectos\calc"));
        var memory = new MemorySettingsViewModel(store);
        var document = new SettingsDocumentViewModel(store, new ObservabilitySettingsViewModel(store), time: new FakeTimeProvider(T0), memory: memory);
        var saved = 0;
        memory.Saved += (_, _) => saved++;

        Assert.Same(memory, document.Memory);
        Assert.True(AppSettings.Default.PlannerHistoryToolEnabled);
        Assert.True(memory.PlannerHistoryToolEnabled && memory.HistorySearchOn);
        Assert.False(document.IsDirty);
        Assert.Equal("Búsqueda en el historial para el orquestador", MemorySettingsViewModel.HistorySearchLabel);
        Assert.Contains("Si la apagas, planifica como antes", MemorySettingsViewModel.HistorySearchExplanation, StringComparison.Ordinal);

        // Turned off on screen: nothing is in force until «Guardar»; then one write, with the rest of the preferences.
        memory.PlannerHistoryToolEnabled = false;
        Assert.True(document.IsDirty);
        Assert.True(memory.HistorySearchOn);
        document.SaveCommand.Execute(null);

        Assert.Equal(1, store.PreferenceSaves);
        Assert.False(store.Current.PlannerHistoryToolEnabled);
        Assert.Equal(@"C:\proyectos\calc", store.Current.LastProjectPath);
        Assert.False(memory.HistorySearchOn);
        Assert.Equal(1, saved);
        Assert.False(document.IsDirty);

        // Undoing a change on screen clears the mark.
        memory.PlannerHistoryToolEnabled = true;
        Assert.True(document.IsDirty);
        memory.PlannerHistoryToolEnabled = false;
        Assert.False(document.IsDirty);
    }

    /// <summary>The settings in memory, with the writes of each kind counted.</summary>
    internal sealed class CountingStore(AppSettings? initial = null) : IAppSettingsStore
    {
        public AppSettings Current { get; private set; } = initial ?? AppSettings.Default;

        public int Saves { get; private set; }

        public int PreferenceSaves { get; private set; }

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Saves++;
            Current = settings;
        }

        public void SavePreferences(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _ = settings.ContextThresholds();
            PreferenceSaves++;
            Current = settings;
        }
    }

    private sealed class FailingStore : IAppSettingsStore
    {
        public AppSettings Load() => AppSettings.Default;

        public void Save(AppSettings settings) => throw new IOException("Disco lleno.");

        public void SavePreferences(AppSettings settings) => throw new IOException("Disco lleno.");
    }
}
