using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Memory.Diagrams;

namespace AgentStudio.Memory.Consistency;

/// <summary>
/// <c>doc.diagram-stale</c> (docs/20 D-27): a diagram of the cache whose model changed. It reads the model of the commit that is being analyzed (the scope hands it over) and
/// the index of the cache: no file that was exported and no cached text. Without a model it does not apply.
/// </summary>
public sealed class DiagramStaleCheck : IConsistencyCheck
{
    private readonly DiagramStaleness _staleness;

    internal DiagramStaleCheck(DiagramStaleness staleness)
    {
        _staleness = staleness;
    }

    public string Code => ConsistencyCodes.DiagramStale;

    public async Task<IReadOnlyList<ConsistencyFinding>?> RunAsync(ConsistencyScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Graph is not { } graph)
        {
            return null;
        }

        var stale = await _staleness.FindAsync(scope.Project, graph, ct).ConfigureAwait(false);
        return [.. stale.Select(s => Finding(s.Diagram, s.Describes))];
    }

    /// <summary>The finding of a diagram that is out of date: it has no file and no line; its facts say which diagram it is and, for the solution, the commit it describes.</summary>
    public static ConsistencyFinding Finding(DiagramRef diagram, string? describes)
    {
        var facts = new List<DoctorFact> { DoctorFact.Of("diagram", DiagramWord(diagram.Kind)) };
        if (diagram.Subject.Length > 0)
        {
            facts.Add(DoctorFact.Of("subject", diagram.Subject));
        }

        if (describes is not null)
        {
            facts.Add(DoctorFact.Of("describes", describes));
        }

        return new ConsistencyFinding(ConsistencyCodes.DiagramStale, null, null, facts);
    }

    /// <summary>The fixed word of a kind of diagram, the value of the fact <c>diagram</c>.</summary>
    public static string DiagramWord(DiagramKind kind) => DiagramGrammar.Word(kind);

    /// <summary>The kind a fixed word names (the other way round of <see cref="DiagramWord"/>), or null when it is not one of them.</summary>
    public static DiagramKind? KindOf(string word) =>
        Enum.GetValues<DiagramKind>().Where(k => string.Equals(DiagramGrammar.Word(k), word, StringComparison.Ordinal)).Select(k => (DiagramKind?)k).FirstOrDefault();
}

/// <summary>
/// <c>requirement.uncovered</c> (docs/20 D-27 and D-29): for each open milestone, the board it projects (<see cref="IBoardProjector"/>); a requirement card in the
/// <c>Backlog</c> column is one that no task covers. It does not apply to a project with no open milestone. A log that does not verify fails closed in the projector:
/// the service reports that the check failed, it does not report «no requirement is uncovered».
/// </summary>
public sealed class RequirementUncoveredCheck(IMilestoneService milestones, IBoardProjector board) : IConsistencyCheck
{
    public string Code => ConsistencyCodes.RequirementUncovered;

    public Task<IReadOnlyList<ConsistencyFinding>?> RunAsync(ConsistencyScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return FindAsync(scope.Project, ct);
    }

    /// <summary>The uncovered requirements of the open milestones, or null when there is none open. It reads the log and the board, never the repository: it is what <see cref="LiveConsistency"/> asks again.</summary>
    internal async Task<IReadOnlyList<ConsistencyFinding>?> FindAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var open = (await milestones.ListAsync(project, ct).ConfigureAwait(false))
            .Where(m => m.Status == MilestoneStatus.Open)
            .ToList();
        if (open.Count == 0)
        {
            return null;
        }

        var findings = new List<ConsistencyFinding>();
        foreach (var milestone in open)
        {
            ct.ThrowIfCancellationRequested();
            var projected = await board.ProjectAsync(project, milestone.Header.Id, ct).ConfigureAwait(false);
            foreach (var card in projected.Requirements.Where(c => c.Column == BoardColumn.Backlog))
            {
                findings.Add(new ConsistencyFinding(
                    ConsistencyCodes.RequirementUncovered,
                    null,
                    null,
                    [
                        DoctorFact.Of("milestone", milestone.Header.Name),
                        DoctorFact.Of("milestone-id", milestone.Header.Id.Value),
                        DoctorFact.Of("requirement", card.Id),
                        DoctorFact.Of("title", card.Title),
                    ]));
                if (findings.Count > ConsistencyReport.MaxFindings)
                {
                    return findings;
                }
            }
        }

        return findings;
    }
}

/// <summary>
/// Runs every check over the copy of a commit and joins the findings (docs/20 D-19 and D-27). A check that throws does not take the analysis down with it and does not
/// pass for «no aplica»: it comes out as a finding of <see cref="ConsistencyCodes.CheckFailed"/> that names the check and never quotes the exception. Only a cancellation
/// goes through. More than <see cref="ConsistencyReport.MaxFindings"/> findings are cut and the report says so.
/// </summary>
public sealed class ConsistencyService : IConsistencyService
{
    private readonly IReadOnlyList<IConsistencyCheck> _checks;

    public ConsistencyService(IEnumerable<IConsistencyCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        _checks = [.. checks.OrderBy(c => Order(c.Code)).ThenBy(c => c.Code, StringComparer.Ordinal)];
    }

    public Task<ConsistencyReport> RunAsync(ProjectContext project, IRepositorySnapshot snapshot, CancellationToken ct) => RunAsync(project, snapshot, null, ct);

    public async Task<ConsistencyReport> RunAsync(ProjectContext project, IRepositorySnapshot snapshot, CodeGraph? graph, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        var scope = new ConsistencyScope(project, snapshot, graph);
        var findings = new List<ConsistencyFinding>();
        var notApplicable = new List<string>();
        foreach (var check in _checks)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ConsistencyFinding>? found;
            try
            {
                found = await check.RunAsync(scope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // A check is behind a port and reads data that is not trusted: that it failed is reported as a finding, never thrown into the analysis.
            catch (Exception)
#pragma warning restore CA1031
            {
                findings.Add(new ConsistencyFinding(ConsistencyCodes.CheckFailed, null, null, [DoctorFact.Of("check", check.Code)]));
                continue;
            }

            if (found is null)
            {
                if (!notApplicable.Contains(check.Code, StringComparer.Ordinal))
                {
                    notApplicable.Add(check.Code);
                }

                continue;
            }

            findings.AddRange(found.Where(f => string.Equals(f.Code, check.Code, StringComparison.Ordinal)));
        }

        var ordered = findings
            .OrderBy(f => Order(f.Code))
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ThenBy(f => f.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0)
            .ThenBy(f => string.Join('\u001f', f.Facts.Select(x => x.Value)), StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > ConsistencyReport.MaxFindings;
        return new ConsistencyReport(snapshot.Commit, truncated ? ordered.Take(ConsistencyReport.MaxFindings).ToList() : ordered, notApplicable, truncated);
    }

    private static int Order(string code)
    {
        for (var i = 0; i < ConsistencyCodes.All.Count; i++)
        {
            if (string.Equals(ConsistencyCodes.All[i], code, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return ConsistencyCodes.All.Count;
    }
}
