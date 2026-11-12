using System.Globalization;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Lock;

/// <summary>
/// «Windows Hello y bloqueo» in «Ajustes» (docs/16 D-13, docs/04 §18.6): «Pedir Windows Hello también en las aprobaciones
/// importantes» (on by default, and it can be turned off; it governs only the configurable approvals: what is on the list of
/// the fixed barrier asks Windows Hello whatever it says, docs/17 D-38) and the idle lock (off by default; from 1 to
/// <see cref="AppSettings.MaxIdleLockMinutes"/> minutes, checked even while it is off). Its texts come from the resources
/// (<see cref="HelloTexts"/>). They are checked before anything is saved, and go to the settings as preferences
/// (<see cref="IAppSettingsStore.SavePreferences"/>), never over the rest. The approvals and the idle lock read them at each
/// question and each check: nothing else needs to be told. «Ajustes» saves this section together with the others
/// (<c>SettingsDocumentViewModel</c>, through <see cref="Validate"/>, <see cref="Apply"/> and <see cref="Accept"/>).
/// </summary>
public sealed partial class LockSettingsViewModel : ObservableObject
{
    /// <summary>The title of the section in «Ajustes».</summary>
    public static string Title => Texts.Get("Lock_Settings_Title");

    /// <summary>What the idle lock does, as the screen says it.</summary>
    public static string IdleExplanation => Texts.Get("Lock_Settings_IdleExplanation");

    /// <summary>The unit next to the time of the idle lock, with the longest time it accepts.</summary>
    public static string MinutesUnit => Texts.Format("Lock_Settings_MinutesUnit", AppSettings.MaxIdleLockMinutes);

    private AppSettings _inForce = AppSettings.Default;

    /// <param name="store">Where what is in force is read from when the section is made.</param>
    public LockSettingsViewModel(IAppSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Show(store.Load());
    }

    /// <summary>Raised after the preferences were saved.</summary>
    public event EventHandler? Saved;

    [ObservableProperty]
    private bool _helloForCriticalApprovals;

    [ObservableProperty]
    private bool _idleLockEnabled;

    [ObservableProperty]
    private string _idleLockMinutesText = string.Empty;

    /// <summary>Why the time of the idle lock on screen cannot be saved; null when it can.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Whether what is on screen differs from what is in force (a time that is not a number counts as a change).</summary>
    internal bool HasChanges =>
        HelloForCriticalApprovals != _inForce.WindowsHelloForImportantApprovals
        || IdleLockEnabled != _inForce.IdleLockEnabled
        || Minutes() != _inForce.IdleLockMinutes;

    /// <summary>
    /// Checks the time of the idle lock on screen. Null when it can be saved; otherwise why not, which <see cref="ErrorMessage"/>
    /// shows too.
    /// </summary>
    internal string? Validate()
    {
        ErrorMessage = Minutes() is null
            ? Texts.Format("Lock_Settings_Invalid", (IdleLockMinutesText ?? string.Empty).Trim(), AppSettings.MaxIdleLockMinutes)
            : null;
        return ErrorMessage;
    }

    /// <summary>Windows Hello and the idle lock as on screen, put into <paramref name="settings"/>; only once <see cref="Validate"/> said yes.</summary>
    internal AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minutes = Minutes() ?? throw new InvalidOperationException("The time of the idle lock on screen is not valid: validate it before applying it.");
        return settings.WithIdleLock(IdleLockEnabled, minutes) with { WindowsHelloForImportantApprovals = HelloForCriticalApprovals };
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
        HelloForCriticalApprovals = settings.WindowsHelloForImportantApprovals;
        IdleLockEnabled = settings.IdleLockEnabled;
        IdleLockMinutesText = settings.IdleLockMinutes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The minutes on screen when they are a time the idle lock accepts; null otherwise.</summary>
    private int? Minutes() =>
        int.TryParse((IdleLockMinutesText ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
        && AppSettings.IsValidIdleLockMinutes(minutes)
            ? minutes
            : null;
}
