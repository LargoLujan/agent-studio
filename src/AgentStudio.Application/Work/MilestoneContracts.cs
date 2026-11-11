using System.Globalization;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Milestones;
using HashFunctions = AgentStudio.Domain.Common.ContentHash;

namespace AgentStudio.Application.Work;

// Phase 9 contracts of the milestones (docs/16 D-17, D-49 and D-50, section 4.6). A milestone is a name, an objective and a
// sequence of frozen sets of requirements; only its header is stored (migration 0006), and its state is rebuilt from the
// decision log (MilestoneStateReplayer). Every write goes through the recorder of the log and fails closed on a log that does
// not verify (DecisionLogIntegrityException); WP1 implements the registry and the service, WP3 the close.

/// <summary>
/// The canonical SHA-256 of the immutable header of a milestone (<see cref="MilestoneHeader"/>), with the pattern of
/// <c>DebateHeaderHash</c>: <c>MilestoneOpened</c> anchors it, and a service that reads the header from <c>milestones</c>
/// recomputes it and refuses any other, so a header rewritten behind the store's back (another objective, a looser budget) is
/// never used. The id and the project are the keys the header is found by, not part of it.
/// </summary>
/// <remarks>
/// Fields are length-prefixed (<see cref="HashFunctions.Canonical"/>): the version of the encoding, the time it was created as an
/// instant, the name, the objective, the hash of the first set of requirements and the budget (<c>-</c> when there is none; else
/// <c>budget</c>, each limit as <c>-</c> or <c>=</c> and its number, and the period), so a missing limit is never a zero.
/// </remarks>
public static class MilestoneHeaderHash
{
    private const string Version = "milestone-header/1";

    public static string Of(MilestoneHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var fields = new List<string?>
        {
            Version,
            header.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            header.Name,
            header.Objective,
            header.RequirementSetHash,
        };
        if (header.Budget is { } budget)
        {
            fields.Add("budget");
            fields.Add(Optional(budget.MaxTokens));
            fields.Add(Optional(budget.MaxAgentRuns));
            fields.Add(budget.Period.ToString());
        }
        else
        {
            fields.Add("-");
        }

        return HashFunctions.Sha256Hex(HashFunctions.Canonical([.. fields]));
    }

    private static string Optional(long? value) => value is null ? "-" : "=" + value.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The headers of the milestones of a project, in <c>milestones</c> (append-only). There is deliberately no update or delete
/// operation: a milestone changes only through the log.
/// </summary>
public interface IMilestoneRegistry
{
    /// <summary>Inserts the header. The same row twice writes nothing; another row with the same id is refused.</summary>
    Task CreateAsync(string projectId, MilestoneHeader header, CancellationToken cancellationToken);

    Task<MilestoneHeader?> FindAsync(string projectId, MilestoneId id, CancellationToken cancellationToken);

    /// <summary>The headers of the project, in the order they were created.</summary>
    Task<IReadOnlyList<MilestoneHeader>> ListAsync(string projectId, CancellationToken cancellationToken);
}

/// <summary>
/// A report of a milestone as one of its closes left it (docs/16 D-49 and D-50): the revision (from 1), the audit, the evidence of
/// the deterministic report and its SHA-256, and the commit audited. Reopening keeps every one of them.
/// </summary>
public sealed record MilestoneReportReference(int Revision, string Audit, EvidenceId Report, string ReportSha256, string? IntegratedCommit, DateTimeOffset At);

/// <summary>
/// A milestone as the screens and the services read it: its header (read from the row, checked against the hash
/// <c>MilestoneOpened</c> anchored), and the state its entries replay to. <paramref name="Problems"/> are what the replay found
/// (a log that does not describe a milestone the domain allows): a milestone with problems is shown as such and nothing more is
/// written to it.
/// </summary>
public sealed record MilestoneView(
    MilestoneHeader Header,
    MilestoneStatus Status,
    string CurrentRequirementSetHash,
    IReadOnlyList<string> RequirementSetHistory,
    IReadOnlyList<MilestoneTarget> Targets,
    IReadOnlyList<MilestoneReportReference> Reports,
    IReadOnlyList<string> Problems);

/// <summary>What the person gives to open a milestone: a name, an objective, the requirements (frozen when it opens) and, optionally, a budget.</summary>
public sealed record OpenMilestoneRequest(ProjectContext Project, string Name, string Objective, FrozenRequirementSet Requirements, WorkBudget? Budget, string OpenedBy);

/// <summary>
/// The milestones of a project (docs/16 D-17): open one, revise its requirements, link runs and debates to it, reopen it, and read
/// it. Everything it writes is an entry of the log; the header is the only row. Writing waits, like the memory, until no run, debate
/// or audit has the project (<c>IProjectActivity</c>), so the head of the log does not move between showing a verdict or an
/// approval and using it, except for the entries of the milestone and its runs themselves.
/// </summary>
public interface IMilestoneService
{
    /// <summary>
    /// Freezes the requirements, stores the header and records <c>MilestoneOpened</c>, in that order: an announcement without its row is
    /// completed by opening again with the same header.
    /// </summary>
    Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken);

    /// <summary>Freezes the new set and records <c>MilestoneRequirementsRevised</c>. The earlier set stays in its history.</summary>
    Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken);

    /// <summary>Records <c>MilestoneLinked</c>. A run is linked with its follow-ups.</summary>
    Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken);

    /// <summary>Records <c>MilestoneReopened</c> with the reason and, but for a person's own decision, its evidence.</summary>
    Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken);

    Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken);

    /// <summary>The milestones of the project, in the order they were opened.</summary>
    Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken);
}

/// <summary>
/// What closes a milestone (docs/16 D-49): which one, who, and what the audit asks: the judge of the template (null: the first judge or
/// auditor that may judge, in the order of the template) and the vendors of the authors the person declares (null: none).
/// </summary>
public sealed record CloseMilestoneRequest(ProjectContext Project, MilestoneId Milestone, string ClosedBy, AgentId? Judge = null, IReadOnlyList<VendorId>? DeclaredAuthors = null);

/// <summary>
/// A close that did not happen, and nothing of it was written as a close: the milestone cannot be closed as it is (closed, with problems
/// or with no run linked: <see cref="Rule"/> is the rule of the domain), nobody may judge, the person did not approve the audit, or the
/// audit did not run to its end. <see cref="Reasons"/> are in Spanish, for the person; <see cref="Audit"/> is the report of an audit that was
/// launched and whose result closes nothing, or null when none was.
/// </summary>
public sealed class MilestoneCloseRefusedException(string rule, IReadOnlyList<string> reasons, AuditReport? audit = null)
    : InvalidOperationException($"{rule}: {string.Join(" ", reasons)}")
{
    public string Rule { get; } = rule;

    public IReadOnlyList<string> Reasons { get; } = reasons;

    public AuditReport? Audit { get; } = audit;
}

/// <summary>
/// Closes a milestone by auditing it (docs/16 D-49, F9-10): «Auditar proyecto completo» with the frozen set of the milestone, narrowed to
/// its runs and debates, on the commit at HEAD, a judge that did not produce the work, the isolated verification and one correction at
/// most; then the deterministic report around the verdict, kept as evidence, and <c>MilestoneClosed</c> with the evidence of that report.
/// The report is no text of an agent. Refusals before any quota is spent (no run linked, closed, problems, nobody may judge) and an
/// audit that was not approved or did not run throw <see cref="MilestoneCloseRefusedException"/>; an audit that ran and escalated
/// or found deviations closes the milestone all the same, and the report says so (D-50: reopening exists for that).
/// </summary>
/// <remarks>
/// A milestone with debates linked and no run is refused (<c>milestone.nothing-to-close</c>, ADR-0038): there is no work to audit.
/// The audit releases the project when it ends and the close is written after that, so a run that starts in between is possible;
/// the log stays correct because the close is an entry of the milestone, not of the project's head.
/// </remarks>
public interface IMilestoneCloser
{
    Task<MilestoneView> CloseAsync(CloseMilestoneRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken);
}

/// <summary>What a listener of the close of a milestone receives: the milestone as it is closed, its report as the last of its <c>Reports</c>, and the audit.</summary>
public sealed record MilestoneClosedNotice(ProjectContext Project, MilestoneView Milestone, MilestoneReportReference Report, AuditReport Audit);

/// <summary>
/// The point where what depends on a closed milestone hooks in (the documentation for the client, WP-14D): called once the close is
/// recorded. A listener that throws never undoes the close (the failure goes to the debug output only).
/// </summary>
public interface IMilestoneCloseListener
{
    Task OnClosedAsync(MilestoneClosedNotice notice, CancellationToken cancellationToken);
}
