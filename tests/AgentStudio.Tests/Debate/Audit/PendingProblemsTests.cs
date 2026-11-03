using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>docs/15 D-31: the problems the report of a whole-project audit lists without a judge, read from the log only.</summary>
public sealed class PendingProblemsTests
{
    private static IReadOnlyList<string> Codes(PreparedRun run) => [.. PendingProblems.From(run.Entries).Select(p => p.Code)];

    [Fact]
    public async Task A_run_that_finished_well_and_was_delivered_leaves_nothing_pending()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();

        Assert.Empty(PendingProblems.From(run.Entries));
    }

    [Theory]
    [InlineData(RunPhase.Failed, PendingProblems.RunFailedCode)]
    [InlineData(null, PendingProblems.RunUnfinishedCode)]
    public async Task A_run_that_failed_or_did_not_finish_is_pending(RunPhase? phase, string code)
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        if (phase is { } finished)
        {
            await run.FinishAsync(finished);
        }

        var problem = Assert.Single(PendingProblems.From(run.Entries));
        Assert.Equal((code, Run.Value), (problem.Code, problem.Source));
        Assert.Contains(Run.Value, problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_review_whose_verdict_needs_a_person_is_pending_until_the_task_is_completed(bool completed)
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1");
        await run.TransitionAsync("R1", WorkTaskStatus.Pending, WorkTaskStatus.Running);
        await run.RecordAsync(new Actor(ActorKind.Judge, Judge.Value), DecisionKind.VerdictIssued, "Veredicto EscalateToHuman válido",
            new VerdictIssuedPayload("v-1", Tid("R1"), VerdictDecision.EscalateToHuman, true, [], run.Frozen.ContentHash, new string('e', 64), null, null) { RunId = Run.Value });
        await run.TransitionAsync("R1", WorkTaskStatus.Running, WorkTaskStatus.AwaitingApproval);
        if (completed)
        {
            await run.TransitionAsync("R1", WorkTaskStatus.AwaitingApproval, WorkTaskStatus.Completed);
        }

        Assert.Equal(completed ? [PendingProblems.RunUnfinishedCode] : [PendingProblems.RunUnfinishedCode, PendingProblems.ReviewNeedsHumanCode], Codes(run));
    }

    [Theory]
    [InlineData(null, PendingProblems.DebateUnfinishedCode)]
    [InlineData(DebateOutcome.Unresolved, PendingProblems.DebateUnresolvedCode)]
    [InlineData(DebateOutcome.EscalatedToUser, PendingProblems.DebateUnresolvedCode)]
    [InlineData(DebateOutcome.Resolved, null)]
    public async Task A_debate_not_closed_or_closed_without_resolution_is_pending(DebateOutcome? outcome, string? code)
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        const string debate = "deb-20261019-0a0b0c";
        await run.RecordAsync(PreparedRun.System, DecisionKind.DebateOpened, "Debate abierto", DebateEntries.Opened(debate));
        if (outcome is { } closed)
        {
            await run.RecordAsync(PreparedRun.System, DecisionKind.DebateClosed, "Debate cerrado",
                DebateEntries.Closed(debate, closed, closed == DebateOutcome.Resolved ? JudgingPanel.Single : JudgingPanel.Disagree, "v-9"));
        }

        Assert.Equal(code is null ? [] : [code], Codes(run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_challenge_is_open_until_its_revision_debate_closes(bool closed)
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        const string revision = "deb-20261019-0d0e0f";
        await run.RecordAsync(PreparedRun.System, DecisionKind.DebateOpened, "Debate de revisión abierto", DebateEntries.Opened(revision, challenges: "veredicto-1"));
        await run.RecordAsync(PreparedRun.Person, DecisionKind.VerdictChallenged, "Veredicto impugnado",
            new VerdictChallengedPayload("veredicto-1", "usuario", new EvidenceId("ev-razones"), new string('f', 64), revision) { RunId = Run.Value });
        if (closed)
        {
            await run.RecordAsync(PreparedRun.System, DecisionKind.DebateClosed, "Revisión cerrada", DebateEntries.Closed(revision, DebateOutcome.Resolved, JudgingPanel.Single, "v-10"));
        }

        Assert.Equal(closed ? [] : [PendingProblems.DebateUnfinishedCode, PendingProblems.ChallengeOpenCode], Codes(run));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("dismissed")]
    [InlineData("followed")]
    [InlineData("dismissed-by-the-system")]
    [InlineData("dismissal-asked-before")]
    [InlineData("granted-without-a-request")]
    public async Task A_request_to_replan_is_pending_until_a_person_dismisses_it_or_a_follow_up_run_starts(string attended)
    {
        var run = await PreparedRun.CleanAsync();
        async Task AskDismissalAsync() =>
            await run.RecordAsync(PreparedRun.System, DecisionKind.ApprovalRequested, "Aprobación pedida: replan.dismiss",
                new ApprovalRequestedPayload(PendingProblems.ReplanDismissalAction, "Descartar la petición.", AgentStudio.Application.Security.RiskLevel.Low, null, []) { RunId = Run.Value });
        async Task AnswerDismissalAsync(Actor by) =>
            await run.RecordAsync(by, DecisionKind.ApprovalGranted, "Petición descartada",
                new ApprovalAnsweredPayload(PendingProblems.ReplanDismissalAction, true, by.Id, null, null) { RunId = Run.Value });

        if (attended == "dismissal-asked-before")
        {
            await AskDismissalAsync();
        }

        await run.RecordAsync(OrchestratorAudit.Auditor, DecisionKind.ReplanRequested, "El auditor pide replanificar",
            new ReplanRequestedPayload([OrchestratorAudit.ApprovalMissingCode], [], "Replanificar.", null) { RunId = Run.Value });
        switch (attended)
        {
            case "dismissed":
                await AskDismissalAsync();
                await AnswerDismissalAsync(PreparedRun.Person);
                break;
            case "dismissed-by-the-system":
                await AskDismissalAsync();
                await AnswerDismissalAsync(PreparedRun.System);
                break;
            case "dismissal-asked-before":
            case "granted-without-a-request":
                await AnswerDismissalAsync(PreparedRun.Person);
                break;
            case "followed":
                await run.RecordAsync(PreparedRun.System, DecisionKind.RunStarted, "Arranca la ejecución de seguimiento",
                    new RunStartedPayload(run.Plan.ContentHash, run.Frozen.ContentHash, BaseCommit, null, run.Profile.ContentHash, [], Run.Value) { RunId = "20261019-f0110f" });
                break;
        }

        var codes = Codes(run);

        if (attended is "dismissed" or "followed")
        {
            Assert.DoesNotContain(PendingProblems.ReplanPendingCode, codes);
        }
        else
        {
            Assert.Equal([PendingProblems.ReplanPendingCode], codes);
        }
    }

    [Fact]
    public async Task Agent_text_in_the_memory_nobody_reviewed_and_pending_tasks_still_in_force_are_pending()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        async Task Remember(string entry, MemoryKind kind, MemoryOrigin origin, string? supersedes = null) =>
            await run.RecordAsync(PreparedRun.System, DecisionKind.MemoryRecorded, "Memoria", MemoryRecordedPayload.Announcing(entry, kind, origin, new string('1', 64), "run:" + Run, supersedes));
        async Task Review(string entry, MemoryReview decision) =>
            await run.RecordAsync(PreparedRun.Person, DecisionKind.MemoryReviewed, "Revisión de memoria", MemoryReviewedPayload.Deciding(entry, decision, "usuario", null));

        await Remember("m-agente", MemoryKind.Decision, MemoryOrigin.Agent);
        await Remember("m-agente-aceptada", MemoryKind.Decision, MemoryOrigin.Agent);
        await Review("m-agente-aceptada", MemoryReview.Accepted);
        await Remember("m-tarea", MemoryKind.PendingTask, MemoryOrigin.System);
        await Remember("m-tarea-retirada", MemoryKind.PendingTask, MemoryOrigin.System);
        await Review("m-tarea-retirada", MemoryReview.Retracted);
        await Remember("m-tarea-vieja", MemoryKind.PendingTask, MemoryOrigin.System);
        await Remember("m-tarea-nueva", MemoryKind.Decision, MemoryOrigin.System, supersedes: "m-tarea-vieja");

        var problems = PendingProblems.From(run.Entries);

        Assert.Equal(
            [(PendingProblems.MemoryUnreviewedCode, "m-agente"), (PendingProblems.MemoryPendingTaskCode, "m-tarea")],
            problems.Select(p => (p.Code, p.Source)));
    }
}
