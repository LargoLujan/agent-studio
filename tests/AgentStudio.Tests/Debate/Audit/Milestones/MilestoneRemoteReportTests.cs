using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;

namespace AgentStudio.Tests.Debate.Audit.Milestones;

/// <summary>
/// docs/21 D-29 and F10-8: the report of the close lists «integrado» by pull request, the deviation «fusionado con cambios que el juez no vio» and «desplegado» only when the
/// platform said so; and for a project without a remote it is the report of Phase 9, byte for byte (no new key appears).
/// </summary>
public sealed class MilestoneRemoteReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RunId => AuditWorld.Run.Value;

    private static async Task<DecisionEntry> ObserveAsync(MilestoneBench bench, RemoteSnapshot snapshot, string? run = null, bool wrongFingerprint = false)
    {
        var payload = RemoteStateObservedPayload.Observing(snapshot, run ?? RunId, null, []);
        return await bench.Audit.World.RecordAsync(PreparedRun.Person, DecisionKind.RemoteStateObserved, "Observado", wrongFingerprint ? payload with { Fingerprint = new string('0', 64) } : payload);
    }

    private static async Task<JsonElement> CloseAsync(MilestoneBench bench)
    {
        await bench.CloseAsync();
        var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(bench.Entries[^1].PayloadJson);
        using var document = JsonDocument.Parse(bench.Audit.World.Evidence.ContentText(closed.Report)!);
        return document.RootElement.Clone();
    }

    private static JsonElement Requirement(JsonElement report, string id) => report.GetProperty("requirements").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    private static List<(string?, string?)> Deviations(JsonElement report) =>
        [.. report.GetProperty("deviations").EnumerateArray().Select(d => (d.GetProperty("code").GetString(), d.GetProperty("subject").GetString()))];

    [Fact]
    public async Task A_project_without_a_remote_has_the_report_of_phase_9_with_no_new_key()
    {
        using var bench = await MilestoneBench.WithRunAsync();

        var report = await CloseAsync(bench);

        Assert.False(report.TryGetProperty("remote", out _));
        Assert.All(report.GetProperty("requirements").EnumerateArray(), r =>
        {
            Assert.False(r.TryGetProperty("deployed", out _));
            Assert.False(r.TryGetProperty("mergedWithUnjudgedChanges", out _));
        });
        Assert.Equal(2, report.GetProperty("notIncluded").GetArrayLength());
        Assert.DoesNotContain(Deviations(report), d => d.Item1 is "requirement.merged-unjudged-changes" or "remote.revert-suspected");
    }

    [Fact]
    public async Task Remote_entries_of_a_run_outside_the_milestone_add_nothing_to_its_report()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash), run: "20261019-ffffff");

        var report = await CloseAsync(bench);

        Assert.False(report.TryGetProperty("remote", out _));
        Assert.Equal(2, report.GetProperty("notIncluded").GetArrayLength());
    }

    /// <summary>The commit the judge saw for the run of the bench: the one of the dossier of its approved verdict, as the log up to the head that verdict names has it.</summary>
    private static string JudgedCommit(MilestoneBench bench)
    {
        var issued = bench.Entries.Where(e => e.Kind == DecisionKind.VerdictIssued)
            .Select(e => DecisionPayloadSerializer.Deserialize<VerdictIssuedPayload>(e.PayloadJson))
            .Last(v => v.RunId == RunId && v.Valid && v.Decision == AgentStudio.Domain.Judging.VerdictDecision.Approved);
        var at = bench.Entries.First(e => e.Hash == issued.DecisionLogHeadHash);
        return AgentStudio.Debate.Judging.RunLog.CandidateCommit(AgentStudio.Debate.Judging.RunLog.EntriesOf(bench.Entries.Where(e => e.Sequence <= at.Sequence), RunId))!;
    }

    [Fact]
    public async Task A_deployed_pull_request_the_judge_did_not_see_is_not_the_deployment_of_the_requirement_even_if_the_card_is_integrated_by_another_way()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Board.Runs["RQ-01"] = RunId;
        Assert.NotEqual(Head, JudgedCommit(bench));
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash, Head) with { Deployment = new RemoteDeployment(5, Result, DeploymentState.Succeeded) });

        var report = await CloseAsync(bench);

        Assert.True(Requirement(report, "RQ-01").GetProperty("integrated").GetBoolean());
        Assert.False(Requirement(report, "RQ-01").TryGetProperty("deployed", out _));
        Assert.True(report.GetProperty("remote").GetProperty("pullRequests")[0].GetProperty("deployed").GetBoolean(), "The platform said it; the requirement does not take it.");
    }

    [Fact]
    public async Task Deployed_exists_only_for_an_integrated_requirement_whose_run_the_platform_said_it_deployed()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Board.Runs["RQ-01"] = RunId;
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash, JudgedCommit(bench)) with { Deployment = new RemoteDeployment(5, Result, DeploymentState.Succeeded) });

        var report = await CloseAsync(bench);

        Assert.True(Requirement(report, "RQ-01").GetProperty("deployed").GetBoolean());
        Assert.False(Requirement(report, "RQ-02").TryGetProperty("deployed", out _), "There is no «no desplegado».");
        var pullRequest = Assert.Single(report.GetProperty("remote").GetProperty("pullRequests").EnumerateArray());
        Assert.Equal(("github.com/LargoLujan/agent-studio#12", RunId, "Merged", Result, "Squash", true, true), (
            pullRequest.GetProperty("id").GetString(), pullRequest.GetProperty("run").GetString(), pullRequest.GetProperty("state").GetString(), pullRequest.GetProperty("mergeCommit").GetString(),
            pullRequest.GetProperty("mergeMode").GetString(), pullRequest.GetProperty("modeDeduced").GetBoolean(), pullRequest.GetProperty("deployed").GetBoolean()));
        var notIncluded = report.GetProperty("notIncluded").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Single(notIncluded);
        Assert.DoesNotContain(notIncluded, n => n!.StartsWith("Despliegue", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DeploymentState.Failed)]
    [InlineData(DeploymentState.Pending)]
    public async Task A_deployment_that_did_not_succeed_makes_no_state_and_the_report_still_says_it_has_no_deployment_data(DeploymentState state)
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Board.Runs["RQ-01"] = RunId;
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash) with { Deployment = new RemoteDeployment(5, Result, state) });

        var report = await CloseAsync(bench);

        Assert.False(Requirement(report, "RQ-01").TryGetProperty("deployed", out _));
        Assert.Equal(2, report.GetProperty("notIncluded").GetArrayLength());
    }

    [Fact]
    public async Task A_requirement_whose_pull_request_was_merged_with_something_the_judge_did_not_see_is_a_deviation_and_not_a_not_integrated_one()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Board.Columns["RQ-01"] = BoardColumn.Done;
        bench.Board.Reasons["RQ-01"] = BoardCardReasons.MergedWithUnjudgedChanges;
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash, "8888888888888888888888888888888888888888"));

        var report = await CloseAsync(bench);

        var requirement = Requirement(report, "RQ-01");
        Assert.True(requirement.GetProperty("mergedWithUnjudgedChanges").GetBoolean());
        Assert.False(requirement.GetProperty("integrated").GetBoolean());
        Assert.Contains((MilestoneReport.RequirementMergedUnjudged, "RQ-01"), Deviations(report));
        Assert.DoesNotContain((MilestoneReport.RequirementNotIntegrated, "RQ-01"), Deviations(report));
    }

    [Fact]
    public async Task A_revert_that_is_suspected_is_a_deviation_said_as_a_deduction_and_does_not_change_integrated()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash) with { RevertCommits = [Revert] });

        var report = await CloseAsync(bench);

        Assert.Contains((MilestoneReport.RevertSuspected, "github.com/LargoLujan/agent-studio#12"), Deviations(report));
        Assert.True(Requirement(report, "RQ-01").GetProperty("integrated").GetBoolean());
        Assert.True(report.GetProperty("remote").GetProperty("pullRequests")[0].GetProperty("revertSuspected").GetBoolean());
        var message = report.GetProperty("deviations").EnumerateArray().Single(d => d.GetProperty("code").GetString() == MilestoneReport.RevertSuspected).GetProperty("message").GetString();
        Assert.Contains("deducida", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_that_does_not_validate_and_names_another_run_adds_nothing_to_the_report_of_the_milestone()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        await ObserveAsync(bench, Merged(RemoteMergeMode.Squash), run: "20261019-ffffff", wrongFingerprint: true);

        var report = await CloseAsync(bench);

        Assert.False(report.TryGetProperty("remote", out _));
    }

    private static async Task<MilestoneReportInput> InputOfAsync(MilestoneBench bench, MilestoneClosedNotice notice, IReadOnlyList<DecisionEntry> log)
    {
        var frozen = (await bench.Audit.World.Requirements.FindAsync(bench.Project.ProjectId, notice.Milestone.CurrentRequirementSetHash, Ct))!;
        var cards = bench.Board.Columns.Select(c => new BoardCard(BoardCardKind.Requirement, c.Key, c.Key, c.Value, bench.Board.Runs.GetValueOrDefault(c.Key), null)).ToList();
        var board = new MilestoneBoard(MilestoneBench.Id, log.Count, log[^1].Hash, cards, []);
        var scope = new AgentStudio.Application.Judging.AuditMilestoneScope(MilestoneBench.Id.Value, "Multiplicar", bench.Header.Objective, [RunId], []);
        return new MilestoneReportInput(notice.Milestone with { Status = MilestoneStatus.Open, Reports = [] }, frozen, log, scope, notice.Audit, board);
    }

    [Fact]
    public async Task The_report_of_a_log_with_no_remote_is_byte_for_byte_the_one_phase_9_built()
    {
        // The comparison is with the builder as it was at 1eb2c56a (Phase9MilestoneReportBaseline: that class copied, nothing else changed) on the same input, and with what the close stored.
        using var bench = await MilestoneBench.WithRunAsync();
        await bench.CloseAsync();
        var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(bench.Entries[^1].PayloadJson);
        var notice = Assert.Single(bench.Listener.Notices);
        var input = await InputOfAsync(bench, notice, [.. bench.Entries.Take(bench.Entries.Count - 2)]);

        var baseline = Phase9MilestoneReportBaseline.Build(input).ToJson();
        var today = MilestoneReportBuilder.Build(input).ToJson();

        Assert.Equal(baseline, today);
        Assert.Equal(baseline, bench.Audit.World.Evidence.ContentText(closed.Report));
        Assert.Equal(MilestoneReportBuilder.Build(input).Sha256(), AgentStudio.Domain.Common.ContentHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(baseline)));
    }

    [Fact]
    public async Task A_remote_entry_that_does_not_validate_is_left_out_and_listed_by_its_sequence()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        var bad = await ObserveAsync(bench, Merged(RemoteMergeMode.Squash), wrongFingerprint: true);

        var report = await CloseAsync(bench);

        var remote = report.GetProperty("remote");
        Assert.Equal([bad.Sequence], remote.GetProperty("ignoredEntries").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal(0, remote.GetProperty("pullRequests").GetArrayLength());
    }

    [Fact]
    public async Task Repeated_and_reordered_observations_give_the_same_pull_requests_and_requirements()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Board.Runs["RQ-01"] = RunId;
        await bench.CloseAsync();
        var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(bench.Entries[^1].PayloadJson);
        var notice = Assert.Single(bench.Listener.Notices);
        var log = bench.Entries.Take(bench.Entries.Count - 2).ToList();
        var frozen = (await bench.Audit.World.Requirements.FindAsync(bench.Project.ProjectId, notice.Milestone.CurrentRequirementSetHash, Ct))!;
        var cards = bench.Board.Columns.Select(c => new BoardCard(BoardCardKind.Requirement, c.Key, c.Key, c.Value, bench.Board.Runs.GetValueOrDefault(c.Key), null)).ToList();
        var board = new MilestoneBoard(MilestoneBench.Id, log.Count, log[^1].Hash, cards, []);
        var scope = new AgentStudio.Application.Judging.AuditMilestoneScope(MilestoneBench.Id.Value, "Multiplicar", bench.Header.Objective, [RunId], []);
        var before = notice.Milestone with { Status = MilestoneStatus.Open, Reports = [] };

        var opened = Open();
        var merged = Merged(RemoteMergeMode.Squash);
        var reverted = merged with { RevertCommits = [Revert] };
        var deployed = reverted with { Deployment = new RemoteDeployment(5, Result, DeploymentState.Succeeded) };

        string Built(params RemoteSnapshot[] order)
        {
            var extra = order.Select((snapshot, index) => log[^1] with
            {
                Sequence = log[^1].Sequence + 1 + index,
                Kind = DecisionKind.RemoteStateObserved,
                PayloadJson = DecisionPayloadSerializer.Serialize(RemoteStateObservedPayload.Observing(snapshot, RunId, null, [])),
            });
            var report = MilestoneReportBuilder.Build(new MilestoneReportInput(before, frozen, [.. log, .. extra], scope, notice.Audit, board));
            return System.Text.Json.JsonSerializer.Serialize(new { report.Remote, report.Requirements, report.Deviations, report.NotIncluded });
        }

        var expected = Built(opened, merged, reverted, deployed);

        Assert.Equal(expected, Built(deployed, reverted, merged, opened));
        Assert.Equal(expected, Built(merged, merged, opened, deployed, deployed, reverted, opened));
        Assert.Contains("\"Deployed\":true", expected, StringComparison.Ordinal);
        Assert.NotNull(closed);
    }
}
