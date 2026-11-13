using System.Globalization;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Memory.Consistency;

/// <summary>
/// The line of the discrepancies in the section «Proyecto» of Auto Doctor (docs/20 D-30): how many there are in the saved version the project is at. It
/// <b>reads what is saved</b> for that version (<see cref="ICommitAnalysisService.FindAsync"/>) and never starts the copy nor the analysis, so opening the panel costs the same
/// as before (the two codes that come from the cache and the log are worked out again, see <see cref="LiveConsistency"/>): with nothing saved it says «sin calcular» and offers to go to Auto Graph, where «Calcular» is. It is not saved as evidence (that is Phase 16).
/// </summary>
public sealed class DiscrepanciesCheckSource(ICommitAnalysisService? analysis, IRepositoryHistoryReader? git, LiveConsistency? live = null) : IDoctorCheckSource
{
    /// <summary>The code of the line.</summary>
    public const string Code = "doctor.project.discrepancies";

    public DoctorSection Section => DoctorSection.Project;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        if (project is null || analysis is null || git is null)
        {
            return [];
        }

        var summary = await git.SummaryAsync(project, ct).ConfigureAwait(false);
        if (!summary.Accepted || summary.HeadCommit is not { } head || !TraceRef.IsFullCommitId(head))
        {
            return [new DoctorCheck(Code, DoctorSection.Project, DoctorStatus.Unknown, [], DoctorFixCatalog.OpenGraph)];
        }

        var found = await analysis.FindAsync(project, head, ct).ConfigureAwait(false);
        if (found is null)
        {
            return [new DoctorCheck(Code, DoctorSection.Project, DoctorStatus.Unknown, [DoctorFact.Of("describes", head[..7])], DoctorFixCatalog.OpenGraph)];
        }

        // The diagrams out of date and the uncovered requirements are worked out now (they come from the cache and the log, not from the files of the commit), so this line
        // and the screen of the discrepancies give the same count; the rest is what was saved.
        var report = live is null ? found.Consistency : await live.RefreshAsync(project, found, ct).ConfigureAwait(false);
        var count = report.Findings.Count;
        var facts = new List<DoctorFact> { DoctorFact.Of("count", count.ToString(CultureInfo.InvariantCulture)), DoctorFact.Of("describes", head[..7]) };
        foreach (var code in ConsistencyCodes.All.Append(ConsistencyCodes.CheckFailed))
        {
            if (report.Count(code) is > 0 and var n)
            {
                facts.Add(DoctorFact.Of(code, n.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return [new DoctorCheck(Code, DoctorSection.Project, count == 0 ? DoctorStatus.Ok : DoctorStatus.Warning, facts, count == 0 ? null : DoctorFixCatalog.OpenGraph)];
    }
}
