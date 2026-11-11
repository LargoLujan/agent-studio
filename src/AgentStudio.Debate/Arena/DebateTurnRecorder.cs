using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// Writes the entries and the evidence of one debate (docs/15 D-02, D-03 and D-17). Every entry carries the debate as its run
/// id, so <c>RunLog.EntriesOf</c> selects it, and no payload carries text of an agent: the prompt of a turn, a contribution
/// and a citation are evidence, and a payload carries their id and SHA-256.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The prompt of a turn is captured in the store before its launch and anchored by <c>AgentRunStarted</c> (its id and
/// SHA-256), like the prompt of a judge: it is not announced with <c>EvidenceRegistered</c>, so it is a record of the launch
/// and not evidence of the debate (<c>RunLog.RegisteredEvidence</c> leaves it out quietly).</item>
/// <item><c>AgentRunFinished</c> of a turn never names an answer: the answers of a round are sealed until all its turns end
/// (D-17), and only then is each contribution captured, announced and published.</item>
/// <item>The evidence of the debate (the question, the contributions, the citations, the tests and the notes of a person) is
/// captured through the store, which computes its hash, and announced with <c>EvidenceRegistered</c> version 2.</item>
/// </list>
/// </remarks>
public sealed class DebateTurnRecorder(IDecisionRecorder decisions, IEvidenceStore evidence, string projectId, RunId debate)
{
    /// <summary>The actor and producer the system records itself as.</summary>
    public const string SystemId = "agent-studio";

    public static readonly Actor System = new(ActorKind.System, SystemId);

    public string ProjectId { get; } = projectId;

    public RunId Debate { get; } = debate;

    public Task<DecisionEntry> RecordAsync(DecisionKind kind, string summary, DecisionPayload payload, CancellationToken cancellationToken, Actor? actor = null) =>
        decisions.RecordAsync(ProjectId, actor ?? System, kind, summary, payload, null, cancellationToken);

    /// <summary>The prompt of one attempt of a turn, in the store only: <c>AgentRunStarted</c> anchors it.</summary>
    public async Task<EvidenceRecord> CapturePromptAsync(TaskId turn, int attempt, string title, string prompt, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(prompt), writable: false);
        return await evidence.CaptureAsync(
            ProjectId,
            new EvidenceDraft(EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, title, SystemId, RunId: Debate.Value, TaskId: turn.Value, Attempt: attempt, MediaType: "text/markdown"),
            stream,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures evidence of the debate and announces it with <c>EvidenceRegistered</c> version 2.</summary>
    public async Task<EvidenceRecord> CaptureRegisteredAsync(
        EvidenceKind kind,
        EvidenceOrigin origin,
        string title,
        string producedBy,
        byte[] content,
        string mediaType,
        TaskId? task,
        int? attempt,
        string? subjectCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EvidenceRecord record;
        using (var stream = new MemoryStream(content, writable: false))
        {
            record = await evidence.CaptureAsync(
                ProjectId,
                new EvidenceDraft(kind, origin, title, producedBy, RunId: Debate.Value, TaskId: task?.Value, Attempt: attempt, MediaType: mediaType, SubjectCommit: subjectCommit),
                stream,
                cancellationToken).ConfigureAwait(false);
        }

        // Every row of the debate is announced (the dossier of the judgment reports one the log never registered, and then
        // nothing of the debate can be approved): once captured, stopping does not leave it unannounced.
        await RegisterAsync(record, task, attempt, CancellationToken.None).ConfigureAwait(false);
        return record;
    }

    /// <summary>Announces evidence another component captured (the verifier) with <c>EvidenceRegistered</c> version 2.</summary>
    public Task<DecisionEntry> RegisterAsync(EvidenceRecord record, TaskId? task, int? attempt, CancellationToken cancellationToken) =>
        RecordAsync(DecisionKind.EvidenceRegistered, $"Evidencia {record.Id}: {record.Title}", EvidenceRegisteredPayload.Anchoring(record, Debate.Value, task, attempt), cancellationToken);

    /// <summary>
    /// <c>AgentRunStarted</c> version 2 of one attempt of a turn, before its launch (D-44: purpose and key of its own); version 3
    /// when the agent sets a reasoning effort, which it then records (docs/18 P47). It never records the parts of the prompt.
    /// </summary>
    public Task<DecisionEntry> RecordTurnStartedAsync(
        TaskId turn,
        int attempt,
        AgentDefinition agent,
        IAgentProvider provider,
        string workingDirectory,
        EvidenceRecord prompt,
        string label,
        CancellationToken cancellationToken) =>
        RecordAsync(
            DecisionKind.AgentRunStarted,
            $"Turno de {label} ({turn}), intento {attempt}",
            new AgentRunStartedPayload(turn, attempt, agent.Id, provider.Provider, provider.Vendor, agent.Model.Model, workingDirectory, prompt.Id, prompt.ContentSha256, [], AgentRunPurpose.DebateParticipant, Effort: agent.Effort)
            {
                RunId = Debate.Value,
                V = agent.Effort is null ? AgentRunStartedPayload.PurposeVersion : AgentRunStartedPayload.ContextPartsVersion,
            },
            cancellationToken);

    /// <summary>
    /// <c>AgentRunFinished</c> version 2 of one attempt of a turn, as soon as it ends, with its usage and what its events said,
    /// and never its answer (D-17); with the receipt of its journal entry when the journal gave one (docs/17 D-02).
    /// </summary>
    public Task<DecisionEntry> RecordTurnFinishedAsync(
        TaskId turn,
        int attempt,
        AttemptOutcome status,
        int? exitCode,
        string? sessionId,
        string? error,
        AgentRunObservations seen,
        string label,
        JournalReceipt? journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seen);
        var usage = seen.Usage;
        return RecordAsync(
            DecisionKind.AgentRunFinished,
            $"Termina el turno de {label} ({turn}), intento {attempt}: {status}",
            new AgentRunFinishedPayload(
                turn,
                attempt,
                status,
                exitCode,
                sessionId ?? seen.SessionId,
                error,
                usage is null ? null : UsageSnapshot.WithBreakdown(usage),
                null,
                seen.LaunchedAt,
                seen.ReportedModel(usage),
                seen.ResumedSession,
                seen.PeakSnapshot(usage),
                seen.Compactions,
                usage?.Turns)
            {
                RunId = Debate.Value,
                V = AgentRunFinishedPayload.UsageVersion,
                Journal = journal,
            },
            cancellationToken);
    }

    /// <summary>
    /// The content of a piece of evidence, only as the decision log anchored it (R3-08): the bytes must hash to
    /// <paramref name="anchoredSha256"/>, and the bytes returned are the very bytes that were hashed.
    /// </summary>
    /// <exception cref="InvalidDataException">The content does not hash to the anchored SHA-256.</exception>
    public async Task<byte[]> ReadBytesAsync(EvidenceId id, string anchoredSha256, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchoredSha256);
        byte[] content;
        var stream = await evidence.OpenContentAsync(ProjectId, id, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            content = buffer.ToArray();
        }

        var actual = ContentHash.Sha256Hex(content);
        if (!string.Equals(actual, anchoredSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Evidence {id} hashes to {actual}, not to {anchoredSha256}, the SHA-256 the decision log anchored for it: its content was rewritten afterwards, and it is not used.");
        }

        return content;
    }

    /// <summary>The text of a piece of evidence, only as the log anchored it (<see cref="ReadBytesAsync"/>).</summary>
    public async Task<string> ReadTextAsync(EvidenceId id, string anchoredSha256, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await ReadBytesAsync(id, anchoredSha256, cancellationToken).ConfigureAwait(false));
}
