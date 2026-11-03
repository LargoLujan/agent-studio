using System.Globalization;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// What one piece of work is doing now, reduced to what the tray says of it: its kind, whether it goes on, the phase of a
/// run, the earliest reset a task of it waits for, how many things wait for the person and how many attempts are running.
/// Read from the monitor of a run or the snapshot of a debate; nothing is computed apart.
/// </summary>
public sealed record WorkStatus(
    BackgroundWorkKind Kind,
    string ProjectRoot,
    string? WorkId,
    bool IsActive,
    RunPhase? Phase,
    DateTimeOffset? QuotaUntil,
    bool WaitsForQuota,
    int AwaitingPerson,
    int? AttemptsInFlight)
{
    /// <summary>
    /// The two forms of a block by a limit (docs/13 §3.7 and docs/16 D-37): the reset the provider announced, and the wait the
    /// system estimated because it announced none. The second does not start with the first.
    /// </summary>
    private static readonly string[] RateLimitedPrefixes = ["RateLimitedUntil(", "RateLimitedUntilEstimated("];

    /// <summary>The status of <paramref name="work"/> now.</summary>
    public static WorkStatus Of(IBackgroundWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        switch (work)
        {
            case RunWork run:
                var monitor = run.Monitor;
                var until = monitor.Tasks
                    .Where(t => t.Status == WorkTaskStatus.Blocked)
                    .Select(t => QuotaResetOf(t.BlockReason))
                    .Where(t => t is not null)
                    .Min();
                var waitsForQuota = monitor.Phase == RunPhase.WaitingForQuota || until is not null;
                var awaiting = monitor.Tasks.Count(t => t.PendingApproval is not null || t.Status == WorkTaskStatus.AwaitingApproval);
                return new WorkStatus(BackgroundWorkKind.Run, run.ProjectRoot, run.WorkId, run.IsActive, monitor.Phase, until, waitsForQuota, awaiting, run.AttemptsInFlight);

            case DebateWork debate:
                var waits = debate.Snapshot()?.Awaiting;
                var person = waits?.Reason is DebateAwaitReason.HumanResolution or DebateAwaitReason.FailedTurn ? 1 : 0;
                var limited = waits?.Reason == DebateAwaitReason.ProviderLimit;
                return new WorkStatus(BackgroundWorkKind.Debate, debate.ProjectRoot, debate.WorkId, debate.IsActive, null, limited ? waits!.Until : null, limited, person, debate.AttemptsInFlight);

            default:
                return new WorkStatus(work.Kind, work.ProjectRoot, work.WorkId, work.IsActive, null, null, false, 0, work.AttemptsInFlight);
        }
    }

    /// <summary>
    /// The end of the wait of a task blocked by a limit, from its structured block reason: <c>RateLimitedUntil(&lt;ISO 8601&gt;)</c>
    /// (docs/13 §3.7), or <c>RateLimitedUntilEstimated(&lt;ISO 8601&gt;)</c> when the system estimated it (docs/16 D-37); null for any
    /// other reason, or one that does not parse.
    /// </summary>
    public static DateTimeOffset? QuotaResetOf(string? blockReason)
    {
        if (string.IsNullOrWhiteSpace(blockReason))
        {
            return null;
        }

        var text = blockReason.Trim();
        foreach (var prefix in RateLimitedPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var close = text.IndexOf(')', prefix.Length);
            return close > prefix.Length
                && DateTimeOffset.TryParse(text[prefix.Length..close], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
                ? until
                : null;
        }

        return null;
    }
}

/// <summary>
/// The tooltip of the tray icon (docs/16 D-14, F8-7): a summary in 127 characters at most, the length of <c>szTip</c>
/// without its terminator, such as «Agent Studio · 2 ejecuciones · 1 espera cuota hasta las 14:05». It names no project
/// and no run: counts and times only. When it does not fit, the least important parts go first (what is paused, then the
/// counts of each kind of work, the waits for quota and what is blocked), so what waits for the person is the last to go;
/// only a text that still does not fit is cut.
/// </summary>
public static class TrayToolTip
{
    /// <summary>The longest tooltip Windows shows (<c>NOTIFYICONDATAW.szTip</c> holds 128 characters with the terminator).</summary>
    public const int MaxLength = 127;

    private const string Separator = " · ";

    /// <param name="active">What is going on.</param>
    /// <param name="exit">Where the managed exit stands.</param>
    /// <param name="attemptsLeft">While waiting to exit, the attempts still running (null: some work cannot tell).</param>
    /// <param name="locked">The interface is locked (D-13): the work goes on, and the tooltip says it is locked.</param>
    /// <param name="zone">The zone of the times shown.</param>
    public static string Compose(IReadOnlyList<WorkStatus> active, ExitState exit, int? attemptsLeft, bool locked, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(zone);

        // In the order they are read; the number is how important each part is (the highest goes first when it does not fit).
        var parts = new List<(string Text, int Drop)> { ("Agent Studio", 0) };
        if (locked)
        {
            parts.Add(("bloqueado", 1));
        }

        if (exit == ExitState.WaitingForIdle)
        {
            parts.Add((attemptsLeft switch
            {
                null => "saldrá cuando termine lo que está en curso",
                0 => "saliendo",
                var left => $"saldrá al terminar {WorkTexts.Count(left.Value, "intento en curso", "intentos en curso")}",
            }, 2));
        }
        else if (exit == ExitState.Exiting)
        {
            parts.Add(("saliendo", 2));
        }

        var going = active.Where(w => w.IsActive).ToList();
        if (going.Count == 0)
        {
            if (exit == ExitState.None)
            {
                parts.Add(("sin trabajo en marcha", 3));
            }

            return Fit(parts);
        }

        AddCount(parts, going, BackgroundWorkKind.Run, "ejecución", "ejecuciones", 6);
        AddCount(parts, going, BackgroundWorkKind.Debate, "debate", "debates", 7);
        AddCount(parts, going, BackgroundWorkKind.Audit, "auditoría", "auditorías", 8);
        AddCount(parts, going, BackgroundWorkKind.Planning, "planificación", "planificaciones", 9);

        var quota = going.Where(w => w.WaitsForQuota).ToList();
        if (quota.Count > 0)
        {
            var verb = quota.Count == 1 ? "espera" : "esperan";
            var earliest = quota.Select(w => w.QuotaUntil).Where(t => t is not null).Min();
            parts.Add((earliest is { } at
                ? $"{quota.Count.ToString(CultureInfo.InvariantCulture)} {verb} cuota hasta las {TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture)}"
                : $"{quota.Count.ToString(CultureInfo.InvariantCulture)} {verb} cuota", 5));
        }

        var person = going.Sum(w => w.AwaitingPerson);
        if (person > 0)
        {
            parts.Add(($"{person.ToString(CultureInfo.InvariantCulture)} {(person == 1 ? "espera" : "esperan")} por ti", 3));
        }

        AddPhase(parts, going, RunPhase.Paused, "en pausa", "en pausa", 10);
        AddPhase(parts, going, RunPhase.Blocked, "bloqueada", "bloqueadas", 4);
        return Fit(parts);
    }

    private static void AddCount(List<(string, int)> parts, List<WorkStatus> going, BackgroundWorkKind kind, string one, string many, int drop)
    {
        var count = going.Count(w => w.Kind == kind);
        if (count > 0)
        {
            parts.Add((WorkTexts.Count(count, one, many), drop));
        }
    }

    private static void AddPhase(List<(string, int)> parts, List<WorkStatus> going, RunPhase phase, string one, string many, int drop)
    {
        var count = going.Count(w => w.Phase == phase);
        if (count > 0)
        {
            parts.Add((WorkTexts.Count(count, one, many), drop));
        }
    }

    /// <summary>Drops the least important parts until the text fits, and cuts it only when the first part alone does not.</summary>
    private static string Fit(List<(string Text, int Drop)> parts)
    {
        var text = string.Join(Separator, parts.Select(p => p.Text));
        while (text.Length > MaxLength && parts.Count > 1)
        {
            parts.Remove(parts.MaxBy(p => p.Drop));
            text = string.Join(Separator, parts.Select(p => p.Text));
        }

        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
