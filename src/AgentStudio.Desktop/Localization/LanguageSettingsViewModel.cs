using System.Globalization;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Localization;

/// <summary>One answer of the language list in «Ajustes»: a language of <see cref="UiLanguages.All"/> or «El de Windows».</summary>
/// <param name="CultureName">The culture saved when this answer is chosen; null for «El de Windows».</param>
/// <param name="Label">What the list shows: the language in its own language, or «El de Windows (…)».</param>
public sealed record LanguageChoice(string? CultureName, string Label)
{
    /// <summary>What a screen reader says of an item of the list: its label.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// «Idioma» in «Ajustes» (docs/17 D-49, ADR-0054): the language of the interface, chosen from <see cref="UiLanguages.All"/>
/// (each one in its own language) or «El de Windows», which saves null. The language is applied once at startup
/// (<see cref="Texts.UseLanguage"/>), so a change applies when Agent Studio restarts, and while the choice would change the
/// language in use, <see cref="RestartNotice"/> says so. The choice goes to the settings as a preference
/// (<see cref="IAppSettingsStore.SavePreferences"/>), never over the rest. «Ajustes» saves this section together with the
/// others (<c>SettingsDocumentViewModel</c>, through <see cref="Apply"/> and <see cref="Accept"/>); nothing here can be wrong.
/// </summary>
public sealed partial class LanguageSettingsViewModel : ObservableObject
{
    private readonly IUiTextProvider _texts;
    private readonly CultureInfo _windowsCulture;
    private readonly UiLanguage _running;
    private AppSettings _inForce = AppSettings.Default;

    /// <param name="store">Where what is in force is read from when the section is made.</param>
    /// <param name="texts">The texts of the language in use, which is the language the person is looking at now; the one of <see cref="Texts"/> by default.</param>
    /// <param name="windowsCulture">The language of the interface of Windows, for «El de Windows»; <see cref="Texts.WindowsUiCulture"/> by default.</param>
    public LanguageSettingsViewModel(IAppSettingsStore store, IUiTextProvider? texts = null, CultureInfo? windowsCulture = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _texts = texts ?? Texts.Current;
        _windowsCulture = windowsCulture ?? Texts.WindowsUiCulture;
        _running = UiLanguages.Find(_texts.Culture.Name) ?? UiLanguages.Resolve(null, _windowsCulture);
        var windowsName = UiLanguages.Resolve(null, _windowsCulture).NativeName;
        Choices =
        [
            new LanguageChoice(null, _texts.Format("Language_Windows", windowsName)),
            .. UiLanguages.All.Select(l => new LanguageChoice(l.CultureName, l.NativeName)),
        ];
        _selected = Choices[0];
        Show(store.Load());
    }

    /// <summary>Raised after the preferences were saved.</summary>
    public event EventHandler? Saved;

    /// <summary>The title of the section in «Ajustes».</summary>
    public string Title => _texts.Get("Info_Language_Title");

    /// <summary>«El de Windows» first, then the languages of the list in their own language.</summary>
    public IReadOnlyList<LanguageChoice> Choices { get; }

    /// <summary>The answer chosen on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartNotice), nameof(HasRestartNotice))]
    private LanguageChoice _selected;

    /// <summary>«El idioma cambia al reiniciar Agent Studio.» while the choice on screen would change the language in use; null otherwise.</summary>
    public string? RestartNotice => WouldChangeLanguage() ? _texts.Get("Language_AppliesOnRestart") : null;

    /// <summary>Whether <see cref="RestartNotice"/> is shown.</summary>
    public bool HasRestartNotice => RestartNotice is not null;

    /// <summary>Whether what is on screen differs from what is in force.</summary>
    internal bool HasChanges => !string.Equals(Selected.CultureName, StoredName(_inForce), StringComparison.Ordinal);

    /// <summary>The choice on screen put into <paramref name="settings"/> as its language: null for «El de Windows».</summary>
    internal AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { Language = Selected.CultureName };
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
        var name = StoredName(settings);
        Selected = Choices.First(c => string.Equals(c.CultureName, name, StringComparison.Ordinal));
    }

    /// <summary>The culture name of the list the settings hold; null for «El de Windows» and for a language that is not in the list.</summary>
    private static string? StoredName(AppSettings settings) => UiLanguages.Find(settings.Language)?.CultureName;

    private bool WouldChangeLanguage() =>
        !string.Equals(UiLanguages.Resolve(Selected.CultureName, _windowsCulture).CultureName, _running.CultureName, StringComparison.OrdinalIgnoreCase);
}
