using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Engine;

namespace AgentStudio.Orchestration.Work;

/// <summary>What the explicit links of a project are, as the verified log says, with the log they were read from.</summary>
internal sealed record TraceState(TraceLinkSet Links, IReadOnlyList<TraceLink> MilestoneLinks, IReadOnlyList<DecisionEntry> Entries);

/// <summary>
/// The explicit links of a project (docs/16 D-29, ADR-0039): entries of the log, outside every run, with no table. A link is never
/// edited: it is retracted (it stays in the log) and another is recorded. The id of a link comes from its content, so the same link twice
/// is one.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The links the system asserts are written by <c>agent-studio</c> and no one else; what a person asserts is theirs.</item>
/// <item><c>MilestoneLinked</c> is the link of a milestone with a run or a debate: one fact, one entry. It is read here as a link from the
/// milestone to the run or the debate, asserted by the system when <see cref="MilestoneService.SystemName"/> linked it, and it is not
/// retracted here (a milestone's links are not).</item>
/// <item>Reading fails closed: a log that does not verify throws <see cref="DecisionLogIntegrityException"/>, and one whose links the domain
/// does not replay (a retraction of a link never in force, an id that is not its content's) throws <see cref="InvalidOperationException"/>.</item>
/// <item>Writing waits until no run, debate or audit has the project (<see cref="WorkWriteGate"/>).</item>
/// </list>
/// </remarks>
public sealed class TraceLinkService(IDecisionRecorder recorder, WorkWriteGate gate, TimeProvider time) : ITraceLinkService
{
    public Task<TraceLink> RecordAsync(ProjectContext project, TraceRef from, TraceRef to, TraceAssertedBy assertedBy, string reason, string decidedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        var link = new TraceLink(from, to, assertedBy, reason?.Trim() ?? string.Empty, time.GetUtcNow());
        return WriteAsync(project, link, null, decidedBy, cancellationToken);
    }

    public Task<TraceLink> ConfirmAsync(ProjectContext project, TraceSuggestion suggestion, string confirmedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedBy);
        return WriteAsync(project, suggestion.Confirm(time.GetUtcNow()), suggestion.Source, confirmedBy, cancellationToken);
    }

    public Task RetractAsync(ProjectContext project, string linkId, string retractedBy, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(retractedBy);
        return gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var state = await ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);

                // The domain refuses a link that is not in force, a reason that is too long and nobody who retracts it.
                state.Links.Retract(linkId, DateTimeOffset.MaxValue, retractedBy, reason ?? string.Empty);
                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, retractedBy),
                    DecisionKind.TraceLinkRetracted,
                    "Retira un vínculo de la traza",
                    TraceLinkRetractedPayload.Retracting(linkId, retractedBy, reason!),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<TraceLink>> LinksAroundAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        var state = await ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
        return [.. state.Links.Around(node), .. state.MilestoneLinks.Where(l => l.From == node || l.To == node)];
    }

    /// <summary>The links of the project in force and those of its milestones, from the verified log. Fails closed: see the class.</summary>
    internal static async Task<TraceState> ReadAsync(IDecisionRecorder recorder, string projectId, CancellationToken cancellationToken)
    {
        var entries = await WorkLog.LoadAsync(recorder, projectId, cancellationToken).ConfigureAwait(false);
        var events = new List<TraceEvent>();
        var milestoneLinks = new List<TraceLink>();
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case DecisionKind.TraceLinkRecorded when RunLog.Payload(entry) is TraceLinkRecordedPayload recorded:
                    try
                    {
                        events.Add(new LinkRecorded(recorded.ToLink(entry.RecordedAt)));
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidOperationException($"Entry {entry.Sequence} of the decision log records a link that is not valid: {ex.Message}", ex);
                    }

                    break;
                case DecisionKind.TraceLinkRetracted when RunLog.Payload(entry) is TraceLinkRetractedPayload retracted:
                    events.Add(new LinkRetracted(entry.RecordedAt, retracted.Link, retracted.RetractedBy, retracted.Reason));
                    break;
                case DecisionKind.MilestoneLinked when RunLog.Payload(entry) is MilestoneLinkedPayload linked && !string.IsNullOrEmpty(linked.RunId):
                    var to = linked.Kind == MilestoneTargetKind.Run ? TraceRef.Run(linked.Target) : TraceRef.Debate(linked.Target);
                    var by = string.Equals(linked.LinkedBy, MilestoneService.SystemName, StringComparison.Ordinal) ? TraceAssertedBy.System : TraceAssertedBy.User;
                    milestoneLinks.Add(new TraceLink(TraceRef.Milestone(linked.RunId), to, by, "milestone-link", entry.RecordedAt));
                    break;
            }
        }

        var replay = TraceLinkReplayer.Replay(events);
        if (!replay.IsConsistent)
        {
            throw new InvalidOperationException($"The decision log does not describe a trace the domain allows: {string.Join(" ", replay.Problems)}");
        }

        return new TraceState(replay.Links, milestoneLinks, entries);
    }

    private Task<TraceLink> WriteAsync(ProjectContext project, TraceLink link, TraceSuggestionSource? suggestion, string decidedBy, CancellationToken cancellationToken)
    {
        var problems = link.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The link is not valid: {string.Join(", ", problems)}.", nameof(link));
        }

        var system = string.Equals(decidedBy, MilestoneService.SystemName, StringComparison.Ordinal);
        if ((link.AssertedBy == TraceAssertedBy.System) != system)
        {
            throw new ArgumentException($"A link asserted by the system is decided by '{MilestoneService.SystemName}', and a link asserted by a person by that person.", nameof(decidedBy));
        }

        return gate.RunAsync(
            project.ProjectId,
            async () =>
            {
                var state = await ReadAsync(recorder, project.ProjectId, cancellationToken).ConfigureAwait(false);
                if (state.Links.Find(link.Id) is { } existing)
                {
                    return existing;
                }

                await recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(system ? ActorKind.System : ActorKind.User, decidedBy),
                    DecisionKind.TraceLinkRecorded,
                    $"Vínculo de la traza: {link.From} → {link.To}",
                    TraceLinkRecordedPayload.Recording(link, suggestion),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return link;
            },
            cancellationToken);
    }
}
