using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Debate.Audit.Fixtures;

namespace AgentStudio.Tests.Debate.Audit.Milestones;

/// <summary>
/// The close of a milestone over the bench of the audit (Claude's clean F3-8 run, delivered, and a scripted judge of another
/// vendor). The service of milestones (WP1) is a double that replays the log with the real <see cref="MilestoneStateReplayer"/>.
/// </summary>
internal sealed class MilestoneBench : IDisposable
{
    public static readonly MilestoneId Id = new("mil-20261019-a1b2c3");

    public MilestoneBench(ProjectAuditBench audit)
    {
        Audit = audit;
        Header = new MilestoneHeader(Id, "Multiplicar", "Que Calc multiplique y esté documentado", audit.World.Frozen.ContentHash, null, AuditWorld.T0);
        Service = new LogMilestoneService(audit, Header);
        Board = new FakeBoard(audit);
        Closer = new MilestoneCloser(Service, audit.Service, audit.Template, audit.Recorder, audit.World.Requirements, audit.World.Evidence, Board, [Listener]);
    }

    public ProjectAuditBench Audit { get; }

    public MilestoneHeader Header { get; }

    public LogMilestoneService Service { get; }

    public FakeBoard Board { get; }

    public RecordingListener Listener { get; } = new();

    public MilestoneCloser Closer { get; }

    public static async Task<MilestoneBench> WithRunAsync(bool linkRun = true)
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        var audit = new ProjectAuditBench(run);
        await audit.SaveRequirementsAsync();
        var bench = new MilestoneBench(audit);
        if (linkRun)
        {
            await bench.LinkAsync(MilestoneTargetKind.Run, AuditWorld.Run.Value);
        }

        return bench;
    }

    /// <summary>A project whose only run started and worked but did not finish: it has a pending problem (<c>run.unfinished</c>).</summary>
    public static async Task<MilestoneBench> WithUnfinishedRunAsync()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        var audit = new ProjectAuditBench(run);
        await audit.SaveRequirementsAsync();
        return new MilestoneBench(audit);
    }

    public MilestoneCloser CloserWith(AgentStudio.Application.Judging.IProjectAuditService audits, IDecisionRecorder recorder) =>
        new(Service, audits, Audit.Template, recorder, Audit.World.Requirements, Audit.World.Evidence, Board, [Listener]);

    public ProjectContext Project => Audit.Project;

    public IReadOnlyList<DecisionEntry> Entries => Audit.Entries;

    public Task LinkAsync(MilestoneTargetKind kind, string id) =>
        Audit.World.RecordAsync(PreparedRun.Person, DecisionKind.MilestoneLinked, $"Vinculado {id}", MilestoneLinkedPayload.Linking(Id, kind, id, "usuario"));

    public Task ReopenAsync() =>
        Audit.World.RecordAsync(PreparedRun.Person, DecisionKind.MilestoneReopened, "Reabierto", MilestoneReopenedPayload.Reopening(Id, MilestoneReopenReason.ByPerson, null, null, "usuario", null));

    public Task<MilestoneView> CloseAsync(AgentId? judge = null) =>
        Closer.CloseAsync(new CloseMilestoneRequest(Project, Id, "usuario", judge), null, TestContext.Current.CancellationToken);

    public void Dispose() => Audit.Dispose();
}

/// <summary>The milestone as the service of WP1 gives it: the header and the replay of its entries in the log.</summary>
internal sealed class LogMilestoneService(ProjectAuditBench audit, MilestoneHeader header) : IMilestoneService
{
    public Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken)
    {
        if (milestone != header.Id)
        {
            return Task.FromResult<MilestoneView?>(null);
        }

        var events = new List<MilestoneEvent>();
        var reports = new List<MilestoneReportReference>();
        foreach (var entry in AgentStudio.Debate.Judging.RunLog.EntriesOf(audit.Entries, milestone.Value))
        {
            switch (entry.Kind)
            {
                case DecisionKind.MilestoneRequirementsRevised:
                    events.Add(new RequirementsRevised(entry.RecordedAt, DecisionPayloadSerializer.Deserialize<MilestoneRequirementsRevisedPayload>(entry.PayloadJson).RequirementSetHash));
                    break;
                case DecisionKind.MilestoneLinked:
                    var linked = DecisionPayloadSerializer.Deserialize<MilestoneLinkedPayload>(entry.PayloadJson);
                    events.Add(new TargetLinked(entry.RecordedAt, new MilestoneTarget(linked.Kind, linked.Target)));
                    break;
                case DecisionKind.MilestoneClosed:
                    var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(entry.PayloadJson);
                    events.Add(new ClosureRecorded(new MilestoneClosure(entry.RecordedAt, closed.Revision, closed.Audit, closed.ReportSha256, closed.IntegratedCommit)));
                    reports.Add(new MilestoneReportReference(closed.Revision, closed.Audit, closed.Report, closed.ReportSha256, closed.IntegratedCommit, entry.RecordedAt));
                    break;
                case DecisionKind.MilestoneReopened:
                    var reopened = DecisionPayloadSerializer.Deserialize<MilestoneReopenedPayload>(entry.PayloadJson);
                    events.Add(new ReopeningRecorded(new MilestoneReopening(entry.RecordedAt, reopened.Reason, reopened.EvidenceSha256)));
                    break;
            }
        }

        var replay = MilestoneStateReplayer.Replay(header, events);
        var m = replay.Milestone!;
        return Task.FromResult<MilestoneView?>(new MilestoneView(header, m.Status, m.CurrentRequirementSetHash, m.RequirementSetHistory, m.Targets, reports, replay.Problems));
    }

    public Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, AgentStudio.Domain.Requirements.FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>The board of the milestone: the column of each requirement a test sets.</summary>
internal sealed class FakeBoard(ProjectAuditBench audit) : IBoardProjector
{
    public Dictionary<string, BoardColumn> Columns { get; } = new(StringComparer.Ordinal)
    {
        ["RQ-01"] = BoardColumn.Integrated,
        ["RQ-02"] = BoardColumn.Backlog,
    };

    /// <summary>The run that decided a card (Phase 10: the report looks for the pull requests of that run).</summary>
    public Dictionary<string, string> Runs { get; } = new(StringComparer.Ordinal);

    /// <summary>The reason a card says (Phase 10: a pull request merged with something the judge did not see).</summary>
    public Dictionary<string, string> Reasons { get; } = new(StringComparer.Ordinal);

    /// <summary>The pull request that integrated a card (Phase 10: «desplegado» is said only of the pull request the card says carried the requirement); none: the local delivery or nothing.</summary>
    public Dictionary<string, string> ByPullRequest { get; } = new(StringComparer.Ordinal);

    public Task<MilestoneBoard> ProjectAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken)
    {
        var entries = audit.Entries;
        var cards = Columns.Select(c => new BoardCard(BoardCardKind.Requirement, c.Key, c.Key, c.Value, Runs.GetValueOrDefault(c.Key), Reasons.GetValueOrDefault(c.Key), ByPullRequest.GetValueOrDefault(c.Key))).ToList();
        return Task.FromResult(new MilestoneBoard(milestone, entries.Count, entries[^1].Hash, cards, []));
    }

    public Task<IReadOnlyList<BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>The recorder of the log, failing once when it is asked to announce the evidence of the report of the milestone (a cut after it was kept).</summary>
internal sealed class CutAfterReportRecorder(IDecisionRecorder inner) : IDecisionRecorder
{
    private bool _cut;

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) => inner.LoadAsync(projectId, cancellationToken);

    public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => inner.LoadAsync(projectId, load, cancellationToken);

    public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default)
    {
        if (!_cut && kind == DecisionKind.EvidenceRegistered && payload.RunId == MilestoneBench.Id.Value)
        {
            _cut = true;
            throw new IOException("el proceso se cortó");
        }

        return inner.RecordAsync(projectId, actor, kind, summary, payload, corrects, cancellationToken);
    }
}

/// <summary>The audit service that runs once and gives the same report again, as if a retry found the audit it had already made.</summary>
internal sealed class CachedAudits(AgentStudio.Application.Judging.IProjectAuditService inner) : AgentStudio.Application.Judging.IProjectAuditService
{
    private AgentStudio.Application.Judging.AuditReport? _report;

    public Task<AgentStudio.Application.Judging.ProjectAuditProposal> PrepareAsync(AgentStudio.Application.Judging.ProjectAuditRequest request, CancellationToken ct) => inner.PrepareAsync(request, ct);

    public async Task<AgentStudio.Application.Judging.AuditReport> AuditAsync(AgentStudio.Application.Judging.ProjectAuditProposal proposal, IProgress<AgentStudio.Application.Providers.AgentEvent>? progress, CancellationToken ct) =>
        _report ??= await inner.AuditAsync(proposal, progress, ct);
}

internal sealed class RecordingListener : IMilestoneCloseListener
{
    public List<MilestoneClosedNotice> Notices { get; } = [];

    public bool Throw { get; set; }

    public Task OnClosedAsync(MilestoneClosedNotice notice, CancellationToken cancellationToken)
    {
        Notices.Add(notice);
        return Throw ? throw new InvalidOperationException("la documentación falló") : Task.CompletedTask;
    }
}
