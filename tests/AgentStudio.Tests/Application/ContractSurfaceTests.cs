using System.Reflection;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The Phase 3 contracts are the interface other packages build against in parallel: these tests fail if one of them
/// drifts from docs/13 (section 3) and docs/04.
/// </summary>
public sealed class ContractSurfaceTests
{
    private static IEnumerable<string> MethodNames(Type type) => type.GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal);

    [Fact]
    public void The_orchestrator_plans_starts_resumes_and_lists_runs_and_no_longer_takes_a_bare_graph()
    {
        Assert.Equal(["ListRunsAsync", "PlanAsync", "ResumeAsync", "StartAsync"], MethodNames(typeof(IOrchestrator)));
        Assert.Null(typeof(IOrchestrator).GetMethod("ExecuteAsync"));
        Assert.Null(typeof(IOrchestrator).Assembly.GetType("AgentStudio.Application.Coordination.OrchestrationProgress"));

        // Phase 5 replaced the coordinator of Phase 0, which ran a debate held in memory: Phase5ContractTests pins the new one.
        Assert.Null(typeof(IDebateCoordinator).GetMethod("RunAsync"));
    }

    [Fact]
    public void A_run_is_followed_through_events_and_controlled_like_an_agent_run()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IOrchestrationRun)));
        Assert.Equal(
            // Phase 13 (docs/17 D-30) adds AnswerAsync, the answer of the person to the question of an agent.
            ["AnswerAsync", "CancelAsync", "PauseAsync", "ResumeDispatchAsync", "RetryBlockedAsync", "Snapshot", "get_Completion", "get_Events", "get_Id"],
            MethodNames(typeof(IOrchestrationRun)));
        Assert.Equal(typeof(IAsyncEnumerable<OrchestrationEvent>), typeof(IOrchestrationRun).GetProperty("Events")!.PropertyType);
    }

    [Fact]
    public void The_run_phases_and_review_outcomes_are_the_documented_ones()
    {
        Assert.Equal(
            ["Running", "Paused", "WaitingForQuota", "AwaitingHuman", "Interrupted", "Succeeded", "Failed", "Blocked", "Cancelled"],
            Enum.GetNames<RunPhase>());
        Assert.Equal(["Approved", "ChangesRequested", "Rejected", "NeedsHuman", "RateLimited"], Enum.GetNames<ReviewDisposition>());
        Assert.Equal(["FastForwardOnly", "MergeCommit"], Enum.GetNames<DeliveryMode>());
        Assert.Equal(["Removed", "AlreadyRemoved", "RefusedDirty"], Enum.GetNames<RemovalOutcome>());
    }

    [Fact]
    public void The_judge_can_be_asked_for_one_correction_and_the_dossier_is_asked_for_by_identifiers()
    {
        var revise = typeof(IJudge).GetMethod(nameof(IJudge.ReviseAsync))!;
        Assert.Equal(
            [typeof(JudgeDossier), typeof(Verdict), typeof(IReadOnlyList<VerdictViolation>), typeof(CancellationToken)],
            revise.GetParameters().Select(p => p.ParameterType));

        var build = typeof(IJudgeDossierBuilder).GetMethod(nameof(IJudgeDossierBuilder.BuildAsync))!;
        Assert.Equal([typeof(DossierRequest), typeof(CancellationToken)], build.GetParameters().Select(p => p.ParameterType));

        var review = typeof(IIndependentReview).GetMethod(nameof(IIndependentReview.ReviewRunAsync))!;
        Assert.Equal(typeof(Task<ReviewOutcome>), review.ReturnType);
        Assert.Equal(typeof(ReviewRequest), review.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void The_stores_of_a_run_exist_with_the_documented_operations()
    {
        Assert.Equal(["FindAsync", "SaveAsync"], MethodNames(typeof(IPlanStore)));
        Assert.Equal(["CreateAsync", "FindAsync", "ListAsync"], MethodNames(typeof(IRunRegistry)));
        Assert.Equal(["AppendAsync", "ListAsync"], MethodNames(typeof(IVerdictStore)));
        Assert.Equal(["ListAsync", "SaveAsync"], MethodNames(typeof(IAgentRoster)));
        Assert.Contains("ListByRunAsync", MethodNames(typeof(IEvidenceStore)));
    }

    [Fact]
    public void The_worktree_manager_can_capture_the_state_of_the_users_repository()
    {
        var capture = typeof(IWorktreeManager).GetMethod(nameof(IWorktreeManager.CaptureUserRepositoryStateAsync))!;

        Assert.Equal(typeof(Task<UserRepositoryState>), capture.ReturnType);
        Assert.Equal([typeof(string), typeof(CancellationToken)], capture.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(
            ["RepositoryRoot", "AdminDirectory"],
            typeof(WorktreeLease).GetProperties().Select(p => p.Name).Where(n => n is "RepositoryRoot" or "AdminDirectory"));
    }

    [Fact]
    public void Code_written_against_the_phase_0_records_still_compiles_and_the_new_fields_default_to_null()
    {
        var request = new ApprovalRequest("merge", "Entregar", RiskLevel.Critical, null, ["src/"]);
        var draft = new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, "t", "agent-studio");

        Assert.Null(request.ProjectId);
        Assert.Null(request.RunId);
        Assert.Null(request.TaskId);
        Assert.Null(draft.RunId);
        Assert.Null(draft.TaskId);
        Assert.Null(draft.Attempt);
        Assert.Null(draft.MediaType);
    }

    [Fact]
    public void The_contracts_of_phase_3_live_in_the_application_assembly_and_no_module_is_needed_to_use_them()
    {
        var assembly = typeof(IOrchestrator).Assembly;

        Assert.Equal("AgentStudio.Application", assembly.GetName().Name);
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies().Select(a => a.Name),
            name => name is "AgentStudio.Orchestration" or "AgentStudio.Debate" or "AgentStudio.Security" or "AgentStudio.Infrastructure" or "AgentStudio.Providers");
    }
}
