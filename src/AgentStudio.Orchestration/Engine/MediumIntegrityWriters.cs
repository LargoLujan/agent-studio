using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>Why a task of a plan writes at medium integrity (ADR-0018, decision 3).</summary>
public enum MediumIntegrityReason
{
    /// <summary>An agent task whose agent is not in the roster, or that names none: it counts as a writer (fail closed).</summary>
    UnknownAgent,

    /// <summary>An executor with a shell (<c>Bash</c>) in a worktree it may change.</summary>
    Shell,

    /// <summary>An executor that writes files on Codex, whose sandbox always has a shell.</summary>
    CodexWrites,

    /// <summary>An executor that writes files on a CLI whose confinement to the worktree was never measured (Gemini, any other).</summary>
    UnmeasuredCli,

    /// <summary>An executor that writes files on a Claude Code whose installed version was not measured, or that nobody could ask.</summary>
    UnmeasuredClaudeVersion,

    /// <summary>A Claude Code executor that writes files with a pre-approved tool the measurement did not cover.</summary>
    ToolsBeyondMeasuredProfile,

    /// <summary>A verification step or tool the plan runs that the profile does not have: it counts as one without isolation (fail closed).</summary>
    UnknownStep,

    /// <summary>A verification step or tool the plan runs at the user's rights (any isolation but Low).</summary>
    StepWithoutIsolation,
}

/// <summary>
/// One reason a task of a plan writes at medium integrity: the agent of an agent task (<see cref="Agent"/>, and
/// <see cref="Provider"/> or <see cref="Tools"/> when they are the reason), or a step it runs (<see cref="Step"/>).
/// </summary>
public sealed record MediumIntegrityWriter(TaskId Task, MediumIntegrityReason Reason)
{
    /// <summary>The agent of the task; null for a step, and for an agent task that names none.</summary>
    public AgentId? Agent { get; init; }

    /// <summary>The CLI the agent writes with, for <see cref="MediumIntegrityReason.UnmeasuredCli"/>.</summary>
    public ProviderId? Provider { get; init; }

    /// <summary>The tools beyond the measured profile, for <see cref="MediumIntegrityReason.ToolsBeyondMeasuredProfile"/>.</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>The verification step or tool, for <see cref="MediumIntegrityReason.UnknownStep"/> and <see cref="MediumIntegrityReason.StepWithoutIsolation"/>.</summary>
    public string? Step { get; init; }
}

/// <summary>
/// The writers at medium integrity a plan creates (ADR-0018, decision 3), read once for both of their consequences: the
/// check of the user's repository becomes strict when there is any (<see cref="UserRepositoryGuard.CanWriteAtMediumIntegrity"/>,
/// ADR-0012), and the warning of the plan names each of them before it is approved and in its approval request
/// (<see cref="Verification.VerificationStepText.PlanWarning(PlanDefinition, IEnumerable{AgentDefinition}, VerificationProfile, bool)"/>).
/// One reading for both, so the warning and the check never disagree. <c>RunStarted</c> anchors them (<see cref="Anchor"/>),
/// and a run is resumed only while they are still the ones it was approved with (<see cref="AreTheApproved"/>, G-02).
/// </summary>
public static class MediumIntegrityWriters
{
    /// <summary>
    /// Every writer at medium integrity of <paramref name="plan"/>, in the order of its tasks: where a same-length swap
    /// inside an ignored folder of the user's repository is possible (ADR-0012).
    /// <list type="bullet">
    /// <item>An executor given a shell in a worktree it may change (the shell <see cref="AgentAttemptWorker.Permissions"/> grants).</item>
    /// <item>An executor that may write files there on any CLI but a Claude Code whose confinement to the worktree was
    /// measured at the version installed now, with permissions no wider than the measured ones (ADR-0018, «Medición»:
    /// 2.1.290 with the launch of <c>ClaudeCodeArguments</c>; Codex, whose sandbox always has a shell, and Gemini always
    /// count).</item>
    /// <item>A verification step or tool the plan runs at the user's rights (any isolation but Low): one a task names, or
    /// one such a step requires (<see cref="VerificationStep.Requires"/>), which the engine runs before it.</item>
    /// </list>
    /// A task whose agent is not in the roster, or a step the profile does not have, counts as one: fail closed. The
    /// judge and the planner only read; a step of the profile that no task runs, nor requires, never runs.
    /// </summary>
    /// <param name="claudeWritesConfined">
    /// Whether the installed Claude Code is a version where the confinement of an executor without a shell was measured,
    /// with the launch its adapter builds today (<see cref="IWriteConfinementProbe"/>); false when nobody could say.
    /// </param>
    public static IReadOnlyList<MediumIntegrityWriter> Find(PlanDefinition plan, IEnumerable<AgentDefinition> roster, VerificationProfile profile, bool claudeWritesConfined)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(profile);
        var agents = roster.ToList();
        var writers = new List<MediumIntegrityWriter>();
        foreach (var spec in plan.Tasks)
        {
            if (spec.Kind == WorkTaskKind.AgentWork)
            {
                var assigned = agents.Where(a => a.Id == spec.Agent).ToList();
                if (assigned.Count == 0)
                {
                    writers.Add(new MediumIntegrityWriter(spec.Id, MediumIntegrityReason.UnknownAgent) { Agent = spec.Agent });
                }

                writers.AddRange(assigned.Select(agent => Writer(spec, agent, claudeWritesConfined)).OfType<MediumIntegrityWriter>());
            }

            // Every step the task runs: each one it names, after the steps that one requires (VerificationStep.Requires),
            // exactly as the engine runs them (RunContext.StepsOf); a step required but missing counts as unknown too.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in spec.VerificationSteps.Append(spec.ToolId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (profile.Find(id) is null)
                {
                    writers.Add(new MediumIntegrityWriter(spec.Id, MediumIntegrityReason.UnknownStep) { Step = id });
                    continue;
                }

                var steps = profile.StepsToRun([id], out var missing);
                foreach (var absent in missing.Where(seen.Add))
                {
                    writers.Add(new MediumIntegrityWriter(spec.Id, MediumIntegrityReason.UnknownStep) { Step = absent });
                }

                foreach (var step in steps.Where(s => seen.Add(s.Id) && s.Isolation != ProcessIsolation.LowIntegrity))
                {
                    writers.Add(new MediumIntegrityWriter(spec.Id, MediumIntegrityReason.StepWithoutIsolation) { Step = step.Id });
                }
            }
        }

        return writers;
    }

    /// <summary>
    /// <paramref name="writers"/> as <c>RunStarted</c> anchors them when the run starts (version 3, G-02): what the warning
    /// approved with the plan names, without its wording, so a later reading can be compared with them.
    /// </summary>
    public static IReadOnlyList<MediumIntegrityWriterEntry> Anchor(IReadOnlyList<MediumIntegrityWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(writers);
        return [.. writers.Select(w => new MediumIntegrityWriterEntry(w.Task, w.Reason.ToString(), w.Agent, w.Provider, w.Tools.Count > 0 ? [.. w.Tools] : null, w.Step))];
    }

    /// <summary>
    /// Whether <paramref name="writers"/>, read now, are exactly the writers a run was approved with (<paramref name="approved"/>,
    /// as <see cref="Anchor"/> wrote them): the same task, reason, agent, CLI, tools and step for each, in any order. One
    /// more, one fewer or one for another reason is not what the user approved, whichever way it goes.
    /// </summary>
    public static bool AreTheApproved(IReadOnlyList<MediumIntegrityWriterEntry> approved, IReadOnlyList<MediumIntegrityWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(writers);
        return Keys(approved).SequenceEqual(Keys(Anchor(writers)), StringComparer.Ordinal);
    }

    /// <summary>One canonical text per writer, sorted: a value that may be missing is <c>-</c>, never the same as an empty one.</summary>
    private static IEnumerable<string> Keys(IEnumerable<MediumIntegrityWriterEntry> writers) =>
        writers
            .Select(w => ContentHash.Canonical([w.Task.Value, w.Reason, Optional(w.Agent?.Value), Optional(w.Provider?.Value), Optional(w.Step), .. w.Tools ?? []]))
            .Order(StringComparer.Ordinal);

    private static string Optional(string? value) => value is null ? "-" : "=" + value;

    /// <summary>
    /// Why <paramref name="agent"/> writes at medium integrity in <paramref name="spec"/>, or null when it does not. A shell
    /// writes wherever the user can; so do the file tools of a CLI, unless it is a Claude Code whose confinement to the
    /// worktree was measured at the installed version and the executor has no permission the measurement did not cover
    /// (<see cref="MeasuredWriteConfinement.Covers"/>: any other pre-approved tool counts as able to write anywhere). An
    /// executor that may not write files writes nowhere.
    /// </summary>
    private static MediumIntegrityWriter? Writer(TaskSpec spec, AgentDefinition agent, bool claudeWritesConfined)
    {
        var permissions = AgentAttemptWorker.Permissions(spec, agent);
        var writer = new MediumIntegrityWriter(spec.Id, MediumIntegrityReason.Shell) { Agent = agent.Id };
        if (permissions.AllowShell)
        {
            return writer;
        }

        if (!permissions.AllowFileWrites)
        {
            return null;
        }

        var provider = agent.Model.Provider;
        if (provider == ProviderId.CodexCli)
        {
            return writer with { Reason = MediumIntegrityReason.CodexWrites };
        }

        if (provider != ProviderId.ClaudeCode)
        {
            return writer with { Reason = MediumIntegrityReason.UnmeasuredCli, Provider = provider };
        }

        if (!claudeWritesConfined)
        {
            return writer with { Reason = MediumIntegrityReason.UnmeasuredClaudeVersion };
        }

        return MeasuredWriteConfinement.Covers(permissions)
            ? null
            : writer with
            {
                Reason = MediumIntegrityReason.ToolsBeyondMeasuredProfile,
                Tools = [.. permissions.AllowedTools.Where(tool => !MeasuredWriteConfinement.Executor.AllowedTools.Contains(tool, StringComparer.OrdinalIgnoreCase))],
            };
    }
}
