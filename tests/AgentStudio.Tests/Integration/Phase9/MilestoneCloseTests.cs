using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Audit;
using AgentStudio.Desktop.Documents;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Integration.Phase5;

namespace AgentStudio.Tests.Integration.Phase9;

/// <summary>
/// F9-10 (docs/16 D-49 and D-50): closing a milestone is auditing it, with the real modules (the isolated verification of HEAD, the audit, the
/// deterministic report kept as evidence) and a scripted judge of Codex over work Claude produced. A close that is refused says its rule and
/// writes nothing, launches no judge and asks nothing of the person; reopening keeps the report; closing again makes the next revision.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class MilestoneCloseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_close_with_a_judge_that_did_not_produce_the_work_gives_a_deterministic_report_and_reopening_keeps_it_and_a_refused_close_says_its_rule_and_writes_nothing()
    {
        await using var world = await AppWorld.OpenAsync(clis: new Phase5Clis { Executor = Phase9Runs.Executor });
        var project = world.App.Project;
        var milestones = world.Get<IMilestoneService>();
        var closer = world.Get<IMilestoneCloser>();

        // The work: a reviewed run, delivered; and the trusted profile the audit verifies HEAD with.
        var run = await Phase9Runs.RunAsync(world, Phase5Runs.Reviewed(), Phase5Runs.Profile());
        var delivered = await world.Get<IDeliveryService>().DeliverAsync(project, run.Id, DeliveryMode.FastForwardOnly, Ct);
        Assert.True(delivered.Delivered, delivered.Message);
        await world.Get<IVerificationProfileSource>().SaveAsync(project, Phase5Runs.Profile(), Ct);
        var milestone = await Phase9Runs.OpenMilestoneAsync(world);
        var id = milestone.Header.Id;
        var empty = await Phase9Runs.OpenMilestoneAsync(world, "Sin trabajo");
        await Phase9Runs.LinkRunAsync(world, id, run.Id);

        async Task<(int Entries, int Launches, int Asked)> SnapshotAsync() =>
            ((await world.App.LoadLogAsync()).Entries.Count, world.Clis.Launches.Count, world.Person.Requests.Count);

        // ---- refused closes: each gives its rule and writes nothing at all.
        var before = await SnapshotAsync();
        var claude = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() =>
            closer.CloseAsync(new CloseMilestoneRequest(project, id, "manuel", Phase5Roster.ClaudeJudge, [VendorId.Anthropic]), null, Ct));
        Assert.Equal("milestone.no-judge", claude.Rule);
        Assert.Contains(claude.Reasons, r => r.Contains("La regla de los productores no se dispensa", StringComparison.Ordinal));
        var nothing = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() =>
            closer.CloseAsync(new CloseMilestoneRequest(project, empty.Header.Id, "manuel", Phase5Roster.CodexJudge, [VendorId.Anthropic]), null, Ct));
        Assert.Equal("milestone.nothing-to-close", nothing.Rule);
        Assert.Null(nothing.Audit);
        Assert.Equal(before, await SnapshotAsync());

        // ---- the close: an audit by Codex's judge over Claude's work, and the deterministic report around its verdict.
        var closed = await closer.CloseAsync(new CloseMilestoneRequest(project, id, "manuel", Phase5Roster.CodexJudge, [VendorId.Anthropic]), null, Ct);
        Assert.Equal(MilestoneStatus.Closed, closed.Status);
        Assert.Empty(closed.Problems);
        var first = Assert.Single(closed.Reports);
        Assert.Equal(1, first.Revision);
        Assert.Equal(ProjectAuditService.ApprovalAction, world.Person.Requests[^1].Action); // the person approved the audit
        var audit = Assert.Single(await world.App.PayloadsAsync<AgentRunStartedPayload>(first.Audit), p => p.Payload.Purpose == AgentRunPurpose.ProjectAudit).Payload;
        Assert.Equal((Phase5Roster.CodexJudge, ProviderId.CodexCli, VendorId.OpenAI), (audit.Agent, audit.Provider, audit.Vendor));

        // The report is the evidence the close names: its bytes are the ones the close anchored, and it holds no prose of an agent.
        var json = await Phase5Checks.TextAsync(world.App, first.Report, first.ReportSha256);
        Assert.Equal(first.ReportSha256, AgentStudio.Domain.Common.ContentHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(json)));
        using var report = JsonDocument.Parse(json);
        var root = report.RootElement;
        Assert.Equal(MilestoneReport.CurrentFormat, root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("revision").GetInt32());
        Assert.Equal(id.Value, root.GetProperty("milestone").GetProperty("id").GetString());
        Assert.Equal(VendorId.OpenAI.Value, root.GetProperty("audit").GetProperty("judgeVendor").GetString());
        Assert.True(root.GetProperty("audit").GetProperty("decisionLogIntact").GetBoolean());
        Assert.Equal(run.Id.Value, Assert.Single(root.GetProperty("runs").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("rationale", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("summary", json, StringComparison.OrdinalIgnoreCase);

        // The documentation for clients is offered for the milestone that was closed (the listener of the composition).
        Assert.Equal(milestone.Header.Name, world.Get<ClientDocumentsViewModel>().Milestone);

        // A closed milestone is not closed again: its rule, and nothing written.
        before = await SnapshotAsync();
        var again = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() =>
            closer.CloseAsync(new CloseMilestoneRequest(project, id, "manuel", Phase5Roster.CodexJudge, [VendorId.Anthropic]), null, Ct));
        Assert.Equal("milestone.not-open", again.Rule);
        Assert.Equal(before, await SnapshotAsync());

        // ---- reopening keeps the report; closing again is the next revision and names the one before.
        var reopened = await milestones.ReopenAsync(project, id, MilestoneReopenReason.ByPerson, null, "manuel", "Me lo repienso.", Ct);
        Assert.Equal(MilestoneStatus.Open, reopened.Status);
        Assert.Equal(first, Assert.Single(reopened.Reports));
        var recloseJson = (await closer.CloseAsync(new CloseMilestoneRequest(project, id, "manuel", Phase5Roster.CodexJudge, [VendorId.Anthropic]), null, Ct)).Reports;
        Assert.Equal([1, 2], recloseJson.Select(r => r.Revision));
        Assert.Equal(first, recloseJson[0]);
        Assert.NotEqual(first.ReportSha256, recloseJson[1].ReportSha256);
        using var second = JsonDocument.Parse(await Phase5Checks.TextAsync(world.App, recloseJson[1].Report, recloseJson[1].ReportSha256));
        var earlier = Assert.Single(second.RootElement.GetProperty("earlierReports").EnumerateArray());
        Assert.Equal((1, first.ReportSha256), (earlier.GetProperty("revision").GetInt32(), earlier.GetProperty("sha256").GetString()));

        var log = await world.App.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
    }
}
