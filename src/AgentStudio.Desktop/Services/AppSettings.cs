using System.IO;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Desktop.Services;

/// <summary>What closing the main window does (docs/16 D-14). Minimizing does what it always did.</summary>
public enum CloseWindowAction
{
    /// <summary>With work going on, ask whether to keep it going in the tray or to exit; with none, exit. The default (D-14).</summary>
    Ask,

    /// <summary>Hide the window: the app, and the work going on, stay in the tray.</summary>
    KeepInTray,

    /// <summary>Exit the app; with work going on, through the managed exit (D-16).</summary>
    Exit,
}

/// <summary>Per-user application settings persisted as JSON.</summary>
public sealed record AppSettings
{
    public const int MaxRecentProjects = 10;

    /// <summary>Where the user keeps the skills index on this machine; used while nothing else is configured and the folder exists.</summary>
    public const string DefaultSkillsIndexPath = @"C:\skills-index";

    /// <summary>The context health thresholds of decision 6 of docs/14 section 12, in percent of the window.</summary>
    public const int DefaultContextWarningPercent = 70;

    /// <inheritdoc cref="DefaultContextWarningPercent"/>
    public const int DefaultContextCompressPercent = 85;

    /// <inheritdoc cref="DefaultContextWarningPercent"/>
    public const int DefaultContextCriticalPercent = 95;

    /// <summary>The time without input after which the interface locks, when the idle lock is on (docs/16 D-13), in minutes.</summary>
    public const int DefaultIdleLockMinutes = 15;

    /// <summary>The longest time without input that can be set for the idle lock: a day.</summary>
    public const int MaxIdleLockMinutes = 24 * 60;

    public string? LastProjectPath { get; init; }

    /// <summary>Most recent first, no duplicates, at most <see cref="MaxRecentProjects"/>.</summary>
    public IReadOnlyList<string> RecentProjects { get; init; } = [];

    /// <summary>
    /// Root folder of the user's skills index, the one that holds <c>catalog.json</c> (docs/10). Null when there is
    /// none: the skills catalog then says it is not configured, and agents run without skills.
    /// </summary>
    public string? SkillsIndexPath { get; init; }

    /// <summary>
    /// The context health thresholds (docs/14 D-11), in percent of the model's window: aviso, compresión recomendada and
    /// crítico. Global, 70, 85 and 95 by default (decision 6 of docs/14 section 12), and they must keep
    /// 0 &lt; aviso &lt; compresión &lt; crítico &lt;= 100 (<see cref="ContextHealthThresholds"/>); a file that breaks the rule is
    /// read with the defaults. These, <see cref="CodexQuotaReadEnabled"/>, <see cref="PlannerHistoryToolEnabled"/> and the
    /// settings of Phase 8 (from <see cref="CloseWindow"/> on) are the preferences of the panels, written by
    /// <see cref="IAppSettingsStore.SavePreferences"/>.
    /// </summary>
    public int ContextWarningPercent { get; init; } = DefaultContextWarningPercent;

    /// <inheritdoc cref="ContextWarningPercent"/>
    public int ContextCompressPercent { get; init; } = DefaultContextCompressPercent;

    /// <inheritdoc cref="ContextWarningPercent"/>
    public int ContextCriticalPercent { get; init; } = DefaultContextCriticalPercent;

    /// <summary>
    /// The user opted in to reading the Codex quota (docs/14 D-15, decision 1 of section 12): on demand and once after each
    /// Codex run, a read of <c>account/rateLimits/read</c> of its app-server with the CLI's own session and no turn of the
    /// model. Off by default; while it is off nothing is launched.
    /// </summary>
    public bool CodexQuotaReadEnabled { get; init; }

    /// <summary>
    /// The orchestrator may look up what is trusted of the previous days with <c>search_project_history</c> when it plans
    /// (docs/17 D-06, F12-9; «Búsqueda en el historial para el orquestador» in «Ajustes»). On by default; turning it off
    /// leaves every planning without the tool, and the proposal says so. It is asked at each planning.
    /// </summary>
    public bool PlannerHistoryToolEnabled { get; init; } = true;

    // ---- Phase 8 (docs/16 D-13, D-14 and D-40): the packages of the phase read them; until they do, nothing changes ----

    /// <summary>What closing the main window does (D-14): by default it asks when there is work going on, and exits when there is none.</summary>
    public CloseWindowAction CloseWindow { get; init; } = CloseWindowAction.Ask;

    /// <summary>
    /// Critical approvals ask Windows Hello after the dialog (D-13, F8-11): on by default, and it can be turned off. Without
    /// Hello on this computer the dialog decides alone, with a warning, and the answer records it.
    /// </summary>
    public bool WindowsHelloForCriticalApprovals { get; init; } = true;

    /// <summary>
    /// The interface locks after <see cref="IdleLockMinutes"/> without input (D-13, F8-10). Off by default. The work goes on
    /// while it is locked, and it is unlocked with Windows Hello.
    /// </summary>
    public bool IdleLockEnabled { get; init; }

    /// <summary>
    /// The minutes without input after which the interface locks, from 1 to <see cref="MaxIdleLockMinutes"/>;
    /// <see cref="DefaultIdleLockMinutes"/> by default, and a file with a time out of range is read with it.
    /// </summary>
    public int IdleLockMinutes { get; init; } = DefaultIdleLockMinutes;

    /// <summary>While there is work going on, the computer does not sleep when idle (D-40). Off by default; the person can still put it to sleep.</summary>
    public bool PreventSleepWhileWorking { get; init; }

    /// <summary>
    /// Where the main window was when the app last ended, and whether it was maximized; null until it first ends (the window
    /// then opens centred, 1280 × 800). It has a writer of its own, <see cref="IAppSettingsStore.SaveWindowPlacement"/>, which
    /// the window calls on the real exit and never when it hides in the tray; the other two keep it as it is stored.
    /// </summary>
    public WindowPlacement? MainWindowPlacement { get; init; }

    public static AppSettings Default { get; } = new();

    /// <summary>Whether <paramref name="minutes"/> is a time the idle lock accepts: from 1 to <see cref="MaxIdleLockMinutes"/>.</summary>
    public static bool IsValidIdleLockMinutes(int minutes) => minutes is >= 1 and <= MaxIdleLockMinutes;

    /// <summary>Returns settings with this idle lock.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minutes"/> is not from 1 to <see cref="MaxIdleLockMinutes"/>.</exception>
    public AppSettings WithIdleLock(bool enabled, int minutes)
    {
        ThrowIfInvalidIdleLock(minutes);
        return this with { IdleLockEnabled = enabled, IdleLockMinutes = minutes };
    }

    /// <summary>Checks the preferences before they are saved: the context thresholds and the time of the idle lock.</summary>
    /// <exception cref="ArgumentOutOfRangeException">One of them breaks its rule.</exception>
    internal void ValidatePreferences()
    {
        _ = ContextThresholds();
        ThrowIfInvalidIdleLock(IdleLockMinutes);
    }

    private static void ThrowIfInvalidIdleLock(int minutes)
    {
        if (!IsValidIdleLockMinutes(minutes))
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), minutes, $"The idle lock time must be from 1 to {MaxIdleLockMinutes} minutes.");
        }
    }

    /// <summary>Whether the thresholds keep the rule of <see cref="ContextHealthThresholds"/>: 0 &lt; aviso &lt; compresión &lt; crítico &lt;= 100.</summary>
    public static bool AreValidContextThresholds(int warning, int compress, int critical)
    {
        try
        {
            _ = Thresholds(warning, compress, critical);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>The thresholds as the context health evaluator takes them (fractions of the window).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The thresholds break the rule (only a value built by hand can).</exception>
    public ContextHealthThresholds ContextThresholds() => Thresholds(ContextWarningPercent, ContextCompressPercent, ContextCriticalPercent);

    /// <summary>Returns settings with these thresholds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">They break the rule of <see cref="ContextHealthThresholds"/>.</exception>
    public AppSettings WithContextThresholds(int warning, int compress, int critical)
    {
        _ = Thresholds(warning, compress, critical);
        return this with { ContextWarningPercent = warning, ContextCompressPercent = compress, ContextCriticalPercent = critical };
    }

    /// <summary>These settings with the preferences of <paramref name="other"/> (thresholds, the Codex opt-in, the history search of the orchestrator and the settings of Phase 8).</summary>
    internal AppSettings WithPreferencesOf(AppSettings other) =>
        this with
        {
            ContextWarningPercent = other.ContextWarningPercent,
            ContextCompressPercent = other.ContextCompressPercent,
            ContextCriticalPercent = other.ContextCriticalPercent,
            CodexQuotaReadEnabled = other.CodexQuotaReadEnabled,
            PlannerHistoryToolEnabled = other.PlannerHistoryToolEnabled,
            CloseWindow = other.CloseWindow,
            WindowsHelloForCriticalApprovals = other.WindowsHelloForCriticalApprovals,
            IdleLockEnabled = other.IdleLockEnabled,
            IdleLockMinutes = other.IdleLockMinutes,
            PreventSleepWhileWorking = other.PreventSleepWhileWorking,
        };

    private static ContextHealthThresholds Thresholds(int warning, int compress, int critical) =>
        new(warning / 100d, compress / 100d, critical / 100d);

    /// <summary>Returns settings where <paramref name="projectPath"/> is the last and most recent project.</summary>
    public AppSettings WithProjectOpened(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var normalized = NormalizePath(projectPath);
        var recents = new List<string> { normalized };
        recents.AddRange(RecentProjects.Where(p => !SamePath(p, normalized)));
        return this with
        {
            LastProjectPath = normalized,
            RecentProjects = recents.Take(MaxRecentProjects).ToList(),
        };
    }

    /// <summary>Returns settings without <paramref name="projectPath"/> (for example because the folder vanished).</summary>
    public AppSettings WithProjectRemoved(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return this with
        {
            LastProjectPath = LastProjectPath is not null && SamePath(LastProjectPath, projectPath) ? null : LastProjectPath,
            RecentProjects = RecentProjects.Where(p => !SamePath(p, projectPath)).ToList(),
        };
    }

    internal static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool SamePath(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
}
