using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Domain;

/// <summary>Shared builders. They only construct valid objects; each test mutates what it needs.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    public static readonly string HashA = new('a', 64);
    public static readonly string HashB = new('b', 64);

    /// <summary>Two git object ids, for evidence bound to the commit it verified.</summary>
    public static readonly string CommitA = new('1', 40);
    public static readonly string CommitB = new('2', 40);

    public static readonly TestRunSummary Green = new(Passed: 10, Failed: 0, Skipped: 0, ExitCode: 0);

    public static readonly TestRunSummary Red = new(Passed: 8, Failed: 2, Skipped: 0, ExitCode: 1);

    public static TaskId Tid(string value) => new(value);

    public static EvidenceId Eid(string value) => new(value);

    public static RequirementId Rid(string value) => new(value);

    public static AgentId Aid(string value) => new(value);

    public static WorkTask NewTask(
        string id,
        WorkTaskKind kind = WorkTaskKind.AgentWork,
        int maxRetries = 2,
        bool? requiresEvidence = null,
        JoinPolicy join = JoinPolicy.AllCompleted) =>
        new(Tid(id), $"Task {id}", kind, T0, maxRetries: maxRetries, requiresEvidence: requiresEvidence, join: join);

    /// <summary>Drives a fresh task to the requested status through the legal public API.</summary>
    public static WorkTask TaskIn(WorkTaskStatus status, string id = "t", int maxRetries = 2)
    {
        if (status == WorkTaskStatus.Skipped)
        {
            return SkippedTask(id, maxRetries);
        }

        var task = NewTask(id, maxRetries: maxRetries);
        switch (status)
        {
            case WorkTaskStatus.Pending:
                break;
            case WorkTaskStatus.Running:
                task.TransitionTo(WorkTaskStatus.Running, T0, "start");
                break;
            case WorkTaskStatus.Blocked:
                task.TransitionTo(WorkTaskStatus.Blocked, T0, "block");
                break;
            case WorkTaskStatus.AwaitingApproval:
                task.TransitionTo(WorkTaskStatus.AwaitingApproval, T0, "ask");
                break;
            case WorkTaskStatus.Completed:
                task.TransitionTo(WorkTaskStatus.Running, T0, "start");
                task.Complete([Eid("ev")], T0, "done");
                break;
            case WorkTaskStatus.Failed:
                task.TransitionTo(WorkTaskStatus.Running, T0, "start");
                task.TransitionTo(WorkTaskStatus.Failed, T0, "boom");
                break;
            case WorkTaskStatus.Cancelled:
                task.TransitionTo(WorkTaskStatus.Cancelled, T0, "cancel");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        return task;
    }

    /// <summary>
    /// Only the graph can skip a task, so the task is built the way it happens for real: it waits for the "yes" branch
    /// of a condition that chose "no".
    /// </summary>
    private static WorkTask SkippedTask(string id, int maxRetries)
    {
        var graph = new TaskGraph();
        var condition = NewTask("cond-" + id, WorkTaskKind.Condition);
        var task = NewTask(id, maxRetries: maxRetries);
        graph.Add(condition);
        graph.Add(task);
        graph.AddDependency(task.Id, condition.Id, "yes");
        condition.TransitionTo(WorkTaskStatus.Running, T0, "start");
        graph.CompleteCondition(condition.Id, "no", T0, "chose no");
        graph.PropagateSkips(T0);
        return task;
    }

    public static DomainRuleViolationException Violation(Action action) => Assert.ThrowsAny<DomainRuleViolationException>(action);

    public static AcceptanceCriterion Crit(string id, VerificationMethod method = VerificationMethod.AutomatedTest, string description = "criterion") =>
        new(id, description, method);

    public static Requirement Req(
        string id,
        RequirementPriority priority = RequirementPriority.Mandatory,
        string statement = "statement",
        params AcceptanceCriterion[] criteria) =>
        new(Rid(id), statement, priority, criteria.Length == 0 && priority == RequirementPriority.Mandatory ? [Crit("c1")] : criteria);

    public static ModelBinding Model(VendorId vendor) => new(new ProviderId("p-" + vendor.Value), vendor);

    public static JudgeIdentity Judge(VendorId? vendor = null, string agent = "judge") => new(Aid(agent), Model(vendor ?? VendorId.OpenAI));

    public static EvidenceRecord TestRunEvidence(
        string id,
        TestRunSummary summary,
        DateTimeOffset? at = null,
        EvidenceOrigin origin = EvidenceOrigin.CapturedBySystem,
        string? commit = null) =>
        new(Eid(id), EvidenceKind.TestRun, origin, "test run", "agent-studio", at ?? T0, HashA, summary, subjectCommit: commit);

    public static EvidenceRecord OtherEvidence(string id, EvidenceOrigin origin, EvidenceKind kind = EvidenceKind.CommandOutput) =>
        new(Eid(id), kind, origin, "evidence", "someone", T0, HashB);

    /// <summary>
    /// A consistent world in which <see cref="ValidVerdict"/> is accepted: R1 is mandatory and tested automatically,
    /// R2 is desirable and reviewed by a human; the only test run is green and captured by the system.
    /// </summary>
    public static JudgeDossier Dossier(
        IReadOnlyList<Requirement>? requirements = null,
        IReadOnlyList<EvidenceRecord>? evidence = null,
        IReadOnlyList<VendorId>? excludedVendors = null)
    {
        IReadOnlyList<Requirement> reqs = requirements ??
        [
            Req("R1", RequirementPriority.Mandatory, "must work", Crit("c1", VerificationMethod.AutomatedTest)),
            Req("R2", RequirementPriority.Desirable, "nice", Crit("c2", VerificationMethod.HumanReview)),
        ];
        IReadOnlyList<EvidenceRecord> ev = evidence ??
        [
            TestRunEvidence("E1", Green),
            OtherEvidence("E2", EvidenceOrigin.ProvidedByHuman, EvidenceKind.HumanNote),
        ];
        var frozen = FrozenRequirementSet.Freeze(reqs, T0);
        return new JudgeDossier(
            "original request",
            frozen,
            "plan",
            [],
            HashB,
            [.. ev],
            [],
            (excludedVendors ?? [VendorId.Anthropic]).ToHashSet());
    }

    public static Verdict ValidVerdict(JudgeDossier dossier, VerdictDecision decision = VerdictDecision.Approved)
    {
        var assessments = dossier.Requirements.Requirements
            .Select(r => new RequirementAssessment(r.Id, AssessmentStatus.Satisfied, [Eid("E1")], "ok"))
            .ToList();
        return new Verdict(
            decision,
            "summary",
            assessments,
            dossier.Evidence.Select(e => e.Id).ToList(),
            [],
            [],
            [],
            new Confidence(ConfidenceLevel.High, "all checks reproduced"),
            decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected ? ["fix it"] : [],
            decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved,
            Judge(),
            dossier.Requirements.ContentHash,
            dossier.DecisionLogHeadHash,
            T0);
    }

    public static DebateParticipant Participant(string agent, DebateRole role, VendorId vendor) => new(Aid(agent), role, Model(vendor));

    public static FrozenRequirementSet Frozen() => FrozenRequirementSet.Freeze([Req("R1")], T0);

    public static DebateSession OpenDebate(
        DebateMode mode = DebateMode.Adversarial,
        DebateLimits? limits = null,
        FrozenRequirementSet? requirements = null)
    {
        IReadOnlyList<DebateParticipant> participants = mode == DebateMode.Tribunal
            ? [Participant("a", DebateRole.Panelist, VendorId.Anthropic), Participant("b", DebateRole.Panelist, VendorId.Google)]
            : [Participant("a", DebateRole.Proponent, VendorId.Anthropic), Participant("b", DebateRole.Critic, VendorId.Google)];
        return DebateSession.Open(
            new DebateId("d1"),
            mode,
            "Which design?",
            ["it must be testable"],
            requirements ?? Frozen(),
            participants,
            Judge(),
            limits ?? new DebateLimits(MaxRounds: 3, MaxDuration: TimeSpan.FromMinutes(30), MaxTotalTokens: 1000),
            T0);
    }

    public static DebateContribution Contribution(string author, int round, ContributionKind kind, int? repliesTo = null) =>
        new(round, Aid(author), kind, $"{kind} by {author}", [], T0, repliesTo);

    public static Verdict DebateVerdict(DebateSession session, VerdictDecision decision, string judgeAgent = "judge", string? requirementsHash = null) =>
        ValidVerdict(Dossier(), decision) with
        {
            Judge = Judge(agent: judgeAgent),
            RequirementSetHash = requirementsHash ?? session.Requirements.ContentHash,
        };
}
