using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Audit.Checks;

/// <summary>
/// Correct integration (D-33, <c>orchestrator.integration-unverified</c>, high). Only an integration the run accepted moved
/// its branch (<c>IntegrationPerformed</c> on <see cref="OrchestratorAudit.RunBranch"/>, with a result and no conflict, as
/// <c>RunLog.CandidateCommit</c> reads it); one recorded on its candidate ref was refused, and needs nothing. Each accepted
/// one is judged against what the run was required to verify, never more: the verification steps its task names in the
/// approved plan, each of the kind the frozen profile <c>RunStarted</c> anchored gives it (ADR-0016). The log must anchor
/// (<c>EvidenceRegistered</c> version 2, captured by the system, of the integration task and bound to the merged commit):
/// <list type="bullet">
/// <item>when a step captures a <c>TestRun</c>, a passing one of that commit;</item>
/// <item>when a step captures anything else (a build: its <c>BuildLog</c>), evidence of that kind of that commit. The log
/// does not say whether a build passed, only that it ran on that commit; the integrator accepts nothing whose verification
/// failed;</item>
/// <item>all of it before anything builds on the commit: before any task after the integration in the plan is dispatched,
/// and before a delivery of it.</item>
/// </list>
/// A project whose plan and profile ask for no tests is not asked for a <c>TestRun</c>: its absence is no finding at all
/// (every finding becomes an alert of the run, and the memory derives a known error from each). Without a trusted plan or
/// profile what was required is unknown, and only what is wrong whatever was required is judged: a failing <c>TestRun</c> of
/// the commit. An integration task that completed without an accepted integration, and a delivery of a commit that no
/// verified integration of the run produced, are findings too.
/// </summary>
internal static class IntegrationCheck
{
    public static void Run(AuditedRun run, AuditFindings findings)
    {
        var branch = OrchestratorAudit.RunBranch(run.Run);
        var registered = run.Items
            .Where(i => i.Payload is EvidenceRegisteredPayload)
            .Select(i => (i.Sequence, Evidence: (EvidenceRegisteredPayload)i.Payload!))
            .ToList();
        var integrations = new List<(long Sequence, IntegrationPerformedPayload Merge)>();
        var verified = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in run.Items)
        {
            switch (item.Payload)
            {
                case IntegrationPerformedPayload { IsDelivery: false } merge:
                    integrations.Add((item.Sequence, merge));
                    if (Accepted(merge, branch) && Verify(run, item, merge, registered, findings))
                    {
                        verified.TryAdd(merge.ResultCommit!, item.Sequence);
                    }

                    break;
                case IntegrationPerformedPayload { IsDelivery: true } delivery:
                    Delivery(item, delivery, branch, verified, findings);
                    break;
                case TaskStatusChangedPayload { To: WorkTaskStatus.Completed } done when run.KindOf(done.Task) == WorkTaskKind.Integration:
                    Completed(item, done.Task, integrations, branch, findings);
                    break;
            }
        }
    }

    private static bool Accepted(IntegrationPerformedPayload merge, string branch) =>
        merge is { ResultCommit: not null, ConflictPaths.Count: 0 } && string.Equals(merge.TargetRef, branch, StringComparison.Ordinal);

    /// <summary>Checks the evidence of an accepted integration; true when the log shows it verified as it must be.</summary>
    private static bool Verify(AuditedRun run, RunItem item, IntegrationPerformedPayload merge, List<(long Sequence, EvidenceRegisteredPayload Evidence)> registered, AuditFindings findings)
    {
        var commit = merge.ResultCommit!;
        var ofCommit = registered
            .Where(r => r.Evidence.AnchorsMetadata()
                && r.Evidence.Origin == EvidenceOrigin.CapturedBySystem
                && r.Evidence.Task == merge.Task
                && string.Equals(r.Evidence.SubjectCommit, commit, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var problems = new List<string>();
        var tests = ofCommit.Where(r => r.Evidence is { Kind: EvidenceKind.TestRun, TestRun: not null }).ToList();
        var passing = tests.Where(r => r.Evidence.TestRun!.ToSummary().Succeeded).ToList();
        var required = new List<(string What, long Sequence)>();
        var task = merge.Task;
        foreach (var kind in RequiredSteps(run, task) ?? [])
        {
            var steps = string.Join(", ", kind.Select(s => $"'{s.Id}'"));
            if (kind.Key == EvidenceKind.TestRun)
            {
                if (passing.Count == 0)
                {
                    problems.Add($"its step {steps} asks for a TestRun, and none the system captured passed on {commit}");
                }
                else
                {
                    required.Add(("passing TestRun", passing.Min(p => p.Sequence)));
                }

                continue;
            }

            var evidence = ofCommit.Where(r => r.Evidence.Kind == kind.Key).Select(r => r.Sequence).ToList();
            if (evidence.Count == 0)
            {
                problems.Add($"its step {steps} asks for a {kind.Key}, and the system captured none of {commit}");
            }
            else
            {
                required.Add(($"{kind.Key} for its step {steps}", evidence.Min()));
            }
        }

        foreach (var (sequence, failed) in tests.Where(r => !r.Evidence.TestRun!.ToSummary().Succeeded))
        {
            var counters = failed.TestRun!;
            problems.Add(FormattableString.Invariant(
                $"the TestRun {failed.Evidence} of {commit} (entry {sequence}) failed (passed={counters.Passed} failed={counters.Failed} skipped={counters.Skipped} exit={counters.ExitCode})"));
        }

        var tasks = new List<TaskId>();
        if (required.Count > 0 && FirstUse(run, item, merge) is { } use)
        {
            var late = required.Where(r => r.Sequence > use.Sequence).ToList();
            if (late.Count > 0)
            {
                if (use.Dependent is { } dependent)
                {
                    tasks.Add(dependent);
                }

                problems.Add(FormattableString.Invariant(
                    $"its {string.Join(" and ", late.Select(l => $"{l.What} (entry {l.Sequence})"))} came only after {use.What}: an integration is verified before anything depends on it"));
            }
        }

        if (problems.Count == 0)
        {
            return true;
        }

        findings.Add(OrchestratorAudit.IntegrationUnverifiedCode, Severity.High, task is { } named ? [named, .. tasks] : tasks, [item.Sequence], FormattableString.Invariant(
            $"Integration '{task?.Value ?? "-"}' (entry {item.Sequence}) moved the run's branch to {commit}, and {string.Join("; ", problems)}."));
        return false;
    }

    /// <summary>
    /// What the run required of the verification of an integration task, by the kind of evidence the frozen profile gives
    /// each step the approved plan names for it; null when that cannot be known (no trusted plan or profile, or the task is
    /// not in the plan). A step the profile does not have requires nothing the audit could check.
    /// </summary>
    private static IReadOnlyList<IGrouping<EvidenceKind, (string Id, EvidenceKind Kind)>>? RequiredSteps(AuditedRun run, TaskId? task)
    {
        if (task is not { } integration || !run.Specs.TryGetValue(integration, out var spec) || run.Profile is not { } profile)
        {
            return null;
        }

        return [.. spec.VerificationSteps
            .Select(id => (Id: id, Step: profile.Find(id)))
            .Where(s => s.Step is not null)
            .Select(s => (s.Id, s.Step!.Kind))
            .GroupBy(s => s.Kind)];
    }

    /// <summary>
    /// The first entry after an integration that builds on its commit: a task after it in the trusted plan dispatched, or a
    /// delivery of that commit. Null when nothing has yet.
    /// </summary>
    private static (long Sequence, string What, TaskId? Dependent)? FirstUse(AuditedRun run, RunItem integration, IntegrationPerformedPayload merge)
    {
        var descendants = merge.Task is { } task && run.Plan is not null ? run.DescendantsOf(task) : new HashSet<TaskId>();
        foreach (var later in run.Items.Where(i => i.Sequence > integration.Sequence))
        {
            if (AuditedRun.IsStart(later, out var started, out var signal) && descendants.Contains(started))
            {
                return (later.Sequence, FormattableString.Invariant($"task '{started}' started building on it (entry {later.Sequence}, {signal})"), started);
            }

            if (later.Payload is IntegrationPerformedPayload { IsDelivery: true } delivery && delivery.Sources.Contains(merge.ResultCommit!, StringComparer.OrdinalIgnoreCase))
            {
                return (later.Sequence, FormattableString.Invariant($"it was delivered to {delivery.TargetRef} (entry {later.Sequence})"), null);
            }
        }

        return null;
    }

    private static void Delivery(RunItem item, IntegrationPerformedPayload delivery, string branch, Dictionary<string, long> verified, AuditFindings findings)
    {
        var commit = delivery.Sources.FirstOrDefault();
        if (commit is not null && verified.ContainsKey(commit))
        {
            return;
        }

        findings.Add(OrchestratorAudit.IntegrationUnverifiedCode, Severity.High, [], [item.Sequence], FormattableString.Invariant(
            $"Entry {item.Sequence} delivers {commit ?? "nothing"} to {delivery.TargetRef}, and no integration of the run accepted on {branch} with a passing TestRun of that commit produced it before."));
    }

    private static void Completed(RunItem item, TaskId task, List<(long Sequence, IntegrationPerformedPayload Merge)> integrations, string branch, AuditFindings findings)
    {
        var own = integrations.Where(i => i.Merge.Task == task).ToList();
        if (own.Any(i => Accepted(i.Merge, branch)))
        {
            return;
        }

        string message;
        if (own.Count == 0)
        {
            message = FormattableString.Invariant($"Integration task '{task}' completed (entry {item.Sequence}) with no IntegrationPerformed for it.");
        }
        else
        {
            var (sequence, last) = own[^1];
            var why = last.ConflictPaths.Count > 0
                ? $"it conflicted on {string.Join(", ", last.ConflictPaths)}"
                : last.ResultCommit is null
                    ? "it has no result commit"
                    : $"it was recorded on {last.TargetRef}, not on the run's branch {branch}";
            message = FormattableString.Invariant($"Integration task '{task}' completed (entry {item.Sequence}), but its integration (entry {sequence}) was not accepted: {why}.");
        }

        findings.Add(OrchestratorAudit.IntegrationUnverifiedCode, Severity.High, [task], [item.Sequence], message);
    }
}
