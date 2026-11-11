using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Metrics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Observability;

/// <summary>
/// «Contexto y cuota» in «Ajustes» (docs/14 D-11 and D-15): the global context thresholds and the opt-in that lets Agent Studio
/// read the Codex quota. The thresholds are checked with the rule of the evaluator before anything is saved, and both go to the
/// settings as preferences (<see cref="IAppSettingsStore.SavePreferences"/>), never over the rest. Until a save succeeds,
/// <see cref="Thresholds"/> and <see cref="CodexOptedIn"/> keep what is in force. «Ajustes» saves this section together with
/// the others (<c>SettingsDocumentViewModel</c>, through <see cref="Validate"/>, <see cref="Apply"/> and <see cref="Accept"/>).
/// </summary>
public sealed partial class ObservabilitySettingsViewModel : ObservableObject
{
    /// <summary>The title of the section in «Ajustes».</summary>
    public static string Title => Texts.Get("Observability_Settings_Title");

    /// <summary>The rule of the thresholds, as the screen states it.</summary>
    public static string RuleText => Texts.Get("Observability_Settings_Rule");

    /// <summary>
    /// What the opt-in allows, as the screen states it (docs/14 D-15): every moment the quota is read (docs/17 F13-20, the text
    /// says what is done): on demand, after each run of Codex at most once every five minutes (<c>QuotaViewModel.CodexReadInterval</c>),
    /// and while a run waits for a limit of Codex (<c>AccessCheck</c> of the engine).
    /// </summary>
    public static string CodexExplanation => Texts.Get("Observability_Settings_CodexExplanation");

    private AppSettings _inForce = AppSettings.Default;

    /// <param name="store">Where what is in force is read from when the section is made.</param>
    public ObservabilitySettingsViewModel(IAppSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Show(store.Load());
    }

    /// <summary>Raised after the preferences were saved: <see cref="Thresholds"/> and <see cref="CodexOptedIn"/> hold the new ones.</summary>
    public event EventHandler? Saved;

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private string _compressText = string.Empty;

    [ObservableProperty]
    private string _criticalText = string.Empty;

    /// <summary>The opt-in as the switch shows it; it is in force once saved (<see cref="CodexOptedIn"/>).</summary>
    [ObservableProperty]
    private bool _codexQuotaReadEnabled;

    /// <summary>Why the thresholds on screen cannot be saved; null when they can.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The thresholds in force: the stored ones, or the last saved.</summary>
    public ContextHealthThresholds Thresholds { get; private set; } = new();

    /// <summary>Whether the Codex quota may be read: the stored opt-in, or the last saved.</summary>
    public bool CodexOptedIn { get; private set; }

    /// <summary>Whether what is on screen differs from what is in force (a text that is not a number counts as a change).</summary>
    internal bool HasChanges =>
        Whole(WarningText) != _inForce.ContextWarningPercent
        || Whole(CompressText) != _inForce.ContextCompressPercent
        || Whole(CriticalText) != _inForce.ContextCriticalPercent
        || CodexQuotaReadEnabled != _inForce.CodexQuotaReadEnabled;

    /// <summary>
    /// Checks the thresholds on screen with the rule of the evaluator. Null when they can be saved; otherwise why not, which
    /// <see cref="ErrorMessage"/> shows too.
    /// </summary>
    internal string? Validate()
    {
        ErrorMessage = null;
        if (Parsed() is not { } thresholds)
        {
            var wrong = new[] { WarningText, CompressText, CriticalText }.First(text => Whole(text) is null).Trim();
            ErrorMessage = Texts.Format("Observability_Settings_NotWhole", wrong);
        }
        else if (!AppSettings.AreValidContextThresholds(thresholds.Warning, thresholds.Compress, thresholds.Critical))
        {
            ErrorMessage = Texts.Get("Observability_Settings_BadOrder");
        }

        return ErrorMessage;
    }

    /// <summary>The thresholds and the opt-in on screen, put into <paramref name="settings"/>; only once <see cref="Validate"/> said yes.</summary>
    internal AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (warning, compress, critical) = Parsed() ?? throw new InvalidOperationException("The thresholds on screen are not whole numbers: validate them before applying them.");
        return settings.WithContextThresholds(warning, compress, critical) with { CodexQuotaReadEnabled = CodexQuotaReadEnabled };
    }

    /// <summary>What was saved is in force now: it is shown, and <see cref="Saved"/> says so.</summary>
    internal void Accept(AppSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        Show(saved);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows again what is in force: what was changed on screen and not saved goes, and so does the error.</summary>
    internal void Revert()
    {
        ErrorMessage = null;
        Show(_inForce);
    }

    private void Show(AppSettings settings)
    {
        _inForce = settings;
        Thresholds = settings.ContextThresholds();
        CodexOptedIn = settings.CodexQuotaReadEnabled;
        WarningText = settings.ContextWarningPercent.ToString(CultureInfo.InvariantCulture);
        CompressText = settings.ContextCompressPercent.ToString(CultureInfo.InvariantCulture);
        CriticalText = settings.ContextCriticalPercent.ToString(CultureInfo.InvariantCulture);
        CodexQuotaReadEnabled = settings.CodexQuotaReadEnabled;
        OnPropertyChanged(nameof(Thresholds));
        OnPropertyChanged(nameof(CodexOptedIn));
    }

    /// <summary>The three thresholds on screen as whole numbers; null when one is not.</summary>
    private (int Warning, int Compress, int Critical)? Parsed() =>
        Whole(WarningText) is { } warning && Whole(CompressText) is { } compress && Whole(CriticalText) is { } critical
            ? (warning, compress, critical)
            : null;

    private static int? Whole(string? text) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
