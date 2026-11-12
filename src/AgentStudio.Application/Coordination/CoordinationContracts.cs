using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Application.Coordination;

// Phase 5 contracts (docs/15 section 5.3). They replace the IDebateCoordinator of Phase 0, which ran a DebateSession held in
// memory: it could not be resumed after a restart and nothing of it was anchored. A debate is now a unit of the decision log,
// like an orchestration run (ADR-0026): its header is an immutable row, its state is replayed from the log, and the text of
// its agents is evidence. The orchestrator contract (IOrchestrator, IOrchestrationRun) lives in AgentStudio.Application.Orchestration.

/// <summary>
/// What a debate is about (D-08): nothing; the commit HEAD of the user's repository, read when it opens; a finished run, by
/// its id; or a stored verdict, by its id, for a challenge.
/// </summary>
public sealed record DebateSubject(DebateSubjectKind Kind, string? Reference);

/// <summary>An agent of the project's template and the role it takes in the debate (D-09).</summary>
public sealed record DebateParticipantRequest(AgentId Agent, DebateRole Role);

/// <summary>
/// What the user asks for in «Nuevo debate» (D-41): the mode, the literal question, the frozen requirements, the subject,
/// the participants with their role, the judges of the template that may judge, the disposition chosen and the limits.
/// <paramref name="MaxAgentRuns"/> null is participants × rounds + 2 retries (D-21).
/// </summary>
public sealed record DebateRequest(
    ProjectContext Project,
    DebateMode Mode,
    string Question,
    FrozenRequirementSet Requirements,
    DebateSubject Subject,
    IReadOnlyList<DebateParticipantRequest> Participants,
    IReadOnlyList<AgentId> Judges,
    DebateJudgingKind Judging,
    DebateLimits Limits,
    int? MaxAgentRuns = null);

/// <summary>What a debate may spend at most (D-21): its launches and its tokens, «estimado por Agent Studio», with the notes that explain the figure.</summary>
public sealed record DebateEstimate(int MaxAgentRuns, MetricValue<long> Tokens, IReadOnlyList<string> Notes);

/// <summary>
/// What «Abrir debate» shows; nothing is written (D-06). <see cref="Judging"/> is the arrangement
/// <c>DebateJudgingRules</c> allows, <see cref="Problems"/> why the debate cannot open as asked (empty when it can),
/// <see cref="HeaderHash"/> the hash of what is shown (<c>DebateHeaderHash.Proposal</c>) that the approval
/// (<c>debate.open</c>) binds and <c>DebateOpened</c> repeats, and <see cref="ApprovalText"/> the text of that approval.
/// </summary>
public sealed record DebateProposal(
    DebateRequest Request,
    JudgingArrangement Judging,
    IReadOnlyList<string> Problems,
    DebateEstimate Estimate,
    string HeaderHash,
    string ApprovalText);

/// <summary>Runs debates (docs/15 D-01 to D-22): prepares, opens after the user's approval, resumes, lists and reads them.</summary>
public interface IDebateCoordinator
{
    /// <summary>Checks the request, computes the arrangement, the estimate and the hash of what will be approved. Writes nothing.</summary>
    Task<DebateProposal> PrepareAsync(DebateRequest request, CancellationToken ct);

    /// <summary>
    /// Asks the approval (<c>IApprovalGate</c>, action <c>debate.open</c>, bound to <see cref="DebateProposal.HeaderHash"/>),
    /// enters the project's activity, captures the question, stores the header, extracts the copy of the commit and records
    /// <c>DebateOpened</c> before the first turn.
    /// </summary>
    Task<IDebateRun> OpenAsync(DebateProposal proposal, CancellationToken ct);

    /// <summary>
    /// Resumes an interrupted debate after a restart (D-04): the log verifies and the header gives its anchored hash, open
    /// turns are closed as interrupted, sealed answers lost with the process are asked again, and nothing announced is
    /// repeated. It spends quota, so the user asks for it and approves it.
    /// </summary>
    Task<IDebateRun> ResumeAsync(ProjectContext project, RunId debate, CancellationToken ct);

    /// <summary>The debates of the project, by replaying each one; one without <c>DebateClosed</c> and no process behind it is interrupted.</summary>
    Task<IReadOnlyList<DebateSummary>> ListAsync(ProjectContext project, CancellationToken ct);

    /// <summary>The whole debate, its text read against the hashes the log anchored (R3-08).</summary>
    Task<DebateTranscript> ReadAsync(ProjectContext project, RunId debate, CancellationToken ct);
}

/// <summary>
/// A debate going on in this process, with the shape of <c>IOrchestrationRun</c>. <see cref="Events"/> has one consumer and
/// is buffered. <see cref="Completion"/> ends when the debate is closed or aborted, or when this process stops following it
/// (its phase is then the one it had, and the list shows it interrupted). Each action is refused, with the reason, when the
/// state of the debate does not allow it.
/// </summary>
public interface IDebateRun : IAsyncDisposable
{
    RunId Id { get; }

    IAsyncEnumerable<DebateProgress> Events { get; }

    Task<DebateResult> Completion { get; }

    DebateSnapshot Snapshot();

    /// <summary>«Pedir juicio ahora» (D-20), or «Pedir juicio con lo que hay» when a turn failed twice (D-22).</summary>
    Task RequestJudgmentAsync();

    /// <summary>«Reintentar turno» (D-22): a new attempt of the turn of that participant.</summary>
    Task RetryTurnAsync(AgentId participant);

    /// <summary>«Añadir evidencia» (D-27): a note of the user, captured as <c>HumanNote</c>/<c>ProvidedByHuman</c>.</summary>
    Task AddHumanEvidenceAsync(string title, string text);

    /// <summary>«Ejecutar pruebas» (D-27): the trusted verification profile on the commit of the subject, isolated at Low integrity.</summary>
    Task RunTestsAsync();

    /// <summary>
    /// «Decidir yo» (D-26), when the judgment needs a person: the decision (null leaves it unresolved), the proposal chosen
    /// and the comment, recorded as <c>ApprovalGranted</c> (action <c>debate.human-resolution</c>) bound to the verdicts, and
    /// then <c>DebateClosed</c> with the rule <c>user</c>.
    /// </summary>
    Task DecideAsync(VerdictDecision? decision, string? chosenProposal, string comment);

    /// <summary>«Abortar»: the turns in course are cancelled and the debate is closed as aborted.</summary>
    Task AbortAsync(string reason);
}

/// <summary>A verdict of the judgment of a debate: its id in the verdict store, the verdict as the judge gave it, what the validator found in it and whether it is the correction.</summary>
public sealed record JudgedVerdict(string Id, Verdict Verdict, IReadOnlyList<VerdictViolation> Violations, bool Corrected);

/// <summary>What the judgment of a debate gave: the last verdict of each judge (after its correction, if it had one) and their combination.</summary>
public sealed record DebateJudgmentResult(IReadOnlyList<JudgedVerdict> Verdicts, PanelOutcome Outcome);

/// <summary>The judging of the module behind a port (D-23 to D-26), so the coordinator is tested with a double.</summary>
public interface IDebateJudgment
{
    /// <summary>Builds the blind dossier from the stores, runs the judge or the panel, validates, allows one correction each and stores every verdict.</summary>
    Task<DebateJudgmentResult> JudgeAsync(ProjectContext project, RunId debate, IProgress<AgentEvent>? progress, CancellationToken ct);
}

// ---- what the screens of D-41 to D-43 show; every record comes from replaying the log ----

/// <summary>A participant as the user sees it (the judge sees only its label and role, D-15).</summary>
public sealed record DebateParticipantView(string Label, DebateRole Role, AgentId Agent, ProviderId Provider, VendorId Vendor, string? Model);

/// <summary>
/// A published contribution: its label (<c>C1</c>…) and index, round, author (<c>P1</c>…), kind and the contribution it
/// replies to; its evidence and SHA-256; <see cref="Text"/>, the JSON of the contribution read against that hash (null when
/// it cannot be read or does not match, with a problem); the evidence it cites, the quotes that could not be resolved, the
/// secrets redacted, whether it was marked finished and when its turn answered.
/// </summary>
public sealed record DebateContributionView(
    string Label,
    int Index,
    int Round,
    string Author,
    ContributionKind Kind,
    string? RepliesTo,
    EvidenceId Evidence,
    string Sha256,
    string? Text,
    IReadOnlyList<EvidenceId> Cites,
    IReadOnlyList<string> UnresolvedCitations,
    int Redactions,
    bool Finished,
    DateTimeOffset AnsweredAt);

/// <summary>What a debate waits for (D-22 and D-26).</summary>
public enum DebateAwaitReason
{
    /// <summary>A turn failed twice: «Reintentar turno», «Pedir juicio con lo que hay» or «Abortar».</summary>
    FailedTurn,

    /// <summary>The usage limit of a provider: it waits alone up to 15 minutes when the CLI said when it resets, and stops otherwise.</summary>
    ProviderLimit,

    /// <summary>The judgment needs a person: «Decidir yo».</summary>
    HumanResolution,
}

/// <summary>
/// Why a debate is waiting, for whom (the label of a participant) and until when. <paramref name="Message"/> is the sentence in Spanish
/// of the debate; <paramref name="Texts"/> says the same as coded texts, one per reason (one per failed turn), which the desktop paints
/// in the language of the person (docs/17 D-47). Null when a producer gives only the message.
/// </summary>
public sealed record DebateAwaiting(DebateAwaitReason Reason, string Message, string? Participant = null, DateTimeOffset? Until = null, IReadOnlyList<UserText>? Texts = null);

/// <summary>A debate in the history (D-41): from replaying its log.</summary>
/// <param name="Question">The literal question, read against its anchored hash; null when it cannot be read.</param>
/// <param name="Interrupted">Unfinished with no process behind it: the list offers «Reanudar».</param>
/// <param name="Challenges">The verdict this debate revises, when it is a challenge.</param>
/// <param name="Problems">What the replay or the integrity checks found; a debate with problems is not resumed.</param>
public sealed record DebateSummary(
    RunId Debate,
    DateTimeOffset OpenedAt,
    DebateMode Mode,
    DebateSubjectKind Subject,
    string? Question,
    DebatePhase Phase,
    DebateOutcome Outcome,
    VerdictDecision? Decision,
    string? ChosenProposal,
    bool Interrupted,
    bool Challenged,
    string? Challenges,
    IReadOnlyList<string> Problems);

/// <summary>
/// The room (D-41): the header (mode, phase, round, time and tokens with their origin, «~» when an estimate is among them,
/// with how many attempts were reported for «parcial (n de m informados)»), the participants, the published contributions,
/// how many turns of the round in course are running (sealed: no text until they all end), why it waits, and whether
/// «Ejecutar pruebas» is possible (a commit and a profile).
/// </summary>
public sealed record DebateSnapshot(
    RunId Debate,
    DebateMode Mode,
    DebateSubjectKind Subject,
    DebatePhase Phase,
    DebateOutcome Outcome,
    int Round,
    DateTimeOffset OpenedAt,
    TimeSpan Elapsed,
    DebateLimits Limits,
    MetricAggregate<long> Tokens,
    int ReportedTurns,
    int AgentRuns,
    int MaxAgentRuns,
    IReadOnlyList<DebateParticipantView> Participants,
    IReadOnlyList<DebateContributionView> Contributions,
    int TurnsInFlight,
    DebateLimitReached LimitReached,
    DebateJudgmentReason? JudgmentReason,
    DebateAwaiting? Awaiting,
    bool CanRunTests,
    IReadOnlyList<string> Problems);

/// <summary>
/// A whole debate read back (D-41 and D-42): its summary, the disposition of the judgment with its waivers, the participants
/// with the agents behind their labels (which the judge did not see), the contributions with their text read against their
/// hashes, the verdicts and the outcome, and what was found while reading.
/// </summary>
public sealed record DebateTranscript(
    DebateSummary Summary,
    string RequirementSetHash,
    DebateJudgingKind Judging,
    IReadOnlyList<VendorId> WaivedParticipantVendors,
    IReadOnlyList<DebateParticipantView> Participants,
    IReadOnlyList<DebateContributionView> Contributions,
    IReadOnlyList<JudgedVerdict> Verdicts,
    PanelOutcome? Outcome,
    DebateLimitReached LimitReached,
    DebateJudgmentReason? JudgmentReason,
    IReadOnlyList<string> Problems);

/// <summary>How a debate ended, or stood when this process stopped following it.</summary>
public sealed record DebateResult(
    RunId Debate,
    DebatePhase Phase,
    DebateOutcome Outcome,
    PanelOutcome? Panel,
    IReadOnlyList<JudgedVerdict> Verdicts,
    DebateLimitReached LimitReached,
    DebateJudgmentReason? JudgmentReason,
    string Summary);

/// <summary>What happens in a debate, for the room. A sealed round shows only that its turns run, never their text (D-17).</summary>
public abstract record DebateProgress(DateTimeOffset At);

/// <summary>The turn of a participant (its label) was launched, for a round and an attempt.</summary>
public sealed record DebateTurnStarted(DateTimeOffset At, string Participant, int Round, int Attempt) : DebateProgress(At);

/// <summary>The turn of a participant ended, how; its answer stays sealed until the round closes.</summary>
public sealed record DebateTurnFinished(DateTimeOffset At, string Participant, int Round, int Attempt, AttemptOutcome Outcome) : DebateProgress(At);

/// <summary>«Ronda N: M turnos en curso, sellados hasta que terminen todos».</summary>
public sealed record DebateRoundSealed(DateTimeOffset At, int Round, int TurnsInFlight) : DebateProgress(At);

/// <summary>A contribution was published when its round closed.</summary>
public sealed record DebateContributionPublished(DateTimeOffset At, DebateContributionView Contribution) : DebateProgress(At);

/// <summary>A round closed: the phase that follows, why it went to judgment when it did, the limit reached and the tokens so far.</summary>
public sealed record DebateRoundEnded(DateTimeOffset At, int Round, DebatePhase Next, DebateJudgmentReason? Judgment, DebateLimitReached Limit, MetricAggregate<long> Tokens) : DebateProgress(At);

/// <summary>A judge was launched (attempt 2 is its correction).</summary>
public sealed record DebateJudgeLaunched(DateTimeOffset At, AgentId Judge, int Attempt) : DebateProgress(At);

/// <summary>A verdict came and was stored.</summary>
public sealed record DebateVerdictReceived(DateTimeOffset At, JudgedVerdict Verdict) : DebateProgress(At);

/// <summary>The debate waits for the user.</summary>
public sealed record DebateAwaitingUser(DateTimeOffset At, DebateAwaiting Awaiting) : DebateProgress(At);

/// <summary>The debate ended.</summary>
public sealed record DebateFinished(DateTimeOffset At, DebateResult Result) : DebateProgress(At);
