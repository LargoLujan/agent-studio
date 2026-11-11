using System.IO;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Debate.Audit.Fixtures;

namespace AgentStudio.Tests.Debate.Audit.Milestones;

/// <summary>docs/16 D-49 and D-50, F9-10 with a scripted judge: the close audits the milestone, keeps a deterministic report and records MilestoneClosed.</summary>
public sealed class MilestoneCloserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Payload<T>(DecisionEntry entry)
        where T : DecisionPayload => DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);

    private static JsonElement ReportOf(MilestoneBench bench, MilestoneClosedPayload closed)
    {
        using var document = JsonDocument.Parse(bench.Audit.World.Evidence.ContentText(closed.Report)!);
        return document.RootElement.Clone();
    }

    private static void AssertNothingRan(MilestoneBench bench, int entriesBefore)
    {
        Assert.Equal(entriesBefore, bench.Entries.Count);
        Assert.Empty(bench.Audit.Gate.Requests);
        Assert.Empty(bench.Audit.Verifier.Calls);
        Assert.Empty(bench.Audit.Judges.Created);
        Assert.Empty(bench.Audit.Audits.Created);
    }

    [Fact]
    public async Task A_milestone_with_only_debates_is_refused_before_anything_is_spent()
    {
        using var bench = await MilestoneBench.WithRunAsync(linkRun: false);
        await bench.LinkAsync(MilestoneTargetKind.Debate, "deb-20261019-abcdef");
        var before = bench.Entries.Count;

        var refused = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync());

        Assert.Equal("milestone.nothing-to-close", refused.Rule);
        Assert.Contains("debates", refused.Reasons[0], StringComparison.Ordinal);
        AssertNothingRan(bench, before);
    }

    [Fact]
    public async Task A_judge_on_a_producer_vendor_leaves_nobody_to_judge_and_nothing_is_spent()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        var before = bench.Entries.Count;

        var refused = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync(ProjectAuditBench.ClaudeJudge));

        Assert.Equal("milestone.no-judge", refused.Rule);
        AssertNothingRan(bench, before);
    }

    [Fact]
    public async Task Closing_audits_the_milestone_keeps_the_report_as_evidence_and_records_the_close_after_it()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        var before = bench.Entries.Count;

        var view = await bench.CloseAsync();

        Assert.Equal(MilestoneStatus.Closed, view.Status);
        var approval = Assert.Single(bench.Audit.Gate.Requests);
        Assert.Contains($"({MilestoneBench.Id.Value})", approval.Description, StringComparison.Ordinal);
        Assert.Contains("Multiplicar", approval.Description, StringComparison.Ordinal);

        var unit = bench.Entries.Skip(before).ToList();
        var closedEntry = unit[^1];
        Assert.Equal(DecisionKind.MilestoneClosed, closedEntry.Kind);
        Assert.Equal(DecisionKind.EvidenceRegistered, unit[^2].Kind);
        var closed = Payload<MilestoneClosedPayload>(closedEntry);
        var registered = Payload<EvidenceRegisteredPayload>(unit[^2]);
        Assert.Equal((MilestoneBench.Id.Value, 1, registered.Evidence, ProjectAuditBench.Head), (closed.RunId, closed.Revision, closed.Report, closed.IntegratedCommit));
        Assert.Equal(ContentHash.Sha256Hex(bench.Audit.World.Evidence.ContentText(closed.Report)!), closed.ReportSha256);
        Assert.StartsWith(AuditIds.Prefix, closed.Audit, StringComparison.Ordinal);
        Assert.Contains(bench.Entries, e => e.Kind == DecisionKind.ProjectAuditFinished && Payload<ProjectAuditFinishedPayload>(e).RunId == closed.Audit);

        var notice = Assert.Single(bench.Listener.Notices);
        Assert.Equal((closed.Report, 1), (notice.Report.Report, notice.Report.Revision));
        Assert.Equal(view, notice.Milestone);
    }

    [Fact]
    public async Task The_report_states_the_requirements_runs_audit_and_deviations_without_consumption_or_deployment()
    {
        using var bench = await MilestoneBench.WithRunAsync();

        await bench.CloseAsync();
        var closed = Payload<MilestoneClosedPayload>(bench.Entries[^1]);
        var report = ReportOf(bench, closed);

        Assert.Equal(MilestoneReport.CurrentFormat, report.GetProperty("format").GetString());
        Assert.Equal(1, report.GetProperty("revision").GetInt32());
        var requirements = report.GetProperty("requirements").EnumerateArray().ToList();
        Assert.Equal(["RQ-01", "RQ-02"], requirements.Select(r => r.GetProperty("id").GetString()));
        var first = requirements[0];
        Assert.Equal(("Satisfied", true, true, true), (first.GetProperty("assessment").GetString(), first.GetProperty("implemented").GetBoolean(), first.GetProperty("reviewed").GetBoolean(), first.GetProperty("integrated").GetBoolean()));
        var second = requirements[1];
        Assert.Equal(("NotVerifiable", false, false), (second.GetProperty("assessment").GetString(), second.GetProperty("implemented").GetBoolean(), second.GetProperty("integrated").GetBoolean()));
        var codes = report.GetProperty("deviations").EnumerateArray().Select(d => (d.GetProperty("code").GetString(), d.GetProperty("subject").GetString())).ToList();
        Assert.Contains((MilestoneReport.RequirementNotVerifiable, "RQ-02"), codes);
        Assert.Contains((MilestoneReport.RequirementUncovered, "RQ-02"), codes);
        Assert.DoesNotContain(codes, c => c.Item2 == "RQ-01");

        var run = Assert.Single(report.GetProperty("runs").EnumerateArray());
        Assert.Equal((AuditWorld.Run.Value, true, "Succeeded"), (run.GetProperty("id").GetString(), run.GetProperty("linkedDirectly").GetBoolean(), run.GetProperty("phase").GetString()));
        Assert.Equal([AuditWorld.CommitOf("delivered")], run.GetProperty("deliveries").EnumerateArray().Select(d => d.GetString()));
        var audit = report.GetProperty("audit");
        Assert.Equal((closed.Audit, "Approved", true), (audit.GetProperty("id").GetString(), audit.GetProperty("disposition").GetString(), audit.GetProperty("trustworthy").GetBoolean()));
        Assert.Equal(2, report.GetProperty("notIncluded").GetArrayLength());
        Assert.DoesNotContain("deployed", report.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_material_gives_the_same_report_byte_for_byte()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        await bench.CloseAsync();
        var closed = Payload<MilestoneClosedPayload>(bench.Entries[^1]);
        var notice = Assert.Single(bench.Listener.Notices);
        var stored = bench.Audit.World.Evidence.ContentText(closed.Report)!;

        // Rebuilt from the log as it stood when the report was read (before its own evidence), the same board and the same audit.
        var log = bench.Entries.Take(bench.Entries.Count - 2).ToList();
        var frozen = (await bench.Audit.World.Requirements.FindAsync(bench.Project.ProjectId, notice.Milestone.CurrentRequirementSetHash, Ct))!;
        var cards = bench.Board.Columns.Select(c => new BoardCard(BoardCardKind.Requirement, c.Key, c.Key, c.Value, null, null)).ToList();
        var board = new MilestoneBoard(MilestoneBench.Id, log.Count, log[^1].Hash, cards, []);
        var scope = new AgentStudio.Application.Judging.AuditMilestoneScope(MilestoneBench.Id.Value, "Multiplicar", bench.Header.Objective, [AuditWorld.Run.Value], []);
        var before = notice.Milestone with { Status = MilestoneStatus.Open, Reports = [] };
        var input = new MilestoneReportInput(before, frozen, log, scope, notice.Audit, board);

        var first = MilestoneReportBuilder.Build(input);
        var second = MilestoneReportBuilder.Build(input);

        Assert.Equal(first.ToJson(), second.ToJson());
        Assert.Equal(stored, first.ToJson());
        Assert.Equal(closed.ReportSha256, first.Sha256());
        Assert.DoesNotContain('\r', stored);
    }

    [Fact]
    public async Task An_audit_the_person_did_not_approve_closes_nothing()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Audit.Gate.Approve = false;

        var refused = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync());

        Assert.Equal("milestone.audit-not-run", refused.Rule);
        Assert.NotNull(refused.Audit);
        Assert.DoesNotContain(bench.Entries, e => e.Kind == DecisionKind.MilestoneClosed);
        Assert.Empty(bench.Listener.Notices);
    }

    [Fact]
    public async Task A_closed_milestone_is_not_closed_again_but_reopened_it_closes_as_revision_two_and_keeps_the_first_report()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        await bench.CloseAsync();
        var first = Payload<MilestoneClosedPayload>(bench.Entries[^1]);
        var refused = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync());
        Assert.Equal("milestone.not-open", refused.Rule);

        await bench.ReopenAsync();
        var view = await bench.CloseAsync();

        var second = Payload<MilestoneClosedPayload>(bench.Entries[^1]);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.Report, second.Report);
        Assert.Equal([1, 2], view.Reports.Select(r => r.Revision));
        Assert.Equal(first.Report, view.Reports[0].Report);
        var report = ReportOf(bench, second);
        var earlier = Assert.Single(report.GetProperty("earlierReports").EnumerateArray());
        Assert.Equal((1, first.Report.Value, first.ReportSha256), (earlier.GetProperty("revision").GetInt32(), earlier.GetProperty("evidence").GetString(), earlier.GetProperty("sha256").GetString()));
        Assert.Equal(first.ReportSha256, ContentHash.Sha256Hex(bench.Audit.World.Evidence.ContentText(first.Report)!));
    }

    [Fact]
    public async Task A_listener_that_fails_does_not_undo_the_close()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        bench.Listener.Throw = true;

        var view = await bench.CloseAsync();

        Assert.Equal(MilestoneStatus.Closed, view.Status);
        Assert.Single(bench.Listener.Notices);
    }

    [Fact]
    public async Task The_dossier_of_the_judge_lists_only_the_runs_and_debates_of_the_milestone()
    {
        // The milestone links a run that is not the one of the log: the run of the log is not its business.
        using var bench = await MilestoneBench.WithRunAsync(linkRun: false);
        await bench.LinkAsync(MilestoneTargetKind.Run, "20261019-ffffff");

        await bench.CloseAsync();

        var launch = Assert.Single(bench.Audit.Launches);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(launch.Files!.Folder, "dossier.json")));
        var root = document.RootElement;
        Assert.Equal(MilestoneBench.Id.Value, root.GetProperty("milestone").GetProperty("id").GetString());
        Assert.Equal(["20261019-ffffff"], root.GetProperty("milestone").GetProperty("runs").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(0, root.GetProperty("runs").GetArrayLength());
        Assert.Equal(0, root.GetProperty("entries").GetArrayLength() - root.GetProperty("entries").EnumerateArray().Count(e => e.GetProperty("kind").GetString() is "ProjectAuditStarted" or "ProjectAuditFinished"));
    }

    [Fact]
    public async Task The_close_of_a_milestone_with_a_log_that_does_not_verify_is_refused()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        var edited = bench.Entries.First(e => e.Kind == DecisionKind.TaskAssigned);
        bench.Audit.World.Store.Tamper(AuditWorld.Project, edited.Sequence, e => e with { Summary = e.Summary + " (editado)" });
        var before = bench.Entries.Count;

        var refused = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync());

        Assert.Equal("log.not-intact", refused.Rule);
        AssertNothingRan(bench, before);
    }
}
