using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Integration.Phase9;
using AgentStudio.Tests.Integration.Phase10;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase7;

/// <summary>
/// F7-5's failure paths through the composed app, real git, SQLite and verification, with scripted providers.
/// Phase 9 and Phase 10 already cover the successful chain and PR merge; these cases keep failed work traceable
/// without borrowing another run's approval or changing the user's branch or the local bare remote.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class FailedWorkTraceabilityLocalToolsTests
{
    private static readonly RemoteBranch Main = RemoteBranch.TryParse("main")!;
    private static readonly TaskId WorkTask = new("T1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlanDefinition ReviewedPlan() => Plan(
        Work("T1", scope: ["src/"]).Verified("check") with { MaxRetries = 0 },
        Node("I1", WorkTaskKind.Integration).After(On("T1")) with { VerificationSteps = ["check"] },
        Node("R1", WorkTaskKind.Review).After(On("I1")));

    private static Task<Phase10World> OpenAsync(Phase5Clis clis) =>
        Phase10World.OpenAsync(services => services.Replace(ServiceDescriptor.Singleton(clis.Providers)));

    private static async Task<RunResult> RunAsync(Phase10World world)
    {
        var request = await world.App.App.RunRequestAsync("Implement the milestone and submit it for independent review.", Requirements(), ReviewedPlan(), Phase5Runs.Profile());
        await using var run = await world.App.App.Orchestrator.StartAsync(request, Ct);
        return await run.Completion.WaitAsync(Phase5Runs.Guard, Ct);
    }

    [Fact]
    public async Task A_success_claim_with_a_failed_system_test_keeps_its_attempt_and_diff_but_never_reaches_integration_review_or_publication()
    {
        var clis = new Phase5Clis
        {
            // The agent says it completed the work, but the committed check requires src/feature.txt.
            Executor = (_, _) => AgentScript.Completed(("src/incomplete.txt", "The requested feature is still missing.\n")),
        };
        await using var world = await OpenAsync(clis);
        var milestone = await Phase9Runs.OpenMilestoneAsync(world.App);
        var userHead = world.App.Repo.Head;
        var remoteHead = world.BareRef("main");

        var result = await RunAsync(world);
        await WriteVerificationDiagnosticsAsync(world, result.Id.Value);
        await Phase9Runs.LinkRunAsync(world.App, milestone.Header.Id, result.Id);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Null(result.IntegrationCommit);
        Assert.Null(result.VerdictId);
        Assert.Contains(new TaskId("I1"), result.Health.UnreachableTasks);
        Assert.Contains(new TaskId("R1"), result.Health.UnreachableTasks);
        Assert.Empty(await world.App.App.PayloadsAsync<IntegrationPerformedPayload>(result.Id.Value));
        Assert.Empty(await world.App.App.PayloadsAsync<ReviewRequestedPayload>(result.Id.Value));
        Assert.Empty(await world.App.Get<IVerdictStore>().ListAsync(world.Project.ProjectId, result.Id.Value, Ct));
        Assert.DoesNotContain(clis.Launches, request => request.TaskId == "R1");

        var chain = await AssertAttemptChainAsync(world, result, milestone.Header.Id, "src/incomplete.txt", testPassed: false);
        var failed = Assert.Single(await world.App.App.PayloadsAsync<TaskStatusChangedPayload>(result.Id.Value), row => row.Payload.Task == WorkTask && row.Payload.To == WorkTaskStatus.Failed);
        var test = Assert.Single(await world.App.App.PayloadsAsync<EvidenceRegisteredPayload>(result.Id.Value), row => row.Payload.Task == WorkTask && row.Payload.Kind == EvidenceKind.TestRun);
        Assert.Contains(test.Payload.Evidence, failed.Payload.Evidence);
        Assert.DoesNotContain(chain.Steps, step => step.Stage == TraceChainStage.Verdict);

        var board = await world.App.Get<IBoardProjector>().ProjectAsync(world.Project, milestone.Header.Id, Ct);
        Assert.Equal(BoardColumn.Returned, Assert.Single(board.Requirements, card => card.Id == "RQ-01").Column);
        Assert.Equal(BoardColumn.Returned, Assert.Single(board.Tasks, card => card.Id == "T1").Column);
        await AssertNotDeliveredOrPublishedAsync(world, result, userHead, remoteHead);
    }

    [Fact]
    public async Task A_rejected_run_keeps_the_judged_commit_and_verdict_in_its_chain_without_inheriting_an_earlier_runs_approval()
    {
        var clis = new Phase5Clis();
        await using var world = await OpenAsync(clis);
        var milestone = await Phase9Runs.OpenMilestoneAsync(world.App);
        var userHead = world.App.Repo.Head;
        var remoteHead = world.BareRef("main");

        // A real approval in the same milestone is a control against attributing it to the later rejected run.
        RunResult approved;
        try
        {
            approved = await world.SucceededRunAsync();
        }
        catch
        {
            // Keep the original control assertion, but capture its evidence before the world is disposed.
            try
            {
                await WriteVerificationDiagnosticsAsync(world);
            }
            catch (Exception diagnosticError)
            {
                TestContext.Current.SendDiagnosticMessage("Could not read verification diagnostics: " + diagnosticError);
            }

            throw;
        }
        await Phase9Runs.LinkRunAsync(world.App, milestone.Header.Id, approved.Id);
        Assert.True((await world.App.Get<IDeliveryService>().CheckAsync(world.Project, approved.Id, Ct)).Ready);
        clis.Decision = _ => VerdictDecision.Rejected;

        var result = await RunAsync(world);
        await WriteVerificationDiagnosticsAsync(world, result.Id.Value);
        await Phase9Runs.LinkRunAsync(world.App, milestone.Header.Id, result.Id);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.NotNull(result.IntegrationCommit);
        Assert.NotNull(result.VerdictId);
        Assert.NotEqual(approved.VerdictId, result.VerdictId);
        var issued = Assert.Single(await world.App.App.PayloadsAsync<VerdictIssuedPayload>(result.Id.Value));
        var stored = Assert.Single(await world.App.Get<IVerdictStore>().ListAsync(world.Project.ProjectId, result.Id.Value, Ct));
        Assert.Equal((result.VerdictId, true, VerdictDecision.Rejected), (issued.Payload.VerdictId, issued.Payload.Valid, issued.Payload.Decision));
        Assert.Equal((result.VerdictId, true, VerdictDecision.Rejected), (stored.Id, stored.Valid, stored.Verdict.Decision));
        Assert.Equal(result.Id.Value, stored.RunId);
        Assert.Equal(milestone.Header.RequirementSetHash, issued.Payload.RequirementSetHash);
        Assert.Equal(issued.Payload.RequirementSetHash, stored.Verdict.RequirementSetHash);
        Assert.Equal(issued.Payload.DecisionLogHeadHash, stored.Verdict.DecisionLogHeadHash);

        // Integration is the candidate the judge inspects; rejection prevents its delivery, rather than erasing that candidate.
        var integration = Assert.Single(await world.App.App.PayloadsAsync<IntegrationPerformedPayload>(result.Id.Value));
        Assert.False(integration.Payload.IsDelivery);
        Assert.Equal(result.IntegrationCommit, integration.Payload.ResultCommit);
        var reviewTree = Assert.Single(await world.App.App.PayloadsAsync<WorktreeCreatedPayload>(result.Id.Value), row => row.Payload.Task == new TaskId("R1"));
        Assert.Equal(result.IntegrationCommit, reviewTree.Payload.BaseCommit);
        Assert.True(integration.Entry.Sequence < issued.Entry.Sequence);
        var integrationTest = Assert.Single(await world.App.App.PayloadsAsync<EvidenceRegisteredPayload>(result.Id.Value), row => row.Payload.Task == new TaskId("I1") && row.Payload.Kind == EvidenceKind.TestRun);
        Assert.Equal(result.IntegrationCommit, integrationTest.Payload.SubjectCommit);
        Assert.Contains(integrationTest.Payload.Evidence, stored.Verdict.EvidenceExamined);
        var launches = await world.App.App.PayloadsAsync<AgentRunStartedPayload>(result.Id.Value);
        var executor = Assert.Single(launches, row => row.Payload.Task == WorkTask).Payload;
        // A run judge is recorded by purpose; its v2 start deliberately has no task id.
        var judge = Assert.Single(launches, row => row.Payload.Purpose == AgentRunPurpose.Judge).Payload;
        Assert.Null(judge.Task);
        Assert.Equal(reviewTree.Payload.Path, judge.WorkingDirectory);
        Assert.NotEqual(executor.Vendor, judge.Vendor);
        Assert.Equal(stored.Verdict.Judge.Agent, judge.Agent);
        var shownHead = Assert.Single((await world.LogAsync()).Entries, entry => entry.Hash == issued.Payload.DecisionLogHeadHash);
        Assert.True(integration.Entry.Sequence <= shownHead.Sequence && shownHead.Sequence < issued.Entry.Sequence);

        var chain = await AssertAttemptChainAsync(world, result, milestone.Header.Id, "src/feature.txt", testPassed: true);
        var rejected = Assert.Single(chain.Steps, step => step.Node == TraceRef.Verdict(result.VerdictId));
        Assert.Equal(TraceVerdictStanding.InForce, rejected.Standing);
        Assert.DoesNotContain(chain.Steps, step => step.Node == TraceRef.Verdict(approved.VerdictId!));
        var requirementChain = await world.App.Get<ITraceChainReader>().ChainAsync(world.Project, TraceRef.Requirement("RQ-01"), Ct);
        Assert.Equal(TraceVerdictStanding.Superseded, Assert.Single(requirementChain.Steps, step => step.Node == TraceRef.Verdict(approved.VerdictId!)).Standing);
        Assert.Equal(TraceVerdictStanding.InForce, Assert.Single(requirementChain.Steps, step => step.Node == TraceRef.Verdict(result.VerdictId)).Standing);

        var board = await world.App.Get<IBoardProjector>().ProjectAsync(world.Project, milestone.Header.Id, Ct);
        var requirement = Assert.Single(board.Requirements, card => card.Id == "RQ-01");
        Assert.Equal((BoardColumn.Returned, result.Id.Value), (requirement.Column, requirement.Run));
        Assert.DoesNotContain(board.Tasks, card => card.Run == result.Id.Value && card.Column is BoardColumn.Done or BoardColumn.Integrated);
        await AssertNotDeliveredOrPublishedAsync(world, result, userHead, remoteHead);
    }

    /// <summary>Read the system's hashed verification evidence while its temporary store still exists.</summary>
    private static async Task WriteVerificationDiagnosticsAsync(Phase10World world, string? runId = null)
    {
        var rows = await world.App.App.PayloadsAsync<EvidenceRegisteredPayload>(runId);
        foreach (var row in rows.Where(row => row.Payload.Kind == EvidenceKind.TestRun))
        {
            var payload = row.Payload;
            var text = await Phase5Checks.TextAsync(world.App.App, payload.Evidence, payload.Sha256);
            var message = $"Verification evidence {payload.RunId}/{payload.Task}/{payload.Attempt}: {payload.TestRun}\n{text}";
            if (TestContext.Current.TestOutputHelper is { } output)
            {
                output.WriteLine(message);
            }
            else
            {
                TestContext.Current.SendDiagnosticMessage(message);
            }
        }
    }

    /// <summary>The chain must name this attempt and its real, hashed diff/test evidence, not just contain stage labels.</summary>
    private static async Task<TraceChain> AssertAttemptChainAsync(Phase10World world, RunResult run, MilestoneId milestone, string changedPath, bool testPassed)
    {
        var app = world.App.App;
        var started = Assert.Single(await app.PayloadsAsync<RunStartedPayload>(run.Id.Value));
        var membership = Assert.Single(await app.PayloadsAsync<MilestoneLinkedPayload>(milestone.Value), row => row.Payload.Target == run.Id.Value);
        Assert.Equal(MilestoneTargetKind.Run, membership.Payload.Kind);
        var keptMilestone = await world.App.Get<IMilestoneService>().GetAsync(world.Project, milestone, Ct);
        Assert.NotNull(keptMilestone);
        Assert.Equal(keptMilestone.CurrentRequirementSetHash, started.Payload.RequirementSetHash);
        var launches = await app.PayloadsAsync<AgentRunStartedPayload>(run.Id.Value);
        var launched = Assert.Single(launches, row => row.Payload.Task == WorkTask);
        Assert.Equal(1, launched.Payload.Attempt);
        var evidence = await app.PayloadsAsync<EvidenceRegisteredPayload>(run.Id.Value);
        var diff = Assert.Single(evidence, row => row.Payload.Task == WorkTask && row.Payload.Attempt == 1 && row.Payload.Kind == EvidenceKind.CodeDiff);
        var test = Assert.Single(evidence, row => row.Payload.Task == WorkTask && row.Payload.Attempt == 1 && row.Payload.Kind == EvidenceKind.TestRun);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, diff.Payload.Origin);
        Assert.Equal(EvidenceOrigin.CapturedBySystem, test.Payload.Origin);
        Assert.True(TraceRef.IsFullCommitId(test.Payload.SubjectCommit));
        Assert.NotNull(test.Payload.TestRun);
        Assert.Equal(testPassed ? (1, 0, 0) : (0, 1, 1), (test.Payload.TestRun.Passed, test.Payload.TestRun.Failed, test.Payload.TestRun.ExitCode));
        Assert.Contains(changedPath, await Phase5Checks.TextAsync(app, diff.Payload.Evidence, diff.Payload.Sha256), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(await Phase5Checks.TextAsync(app, test.Payload.Evidence, test.Payload.Sha256)));
        Assert.True(launched.Entry.Sequence < diff.Entry.Sequence && diff.Entry.Sequence < test.Entry.Sequence);

        var chain = await world.App.Get<ITraceChainReader>().ChainAsync(world.Project, TraceRef.Task(run.Id.Value, "T1"), Ct);
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Requirement && step.Node == TraceRef.Requirement("RQ-01"));
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Milestone && step.Node == TraceRef.Milestone(milestone.Value));
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Task && step.Node == TraceRef.Task(run.Id.Value, "T1"));
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Attempt && step.Node == TraceRef.Attempt(run.Id.Value, "T1", 1));
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Change && step.Node == TraceRef.Evidence(diff.Payload.Evidence.Value));
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Change && step.Node == TraceRef.Commit(test.Payload.SubjectCommit!));
        Assert.Contains(chain.Steps, step => step.Stage == TraceChainStage.Verification && step.Node == TraceRef.Evidence(test.Payload.Evidence.Value));
        Assert.All(chain.Steps, step => Assert.False(step.Suggested));
        return chain;
    }

    private static async Task AssertNotDeliveredOrPublishedAsync(Phase10World world, RunResult run, string userHead, string? remoteHead)
    {
        var delivery = world.App.Get<IDeliveryService>();
        var readiness = await delivery.CheckAsync(world.Project, run.Id, Ct);
        Assert.False(readiness.Ready);
        Assert.Null(readiness.Commit);
        Assert.NotNull(readiness.ReasonText);
        var approvals = world.App.Person.Requests.Count;
        var processes = world.Git.Specs.Count;
        var platformCalls = world.Platform.Calls.ToArray();

        var delivered = await delivery.DeliverAsync(world.Project, run.Id, DeliveryMode.FastForwardOnly, Ct);
        Assert.False(delivered.Delivered);
        Assert.Null(delivered.ResultCommit);
        var preview = await world.Publication.PreviewAsync(world.Project, run.Id, Main, Ct);
        Assert.False(preview.Ready);
        Assert.Equal(readiness.ReasonText.Code, preview.NotReadyReason?.Code);
        var status = await RemoteJourneyLocalToolsTests.PublishAsync(world, new PublicationRequest(world.Project, run.Id, Main, new PullRequestText("Unapproved work", "This must not leave the repository."), false));
        Assert.Equal(RemoteWorkState.Failed, status?.State);

        Assert.Equal(approvals, world.App.Person.Requests.Count);
        Assert.Equal(processes, world.Git.Specs.Count);
        Assert.Equal(platformCalls, world.Platform.Calls);
        Assert.Equal(userHead, world.App.Repo.Head);
        Assert.Equal(remoteHead, world.BareRef("main"));
        Assert.Null(world.BareRef(RemoteBranch.ForRun(run.Id).Name));
        Assert.DoesNotContain(await world.App.App.PayloadsAsync<IntegrationPerformedPayload>(run.Id.Value), row => row.Payload.IsDelivery);
        Assert.Empty(await world.App.Get<IPullRequestProjector>().ListAsync(world.Project, Ct));
        var chain = await world.App.Get<ITraceChainReader>().ChainAsync(world.Project, TraceRef.Task(run.Id.Value, "T1"), Ct);
        Assert.DoesNotContain(chain.Steps, step => step.Stage is TraceChainStage.Delivery or TraceChainStage.Publication or TraceChainStage.RemoteMerge);
        var log = await world.LogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
    }
}
