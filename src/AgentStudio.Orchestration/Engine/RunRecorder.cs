using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Writes the decision-log entries and the evidence of one run. Evidence is always captured through the store (which
/// computes the hash) and announced with <c>EvidenceRegistered</c>; payloads carry evidence ids and hashes, never the
/// content (prompts and agent output live only in evidence).
/// </summary>
internal sealed class RunRecorder(IDecisionRecorder decisions, IEvidenceStore evidence, string projectId, RunId run)
{
    public const string ProducedBySystem = "agent-studio";

    public static readonly Actor System = new(ActorKind.System, ProducedBySystem);

    public string ProjectId { get; } = projectId;

    public RunId Run { get; } = run;

    public string RunValue => Run.Value;

    public Task<DecisionEntry> RecordAsync(Actor actor, DecisionKind kind, string summary, DecisionPayload payload, CancellationToken cancellationToken) =>
        decisions.RecordAsync(ProjectId, actor, kind, summary, payload, null, cancellationToken);

    public Task<DecisionEntry> RecordAsync(DecisionKind kind, string summary, DecisionPayload payload, CancellationToken cancellationToken) =>
        RecordAsync(System, kind, summary, payload, cancellationToken);

    /// <summary>Captures text as evidence of this run and records <c>EvidenceRegistered</c>.</summary>
    public Task<EvidenceRecord> CaptureAsync(
        EvidenceKind kind,
        EvidenceOrigin origin,
        string title,
        string producedBy,
        string content,
        string mediaType,
        TaskId? task,
        int? attempt,
        CancellationToken cancellationToken,
        TestRunSummary? tests = null,
        IReadOnlyList<RequirementId>? covers = null) =>
        CaptureBytesAsync(kind, origin, title, producedBy, Encoding.UTF8.GetBytes(content), mediaType, task, attempt, cancellationToken, tests, covers);

    /// <summary>Captures bytes as they are (a diff need not be valid UTF-8) and records <c>EvidenceRegistered</c>.</summary>
    public async Task<EvidenceRecord> CaptureBytesAsync(
        EvidenceKind kind,
        EvidenceOrigin origin,
        string title,
        string producedBy,
        byte[] content,
        string mediaType,
        TaskId? task,
        int? attempt,
        CancellationToken cancellationToken,
        TestRunSummary? tests = null,
        IReadOnlyList<RequirementId>? covers = null)
    {
        var draft = new EvidenceDraft(
            kind,
            origin,
            title,
            producedBy,
            tests,
            covers,
            RunId: RunValue,
            TaskId: task?.Value,
            Attempt: attempt,
            MediaType: mediaType);
        EvidenceRecord record;
        using (var stream = new MemoryStream(content, writable: false))
        {
            record = await evidence.CaptureAsync(ProjectId, draft, stream, cancellationToken).ConfigureAwait(false);
        }

        await RegisterAsync(record, task, attempt, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Records <c>EvidenceRegistered</c> for evidence another component captured (verification, integration). It is
    /// version 2: besides the content, the log anchors what decides an approval (the commit a test run verified, its
    /// counters, when it was captured, by whom and its title), so a row of the store changed later never passes for it.
    /// </summary>
    public Task<DecisionEntry> RegisterAsync(EvidenceRecord record, TaskId? task, int? attempt, CancellationToken cancellationToken) =>
        RecordAsync(
            DecisionKind.EvidenceRegistered,
            $"Evidencia {record.Id}: {record.Title}",
            EvidenceRegisteredPayload.Anchoring(record, RunValue, task, attempt),
            cancellationToken);

    /// <summary>
    /// The content of a piece of evidence, only as the decision log anchored it (R3-08): the bytes read must hash to
    /// <paramref name="anchoredSha256"/>, the SHA-256 of its <c>EvidenceRegistered</c> entry, and the bytes returned are
    /// the very bytes that were hashed. The store checks the content against its own row, which whoever rewrote the
    /// content may have rewritten too; there is no way to read evidence here without the anchored hash.
    /// </summary>
    /// <exception cref="InvalidDataException">The content does not hash to the anchored SHA-256 (or the store found it modified or missing).</exception>
    /// <exception cref="KeyNotFoundException">The store has no row for <paramref name="id"/>.</exception>
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
                $"Evidence {id} hashes to {actual}, not to {anchoredSha256}, the SHA-256 the decision log anchored when it was registered: its content was rewritten afterwards, and it is not used.");
        }

        return content;
    }

    /// <summary>The text of a piece of evidence, only as the decision log anchored it (<see cref="ReadBytesAsync"/>).</summary>
    public async Task<string> ReadTextAsync(EvidenceId id, string anchoredSha256, CancellationToken cancellationToken)
    {
        var content = await ReadBytesAsync(id, anchoredSha256, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(new MemoryStream(content, writable: false), Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<EvidenceRecord>> ListEvidenceAsync(CancellationToken cancellationToken) =>
        evidence.ListByRunAsync(ProjectId, RunValue, cancellationToken);
}
