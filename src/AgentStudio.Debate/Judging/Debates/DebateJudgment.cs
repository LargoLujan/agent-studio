using System.Diagnostics;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>
/// The judgment of a debate (docs/15 D-23 to D-26, ADR-0029): the judge of Phase 3 (<see cref="AgentJudge"/>, read-only,
/// with the identity and the time the system gives, one correction at most) with the brief of a debate, on the blind dossier
/// of <see cref="DebateDossierBuilder"/>, as many judges as the arrangement of the header seats, and their verdicts combined by
/// <see cref="JudgingPanel.Combine"/>.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The log is read and verified first. If it is not intact nothing can be anchored to it: no dossier is written, no
/// judge is launched and nothing is recorded; the result is an invalid set of verdicts, which needs a person.</item>
/// <item>The dossier comes from the stores, and the judges from the header of the debate. That header is a row of the store,
/// so a judge is launched only when it hashes to what <c>DebateOpened</c> anchored (N-05): one that does not records an
/// alert (<see cref="HeaderMismatchAlert"/>) and needs a person. So does an arrangement the domain does not allow
/// (<see cref="JudgingNotAllowedAlert"/>), and a judge whose real vendor (that of its provider, not of the template) is one the
/// dossier excludes (<c>JudgeNotIndependent</c>): its verdict could never be valid, so none of the judges is launched.</item>
/// <item>Every judge reads the same dossier, at the same head of the log, so the verdicts of a panel speak of the same
/// material (D-14); the second reads the proposals reversed. The judges of a panel run at the same time, each in a new
/// session, each validated on its own with its own correction.</item>
/// <item>Each launch is recorded around the run by <see cref="JudgeRunRecorder"/> (<see cref="JudgeRunScope.ForDebate"/>: the
/// debate, the task <c>juez-&lt;agent&gt;</c> and the purpose <c>DebateJudge</c>), and each verdict is kept as the judge gave
/// it by <see cref="VerdictKeeper"/>, in the debate and <see cref="AuditScope.Debate"/>. A revision debate keeps its verdicts
/// in the unit and scope of the verdict it challenges, and the first verdict of each judge supersedes it (D-40), so the chain
/// of verdicts of a run, a debate or an audit stays one.</item>
/// <item>A judge that gives no verdict (its run fails, hits its own limits or the usage limit of its provider, or answers
/// something that is not a verdict) needs a person (D-26): the result has no verdict of it, so <c>Combine</c> sees one
/// verdict too few. An alert in the debate says why (<see cref="NoVerdictAlert"/>), its run (when there was one) is in the log
/// with how it ended, and its events (a <see cref="RateLimitSignaled"/> among them) go to the progress of the caller. Only a
/// cancellation is thrown.</item>
/// <item>A correction that cannot be obtained leaves the first verdict, with its issues: it is not the correction, and an alert
/// says why (<see cref="NoCorrectionAlert"/>).</item>
/// </list>
/// The judge never gets project memory (ADR-0022): its launches carry no pack, and the dossier leaves out every prompt that
/// carried one.
/// </remarks>
/// <param name="providers">The composed providers of the project, which say whether the judge's CLI reports the context of each step; without them it is not exposed.</param>
public sealed class DebateJudgment(
    IDecisionRecorder decisions,
    DebateDossierBuilder dossiers,
    IAgentJudgeFactory judges,
    IVerdictStore verdicts,
    IEvidenceStore evidence,
    TimeProvider timeProvider,
    IProjectAgentProviders? providers = null) : IDebateJudgment
{
    /// <summary>The code of the alert recorded when no judge is launched because the stored header of the debate is not the one <c>DebateOpened</c> anchored.</summary>
    public const string HeaderMismatchAlert = "DebateHeaderMismatch";

    /// <summary>The code of the alert recorded when the arrangement of the header is not one the domain allows for the debate.</summary>
    public const string JudgingNotAllowedAlert = "DebateJudgingNotAllowed";

    /// <summary>The code of the alert recorded when a judge would run on a vendor the dossier excludes, as the review of a run records it.</summary>
    public const string JudgeNotIndependentAlert = "JudgeNotIndependent";

    /// <summary>The code of the alert recorded when a judge gives no verdict: why the judgment needs a person (D-26).</summary>
    public const string NoVerdictAlert = "DebateJudgeNoVerdict";

    /// <summary>The code of the alert recorded when a judge gives no correction of its invalid verdict: the first one stays.</summary>
    public const string NoCorrectionAlert = "DebateJudgeNoCorrection";

    private static readonly Actor SystemActor = new(ActorKind.System, JudgeRunRecorder.SystemId);

    /// <exception cref="InvalidOperationException">The debate never opened, or its header or requirements are not stored.</exception>
    /// <exception cref="OperationCanceledException">The judgment was cancelled.</exception>
    public async Task<DebateJudgmentResult> JudgeAsync(ProjectContext project, RunId debate, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var projectId = project.ProjectId;

        // 1: the log the verdicts will be anchored to must be intact before anything is written.
        var log = await decisions.LoadAsync(projectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            return Unjudged(null);
        }

        // 2: the dossier from the stores, and the judges from a header the log vouches for.
        var package = await dossiers.BuildPackageAsync(project, debate, ct).ConfigureAwait(false);
        var arrangement = package.Judging;
        if (!package.DecisionLogIntact)
        {
            return Unjudged(arrangement);
        }

        var header = package.Header;
        if (!package.HeaderVerified)
        {
            await AlertAsync(projectId, debate, HeaderMismatchAlert, "Cabecera del debate no verificada",
                $"No se lanza ningún juez del debate {debate}: la cabecera guardada no da el hash que ancló DebateOpened, así que los jueces (instrucciones, herramientas y modelo), los participantes o la disposición no son los que se aprobaron.", ct).ConfigureAwait(false);
            return Unjudged(arrangement);
        }

        var notAllowed = DebateJudgingRules.Violations(arrangement, header.Mode, header.Subject.Kind, package.Participants);
        if (notAllowed.Count > 0)
        {
            await AlertAsync(projectId, debate, JudgingNotAllowedAlert, "Disposición del juicio no permitida",
                $"No se lanza ningún juez del debate {debate}: la disposición {header.Judging} incumple {string.Join(", ", notAllowed)}.", ct).ConfigureAwait(false);
            return Unjudged(arrangement);
        }

        var dossier = package.Dossier.Dossier;
        var workingDirectory = package.Snapshot ?? package.Files.Folder;
        var seats = new List<Seat>();
        for (var i = 0; i < header.Judges.Count; i++)
        {
            var agent = header.Judges[i];
            var recorder = new JudgeRunRecorder(decisions, evidence, JudgeRunScope.ForDebate(projectId, debate.Value, agent.Id), agent, timeProvider, ReportsContext(project, agent), progress);
            var brief = new DebateJudgeBrief(package, i);
            var judge = judges.Create(
                project,
                new JudgeLaunch(agent, workingDirectory, package.Files, recorder, recorder, brief.Purpose, JudgeRunScope.JudgeTask(agent.Id).Value, brief));
            seats.Add(new Seat(agent, judge));
        }

        // Rule P is never waived and rule Q only by the header: a judge on an excluded vendor is never launched (ADR-0027).
        var excluded = string.Join(", ", dossier.ExcludedVendors.Select(v => v.Value).Order(StringComparer.Ordinal));
        if (seats.FirstOrDefault(s => dossier.ExcludedVendors.Contains(s.Judge.Identity.Model.Vendor)) is { } dependent)
        {
            await AlertAsync(projectId, debate, JudgeNotIndependentAlert, "Juez no independiente",
                $"No se lanza ningún juez del debate {debate}: '{dependent.Agent.Id}' corre en '{dependent.Judge.Identity.Model.Vendor}', uno de los vendors excluidos ({excluded}).", ct).ConfigureAwait(false);
            return Unjudged(arrangement);
        }

        var challenged = package.Dossier.Challenge?.Stored;
        var unit = challenged?.RunId ?? debate.Value;
        var scope = challenged?.Scope ?? AuditScope.Debate;
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            var judged = await Task.WhenAll(seats.Select(seat =>
            {
                var keeper = new VerdictKeeper(verdicts, decisions, projectId, unit, JudgeRunScope.JudgeTask(seat.Agent.Id), scope);
                var note = VendorNote(seat.Judge.Identity.Model.Vendor, header, excluded, challenged?.Id);
                return JudgeSeatAsync(projectId, debate, seat, dossier, keeper, gate, challenged?.Id, note, ct);
            })).ConfigureAwait(false);

            IReadOnlyList<JudgedVerdict> given = [.. judged.OfType<JudgedVerdict>()];
            var outcome = JudgingPanel.Combine(arrangement, [.. given.Select(v => (v.Verdict, v.Violations.Count == 0))]);
            return new DebateJudgmentResult(given, outcome);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private sealed record Seat(AgentDefinition Agent, IJudge Judge);

    /// <summary>
    /// One judge: its verdict as given, validated, kept and announced; one correction at most. Null when it gave no verdict,
    /// and then an alert says why (<see cref="NoVerdictAlert"/>): a launch that could not even be recorded leaves nothing
    /// else behind. The verdicts are kept one at a time (<paramref name="gate"/>), so the two judges of a panel never write
    /// at once.
    /// </summary>
    private async Task<JudgedVerdict?> JudgeSeatAsync(string projectId, RunId debate, Seat seat, JudgeDossier dossier, VerdictKeeper keeper, SemaphoreSlim gate, string? supersedes, string note, CancellationToken ct)
    {
        var task = JudgeRunScope.JudgeTask(seat.Agent.Id);
        Verdict first;
        try
        {
            first = await seat.Judge.EvaluateAsync(dossier, ct).ConfigureAwait(false);
        }
        catch (JudgeRunException ex) when (ct.IsCancellationRequested)
        {
            // The run ended as cancelled because the judgment was: that is a cancellation, not an answer.
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // D-26: a verdict that does not come needs a person, and nothing goes on in silence (D-22).
            await TryAlertAsync(projectId, debate, NoVerdictAlert, task, "Juez sin veredicto",
                $"El juez '{seat.Agent.Id}' no dio un veredicto del debate {debate}, así que el juicio necesita a una persona: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        var violations = VerdictValidator.Validate(first, dossier);
        var firstId = await KeepAsync(keeper, gate, seat.Agent.Id, dossier, first, violations, supersedes, note).ConfigureAwait(false);
        if (violations.Count == 0)
        {
            return new JudgedVerdict(firstId, first, violations, Corrected: false);
        }

        Verdict corrected;
        try
        {
            corrected = await seat.Judge.ReviseAsync(dossier, first, violations, ct).ConfigureAwait(false);
        }
        catch (JudgeRunException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No correction to show: the first verdict stays, with its issues, and the alert says why.
            await TryAlertAsync(projectId, debate, NoCorrectionAlert, task, "Juez sin corrección",
                $"El juez '{seat.Agent.Id}' no dio la corrección de su veredicto {firstId}, que queda con sus incidencias: {ex.Message}").ConfigureAwait(false);
            return new JudgedVerdict(firstId, first, violations, Corrected: false);
        }

        var remaining = VerdictValidator.Validate(corrected, dossier);
        var correctedId = await KeepAsync(keeper, gate, seat.Agent.Id, dossier, corrected, remaining, firstId, note).ConfigureAwait(false);
        return new JudgedVerdict(correctedId, corrected, remaining, Corrected: true);
    }

    private static async Task<string> KeepAsync(VerdictKeeper keeper, SemaphoreSlim gate, AgentId judge, JudgeDossier dossier, Verdict verdict, IReadOnlyList<VerdictViolation> violations, string? supersedes, string note)
    {
        // Once a verdict exists it is kept even if the judgment is being cancelled: the quota was spent.
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await keeper.KeepAsync(judge, dossier, verdict, violations, supersedes, note).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The disposition, the waivers and the vendors excluded, for <c>VerdictIssued</c> (the card shows the waiver, ADR-0027).</summary>
    private static string VendorNote(VendorId vendor, DebateRecord header, string excluded, string? challenged)
    {
        var waived = header.WaivedParticipantVendors.Count == 0
            ? "sin dispensa"
            : "dispensa de la regla de los participantes para " + string.Join(", ", header.WaivedParticipantVendors.Select(v => v.Value).Order(StringComparer.Ordinal));
        var revision = challenged is null ? string.Empty : $"; revisa el veredicto {challenged}";
        return $"Juez en '{vendor}'; disposición {header.Judging}, {waived}; vendors excluidos: {(excluded.Length == 0 ? "ninguno" : excluded)} (ADR-0027){revision}.";
    }

    private bool ReportsContext(ProjectContext project, AgentDefinition agent) =>
        providers?.For(project.RootPath).FirstOrDefault(p => p.Provider == agent.Model.Provider)?.Capabilities.ReportsContextOccupancy ?? false;

    /// <summary>
    /// Records an alert of one judge, best effort: the judge already failed, and a log that refuses the alert too (it broke
    /// meanwhile) is something the caller sees when it records how the debate ends.
    /// </summary>
    private async Task TryAlertAsync(string projectId, RunId debate, string code, TaskId task, string summary, string message)
    {
        try
        {
            await decisions.RecordAsync(projectId, SystemActor, DecisionKind.AuditAlertRaised, summary, new AuditAlertPayload(code, [task], message) { RunId = debate.Value }, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"Debate judgment: could not record the alert {code} of {task}: {ex.Message}");
        }
    }

    private async Task AlertAsync(string projectId, RunId debate, string code, string summary, string message, CancellationToken ct)
    {
        await decisions.RecordAsync(projectId, SystemActor, DecisionKind.AuditAlertRaised, summary, new AuditAlertPayload(code, [], message) { RunId = debate.Value }, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>No judge gave a verdict: the domain combines nothing into an invalid set, which needs a person (D-26).</summary>
    private static DebateJudgmentResult Unjudged(JudgingArrangement? arrangement) =>
        new([], arrangement is null ? new PanelOutcome(DebateOutcome.EscalatedToUser, null, null, JudgingPanel.Invalid) : JudgingPanel.Combine(arrangement, []));
}
