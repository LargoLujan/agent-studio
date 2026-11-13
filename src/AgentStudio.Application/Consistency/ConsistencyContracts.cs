using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Application.Consistency;

// Phase 6 contracts (docs/20 D-27 to D-30, section 4.2, ADR-0045). The four checks and the service are WP-D (src/AgentStudio.Memory/Consistency).
// Phase 16 adds the rest of the doc.* codes of docs/19 D-27 as more IConsistencyCheck.

/// <summary>
/// The codes of the discrepancies Phase 6 detects without an AI (docs/20 D-27). The three <c>doc.*</c> are those of docs/19 D-27 with the same
/// meaning, so Phase 16 extends the list and renames nothing; the fourth is the one the report of a milestone already uses.
/// </summary>
public static class ConsistencyCodes
{
    /// <summary>A relative link of a Markdown file points to a path that does not exist in the commit.</summary>
    public const string LinkBroken = "doc.link-broken";

    /// <summary>An ADR names, in backticks, a path that does not exist in the commit.</summary>
    public const string AdrCitesMissing = "doc.adr-cites-missing";

    /// <summary>A generated diagram is out of date (docs/20 D-25).</summary>
    public const string DiagramStale = "doc.diagram-stale";

    /// <summary>A requirement of an open milestone that no task covers.</summary>
    public const string RequirementUncovered = "requirement.uncovered";

    /// <summary>
    /// A check that could not run (an exception, a log that does not verify): added by WP-D so a failure is never shown as «no aplica» or as «nothing found». Its only fact is
    /// the code of the check that failed; it never quotes the exception. It is not in <see cref="All"/>, the codes the checks of Phase 6 answer for.
    /// </summary>
    public const string CheckFailed = "consistency.check-failed";

    /// <summary>The codes of Phase 6, in the order the screen groups them.</summary>
    public static IReadOnlyList<string> All { get; } = [LinkBroken, AdrCitesMissing, DiagramStale, RequirementUncovered];
}

/// <summary>
/// One discrepancy (docs/20 D-27): its code, where it is (a file of the commit and, when it has one, the line; or none, for a requirement) and
/// its facts (the link, the milestone and the requirement…). Everything here came from a repository or from the log and is data: the path
/// is relative to the copy and the facts are cut at 200 characters. It never carries text for the person: the desktop paints it from the code.
/// </summary>
public sealed record ConsistencyFinding
{
    public ConsistencyFinding(string code, string? path, int? line, IReadOnlyList<DoctorFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        Code = DoctorCodes.Require(code, nameof(code));
        if (path is not null && !RepositoryPaths.IsSafeRelative(path))
        {
            throw new ArgumentException("The path of a finding is relative to the commit and never leads out of it.", nameof(path));
        }

        if (line is < 1 || (line is not null && path is null))
        {
            throw new ArgumentException("A line goes with a path and starts at 1.", nameof(line));
        }

        if (facts.Count > DoctorCheck.MaxFacts || facts.Any(f => f is null))
        {
            throw new ArgumentException($"A finding holds up to {DoctorCheck.MaxFacts} facts.", nameof(facts));
        }

        Path = path;
        Line = line;
        Facts = [.. facts];
    }

    public string Code { get; }

    public string? Path { get; }

    public int? Line { get; }

    public IReadOnlyList<DoctorFact> Facts { get; }

    public bool Equals(ConsistencyFinding? other) =>
        other is not null && Code == other.Code && Path == other.Path && Line == other.Line && Facts.SequenceEqual(other.Facts);

    public override int GetHashCode() => HashCode.Combine(Code, Path, Line, Facts.Count);
}

/// <summary>
/// The discrepancies of a commit (docs/20 D-27): the findings, the checks that did not apply (a project with no open milestone has no requirement
/// to cover) and whether the cap of 2 000 findings cut the list.
/// </summary>
public sealed record ConsistencyReport
{
    /// <summary>The most findings a report holds (docs/20 D-28).</summary>
    public const int MaxFindings = 2_000;

    public ConsistencyReport(string commit, IReadOnlyList<ConsistencyFinding> findings, IReadOnlyList<string> notApplicable, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(notApplicable);
        if (!TraceRef.IsFullCommitId(commit))
        {
            throw new ArgumentException("A report describes a commit by its full id.", nameof(commit));
        }

        if (findings.Count > MaxFindings || findings.Any(f => f is null))
        {
            throw new ArgumentException($"A report holds up to {MaxFindings} findings.", nameof(findings));
        }

        if (notApplicable.Any(c => !DoctorCodes.IsCode(c)) || notApplicable.Distinct(StringComparer.Ordinal).Count() != notApplicable.Count)
        {
            throw new ArgumentException("The checks that did not apply are named by their codes, each once.", nameof(notApplicable));
        }

        Commit = commit;
        Findings = [.. findings];
        NotApplicable = [.. notApplicable];
        Truncated = truncated;
    }

    public string Commit { get; }

    public IReadOnlyList<ConsistencyFinding> Findings { get; }

    public IReadOnlyList<string> NotApplicable { get; }

    public bool Truncated { get; }

    /// <summary>How many findings carry <paramref name="code"/>.</summary>
    public int Count(string code) => Findings.Count(f => f.Code == code);

    public bool Equals(ConsistencyReport? other) =>
        other is not null && Commit == other.Commit && Truncated == other.Truncated
        && Findings.SequenceEqual(other.Findings) && NotApplicable.SequenceEqual(other.NotApplicable);

    public override int GetHashCode() => HashCode.Combine(Commit, Findings.Count, Truncated);
}

/// <summary>
/// What a check reads: the project (for the log and the board), the copy of the commit (for the files) and, when the analysis made one, the model of that
/// commit. The model is added by WP-D: the check of the diagrams cannot ask for it (the analysis that is running has not stored it yet, and asking its
/// service would loop), so the service that runs the checks hands it over.
/// </summary>
public sealed record ConsistencyScope(ProjectContext Project, IRepositorySnapshot Snapshot, CodeGraph? Graph = null);

/// <summary>One check (docs/20 D-27).</summary>
public interface IConsistencyCheck
{
    /// <summary>The code of its findings.</summary>
    string Code { get; }

    /// <summary>
    /// The findings of the check: an empty list when the commit is consistent, and null when the check does not apply (a project with no open
    /// milestone has no requirement to cover): the service then lists its code in <see cref="ConsistencyReport.NotApplicable"/>.
    /// </summary>
    Task<IReadOnlyList<ConsistencyFinding>?> RunAsync(ConsistencyScope scope, CancellationToken ct);
}

/// <summary>Runs every check over the copy of a commit (docs/20 D-19: the same copy the graph reads) and joins the findings.</summary>
public interface IConsistencyService
{
    Task<ConsistencyReport> RunAsync(ProjectContext project, IRepositorySnapshot snapshot, CancellationToken ct);

    /// <summary>
    /// The same, with the model of the commit the analysis just made (WP-D). A service that does not use it keeps the answer of the call above; the one of Phase 6 overrides
    /// this one, and <c>CommitAnalysisService</c> calls it.
    /// </summary>
    Task<ConsistencyReport> RunAsync(ProjectContext project, IRepositorySnapshot snapshot, CodeGraph? graph, CancellationToken ct) => RunAsync(project, snapshot, ct);
}
