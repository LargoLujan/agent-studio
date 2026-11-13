using AgentStudio.Application.Work;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;
using static AgentStudio.Tests.Orchestration.WorkTracking.RemoteTestSupport;

namespace AgentStudio.Tests.Orchestration.WorkTracking;

/// <summary>
/// docs/21 D-28, F10-7: after a squash, a rebase, a merge commit or a revert the chain joins the original commits (with their attempt, task and evidence) to the commits
/// the merge left, deletes nothing, and says what is only a deduction. It is the same from every node it holds, and a repeated or reordered observation changes nothing.
/// </summary>
public sealed class RemoteChainTests
{
    private const string SystemCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PersonCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string RebasedOne = "dddddddddddddddddddddddddddddddddddddddd";
    private const string RebasedTwo = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Squashed = "ffffffffffffffffffffffffffffffffffffffff";
    private const string RevertOfIt = "abababababababababababababababababababab";

    private const string Run = WorkWorld.Run1;

    private static CancellationToken Ct => WorkWorld.Ct;

    private static TraceChainReader Reader(WorkWorld world) => new(world.Instance.Recorder, world.Instance.Requirements, world.Instance.Plans, world.Instance.Verdicts);

    private static TraceRef Pr => TraceRef.PullRequest(PullRequest);

    private static List<(TraceChainStage, TraceRef)> Shape(TraceChain chain) => [.. chain.Steps.Where(s => s.Stage != TraceChainStage.Milestone).Select(s => (s.Stage, s.Node))];

    /// <summary>T1 of the run made an attempt whose commit is <see cref="SystemCommit"/> (a test run bound to it), was judged on <see cref="Head"/> and finished.</summary>
    private static async Task<WorkWorld> RunAsync()
    {
        var world = await WorkWorld.OpenAsync();
        var view = await world.OpenMilestoneAsync();
        await world.StartRunAsync();
        await world.Milestones.LinkAsync(world.Project, view.Header.Id, new MilestoneTarget(MilestoneTargetKind.Run, Run), "manuel", Ct);
        await world.LaunchAsync(Run, "T1", 1);
        await world.EvidenceAsync(Run, "T1", 1, EvidenceKind.CodeDiff, "diff1");
        await world.EvidenceAsync(Run, "T1", 1, EvidenceKind.TestRun, "test1", SystemCommit);
        await world.RunAndCompleteAsync(Run, "T1");
        await world.FinishAsync(Run, AgentStudio.Application.Orchestration.RunPhase.Succeeded);
        await world.JudgeAsync(VerdictDecision.Approved);
        return world;
    }

    private static RemoteSnapshot Merged(RemoteMergeMode mode, IReadOnlyList<string> results) =>
        AgentStudio.Tests.Domain.Hosting.HostingP0Data.Merged(mode) with { OriginalCommits = [SystemCommit, PersonCommit], ResultCommits = results, MergeCommit = results[^1] };

    private static RemoteSnapshot Opened() => Open() with { OriginalCommits = [SystemCommit, PersonCommit] };

    [Fact]
    public async Task An_open_pull_request_is_a_publication_that_joins_its_commits_and_the_tasks_that_made_them()
    {
        using var world = await RunAsync();
        await world.ObserveAsync(Opened());

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        var shape = Shape(chain);
        Assert.Contains((TraceChainStage.Publication, Pr), shape);
        Assert.Contains((TraceChainStage.Change, TraceRef.Commit(SystemCommit)), shape);
        Assert.Contains((TraceChainStage.Change, TraceRef.Commit(PersonCommit)), shape);
        Assert.DoesNotContain(chain.Steps, s => s.Stage is TraceChainStage.RemoteMerge or TraceChainStage.Deployment);
    }

    [Fact]
    public async Task A_squash_joins_every_original_with_the_commit_it_left_and_back_and_the_mode_is_a_deduction()
    {
        using var world = await RunAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]));

        var fromOriginal = await Reader(world).ChainAsync(world.Project, TraceRef.Commit(SystemCommit), Ct);
        var fromResult = await Reader(world).ChainAsync(world.Project, TraceRef.Commit(Squashed), Ct);

        // From the commit of the system: its attempt, its task, its requirement, its evidence, the pull request and what the merge left.
        var shape = Shape(fromOriginal);
        Assert.Contains((TraceChainStage.Requirement, TraceRef.Requirement("R1")), shape);
        Assert.Contains((TraceChainStage.Task, TraceRef.Task(Run, "T1")), shape);
        Assert.Contains((TraceChainStage.Attempt, TraceRef.Attempt(Run, "T1", 1)), shape);
        Assert.Contains((TraceChainStage.Verification, TraceRef.Evidence("test1")), shape);
        Assert.Contains((TraceChainStage.Publication, Pr), shape);
        var merge = Assert.Single(fromOriginal.Steps, s => s.Stage == TraceChainStage.RemoteMerge);
        Assert.Equal((TraceRef.Commit(Squashed), RemoteTraceTitles.MergeMode(RemoteMergeMode.Squash), true), (merge.Node, merge.Title, merge.Suggested));

        // And back: from the commit of the remote, the originals and everything that comes with them (a commit is not a shared node: its chain is its own, so it is inside the other).
        Assert.Contains((TraceChainStage.Change, TraceRef.Commit(SystemCommit)), Shape(fromResult));
        Assert.Contains((TraceChainStage.Change, TraceRef.Commit(PersonCommit)), Shape(fromResult));
        Assert.All(Shape(fromOriginal), s => Assert.Contains(s, Shape(fromResult)));
    }

    [Fact]
    public async Task A_mode_the_app_merged_with_is_not_shown_as_a_suggestion()
    {
        using var world = await RunAsync();
        await world.OperationAsync(AgentStudio.Domain.Hosting.HostingOperationKind.MergePullRequest, 12, MergeMethod.Squash);
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]));

        var chain = await Reader(world).ChainAsync(world.Project, Pr, Ct);

        Assert.False(Assert.Single(chain.Steps, s => s.Stage == TraceChainStage.RemoteMerge).Suggested);
    }

    [Fact]
    public async Task A_rebase_leaves_one_node_per_commit_in_the_order_the_platform_listed_them()
    {
        using var world = await RunAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Rebase, [RebasedTwo, RebasedOne]));

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Commit(SystemCommit), Ct);

        // Not by id: «eeee…» comes before «dddd…» as the platform said, and that is the order of the log of git.
        Assert.Equal(
            [TraceRef.Commit(RebasedTwo), TraceRef.Commit(RebasedOne)],
            chain.Steps.Where(s => s.Stage == TraceChainStage.RemoteMerge).Select(s => s.Node));
    }

    [Fact]
    public async Task A_revert_is_a_step_of_the_remote_merge_stage_after_the_merge_and_always_a_suggestion()
    {
        using var world = await RunAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.MergeCommit, [Squashed]));
        await world.ObserveAsync(Merged(RemoteMergeMode.MergeCommit, [Squashed]) with { RevertCommits = [RevertOfIt] }, Run, RemoteChangeKind.RevertSuspected);

        var chain = await Reader(world).ChainAsync(world.Project, Pr, Ct);

        var steps = chain.Steps.Where(s => s.Stage == TraceChainStage.RemoteMerge).ToList();
        Assert.Equal([TraceRef.Commit(Squashed), TraceRef.Commit(RevertOfIt)], steps.Select(s => s.Node));
        Assert.Equal((RemoteTraceTitles.RevertedBy, true), (steps[1].Title, steps[1].Suggested));
        Assert.Contains((TraceChainStage.Change, TraceRef.Commit(SystemCommit)), Shape(chain));
    }

    [Fact]
    public async Task A_deployment_that_succeeded_is_a_step_after_the_merge_and_one_that_did_not_is_not_there()
    {
        using var world = await RunAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]));
        var none = await Reader(world).ChainAsync(world.Project, Pr, Ct);
        Assert.DoesNotContain(none.Steps, s => s.Stage == TraceChainStage.Deployment);

        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]) with { Deployment = new RemoteDeployment(3, Squashed, DeploymentState.Failed) });
        Assert.DoesNotContain((await Reader(world).ChainAsync(world.Project, Pr, Ct)).Steps, s => s.Stage == TraceChainStage.Deployment);

        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]) with { Deployment = new RemoteDeployment(4, Squashed, DeploymentState.Succeeded) });
        var chain = await Reader(world).ChainAsync(world.Project, Pr, Ct);

        var deployment = Assert.Single(chain.Steps, s => s.Stage == TraceChainStage.Deployment);
        Assert.Equal((TraceRef.Commit(Squashed), RemoteTraceTitles.Deployed), (deployment.Node, deployment.Title));
        Assert.Equal(TraceChainStage.Deployment, chain.Steps[^1].Stage);
    }

    [Fact]
    public async Task The_originals_stay_in_the_chain_and_in_the_log_after_the_merge_and_the_chain_is_the_same_from_every_node_it_holds()
    {
        using var world = await RunAsync();
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]) with { HeadBranchExists = false });
        await world.ObserveAsync(Merged(RemoteMergeMode.Squash, [Squashed]) with { RevertCommits = [RevertOfIt], Deployment = new RemoteDeployment(4, Squashed, DeploymentState.Succeeded) });
        var expected = Shape(await Reader(world).ChainAsync(world.Project, Pr, Ct));

        Assert.Contains((TraceChainStage.Change, TraceRef.Commit(SystemCommit)), expected);
        Assert.Contains((TraceChainStage.Change, TraceRef.Evidence("diff1")), expected);
        // The shared nodes (as the verdict and the delivery are in Phase 9): the pull request, what its merge left and the verdict give the same chain from each of them.
        foreach (var (stage, node) in expected.Where(s => s.Item1 is TraceChainStage.Publication or TraceChainStage.RemoteMerge or TraceChainStage.Verdict).Distinct())
        {
            var chain = await Reader(world).ChainAsync(world.Project, node, Ct);
            var got = Shape(chain);
            Assert.True(expected.OrderBy(s => (s.Item1, s.Item2.ToString())).SequenceEqual(got.OrderBy(s => (s.Item1, s.Item2.ToString()))), $"The chain from {node} ({stage}) is another one: missing [{string.Join(", ", expected.Except(got))}], extra [{string.Join(", ", got.Except(expected))}].");
        }

        var log = await world.Instance.Recorder.LoadAsync(world.ProjectId, Ct);
        Assert.True(log.Verification.IsValid);
    }

    [Fact]
    public async Task Repeated_and_reordered_observations_do_not_duplicate_a_node_nor_change_the_chain()
    {
        var opened = Opened();
        var merged = Merged(RemoteMergeMode.Squash, [Squashed]);
        var reverted = merged with { RevertCommits = [RevertOfIt] };
        var deployed = reverted with { Deployment = new RemoteDeployment(4, Squashed, DeploymentState.Succeeded) };
        var orders = new[]
        {
            new[] { opened, merged, reverted, deployed },
            new[] { deployed, reverted, merged, opened },
            new[] { merged, merged, opened, deployed, deployed, opened, reverted, merged },
        };

        List<(TraceChainStage, TraceRef)>? expected = null;
        foreach (var order in orders)
        {
            using var world = await RunAsync();
            foreach (var snapshot in order)
            {
                await world.ObserveAsync(snapshot);
            }

            var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);
            var shape = Shape(chain);
            expected ??= shape;
            Assert.Equal(expected, shape);
            Assert.Equal(shape.Count, shape.Distinct().Count());
        }
    }

    [Fact]
    public async Task A_remote_entry_that_does_not_validate_is_left_out_and_the_chain_is_the_one_without_it()
    {
        using var world = await RunAsync();
        var before = Shape(await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct));

        await world.ObserveWithAWrongFingerprintAsync(Merged(RemoteMergeMode.Squash, [Squashed]));

        Assert.Equal(before, Shape(await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct)));
    }

    [Fact]
    public async Task A_chain_without_a_remote_is_the_one_of_phase_9()
    {
        using var world = await RunAsync();

        var chain = await Reader(world).ChainAsync(world.Project, TraceRef.Requirement("R1"), Ct);

        Assert.DoesNotContain(chain.Steps, s => s.Stage is TraceChainStage.Publication or TraceChainStage.RemoteMerge or TraceChainStage.Deployment);
        Assert.All(chain.Steps, s => Assert.False(s.Suggested));
    }
}
