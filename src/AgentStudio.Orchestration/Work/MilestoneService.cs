using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>
/// The milestones of a project (docs/16 D-17, ADR-0038): the header is the only row, and everything else is an entry of the log that
/// <see cref="MilestoneStateReplayer"/> replays.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Opening</b> writes in this order, as a debate does: the frozen requirements, the row, and <c>MilestoneOpened</c> with the
/// hash of the header as the registry gives it back. A row with no <c>MilestoneOpened</c> is half open: it is not listed, and opening
/// again with the same name, objective, requirements and budget completes it instead of making another.</item>
/// <item><b>Reading</b> only returns a milestone the log opened. The header is the row's, checked against the hash <c>MilestoneOpened</c>
/// anchored: a header that is not that one (a row rewritten with its own hash behind the store's back) is returned with its problem and
/// nothing else, and nothing is written to it. A log that does not verify throws <see cref="DecisionLogIntegrityException"/>.</item>
/// <item><b>Writing</b> waits until no run, debate or audit has the project (<see cref="WorkWriteGate"/>) and applies the change to the
/// replayed milestone first, so what the domain refuses is never recorded.</item>
/// </list>
/// </remarks>
public sealed class MilestoneService(
    IDecisionRecorder recorder,
    IMilestoneRegistry registry,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    WorkWriteGate gate,
    TimeProvider time) : IMilestoneService
{
    /// <summary>Who the system is in the entries it writes for itself.</summary>
    public const string SystemName = "agent-studio";

    /// <summary>The problem a header that is not the one <c>MilestoneOpened</c> anchored is shown with.</summary>
    public const string HeaderNotAnchoredProblem = "milestone.header-not-anchored";

    public Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Requirements);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OpenedBy);
        var project = request.Project.ProjectId;
        return gate.RunAsync(
            project,
            async () =>
            {
                var entries = await WorkLog.LoadAsync(recorder, project, cancellationToken).ConfigureAwait(false);
                var headers = await registry.ListAsync(project, cancellationToken).ConfigureAwait(false);
                var opened = OpenedIn(entries);

                // The same name, objective, requirements and budget as a row nobody announced: the opening that was left half done.
                var header = headers.FirstOrDefault(h => !opened.ContainsKey(h.Id.Value) && Same(h, request));
                header ??= new MilestoneHeader(
                    NewId(headers, entries),
                    request.Name?.Trim() ?? string.Empty,
                    request.Objective?.Trim() ?? string.Empty,
                    request.Requirements.ContentHash,
                    request.Budget,
                    time.GetUtcNow());
                Milestone.Open(header);

                if (await requirements.FindAsync(project, header.RequirementSetHash, cancellationToken).ConfigureAwait(false) is null)
                {
                    await requirements.SaveAsync(project, request.Requirements, cancellationToken).ConfigureAwait(false);
                }

                await registry.CreateAsync(project, header, cancellationToken).ConfigureAwait(false);
                var stored = await registry.FindAsync(project, header.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"The registry of milestones does not return the header of {header.Id.Value} it has just stored: it is not opened.");
                if (!opened.ContainsKey(stored.Id.Value))
                {
                    await recorder.RecordAsync(
                        project,
                        new Actor(ActorKind.User, request.OpenedBy),
                        DecisionKind.MilestoneOpened,
                        $"Abre el hito {stored.Id.Value}: {stored.Name}",
                        MilestoneOpenedPayload.Opening(stored.Id, MilestoneHeaderHash.Of(stored), stored.Name, stored.RequirementSetHash, stored.Budget, request.OpenedBy),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                return (await ViewAsync(project, stored.Id, cancellationToken).ConfigureAwait(false))!;
            },
            cancellationToken);
    }

    public Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, FrozenRequirementSet requirementSet, string revisedBy, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirementSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisedBy);
        return WriteAsync(
            project,
            milestone,
            async (view, replayed) =>
            {
                replayed.ReviseRequirements(requirementSet.ContentHash, DateTimeOffset.MaxValue);
                if (await requirements.FindAsync(project.ProjectId, requirementSet.ContentHash, cancellationToken).ConfigureAwait(false) is null)
                {
                    await requirements.SaveAsync(project.ProjectId, requirementSet, cancellationToken).ConfigureAwait(false);
                }

                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, revisedBy),
                    DecisionKind.MilestoneRequirementsRevised,
                    $"Revisa los requisitos del hito {milestone.Value}",
                    MilestoneRequirementsRevisedPayload.Revising(milestone, requirementSet.ContentHash, view.CurrentRequirementSetHash, revisedBy, reason),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkedBy);
        return WriteAsync(
            project,
            milestone,
            async (_, replayed) =>
            {
                replayed.Link(target, DateTimeOffset.MaxValue);

                // Only what the log knows is linked: a run or a debate that was never started is not a unit of this project.
                var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
                if (!RunLog.RunIds(entries).Contains(target.Id))
                {
                    throw new ArgumentException($"{target.Kind} '{target.Id}' has no entries in the decision log of the project: there is nothing to link.", nameof(target));
                }

                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(string.Equals(linkedBy, SystemName, StringComparison.Ordinal) ? ActorKind.System : ActorKind.User, linkedBy),
                    DecisionKind.MilestoneLinked,
                    $"Vincula {target.Kind} {target.Id} al hito {milestone.Value}",
                    MilestoneLinkedPayload.Linking(milestone, target.Kind, target.Id, linkedBy),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, EvidenceId? evidenceId, string reopenedBy, string? note, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reopenedBy);
        return WriteAsync(
            project,
            milestone,
            async (_, replayed) =>
            {
                string? sha = null;
                if (evidenceId is { } id)
                {
                    var record = (await evidence.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Id == id)
                        ?? throw new ArgumentException($"Evidence '{id}' is not stored in the project: a milestone is not reopened with evidence nobody can read.", nameof(evidenceId));
                    sha = record.ContentSha256;
                }

                replayed.Reopen(new MilestoneReopening(DateTimeOffset.MaxValue, reason, sha));
                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, reopenedBy),
                    DecisionKind.MilestoneReopened,
                    $"Reabre el hito {milestone.Value}",
                    MilestoneReopenedPayload.Reopening(milestone, reason, evidenceId, sha, reopenedBy, note),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public async Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return await ViewAsync(project.ProjectId, milestone, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        var headers = await registry.ListAsync(project.ProjectId, cancellationToken).ConfigureAwait(false);
        var opened = OpenedIn(entries);
        return [.. headers.Where(h => opened.ContainsKey(h.Id.Value)).Select(h => Build(h, opened[h.Id.Value], entries))];
    }

    /// <summary>The milestone as the log and the row say it, or null when there is no row or the log never opened it.</summary>
    internal async Task<MilestoneView?> ViewAsync(string projectId, MilestoneId milestone, CancellationToken cancellationToken)
    {
        var entries = await WorkLog.LoadAsync(recorder, projectId, cancellationToken).ConfigureAwait(false);
        var header = await registry.FindAsync(projectId, milestone, cancellationToken).ConfigureAwait(false);
        return header is not null && OpenedIn(entries).TryGetValue(milestone.Value, out var opened) ? Build(header, opened, entries) : null;
    }

    private async Task<MilestoneView> WriteAsync(ProjectContext project, MilestoneId milestone, Func<MilestoneView, Milestone, Task> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return await gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var view = await ViewAsync(project.ProjectId, milestone, cancellationToken).ConfigureAwait(false)
                    ?? throw new ArgumentException($"Milestone '{milestone.Value}' was never opened in this project.", nameof(milestone));
                if (view.Problems.Count > 0)
                {
                    throw new InvalidOperationException($"Milestone '{milestone.Value}' has problems and nothing is written to it: {string.Join(" ", view.Problems)}");
                }

                // The change is applied to the milestone as the log leaves it, so what the domain refuses is never recorded.
                var entries = await WorkLog.LoadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
                var replayed = MilestoneStateReplayer.Replay(view.Header, EventsOf(entries, milestone.Value)).Milestone!;
                await change(view, replayed).ConfigureAwait(false);
                return (await ViewAsync(project.ProjectId, milestone, cancellationToken).ConfigureAwait(false))!;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static MilestoneView Build(MilestoneHeader header, MilestoneOpenedPayload opened, IReadOnlyList<DecisionEntry> entries)
    {
        if (!string.Equals(opened.HeaderHash, MilestoneHeaderHash.Of(header), StringComparison.Ordinal))
        {
            return new MilestoneView(
                header,
                MilestoneStatus.Open,
                header.RequirementSetHash,
                [header.RequirementSetHash],
                [],
                [],
                [$"{HeaderNotAnchoredProblem}: the header stored for {header.Id.Value} is not the one MilestoneOpened anchored."]);
        }

        var replay = MilestoneStateReplayer.Replay(header, EventsOf(entries, header.Id.Value));
        var milestone = replay.Milestone;
        if (milestone is null)
        {
            return new MilestoneView(header, MilestoneStatus.Open, header.RequirementSetHash, [header.RequirementSetHash], [], [], replay.Problems);
        }

        var closed = RunLog.Of(entries, header.Id.Value).Select(e => (e.Entry, e.Payload)).Where(e => e.Payload is MilestoneClosedPayload).Select(e => (MilestoneClosedPayload)e.Payload).ToList();
        var reports = milestone.Closures.Select((c, i) => new MilestoneReportReference(c.Revision, c.Audit, closed[i].Report, c.ReportSha256, c.IntegratedCommit, c.At)).ToList();
        return new MilestoneView(header, milestone.Status, milestone.CurrentRequirementSetHash, [.. milestone.RequirementSetHistory], [.. milestone.Targets], reports, replay.Problems);
    }

    /// <summary>The events of a milestone in its unit of the log, as the replayer takes them.</summary>
    private static IEnumerable<MilestoneEvent> EventsOf(IReadOnlyList<DecisionEntry> entries, string milestone)
    {
        foreach (var (entry, payload) in RunLog.Of(entries, milestone))
        {
            switch (payload)
            {
                case MilestoneRequirementsRevisedPayload revised:
                    yield return new RequirementsRevised(entry.RecordedAt, revised.RequirementSetHash);
                    break;
                case MilestoneLinkedPayload linked:
                    yield return new TargetLinked(entry.RecordedAt, new MilestoneTarget(linked.Kind, linked.Target));
                    break;
                case MilestoneClosedPayload closed:
                    yield return new ClosureRecorded(new MilestoneClosure(entry.RecordedAt, closed.Revision, closed.Audit, closed.ReportSha256, closed.IntegratedCommit));
                    break;
                case MilestoneReopenedPayload reopened:
                    yield return new ReopeningRecorded(new MilestoneReopening(entry.RecordedAt, reopened.Reason, reopened.EvidenceSha256));
                    break;
            }
        }
    }

    /// <summary>The first <c>MilestoneOpened</c> of each milestone: the one that anchors its header.</summary>
    private static Dictionary<string, MilestoneOpenedPayload> OpenedIn(IReadOnlyList<DecisionEntry> entries)
    {
        var opened = new Dictionary<string, MilestoneOpenedPayload>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Kind == DecisionKind.MilestoneOpened))
        {
            if (RunLog.Payload(entry) is MilestoneOpenedPayload payload && !string.IsNullOrEmpty(payload.RunId))
            {
                opened.TryAdd(payload.RunId, payload);
            }
        }

        return opened;
    }

    private static bool Same(MilestoneHeader header, OpenMilestoneRequest request) =>
        string.Equals(header.Name, request.Name?.Trim(), StringComparison.Ordinal)
        && string.Equals(header.Objective, request.Objective?.Trim(), StringComparison.Ordinal)
        && string.Equals(header.RequirementSetHash, request.Requirements.ContentHash, StringComparison.Ordinal)
        && Equals(header.Budget, request.Budget);

    private MilestoneId NewId(IReadOnlyList<MilestoneHeader> headers, IReadOnlyList<DecisionEntry> entries)
    {
        var day = time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var taken = headers.Select(h => h.Id.Value).Concat(RunLog.RunIds(entries)).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 64; i++)
        {
            var id = $"{MilestoneId.Prefix}{day}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            if (!taken.Contains(id))
            {
                return new MilestoneId(id);
            }
        }

        throw new InvalidOperationException("Could not allocate a free milestone id.");
    }
}
