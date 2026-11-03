using System.Globalization;
using AgentStudio.Application.Persistence;
using HashFunctions = AgentStudio.Domain.Common.ContentHash;

namespace AgentStudio.Application.Orchestration;

/// <summary>
/// The canonical SHA-256 of the immutable header of a run (<see cref="RunRecord"/>): everything the engine runs with that
/// the user approved when the run started. <c>RunStarted</c> (version 2) anchors it in the decision log, and resuming
/// recomputes it from the stored header and refuses any other, so a header rewritten behind the store's back (an agent
/// given <c>Bash</c>, a looser budget, another plan) is never resumed.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It covers the time the run started (as an instant), the hashes of the plan, the frozen requirements and the
/// verification profile, the evidence of the original request, the base commit and ref, the run it follows, the options
/// and every member of every agent of the roster: instructions, objectives, tools, path grants, budget, rounds, memory
/// and review policy.</item>
/// <item>The roster is written with the encoding of <see cref="RosterHash"/>, the only one there is: in the order of the
/// agents' ids, so the order a store lists it in does not matter; two entries with the same id stay in their order. The
/// run id and the project are the keys the header is found by, not part of it (<c>RunStarted</c> itself names the
/// run).</item>
/// <item>Fields are length-prefixed (<see cref="HashFunctions.Canonical"/>); a value that may be missing is written as
/// <c>-</c> when missing and with a leading <c>=</c> otherwise, so a missing value is never an empty one.</item>
/// <item>The options of Phase 8 (<see cref="RunOptions.QuotaResetMargin"/>, <see cref="RunOptions.MaxEstimatedQuotaWait"/>
/// and <see cref="RunOptions.AutoResumeConsent"/>, ADR-0034) come after the roster, and only when they are not the defaults,
/// each as its name and its value in a fixed order: the hash of every header stored before them does not change. The roster
/// delimits itself (its count and the fields of each agent), so a header with them never reads as one without.</item>
/// </list>
/// </remarks>
public static class RunHeaderHash
{
    private const string Version = "run-header/1";

    public static string Of(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var options = run.Options;
        var fields = new List<string?>
        {
            Version,
            Number(run.CreatedAt.UtcTicks),
            run.PlanHash,
            run.RequirementSetHash,
            run.OriginalRequest.Value,
            run.BaseCommit,
            Optional(run.BaseRef),
            run.Verification.ContentHash,
            Optional(run.Follows?.Value),
            Number(options.MaxParallelTasks),
            Number(options.MaxAgentRuns),
            Optional(options.StallThreshold),
            Optional(options.HeartbeatInterval),
            Optional(options.DefaultTaskTimeout),
            Optional(options.MaxAutoWaitForQuota),
        };

        RosterHash.Append(fields, run.Roster);
        AppendPhase8Options(fields, options);
        return HashFunctions.Sha256Hex(HashFunctions.Canonical([.. fields]));
    }

    private static void AppendPhase8Options(List<string?> fields, RunOptions options)
    {
        if (options.QuotaResetMargin is { } margin)
        {
            fields.Add("quotaResetMargin");
            fields.Add(Number(margin.Ticks));
        }

        if (options.MaxEstimatedQuotaWait is { } cap)
        {
            fields.Add("maxEstimatedQuotaWait");
            fields.Add(Number(cap.Ticks));
        }

        if (options.AutoResumeConsent)
        {
            fields.Add("autoResumeConsent");
            fields.Add("true");
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Optional(string? value) => value is null ? "-" : "=" + value;

    private static string Optional(TimeSpan? value) => Optional(value?.Ticks.ToString(CultureInfo.InvariantCulture));
}
