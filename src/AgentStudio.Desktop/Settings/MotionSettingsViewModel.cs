using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Settings;

/// <summary>
/// «Reducir movimiento», a section of «Ajustes» (docs/17 D-15 and F13-7) next to «Idioma»: when it is on, the animations of
/// the canvas are off, as when Windows turns off the animations of its windows. It is saved with the one «Guardar» of the
/// document, through <see cref="IAppSettingsStore.SavePreferences"/> (<see cref="AppSettings.ReduceMotion"/>).
/// </summary>
public sealed partial class MotionSettingsViewModel : ObservableObject
{
    private readonly IUiTextProvider _texts;
    private AppSettings _inForce = AppSettings.Default;

    public MotionSettingsViewModel(IAppSettingsStore store, IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _texts = texts ?? Texts.Current;
        Show(store.Load());
    }

    /// <summary>Raised when the preferences were saved and this section accepted them.</summary>
    public event EventHandler? Saved;

    public string Title => _texts.Get("Settings_ReduceMotion");

    public string Help => _texts.Get("Settings_ReduceMotion_Help");

    /// <summary>The animations of the canvas are off.</summary>
    [ObservableProperty]
    private bool _reduceMotion;

    internal bool HasChanges => ReduceMotion != _inForce.ReduceMotion;

    internal AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { ReduceMotion = ReduceMotion };
    }

    internal void Accept(AppSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Show(saved);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    internal void Revert() => Show(_inForce);

    private void Show(AppSettings settings)
    {
        _inForce = settings;
        ReduceMotion = settings.ReduceMotion;
    }
}
