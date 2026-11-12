using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/15 §5: the contracts of Phase 5 that the packages WP1 to WP6 build against in parallel. These tests fail if one of
/// them drifts from what docs/15 §5 and docs/04 §17 say.
/// </summary>
public sealed class Phase5ContractTests
{
    private static IEnumerable<string> MethodNames(Type type) => type.GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);

    private static IEnumerable<Type> Parameters(Type type, string method) => type.GetMethod(method)!.GetParameters().Select(p => p.ParameterType);

    private static IEnumerable<string> PropertyNames(Type type) => type.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal);

    private static readonly AgentDefinition Participant = new(
        Aid("arquitecto"),
        "Arquitecto",
        AgentRole.Architect,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"),
        "instructions",
        [],
        [],
        [],
        new ExecutionBudget(12, TimeSpan.FromMinutes(5)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    // ---- the debate (section 5.3) ----

    [Fact]
    public void The_coordinator_prepares_opens_resumes_lists_and_reads_debates_kept_in_the_log()
    {
        Assert.Equal(["ListAsync", "OpenAsync", "PrepareAsync", "ReadAsync", "ResumeAsync"], MethodNames(typeof(IDebateCoordinator)));
        Assert.Equal([typeof(DebateRequest), typeof(CancellationToken)], Parameters(typeof(IDebateCoordinator), nameof(IDebateCoordinator.PrepareAsync)));
        Assert.Equal([typeof(DebateProposal), typeof(CancellationToken)], Parameters(typeof(IDebateCoordinator), nameof(IDebateCoordinator.OpenAsync)));
        Assert.Equal([typeof(ProjectContext), typeof(RunId), typeof(CancellationToken)], Parameters(typeof(IDebateCoordinator), nameof(IDebateCoordinator.ResumeAsync)));
        Assert.Equal(typeof(Task<IReadOnlyList<DebateSummary>>), typeof(IDebateCoordinator).GetMethod(nameof(IDebateCoordinator.ListAsync))!.ReturnType);
        Assert.Equal(typeof(Task<DebateTranscript>), typeof(IDebateCoordinator).GetMethod(nameof(IDebateCoordinator.ReadAsync))!.ReturnType);
    }

    [Fact]
    public void A_debate_is_followed_through_events_and_controlled_like_a_run()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IDebateRun)));
        Assert.Equal(
            ["AbortAsync", "AddHumanEvidenceAsync", "DecideAsync", "RequestJudgmentAsync", "RetryTurnAsync", "RunTestsAsync", "Snapshot", "get_Completion", "get_Events", "get_Id"],
            MethodNames(typeof(IDebateRun)));
        Assert.Equal(typeof(IAsyncEnumerable<DebateProgress>), typeof(IDebateRun).GetProperty(nameof(IDebateRun.Events))!.PropertyType);
        Assert.Equal(typeof(Task<DebateResult>), typeof(IDebateRun).GetProperty(nameof(IDebateRun.Completion))!.PropertyType);
        Assert.Equal([typeof(VerdictDecision?), typeof(string), typeof(string)], Parameters(typeof(IDebateRun), nameof(IDebateRun.DecideAsync)));
    }

    [Fact]
    public void The_progress_of_a_debate_says_what_a_sealed_round_may_show_and_nothing_more()
    {
        var kinds = typeof(DebateProgress).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(DebateProgress))).Select(t => t.Name).Order(StringComparer.Ordinal);

        Assert.Equal(
            ["DebateAwaitingUser", "DebateContributionPublished", "DebateFinished", "DebateJudgeLaunched", "DebateRoundEnded", "DebateRoundSealed", "DebateTurnFinished", "DebateTurnStarted", "DebateVerdictReceived"],
            kinds);
        Assert.DoesNotContain(PropertyNames(typeof(DebateTurnFinished)), n => n.Contains("Text", StringComparison.Ordinal) || n.Contains("Output", StringComparison.Ordinal));
        Assert.Equal(["FailedTurn", "ProviderLimit", "HumanResolution"], Enum.GetNames<DebateAwaitReason>());
    }

    [Fact]
    public void A_request_for_a_debate_names_its_subject_participants_judges_and_limits()
    {
        var request = new DebateRequest(
            new ProjectContext("p1", "C:\\repo"),
            DebateMode.Adversarial,
            "¿Qué diseño?",
            Frozen(),
            new DebateSubject(DebateSubjectKind.None, null),
            [new DebateParticipantRequest(Aid("arquitecto"), DebateRole.Proponent), new DebateParticipantRequest(Aid("critico"), DebateRole.Critic)],
            [Aid("juez-revision")],
            DebateJudgingKind.IndependentJudge,
            DebateLimits.Default);

        Assert.Null(request.MaxAgentRuns);
        Assert.Equal(
            ["ApprovalText", "Estimate", "HeaderHash", "Judging", "ProblemTexts", "Problems", "Request"],
            PropertyNames(typeof(DebateProposal)));
        Assert.Equal(["MaxAgentRuns", "NoteTexts", "Notes", "Tokens"], PropertyNames(typeof(DebateEstimate)));
    }

    [Fact]
    public void The_judgment_of_a_debate_is_behind_a_port_so_the_coordinator_is_tested_with_a_double()
    {
        Assert.Equal(["JudgeAsync"], MethodNames(typeof(IDebateJudgment)));
        Assert.Equal(
            [typeof(ProjectContext), typeof(RunId), typeof(IProgress<AgentEvent>), typeof(CancellationToken)],
            Parameters(typeof(IDebateJudgment), nameof(IDebateJudgment.JudgeAsync)));
        Assert.Equal(["Corrected", "Id", "Verdict", "Violations"], PropertyNames(typeof(JudgedVerdict)));
        Assert.Equal(["Outcome", "Verdicts"], PropertyNames(typeof(DebateJudgmentResult)));
    }

    [Fact]
    public void What_the_screens_show_comes_from_replaying_the_log()
    {
        Assert.Contains("Interrupted", PropertyNames(typeof(DebateSummary)));
        Assert.Contains("Challenged", PropertyNames(typeof(DebateSummary)));
        Assert.Contains("TurnsInFlight", PropertyNames(typeof(DebateSnapshot)));
        Assert.Contains("Tokens", PropertyNames(typeof(DebateSnapshot)));
        Assert.Contains("ReportedTurns", PropertyNames(typeof(DebateSnapshot)));
        Assert.Contains("UnresolvedCitations", PropertyNames(typeof(DebateContributionView)));
        Assert.Equal(["Agent", "Label", "Model", "Provider", "Role", "Vendor"], PropertyNames(typeof(DebateParticipantView)));
        Assert.Contains("Verdicts", PropertyNames(typeof(DebateTranscript)));
        Assert.Contains("Panel", PropertyNames(typeof(DebateResult)));
    }

    // ---- audits and challenges (section 5.4) ----

    [Fact]
    public void A_whole_project_audit_is_prepared_without_writing_and_run_after_the_users_approval()
    {
        Assert.Equal(["AuditAsync", "PrepareAsync"], MethodNames(typeof(IProjectAuditService)));
        Assert.Equal([typeof(ProjectAuditRequest), typeof(CancellationToken)], Parameters(typeof(IProjectAuditService), nameof(IProjectAuditService.PrepareAsync)));
        Assert.Equal(
            [typeof(ProjectAuditProposal), typeof(IProgress<AgentEvent>), typeof(CancellationToken)],
            Parameters(typeof(IProjectAuditService), nameof(IProjectAuditService.AuditAsync)));
        Assert.Equal(["WholeProject", "CurrentPlan", "OrchestratorDecisions", "Debate"], Enum.GetNames<AuditScope>());
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void An_audit_report_is_trustworthy_only_with_a_valid_stored_verdict_on_an_intact_log(bool noIssues, bool intact, bool stored, bool trustworthy)
    {
        var dossier = Dossier();
        var report = new AuditReport(
            new RunId("aud-20261019-0a1b2c"),
            AuditScope.WholeProject,
            CommitA,
            false,
            ValidVerdict(dossier),
            stored ? "v-1" : null,
            noIssues ? [] : [new VerdictViolation(VerdictViolationCode.MalformedVerdict, "x")],
            false,
            intact,
            ReviewDisposition.Approved,
            [],
            [],
            T0);

        Assert.Equal(trustworthy, report.IsTrustworthy);
    }

    [Fact]
    public void The_auditor_of_the_orchestrator_audits_one_run_for_free_and_challenges_open_revision_debates()
    {
        Assert.Equal(["AuditRunAsync"], MethodNames(typeof(IOrchestratorAuditService)));
        Assert.Equal(["AcceptRevisionAsync", "ChallengeAsync", "PrepareAsync", "StatusAsync"], MethodNames(typeof(IVerdictChallenges)));
        Assert.Equal(typeof(Task<IDebateRun>), typeof(IVerdictChallenges).GetMethod(nameof(IVerdictChallenges.ChallengeAsync))!.ReturnType);
        Assert.Equal(["None", "Pending", "Confirmed", "Revoked", "AwaitingAcceptance", "Accepted", "DecidedByUser", "Aborted"], Enum.GetNames<ChallengeStatus>());
        Assert.Equal(["Code", "Entries", "Message", "Severity", "Tasks"], PropertyNames(typeof(OrchestratorFinding)));
    }

    // ---- providers, stores and activity (section 5.5) ----

    [Fact]
    public void A_run_request_persists_its_session_and_belongs_to_no_debate_unless_asked()
    {
        var request = new AgentRunRequest(Participant, "p", "C:\\wd", new PermissionProfile(false, false, false, [], [], []));

        Assert.True(request.PersistSession);
        Assert.Null(request.DebateId);
        Assert.False((request with { PersistSession = false }).PersistSession);
    }

    [Fact]
    public void The_headers_of_debates_and_audits_are_stored_once_and_found_again()
    {
        Assert.Equal(["CreateAsync", "FindAsync", "ListAsync"], MethodNames(typeof(IDebateRegistry)));
        Assert.Equal(["CreateAsync", "FindAsync", "ListAsync"], MethodNames(typeof(IProjectAuditRegistry)));
        Assert.Equal(typeof(Task<DebateRecord?>), typeof(IDebateRegistry).GetMethod(nameof(IDebateRegistry.FindAsync))!.ReturnType);
    }

    [Fact]
    public void One_activity_with_agents_per_project_is_also_what_the_memory_waits_on()
    {
        Assert.True(typeof(IRunActivity).IsAssignableFrom(typeof(IProjectActivity)));
        Assert.Equal(["Current", "TryEnter"], MethodNames(typeof(IProjectActivity)));
        Assert.Equal(["IsBusy"], MethodNames(typeof(IRunActivity)));
        Assert.Equal(["Run", "Planning", "Debate", "Audit"], Enum.GetNames<ProjectActivityKind>());
        Assert.Equal(typeof(IDisposable), typeof(IProjectActivity).GetMethod(nameof(IProjectActivity.TryEnter))!.ReturnType);
    }

    [Fact]
    public void Prompt_data_is_public_in_application_and_no_longer_in_the_engine()
    {
        Assert.True(typeof(PromptData).IsPublic);
        Assert.Equal("AgentStudio.Application", typeof(PromptData).Assembly.GetName().Name);
        Assert.Null(typeof(AgentStudio.Orchestration.Engine.Orchestrator).Assembly.GetType("AgentStudio.Orchestration.Engine.PromptData"));
    }

    [Fact]
    public void The_kinds_of_phase_5_are_in_the_log()
    {
        Assert.True(Enum.IsDefined(DecisionKind.DebateOpened));
        Assert.True(Enum.IsDefined(DecisionKind.ProjectAuditFinished));
    }
}
