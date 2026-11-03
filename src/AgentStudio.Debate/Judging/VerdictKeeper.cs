using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// Keeps the verdicts of one judge (docs/15 D-23): each one is stored append-only exactly as the judge gave it, with its
/// issues and the verdict it supersedes, and announced with <c>VerdictIssued</c>, which carries the hashes of the material
/// the judge examined (the requirements and the head of the log of its dossier). It moved out of
/// <see cref="IndependentReview"/> without changing what the review keeps, so the judgment of a debate and the audit keep
/// their verdicts the same way, each in its unit of the log (<paramref name="run"/>), task and scope.
/// </summary>
/// <remarks>
/// Once a verdict exists it is kept even if the caller is being cancelled: the quota was spent and the verdict is evidence
/// of what the judge said.
/// </remarks>
/// <param name="run">The run, debate or audit the verdict belongs to; a revision keeps that of the verdict it challenges (D-40).</param>
/// <param name="task">The review task of a run, or the task of the judge of a debate or an audit (<c>juez-&lt;agent&gt;</c>).</param>
/// <param name="scope">What was judged: <see cref="AuditScope.CurrentPlan"/> for a run, <see cref="AuditScope.Debate"/>, <see cref="AuditScope.WholeProject"/>.</param>
public sealed class VerdictKeeper(IVerdictStore verdicts, IDecisionRecorder decisions, string projectId, string run, TaskId task, AuditScope scope)
{
    /// <summary>Stores <paramref name="verdict"/> of <paramref name="judge"/> and announces it; returns its id in the store.</summary>
    public async Task<string> KeepAsync(AgentId judge, JudgeDossier dossier, Verdict verdict, IReadOnlyList<VerdictViolation> violations, string? supersedes, string vendorNote)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(violations);

        var valid = violations.Count == 0;
        var stored = new StoredVerdict(Guid.NewGuid().ToString("N"), verdict, run, task.Value, scope, valid, violations, supersedes);
        var id = await verdicts.AppendAsync(projectId, stored, CancellationToken.None).ConfigureAwait(false);

        var summary = new StringBuilder($"Veredicto {verdict.Decision} {(valid ? "válido" : $"inválido ({violations.Count} incidencias)")}");
        if (supersedes is not null)
        {
            summary.Append(", corrige ").Append(supersedes);
        }

        await decisions.RecordAsync(
            projectId,
            new Actor(ActorKind.Judge, judge.Value),
            DecisionKind.VerdictIssued,
            summary.ToString(),
            new VerdictIssuedPayload(
                id,
                task,
                verdict.Decision,
                valid,
                [.. violations.Select(v => v.Code)],
                dossier.Requirements.ContentHash,
                dossier.DecisionLogHeadHash,
                supersedes,
                vendorNote) { RunId = run },
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return id;
    }
}
