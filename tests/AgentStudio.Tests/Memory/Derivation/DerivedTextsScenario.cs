using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Memory.Derivation;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Derivation;

/// <summary>
/// A log with every shape the derivation writes a text for (plans, deliveries, pending tasks in every state, failed tests,
/// conflicts, alerts, integrations, verdicts, challenges, replans and debates, in runs, debates and audits), so the tests of the
/// words of the memory (the anchored text never changes, and its coded form says the same in Spanish) go over all of them.
/// </summary>
internal static class DerivedTextsScenario
{
    public const string Debate = "deb-20261019-a1b2c3";
    public const string Audit = "aud-20261019-0000a1";
    private static readonly string[] EmptyList = [];

    public static async Task WriteAsync(DerivationWorld world)
    {
        await PlansAndDeliveriesAsync(world);
        await PendingAsync(world);
        await KnownErrorsAndChangesAsync(world);
        await VerdictsAsync(world);
        await DebatesAsync(world);
        await ChallengesAndReplansAsync(world);
        await UnitsAsync(world);
    }

    private static string RunId(int n) => $"20261101-{n:x6}";

    private static string DebateId(int n) => $"deb-20261101-{n:x6}";

    private static async Task PlansAndDeliveriesAsync(DerivationWorld world)
    {
        // Granted with one task, granted with several, denied by a person, denied without a valid answer from a person, and no plan known.
        var one = world.Run(RunId(1));
        var (hashOne, _) = await one.PlanAsync("Plan de una tarea.", 1);
        await one.StartAsync(hashOne, "manuel", ("T1", WorkTaskKind.AgentWork));
        var many = world.Run(RunId(2));
        var (hashMany, _) = await many.PlanAsync("Plan de varias tareas.");
        await many.StartAsync(hashMany, "manuel", ("T1", WorkTaskKind.AgentWork), ("T2", WorkTaskKind.Review), ("T3", WorkTaskKind.Integration));
        await world.Run(RunId(3)).RecordAsync(
            DecisionKind.ApprovalDenied,
            new ApprovalAnsweredPayload(MemoryDerivation.PlanApprovalAction, false, "manuel", null, null) { RunId = RunId(3) },
            new Actor(ActorKind.User, "manuel"));
        await world.Run(RunId(4)).RecordAsync(
            DecisionKind.ApprovalDenied,
            new ApprovalAnsweredPayload(MemoryDerivation.PlanApprovalAction, false, "auto", null, null) { RunId = RunId(4) },
            RunWriter.System);
        await world.Run(RunId(5)).RecordAsync(
            DecisionKind.ApprovalGranted,
            new ApprovalAnsweredPayload(MemoryDerivation.PlanApprovalAction, true, "manuel", null, null) { RunId = RunId(5) },
            new Actor(ActorKind.User, "manuel"));

        // Deliveries: with 2 paths, with 1, with none, denied, with no request before the answer, and one with more paths than are listed.
        foreach (var (n, paths) in new[] { (6, new[] { "src/A.cs", "src/B.cs" }), (7, ["src/A.cs"]), (8, EmptyList) })
        {
            var run = world.Run(RunId(n));
            await run.FinishAsync(RunPhase.Succeeded, CommitA, "v1");
            await run.DeliverAsync(true, paths, CommitB, CommitA);
        }

        await world.Run(RunId(9)).DeliverAsync(false, ["src/A.cs"], CommitB, CommitA);
        await world.Run(RunId(10)).RecordAsync(
            DecisionKind.ApprovalGranted,
            new ApprovalAnsweredPayload(MemoryDerivation.DeliveryApprovalAction, true, "manuel", null, null) { RunId = RunId(10) },
            new Actor(ActorKind.User, "manuel"));
        await world.Run(RunId(11)).RecordAsync(
            DecisionKind.ApprovalDenied,
            new ApprovalAnsweredPayload(MemoryDerivation.DeliveryApprovalAction, false, "auto", null, null) { RunId = RunId(11) },
            RunWriter.System);
        var long11 = world.Run(RunId(12));
        await long11.DeliverAsync(true, [.. Enumerable.Range(1, 45).Select(i => $"src/File{i:00}.cs")], CommitB, CommitA);
    }

    private static async Task PendingAsync(DerivationWorld world)
    {
        // One run with a task in every state and every block reason the engine writes.
        var run = world.Run(RunId(20));
        var (hash, _) = await run.PlanAsync("Plan con todo.");
        await run.StartAsync(
            hash,
            "manuel",
            ("T1", WorkTaskKind.AgentWork),
            ("T2", WorkTaskKind.Condition),
            ("T3", WorkTaskKind.ToolRun),
            ("T4", WorkTaskKind.Review),
            ("T5", WorkTaskKind.Debate),
            ("T6", WorkTaskKind.HumanApproval),
            ("T7", WorkTaskKind.Integration),
            ("T8", WorkTaskKind.AgentWork),
            ("T9", WorkTaskKind.AgentWork),
            ("T10", WorkTaskKind.AgentWork),
            ("T11", WorkTaskKind.AgentWork),
            ("T12", WorkTaskKind.AgentWork),
            ("T13", WorkTaskKind.AgentWork),
            ("T14", WorkTaskKind.AgentWork));
        await run.MoveAsync("T1", WorkTaskStatus.Pending, WorkTaskStatus.Completed, "ok");
        await run.MoveAsync("T2", WorkTaskStatus.Pending, WorkTaskStatus.Running, "go");
        await run.MoveAsync("T3", WorkTaskStatus.Running, WorkTaskStatus.AwaitingApproval, "wait");
        await run.MoveAsync("T5", WorkTaskStatus.Running, WorkTaskStatus.Cancelled, "stop");
        await run.MoveAsync("T6", WorkTaskStatus.Running, WorkTaskStatus.Failed, "fail", attempt: 3);
        await run.MoveAsync("T7", WorkTaskStatus.Pending, WorkTaskStatus.Pending, "again", attempt: 1);
        await run.MoveAsync("T8", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "b1", "AwaitingHuman");
        await run.MoveAsync("T9", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "b2", "Interrupted", 2);
        await run.MoveAsync("T10", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "b3", "ProviderUnavailable");
        await run.MoveAsync("T11", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "b4", "RateLimitedUntil(2026-11-01T10:30:00.0000000+00:00)");
        await run.MoveAsync("T12", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "b5", "RateLimitedUntilEstimated(2026-11-01T11:45:00.0000000+00:00)");
        await run.MoveAsync("T13", WorkTaskStatus.Running, WorkTaskStatus.Blocked, "b6", "SomethingElse");
        await run.MoveAsync("T14", WorkTaskStatus.Pending, WorkTaskStatus.Skipped, "skip");
        await run.FinishAsync(RunPhase.Failed);

        // The phase of a run that ended with something unfinished, one run for each.
        var phases = Enum.GetValues<RunPhase>().ToList();
        for (var i = 0; i < phases.Count; i++)
        {
            var id = RunId(30 + i);
            var each = world.Run(id);
            await each.RecordAsync(DecisionKind.TaskCreated, new TaskCreatedPayload(Tid("T1"), "Título", WorkTaskKind.AgentWork, JoinPolicy.AllCompleted, 1) { RunId = id });
            await each.FinishAsync(phases[i]);
        }
    }

    private static async Task KnownErrorsAndChangesAsync(DerivationWorld world)
    {
        var run = world.Run(RunId(50));
        // Failed tests: of a task with its attempt, of a task without attempt, of the run; with no passes; and a bad exit code with passes.
        await run.TestRunAsync("T1", 1, Red, CommitA);
        await run.TestRunAsync("T1", 2, new TestRunSummary(5, 1, 0, 1), CommitB);
        await run.CaptureAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Tests (test)", "agent-studio:verification", "# tests\n", "text/plain", null, null, new TestRunSummary(0, 0, 3, 1), CommitA);
        await run.CaptureAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Tests (build)", "agent-studio:verification", "# tests\n", "text/plain", "T2", null, new TestRunSummary(4, 0, 2, 7), null);

        // Conflicts: with and without a task, one file and several.
        await run.IntegrationAsync("T3", run.Candidate("T3"), CommitA, [CommitA, CommitB], "src/Calc.cs");
        await run.RecordAsync(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, run.Candidate("T9"), [CommitA], CommitA, ["src/One.cs", "src/Two.cs"], false) { RunId = RunId(50) });

        // Alerts: with tasks and message, without tasks, without message.
        await run.AlertAsync("task.retries-exhausted", "Task 'T1' failed and has no retries left.", "T1", "T2");
        await run.AlertAsync("run.something", "A message with no tasks.");
        await run.AlertAsync("run.silent", " ");

        // Changes: an integration with a task and sources, without task and without sources, and a delivery that no request precedes.
        await run.IntegrationAsync("T3", run.Branch, CommitA, [CommitB, CommitA]);
        await run.RecordAsync(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, run.Branch, [], CommitB, [], false) { RunId = RunId(50) });
        await run.RecordAsync(DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(null, "refs/heads/main", [CommitA], CommitB, [], true) { RunId = RunId(50) });
    }

    private static async Task VerdictsAsync(DerivationWorld world)
    {
        var run = world.Run(RunId(60));
        await run.VerdictAsync("v1", "T7", VerdictDecision.Approved, true, []);
        await run.VerdictAsync("v2", "T7", VerdictDecision.ChangesRequested, false, []);
        await run.VerdictAsync("v3", "T7", VerdictDecision.Rejected, false, [VerdictViolationCode.ApprovedWithoutTestRun], supersedes: "v1");
        await run.VerdictAsync("v4", "T8", VerdictDecision.EscalateToHuman, true, []);
        await run.VerdictAsync("v5", "T8", VerdictDecision.Unresolved, true, []);
        await run.RecordAsync(
            DecisionKind.VerdictIssued,
            new VerdictIssuedPayload("v6", null, VerdictDecision.Approved, true, [], HashA, HashB, null, null) { RunId = RunId(60) },
            new Actor(ActorKind.Judge, "juez"));

        // A verdict that revises a challenged one (D-40).
        var revision = world.Run(RunId(61));
        await revision.VerdictAsync("v0", "T7", VerdictDecision.EscalateToHuman, true, []);
        var (reasons, _) = await revision.CaptureAsync(EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, "Razones", "manuel", "Razones.", "text/plain", null, null);
        await revision.RecordAsync(
            DecisionKind.VerdictChallenged,
            new VerdictChallengedPayload("v0", "manuel", reasons.Id, reasons.ContentSha256, DebateId(70)) { RunId = RunId(61) },
            new Actor(ActorKind.User, "manuel"));
        await revision.VerdictAsync("r1", "T7", VerdictDecision.Approved, true, [], supersedes: "v0");
        await revision.RecordAsync(
            DecisionKind.VerdictIssued,
            new VerdictIssuedPayload("r2", null, VerdictDecision.Approved, true, [], HashA, HashB, null, null) { RunId = RunId(61) },
            new Actor(ActorKind.Judge, "juez"));
    }

    private static async Task DebatesAsync(DerivationWorld world)
    {
        var n = 80;
        foreach (var mode in Enum.GetValues<DebateMode>())
        {
            var debate = new DebateWriter(world, DebateId(n++));
            await debate.OpenAsync("¿Qué hacemos?", mode);
            await debate.CloseAsync(DebateOutcome.Resolved, JudgingPanel.Single, "P1");
        }

        var variants = new (DebateOutcome Outcome, string Rule, string? Chosen, DebateSubjectKind Subject, string? Ref, string? Challenges)[]
        {
            (DebateOutcome.Unresolved, JudgingPanel.Agree, null, DebateSubjectKind.Commit, "abc1234", null),
            (DebateOutcome.EscalatedToUser, JudgingPanel.Disagree, "no es una etiqueta!", DebateSubjectKind.Run, "20261015-a1b2c3", null),
            (DebateOutcome.Aborted, DebateClosedPayload.AbortedRule, null, DebateSubjectKind.Verdict, "v9", null),
            (DebateOutcome.Resolved, JudgingPanel.Invalid, "P2", DebateSubjectKind.Commit, "con espacios", null),
            (DebateOutcome.Resolved, JudgingPanel.User, "P1", DebateSubjectKind.None, null, "v0"),
            (DebateOutcome.Open, "regla-inventada", null, DebateSubjectKind.None, null, null),
        };
        foreach (var (outcome, rule, chosen, subject, reference, challenges) in variants)
        {
            var debate = new DebateWriter(world, DebateId(n++));
            await debate.OpenAsync("¿Y esto otro?", DebateMode.Free, subject, reference, challenges);
            await debate.CloseAsync(outcome, rule, chosen);
        }

        // Verdicts of a debate: one in the log valid, one invalid, and one the log does not have.
        var judged = DebateId(n++);
        var writer = new DebateWriter(world, judged);
        await writer.OpenAsync("¿Con veredictos?", DebateMode.Tribunal);
        await world.Run(judged).VerdictAsync("vd-1", "juez-a", VerdictDecision.Approved, true, []);
        await world.Run(judged).VerdictAsync("vd-2", "juez-b", VerdictDecision.ChangesRequested, false, []);
        await writer.CloseAsync(DebateOutcome.Resolved, JudgingPanel.Agree, "P1", "vd-1", "vd-2", "vd-ausente");

        // The question: empty, and written by an agent.
        var empty = new DebateWriter(world, DebateId(n++));
        await empty.OpenAsync(string.Empty, DebateMode.Free);
        await empty.CloseAsync(DebateOutcome.Resolved, JudgingPanel.Single, null);
        var byAgent = new DebateWriter(world, DebateId(n));
        await byAgent.OpenAsync("Pregunta de un agente", DebateMode.Free, origin: EvidenceOrigin.ReportedByAgent);
        await byAgent.CloseAsync(DebateOutcome.Resolved, JudgingPanel.Single, null);
    }

    private static async Task ChallengesAndReplansAsync(DerivationWorld world)
    {
        var id = RunId(200);
        var run = world.Run(id);
        var (reasons, _) = await run.CaptureAsync(EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, "Razones", "manuel", "Razones.", "text/plain", null, null);
        await run.RecordAsync(
            DecisionKind.VerdictChallenged,
            new VerdictChallengedPayload("v0", "manuel", reasons.Id, reasons.ContentSha256, DebateId(70)) { RunId = id },
            new Actor(ActorKind.User, "manuel"));
        foreach (var (granted, actor) in new[] { (true, new Actor(ActorKind.User, "manuel")), (false, RunWriter.System) })
        {
            foreach (var action in new[] { MemoryDerivation.RevisionAcceptanceAction, MemoryDerivation.ReplanDismissalAction })
            {
                await run.RecordAsync(
                    granted ? DecisionKind.ApprovalGranted : DecisionKind.ApprovalDenied,
                    new ApprovalAnsweredPayload(action, granted, "manuel", null, null) { RunId = id },
                    actor);
            }
        }

        // Requests to replan: of the auditor, of a judge (only counted), of the system; with and without findings, tasks, verdict and message.
        await run.RecordAsync(DecisionKind.ReplanRequested, new ReplanRequestedPayload(["a.one", "a.two"], [Tid("T7"), Tid("T8")], "Message of the auditor.", "r9") { RunId = id }, new Actor(ActorKind.Auditor, "auditor"));
        await run.RecordAsync(DecisionKind.ReplanRequested, new ReplanRequestedPayload(["acciones-del-juez"], [Tid("T7")], "Message of the judge.", null) { RunId = id }, new Actor(ActorKind.Judge, "juez-revision"));
        await run.RecordAsync(DecisionKind.ReplanRequested, new ReplanRequestedPayload(["f1", "f2", "f3"], [], "Message of the judge.", "r1") { RunId = id }, new Actor(ActorKind.Judge, "juez-revision"));
        await run.RecordAsync(DecisionKind.ReplanRequested, new ReplanRequestedPayload([], [], string.Empty, null) { RunId = id }, RunWriter.System);
        await run.RecordAsync(DecisionKind.ReplanRequested, new ReplanRequestedPayload(["sys.finding"], [], "Message of the system.", null) { RunId = id }, RunWriter.System);
    }

    private static async Task UnitsAsync(DerivationWorld world)
    {
        // The same texts in a debate and in an audit, which name themselves as what they are.
        foreach (var unit in new[] { Debate, Audit })
        {
            var run = world.Run(unit);
            await run.AlertAsync("debate.something", "Something happened.", "T1");
            await run.TestRunAsync("T1", 1, Red, CommitA);
            await run.CaptureAsync(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Tests (test)", "agent-studio:verification", "# tests\n", "text/plain", null, null, Red, CommitA);
            await run.VerdictAsync("vu-1", "juez-codex", VerdictDecision.Rejected, true, []);
            await run.RecordAsync(
                DecisionKind.VerdictIssued,
                new VerdictIssuedPayload("vu-2", null, VerdictDecision.Approved, true, [], HashA, HashB, null, "Nota.") { RunId = unit },
                new Actor(ActorKind.Judge, "juez"));
            var (reasons, _) = await run.CaptureAsync(EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, "Razones", "manuel", "Razones.", "text/plain", null, null);
            await run.RecordAsync(
                DecisionKind.VerdictChallenged,
                new VerdictChallengedPayload("vu-1", "manuel", reasons.Id, reasons.ContentSha256, DebateId(71)) { RunId = unit },
                new Actor(ActorKind.User, "manuel"));
            await run.RecordAsync(DecisionKind.ReplanRequested, new ReplanRequestedPayload(["x"], [], "M.", null) { RunId = unit }, RunWriter.System);
            await run.RecordAsync(
                DecisionKind.ApprovalGranted,
                new ApprovalAnsweredPayload(MemoryDerivation.RevisionAcceptanceAction, true, "manuel", null, null) { RunId = unit },
                new Actor(ActorKind.User, "manuel"));
            await run.RecordAsync(
                DecisionKind.ApprovalGranted,
                new ApprovalAnsweredPayload(MemoryDerivation.ReplanDismissalAction, true, "manuel", null, null) { RunId = unit },
                new Actor(ActorKind.User, "manuel"));
        }
    }
}
