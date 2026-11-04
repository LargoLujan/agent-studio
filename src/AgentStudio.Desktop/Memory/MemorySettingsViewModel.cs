using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// «Memoria del proyecto» in «Ajustes» (docs/17 D-06, F12-9): whether the orchestrator may look up what is trusted of the
/// previous days with <c>search_project_history</c> when it plans («Búsqueda en el historial para el orquestador», on by
/// default). It goes to the settings as a preference (<see cref="IAppSettingsStore.SavePreferences"/>), with the other
/// sections and one «Guardar» (<c>SettingsDocumentViewModel</c>, through <see cref="Apply"/> and <see cref="Accept"/>); until
/// then <see cref="HistorySearchOn"/> keeps what is in force. The planner asks the saved setting at each planning, so a change
/// applies to the next one.
/// </summary>
public sealed partial class MemorySettingsViewModel : ObservableObject
{
    /// <summary>The title of the section in «Ajustes».</summary>
    public const string Title = "Memoria del proyecto";

    /// <summary>The name of the switch.</summary>
    public const string HistorySearchLabel = "Búsqueda en el historial para el orquestador";

    /// <summary>What the switch does, as the screen says it.</summary>
    public const string HistorySearchExplanation =
        "Al planificar, el orquestador puede buscar lo de días anteriores del proyecto (decisiones, errores conocidos y la bitácora " +
        "de confianza). Solo lee, no sale de tu equipo y no gasta cuota aparte. Solo funciona con Claude Code y si el orquestador " +
        "recibe la memoria del proyecto en la plantilla. Si la apagas, planifica como antes, y la propuesta lo dice.";

    private AppSettings _inForce = AppSettings.Default;

    /// <param name="store">Where what is in force is read from when the section is made.</param>
    public MemorySettingsViewModel(IAppSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Show(store.Load());
    }

    /// <summary>Raised after the preferences were saved: <see cref="HistorySearchOn"/> holds the new one.</summary>
    public event EventHandler? Saved;

    /// <summary>The switch as the screen shows it; it is in force once saved (<see cref="HistorySearchOn"/>).</summary>
    [ObservableProperty]
    private bool _plannerHistoryToolEnabled;

    /// <summary>Whether the orchestrator may use the search now: the stored setting, or the last saved.</summary>
    public bool HistorySearchOn { get; private set; }

    /// <summary>Whether what is on screen differs from what is in force.</summary>
    internal bool HasChanges => PlannerHistoryToolEnabled != _inForce.PlannerHistoryToolEnabled;

    /// <summary>The switch on screen, put into <paramref name="settings"/>.</summary>
    internal AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { PlannerHistoryToolEnabled = PlannerHistoryToolEnabled };
    }

    /// <summary>What was saved is in force now: it is shown, and <see cref="Saved"/> says so.</summary>
    internal void Accept(AppSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Show(saved);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows again what is in force: what was changed on screen and not saved goes.</summary>
    internal void Revert() => Show(_inForce);

    private void Show(AppSettings settings)
    {
        _inForce = settings;
        HistorySearchOn = settings.PlannerHistoryToolEnabled;
        PlannerHistoryToolEnabled = settings.PlannerHistoryToolEnabled;
        OnPropertyChanged(nameof(HistorySearchOn));
    }
}
