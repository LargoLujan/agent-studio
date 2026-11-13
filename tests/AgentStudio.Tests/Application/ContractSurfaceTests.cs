using System.Reflection;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Application.Work;
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
        Assert.Equal(["DeleteAsync", "ListAsync", "SaveAsync"], MethodNames(typeof(IAgentRoster)));
        Assert.Contains("ListByRunAsync", MethodNames(typeof(IEvidenceStore)));
    }

    /// <summary>Phase 9 (docs/16 section 4.6): the stores and services the packages of the phase build against in parallel.</summary>
    [Fact]
    public void The_stores_and_services_of_phase_9_exist_with_the_documented_operations()
    {
        Assert.Equal(["CreateAsync", "FindAsync", "ListAsync"], MethodNames(typeof(IMilestoneRegistry)));
        Assert.Equal(["AppendAsync", "FindAsync", "ListAsync"], MethodNames(typeof(IBacklogStore)));
        Assert.Equal(["GetAsync", "LinkAsync", "ListAsync", "OpenAsync", "ReopenAsync", "ReviseRequirementsAsync"], MethodNames(typeof(IMilestoneService)));
        Assert.Equal(["CloseAsync"], MethodNames(typeof(IMilestoneCloser)));
        Assert.Equal(["AddAsync", "HistoryAsync", "ListAsync", "ReorderAsync", "RetireAsync", "ReviseAsync"], MethodNames(typeof(IBacklogService)));
        Assert.Equal(["BacklogAsync", "ProjectAsync"], MethodNames(typeof(IBoardProjector)));
        Assert.Equal(["ConfirmAsync", "LinksAroundAsync", "RecordAsync", "RetractAsync"], MethodNames(typeof(ITraceLinkService)));
        Assert.Equal(["SuggestAsync"], MethodNames(typeof(ITraceSuggester)));
        Assert.Equal(["ChainAsync"], MethodNames(typeof(ITraceChainReader)));
        Assert.Equal(["ProjectAsync"], MethodNames(typeof(IParticipationProjector)));
        Assert.Equal(["LoadAsync", "SaveAsync"], MethodNames(typeof(IProjectBudgetStore)));
        Assert.Equal(["BranchesAsync", "CommitsAsync", "SummaryAsync"], MethodNames(typeof(IRepositoryHistoryReader)));
        Assert.Equal(["AnnotateAsync"], MethodNames(typeof(IRepositoryAnnotator)));
        Assert.Equal(["ListAsync"], MethodNames(typeof(IRepositoryActivityReader)));
    }

    [Fact]
    public void The_roles_of_participation_and_the_stages_of_the_chain_are_the_documented_ones()
    {
        Assert.Equal(["ProducingAgent", "GitAuthorAndCommitter", "PublishingAccount", "Reviewer", "Approver", "Integrator"], Enum.GetNames<ParticipationRole>());
        // Phase 10 (docs/21 §5.1) appends Publication, RemoteMerge and Deployment to the chain, and RemoteRun to the branches.
        Assert.Equal(["Requirement", "Milestone", "Task", "Attempt", "Change", "Verification", "Verdict", "Delivery", "Publication", "RemoteMerge", "Deployment"], Enum.GetNames<TraceChainStage>());
        Assert.False(ParticipationEntry.NotRecorded(ParticipationRole.Reviewer).IsRecorded);
        Assert.Equal(["Local", "Run", "RemoteRun"], Enum.GetNames<AgentStudio.Application.Git.BranchKind>());
        Assert.Equal(["Integration", "Delivery", "WorktreeRemoved", "RemoteOperation", "RemoteObservation"], Enum.GetNames<RepositoryActivityKind>());
        Assert.Equal(
            ["participation.attempt", "participation.commit", "participation.verdict", "participation.approval", "participation.local-delivery", "participation.review-skipped",
             "participation.remote-publication", "participation.remote-review", "participation.remote-merge"],
            typeof(ParticipationSources).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!));
    }

    /// <summary>Phase 10 (docs/21 §5.2): the interfaces the packages of the phase build against in parallel, and what they do not have.</summary>
    [Fact]
    public void The_contracts_of_phase_10_exist_with_the_documented_members_and_without_what_they_must_not_have()
    {
        Assert.Equal(
            ["CloseAsync", "DeleteRunBranchAsync", "FindPullRequestAsync", "MergeAsync", "ObserveAsync", "OpenPullRequestAsync", "ProbeAsync", "PublishVerdictAsync", "ReadAccountAsync",
             "ReadBranchRulesAsync", "ReadRepositoryAsync", "RequestReviewersAsync", "UpdatePullRequestAsync", "get_Kind"],
            MethodNames(typeof(IHostingProvider)));
        Assert.Equal(["For", "IdentifyAsync"], MethodNames(typeof(IHostingProviders)));
        Assert.Equal(["InspectAsync", "PushRunBranchAsync", "ReadBranchAsync"], MethodNames(typeof(IRemoteGitRunner)));
        Assert.Equal(["AuthorizeAsync", "GrantAsync", "GrantsAsync", "RevokeAsync"], MethodNames(typeof(IHostingPolicyGate)));
        Assert.Equal(
            ["CloseAsync", "DeleteRunBranchAsync", "InspectAsync", "MergeAsync", "PreviewAsync", "PublishAsync", "RequestReviewersAsync", "UpdateVerdictAsync"],
            MethodNames(typeof(IPublicationService)));
        Assert.Equal(
            ["CancelAsync", "FollowAsync", "Health", "Pending", "SyncNowAsync", "UnfollowAsync", "add_Changed", "remove_Changed"],
            MethodNames(typeof(IRemoteSync)));
        Assert.Equal(["PeriodicSyncAllowed", "get_Interval"], MethodNames(typeof(IRemoteSyncPolicy)));
        Assert.Equal(["ListAsync"], MethodNames(typeof(IPullRequestProjector)));
        Assert.Equal(["LoadAsync", "SaveAsync"], MethodNames(typeof(IRemoteSyncStore)));
        Assert.Equal(["EnterAsync"], typeof(ProjectActivityEntry).GetMethods().Where(m => m.DeclaringType == typeof(ProjectActivityEntry)).Select(m => m.Name));

        // D-03 and D-07: three members, and none takes a text that could be a command, an argument, a refspec or an address; nothing says «force».
        var runner = typeof(IRemoteGitRunner).GetMethods();
        Assert.Equal(3, runner.Length);
        Assert.DoesNotContain(runner.SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(string));
        Assert.DoesNotContain(typeof(RemotePush).GetProperties(), p => p.PropertyType == typeof(string) && p.Name != nameof(RemotePush.Commit));

        // D-21: no member of the provider submits a review, approves, or merges as an administrator.
        var forbidden = new[] { "Review", "Approve", "Submit", "Admin", "Force", "Auto" };
        Assert.DoesNotContain(typeof(IHostingProvider).GetMethods().Select(m => m.Name), n => forbidden.Any(f => n.StartsWith(f, StringComparison.Ordinal) || n.Contains("Admin", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            typeof(IHostingProvider).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.Name!),
            n => n.Contains("force", StringComparison.OrdinalIgnoreCase) || n.Contains("admin", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["Ok", "NotModified", "SignInNeeded", "RateLimited", "Unreachable", "Forbidden", "NotFound", "Refused", "Unsupported", "InvalidAnswer"], Enum.GetNames<HostingOutcome>());
        Assert.Equal(
            ["Pushed", "AlreadyUpToDate", "RejectedNotFastForward", "CredentialsNeeded", "Unreachable", "Redirected", "ConfigurationRefused", "LfsRefused", "NotConfirmed", "Failed"],
            Enum.GetNames<RemotePushOutcome>());
        Assert.Equal(["Queued", "WaitingForProject", "Running", "PendingSync", "Done", "Failed", "Cancelled", "Expired"], Enum.GetNames<RemoteWorkState>());
        Assert.Equal(["Offline", "SignInNeeded", "CredentialsNeeded", "RateLimited", "ProjectBusy"], Enum.GetNames<PendingReason>());
        Assert.Equal(["Approved", "Rejected", "InReview"], Enum.GetNames<VerdictStanding>());
        Assert.Equal(["Run", "Planning", "Debate", "Audit", "RemoteOperation"], Enum.GetNames<ProjectActivityKind>());
    }

    [Fact]
    public void Code_written_against_the_commit_annotation_of_phase_9_still_compiles_and_the_successor_defaults_to_null()
    {
        var annotation = new CommitAnnotation("run", "T1", 1, null, null, [], []);

        Assert.Null(annotation.Successor);
        Assert.Equal(
            ["Run", "Task", "Attempt", "Agent", "Model", "Links", "Suggestions", "Successor"],
            typeof(CommitAnnotation).GetConstructors().Single().GetParameters().Select(p => p.Name));
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
