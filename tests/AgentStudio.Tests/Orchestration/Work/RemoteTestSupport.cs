using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// What the projections of Phase 10 read, written into the real log of a <see cref="WorkWorld"/> as WP3 will write it: the observations of a pull request, the operations the app
/// did on the remote and the integration the judge was shown. The commits are those of <see cref="HostingP0Data"/>: the commit the judge saw is <see cref="Head"/>.
/// </summary>
internal static class RemoteTestSupport
{
    public const string Run = WorkWorld.Run1;

    public static Task ObserveAsync(this WorkWorld world, RemoteSnapshot snapshot, string? run = Run, params RemoteChangeKind[] changes) =>
        world.RecordAsync(Run, DecisionKind.RemoteStateObserved, RemoteStateObservedPayload.Observing(snapshot, run, null, changes));

    /// <summary>An observation the log holds but that is not what an observation holds: the fingerprint is not that of its snapshot.</summary>
    public static Task ObserveWithAWrongFingerprintAsync(this WorkWorld world, RemoteSnapshot snapshot) =>
        world.RecordAsync(Run, DecisionKind.RemoteStateObserved, RemoteStateObservedPayload.Observing(snapshot, Run, null, []) with { Fingerprint = new string('0', 64) });

    public static Task OperationAsync(
        this WorkWorld world,
        HostingOperationKind operation,
        int? pullRequest = 12,
        MergeMethod? method = null,
        HostingOutcome outcome = HostingOutcome.Ok,
        string? account = null,
        string run = Run) =>
        world.RecordAsync(
            Run,
            DecisionKind.HostingOperationPerformed,
            HostingOperationPerformedPayload.Of(
                operation, run, Repository, pullRequest, operation is HostingOperationKind.PushRunBranch ? RemoteBranch.ForRun(new RunId(run)).Name : null, Head, method, null, 1, outcome, null,
                account is null ? null : RemoteLogin.TryParse(account), null, null, null, null));

    /// <summary>The integration of the run into its own branch (what the dossier of the judge is built on), then the verdict of the judge on the head of the log that has it.</summary>
    public static async Task JudgeAsync(this WorkWorld world, VerdictDecision decision = VerdictDecision.Approved, string candidate = Head, string run = Run)
    {
        await world.RecordAsync(run, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, "agentstudio/run-" + run, [], candidate, [], false) { RunId = run });
        var log = await world.Instance.Recorder.LoadAsync(world.ProjectId, WorkWorld.Ct);
        await world.VerdictAsync(run, decision, head: log.HeadHash);
    }

    /// <summary>A started and linked run whose T1 completed and finished, judged on <see cref="Head"/>: the board has R1 in «hecho» (an approval) or in review.</summary>
    public static async Task<(WorkWorld World, AgentStudio.Domain.Work.Milestones.MilestoneId Milestone)> JudgedRunAsync(VerdictDecision decision = VerdictDecision.Approved)
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new AgentStudio.Domain.Work.Milestones.MilestoneTarget(AgentStudio.Domain.Work.Milestones.MilestoneTargetKind.Run, WorkWorld.Run1), "manuel", WorkWorld.Ct);
        await world.RunAndCompleteAsync(Run, "T1");
        await world.FinishAsync(Run, AgentStudio.Application.Orchestration.RunPhase.Succeeded);
        await world.JudgeAsync(decision);
        return (world, view.Header.Id);
    }

    /// <summary>Sorts out the shape of a board to compare two of them: the column and the reason of every card.</summary>
    public static List<(string Id, AgentStudio.Domain.Work.Milestones.BoardColumn Column, string? Reason)> Shape(AgentStudio.Application.Work.MilestoneBoard board) =>
        [.. board.Requirements.Concat(board.Tasks).Select(c => (c.Id, c.Column, c.Reason))];
}
