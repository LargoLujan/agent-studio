using System.IO;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Debate.Audit.Fixtures;

namespace AgentStudio.Tests.Debate.Audit.Milestones;

/// <summary>The close of a milestone, hardened: what the judge is shown stays in the scope, two closes do not audit twice, a retry does not keep the report twice, and no local path travels in the report.</summary>
public sealed class MilestoneCloserHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pending_problem_of_a_run_outside_the_milestone_is_not_in_the_dossier_of_the_judge()
    {
        using var bench = await MilestoneBench.WithUnfinishedRunAsync();
        await bench.LinkAsync(MilestoneTargetKind.Run, "20261019-ffffff");

        // The audit's own report still lists what the project has pending; the judge is only told about the milestone.
        await bench.CloseAsync();

        var launch = Assert.Single(bench.Audit.Launches);
        var text = File.ReadAllText(Path.Combine(launch.Files!.Folder, "dossier.json"));
        using var document = JsonDocument.Parse(text);
        Assert.Equal(0, document.RootElement.GetProperty("pendingProblems").GetArrayLength());
        Assert.DoesNotContain("run.unfinished", text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuditWorld.Run.Value, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pending_problem_of_a_run_of_the_milestone_is_in_the_dossier()
    {
        using var bench = await MilestoneBench.WithUnfinishedRunAsync();
        await bench.LinkAsync(MilestoneTargetKind.Run, AuditWorld.Run.Value);

        await bench.CloseAsync();

        var launch = Assert.Single(bench.Audit.Launches);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(launch.Files!.Folder, "dossier.json")));
        var pending = document.RootElement.GetProperty("pendingProblems").EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains("run.unfinished", pending);
    }

    [Fact]
    public async Task A_second_close_of_the_same_milestone_is_refused_before_it_audits()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bench.Audit.Verdict = (dossier, identity) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return ProjectAuditBench.Approval(dossier, identity);
        };

        var first = Task.Run(() => bench.CloseAsync(), Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), Ct));
        var refused = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync());
        release.Set();
        var view = await first;

        Assert.Equal("milestone.closing", refused.Rule);
        Assert.Single(bench.Audit.Gate.Requests);
        Assert.Single(bench.Audit.Audits.Created);
        Assert.Equal(MilestoneStatus.Closed, view.Status);

        // Once the first ended, the milestone is free of the guard: what it says now is that it is closed.
        var again = await Assert.ThrowsAsync<MilestoneCloseRefusedException>(() => bench.CloseAsync());
        Assert.Equal("milestone.not-open", again.Rule);
    }

    [Fact]
    public async Task A_retry_after_a_cut_between_the_report_and_its_announcement_reuses_the_identical_report()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        var audits = new CachedAudits(bench.Audit.Service);
        var closer = bench.CloserWith(audits, new CutAfterReportRecorder(bench.Audit.Recorder));
        var request = new CloseMilestoneRequest(bench.Project, MilestoneBench.Id, "usuario");

        await Assert.ThrowsAsync<IOException>(() => closer.CloseAsync(request, null, Ct));
        Assert.DoesNotContain(bench.Entries, e => e.Kind == DecisionKind.MilestoneClosed);
        var view = await closer.CloseAsync(request, null, Ct);

        Assert.Equal(MilestoneStatus.Closed, view.Status);
        var kept = (await bench.Audit.World.Evidence.ListByRunAsync(bench.Project.ProjectId, MilestoneBench.Id.Value, Ct)).Where(e => e.Kind == EvidenceKind.Document).ToList();
        var report = Assert.Single(kept);
        var announced = bench.Entries.Where(e => e.Kind == DecisionKind.EvidenceRegistered && e.PayloadJson.Contains(MilestoneBench.Id.Value, StringComparison.Ordinal)).ToList();
        Assert.Single(announced);
        var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(bench.Entries[^1].PayloadJson);
        Assert.Equal((report.Id, report.ContentSha256), (closed.Report, closed.ReportSha256));
    }

    [Fact]
    public async Task The_paths_of_this_machine_in_the_alerts_do_not_reach_the_report()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        await bench.Audit.World.RecordAsync(
            PreparedRun.System,
            DecisionKind.AuditAlertRaised,
            "Alerta",
            new AuditAlertPayload("RUN_PATH", [], @"No se pudo leer C:\Users\ana\repos\calc\src\Calc.cs al terminar.") { RunId = AuditWorld.Run.Value });

        await bench.CloseAsync();

        var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(bench.Entries[^1].PayloadJson);
        var text = bench.Audit.World.Evidence.ContentText(closed.Report)!;
        Assert.DoesNotContain(@"C:\Users", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ana", text.Replace("Multiplicar", string.Empty, StringComparison.Ordinal).Replace("analiz", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.Ordinal);
        Assert.Contains("[ruta local]", text, StringComparison.Ordinal);
        Assert.Contains("RUN_PATH", text, StringComparison.Ordinal);
    }
}
