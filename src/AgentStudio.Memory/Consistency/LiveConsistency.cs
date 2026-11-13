using AgentStudio.Application.Consistency;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Memory.Diagrams;

namespace AgentStudio.Memory.Consistency;

/// <summary>
/// The discrepancies of a saved analysis, with the two that do not depend on the files of the commit worked out again now (docs/20 D-27 and D-30): a diagram that is out of date
/// (regenerating one, or a new entry in the log of a run, changes it without a new analysis) and a requirement no task covers (it comes from the log). Neither reads the repository
/// nor starts the analysis; the other codes are taken from what was saved. The screen of «Discrepancias» and the line of Auto Doctor both go through here, so they give the same count.
/// </summary>
public sealed class LiveConsistency
{
    private readonly DiagramStaleness _staleness;
    private readonly RequirementUncoveredCheck? _requirements;

    internal LiveConsistency(DiagramStaleness staleness, RequirementUncoveredCheck? requirements)
    {
        ArgumentNullException.ThrowIfNull(staleness);
        _staleness = staleness;
        _requirements = requirements;
    }

    /// <summary>
    /// The report with the two codes of the log and the cache recalculated. A check that cannot be done is a finding of <see cref="ConsistencyCodes.CheckFailed"/>, never a silent
    /// «nothing». Without the milestones (the check is not composed) the saved findings of <see cref="ConsistencyCodes.RequirementUncovered"/> stay.
    /// </summary>
    public async Task<ConsistencyReport> RefreshAsync(ProjectContext project, CommitAnalysis saved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(saved);

        // The work below (the index, the rendering of the diagrams, the log) never starts on the thread that called, which may be the one of the interface.
        await Task.Run(static () => { }).ConfigureAwait(false);
        var report = saved.Consistency;
        var notApplicable = report.NotApplicable.ToList();
        var findings = report.Findings
            .Where(f => !Replaced(f))
            .ToList();

        // The diagrams: the index of the cache and the model of the saved analysis.
        notApplicable.Remove(ConsistencyCodes.DiagramStale);
        try
        {
            findings.AddRange((await _staleness.FindAsync(project, saved.Graph, ct).ConfigureAwait(false)).Select(s => DiagramStaleCheck.Finding(s.Diagram, s.Describes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            findings.Add(Failed(ConsistencyCodes.DiagramStale));
        }

        if (_requirements is not null)
        {
            try
            {
                var uncovered = await _requirements.FindAsync(project, ct).ConfigureAwait(false);
                if (uncovered is null)
                {
                    if (!notApplicable.Contains(ConsistencyCodes.RequirementUncovered))
                    {
                        notApplicable.Add(ConsistencyCodes.RequirementUncovered);
                    }
                }
                else
                {
                    notApplicable.Remove(ConsistencyCodes.RequirementUncovered);
                    findings.AddRange(uncovered);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // The board is behind a port; a log that does not verify fails closed there: it is reported as a check that failed, never as «nothing».
            catch (Exception)
#pragma warning restore CA1031
            {
                findings.Add(Failed(ConsistencyCodes.RequirementUncovered));
            }
        }

        var ordered = findings
            .OrderBy(f => OrderOf(f.Code))
            .ThenBy(f => f.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0)
            .ToList();
        var truncated = report.Truncated || ordered.Count > ConsistencyReport.MaxFindings;
        return new ConsistencyReport(report.Commit, ordered.Take(ConsistencyReport.MaxFindings).ToList(), notApplicable, truncated);
    }

    /// <summary>A saved finding of one of the codes this works out again, or a saved failure of the check of one of them.</summary>
    private bool Replaced(ConsistencyFinding finding)
    {
        var code = finding.Code == ConsistencyCodes.CheckFailed ? finding.Facts.FirstOrDefault(f => f.Name == "check")?.Value : finding.Code;
        return code == ConsistencyCodes.DiagramStale || (_requirements is not null && code == ConsistencyCodes.RequirementUncovered);
    }

    private static int OrderOf(string code)
    {
        var index = ((IList<string>)[.. ConsistencyCodes.All, ConsistencyCodes.CheckFailed]).IndexOf(code);
        return index < 0 ? int.MaxValue : index;
    }

    private static ConsistencyFinding Failed(string code) => new(ConsistencyCodes.CheckFailed, null, null, [DoctorFact.Of("check", code)]);
}
