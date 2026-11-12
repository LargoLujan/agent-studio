using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Application.Work;

// Phase 9 contracts of the trace (docs/16 D-29 to D-32, F9-3 to F9-5). The links are entries of the log
// (TraceLinkRecorded and TraceLinkRetracted, outside every run); there is no table. A link the system infers is a suggestion
// until a person confirms it, and nothing inferred reaches the judge as a fact. WP1 implements them.

/// <summary>
/// The explicit links of a project. Writing waits until no run, debate or audit has the project, like the memory, and fails
/// closed on a log that does not verify. The link the milestone service writes when a run is launched from a milestone is
/// <c>MilestoneLinked</c>, not a trace link: the one fact has one entry, and the trace reads both.
/// </summary>
public interface ITraceLinkService
{
    /// <summary>Records a link; the same link in force is returned and nothing is written. <paramref name="decidedBy"/> is the person, or <c>agent-studio</c> for <see cref="TraceAssertedBy.System"/>.</summary>
    Task<TraceLink> RecordAsync(ProjectContext project, TraceRef from, TraceRef to, TraceAssertedBy assertedBy, string reason, string decidedBy, CancellationToken cancellationToken);

    /// <summary>Records the link a person confirms from a suggestion, with the source of the suggestion in the entry.</summary>
    Task<TraceLink> ConfirmAsync(ProjectContext project, TraceSuggestion suggestion, string confirmedBy, CancellationToken cancellationToken);

    /// <summary>Records <c>TraceLinkRetracted</c>. The link stays in the log.</summary>
    Task RetractAsync(ProjectContext project, string linkId, string retractedBy, string reason, CancellationToken cancellationToken);

    /// <summary>The links in force that touch the node at either end.</summary>
    Task<IReadOnlyList<TraceLink>> LinksAroundAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken);
}

/// <summary>
/// Infers links from commit messages, the trailers the system writes, the branches of the runs and the pull requests that cite a
/// milestone (D-30). It never records anything. A suggestion already confirmed, or whose link was retracted by a person, is not offered again.
/// </summary>
public interface ITraceSuggester
{
    Task<IReadOnlyList<TraceSuggestion>> SuggestAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken);
}

/// <summary>The stages of the chain the screen walks (docs/16 F9-4): requirement, milestone, task, attempt, change (the diff), verification, verdict and delivery.</summary>
public enum TraceChainStage
{
    Requirement,
    Milestone,
    Task,
    Attempt,
    Change,
    Verification,
    Verdict,
    Delivery,
}

/// <summary>
/// Where a verdict of the chain stands (docs/15 D-38 to D-40): the one in force for the work it judged, one a later verdict replaced (a retry after a rejection, a revision, a
/// follow-up run), the latest one while a person's challenge keeps it in review, or the latest one whose challenge closed with another decision than its own (a withdrawn
/// approval, an approval a person accepted). A superseded, challenged or revised verdict is never shown as the one that stands.
/// </summary>
public enum TraceVerdictStanding
{
    InForce,
    Superseded,
    Challenged,
    Revised,
}

/// <summary>
/// One step of the chain: the node, what stage it is and whether it comes from a suggestion nobody confirmed (shown as such). <paramref name="Standing"/> is set only on
/// the verdicts (null on every other step).
/// </summary>
public sealed record TraceChainStep(TraceChainStage Stage, TraceRef Node, string? Title, bool Suggested, TraceVerdictStanding? Standing = null);

/// <summary>The chain from a node, read from the verified log in both directions.</summary>
public sealed record TraceChain(TraceRef Start, IReadOnlyList<TraceChainStep> Steps);

/// <summary>Walks the chain of a node (F9-4). It reads only; a log that does not verify fails closed.</summary>
public interface ITraceChainReader
{
    Task<TraceChain> ChainAsync(ProjectContext project, TraceRef start, CancellationToken cancellationToken);
}

/// <summary>The six roles that are never confused (docs/16 D-32). Names are appended, never reordered.</summary>
public enum ParticipationRole
{
    /// <summary>The agent of the attempt.</summary>
    ProducingAgent,

    /// <summary>The author and committer of git: «Agent Studio» in the internal commits, the person in the delivery, the platform in a squash.</summary>
    GitAuthorAndCommitter,

    /// <summary>The account that published (Phase 10).</summary>
    PublishingAccount,

    /// <summary>The judge by its verdict, or the person by the review of the pull request (Phase 10).</summary>
    Reviewer,

    /// <summary>Whoever signs the approval (<c>DecidedBy</c>).</summary>
    Approver,

    /// <summary>The local delivery, or <c>merged_by</c> of the remote (Phase 10).</summary>
    Integrator,
}

/// <summary>Stable codes of where a role comes from, for the desktop to say.</summary>
public static class ParticipationSources
{
    public const string Attempt = "participation.attempt";
    public const string Commit = "participation.commit";
    public const string Verdict = "participation.verdict";
    public const string Approval = "participation.approval";
    public const string LocalDelivery = "participation.local-delivery";
    public const string SkippedReview = "participation.review-skipped";
}

/// <summary>
/// One who took a role, and the source that says so. <paramref name="Who"/> null is «no consta»: a role without a source is never
/// deduced (D-32).
/// </summary>
public sealed record ParticipationEntry(ParticipationRole Role, string? Who, string? Detail, string? Source)
{
    public bool IsRecorded => Who is not null;

    /// <summary>The role with no source.</summary>
    public static ParticipationEntry NotRecorded(ParticipationRole role) => new(role, null, null, null);
}

/// <summary>Who took part in a task, a run or a milestone. Every role is there at least once, recorded or «no consta».</summary>
public sealed record ParticipationView(TraceRef Subject, IReadOnlyList<ParticipationEntry> Entries);

/// <summary>Projects the participation of a subject (a task, a run or a milestone) from the verified log. It reads only.</summary>
public interface IParticipationProjector
{
    Task<ParticipationView> ProjectAsync(ProjectContext project, TraceRef subject, CancellationToken cancellationToken);
}
