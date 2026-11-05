using AgentStudio.Desktop.Lock;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Background;

/// <summary>One answer of «Al cerrar la ventana», and whether it is the one chosen on screen.</summary>
public sealed partial class CloseWindowChoice(CloseWindowAction value, string label) : ObservableObject
{
    public CloseWindowAction Value { get; } = value;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What a screen reader says of an answer of «Al cerrar la ventana» (V34): its label.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// «Trabajo desatendido» in «Ajustes» (docs/16 D-13, D-14 and D-40; docs/04 §18.6): what closing the window does
/// (<see cref="AppSettings.CloseWindow"/>) and «Impedir la suspensión mientras hay trabajo»
/// (<see cref="AppSettings.PreventSleepWhileWorking"/>). Both go to the settings as preferences
/// (<see cref="IAppSettingsStore.SavePreferences"/>), never over the rest. The managed exit reads the first at each close; the
/// second is read again when <see cref="Saved"/> says so (<c>BackgroundHost.SettingsChanged</c>). The settings of Windows Hello
/// and the lock (<see cref="Lock"/>) come with it, and «Ajustes» shows them as their own section. Nothing here can be wrong:
/// «Ajustes» saves this section together with the others (<c>SettingsDocumentViewModel</c>, through <see cref="Apply"/> and
/// <see cref="Accept"/>).
/// </summary>
public sealed partial class UnattendedSettingsViewModel : ObservableObject
{
    public const string Title = "Trabajo desatendido";

    /// <summary>What closing the window does, as the screen says it.</summary>
    public const string CloseWindowExplanation =
        "Con trabajo en marcha, «Preguntar» ofrece seguir en la bandeja, pausar y salir cuando terminen los intentos en curso, "
        + "o salir ahora. Salir con trabajo en marcha siempre pregunta, y tu elección queda en el registro de cada ejecución.";

    /// <summary>What «Impedir la suspensión mientras hay trabajo» does, as the screen says it.</summary>
    public const string PreventSleepExplanation =
        "Mientras haya una ejecución, un debate o una auditoría en marcha, el equipo no se suspende por inactividad. La "
        + "pantalla se puede apagar, y tú puedes suspender el equipo igual.";

    private AppSettings _inForce = AppSettings.Default;

    /// <param name="store">Where what is in force is read from when the section is made.</param>
    /// <param name="lockSettings">The settings of Windows Hello and the lock, which «Ajustes» shows next to these; null when they were not composed.</param>
    public UnattendedSettingsViewModel(IAppSettingsStore store, LockSettingsViewModel? lockSettings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Lock = lockSettings;
        foreach (var choice in CloseWindowChoices)
        {
            choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CloseWindow));
        }

        Show(store.Load());
    }

    /// <summary>Raised after these preferences were saved.</summary>
    public event EventHandler? Saved;

    /// <summary>The settings of Windows Hello and the lock (WP4), a section of «Ajustes» of their own.</summary>
    public LockSettingsViewModel? Lock { get; }

    /// <summary>The three answers of «Al cerrar la ventana», in the order of <see cref="CloseWindowAction"/>; one is selected.</summary>
    public IReadOnlyList<CloseWindowChoice> CloseWindowChoices { get; } =
    [
        new(CloseWindowAction.Ask, "Preguntar si hay trabajo en marcha (sin trabajo, sale)"),
        new(CloseWindowAction.KeepInTray, "Seguir siempre en la bandeja"),
        new(CloseWindowAction.Exit, "Salir de Agent Studio"),
    ];

    /// <summary>The answer chosen on screen; «Preguntar» when none is.</summary>
    public CloseWindowAction CloseWindow => CloseWindowChoices.FirstOrDefault(c => c.IsSelected)?.Value ?? CloseWindowAction.Ask;

    [ObservableProperty]
    private bool _preventSleepWhileWorking;

    /// <summary>Whether what is on screen differs from what is in force.</summary>
    internal bool HasChanges => CloseWindow != Stored(_inForce) || PreventSleepWhileWorking != _inForce.PreventSleepWhileWorking;

    /// <summary>Chooses <paramref name="action"/> as the answer of «Al cerrar la ventana» on screen (the radio buttons do it too).</summary>
    public void Choose(CloseWindowAction action)
    {
        foreach (var choice in CloseWindowChoices)
        {
            choice.IsSelected = choice.Value == action;
        }
    }

    /// <summary>What closing the window does and whether the computer is kept awake, as on screen, put into <paramref name="settings"/>.</summary>
    internal AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { CloseWindow = CloseWindow, PreventSleepWhileWorking = PreventSleepWhileWorking };
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
        Choose(Stored(settings));
        PreventSleepWhileWorking = settings.PreventSleepWhileWorking;
    }

    /// <summary>The stored answer of «Al cerrar la ventana»; «Preguntar» for one that does not exist.</summary>
    private static CloseWindowAction Stored(AppSettings settings) =>
        Enum.IsDefined(settings.CloseWindow) ? settings.CloseWindow : CloseWindowAction.Ask;
}
