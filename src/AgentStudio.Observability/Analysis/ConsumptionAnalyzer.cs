using System.Text;
using System.Text.Json;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Observability.Analysis;

/// <summary>What the deterministic analysis of consumption is composed with: its thresholds and the catalog of models (docs/17 D-23), when there is one.</summary>
public sealed record ConsumptionAnalysisOptions
{
    public ConsumptionThresholds Thresholds { get; init; } = new();

    /// <summary>The catalog of models; null: <c>model.oversized</c> says nothing.</summary>
    public ModelCatalog? Catalog { get; init; }
}

/// <summary>
/// The deterministic analysis of consumption (docs/17 D-34, F13-15, ADR-0052): free and instant, at the end of every run and
/// whenever the person asks, over the verified log of the project. It launches nothing, spends no quota and writes nothing.
/// <list type="bullet">
/// <item>The log is loaded and verified; one that does not verify fails closed (<see cref="DecisionLogIntegrityException"/>),
/// as the memory does.</item>
/// <item>For <c>model.oversized</c> only, it reads the size of the change of the attempts that could be one
/// (<see cref="ConsumptionRules.OversizedCandidates"/>): the files of the attempt record and the lines of the diff, each only
/// as the log anchored it (its SHA-256). Evidence that does not match, or cannot be read, gives no size and no finding.</item>
/// <item>The rules are <see cref="ConsumptionRules"/>; the judge never gets any of this (D-36).</item>
/// </list>
/// </summary>
public sealed class ConsumptionAnalyzer(IDecisionRecorder decisions, IEvidenceStore evidence, TimeProvider time, ConsumptionAnalysisOptions? options = null) : IConsumptionAnalyzer
{
    /// <summary>The largest evidence read for the size of a change.</summary>
    public const int MaxEvidenceBytes = 8 * 1024 * 1024;

    /// <summary>The kind of the attempt record the engine captures (<c>AttemptRecord.RecordKind</c>; Observability cannot see it).</summary>
    public const string AttemptRecordKind = "agent-studio/attempt-record";

    /// <summary>How the title of an attempt record starts (<c>AttemptRecord.TitlePrefix</c>).</summary>
    public const string AttemptRecordTitlePrefix = "Registro del intento";

    private readonly ConsumptionAnalysisOptions _options = options ?? new ConsumptionAnalysisOptions();

    public async Task<ConsumptionReport> AnalyzeAsync(ProjectContext project, RunId run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var log = await decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        var candidates = ConsumptionRules.OversizedCandidates(log.Entries, run, _options.Catalog);
        var changes = candidates.Count == 0
            ? new Dictionary<AttemptRef, AttemptChange>()
            : await ChangesAsync(project.ProjectId, log.Entries, run, candidates, ct).ConfigureAwait(false);

        // The rules are CPU work over the whole log: never on the caller's thread, which may be the UI's.
        var input = new ConsumptionInput(project.ProjectId, run, log.Entries, time.GetUtcNow())
        {
            Thresholds = _options.Thresholds,
            Catalog = _options.Catalog,
            Changes = changes,
        };
        return await Task.Run(() => ConsumptionRules.Analyze(input), ct).ConfigureAwait(false);
    }

    /// <summary>The size of the change of each candidate, from its anchored attempt record (files) and diff (lines).</summary>
    private async Task<Dictionary<AttemptRef, AttemptChange>> ChangesAsync(string projectId, IReadOnlyList<DecisionEntry> entries, RunId run, IReadOnlyList<AttemptRef> candidates, CancellationToken ct)
    {
        var wanted = candidates.ToHashSet();
        var records = new Dictionary<AttemptRef, EvidenceRegisteredPayload>();
        var diffs = new Dictionary<AttemptRef, EvidenceRegisteredPayload>();
        foreach (var entry in entries.Where(e => e.Kind == DecisionKind.EvidenceRegistered).OrderBy(e => e.Sequence))
        {
            EvidenceRegisteredPayload registered;
            try
            {
                registered = DecisionPayloadSerializer.Deserialize<EvidenceRegisteredPayload>(entry.PayloadJson);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
            {
                continue;
            }

            if (registered.RunId != run.Value || registered.Task is not { } task || registered.Attempt is not { } attempt || !wanted.Contains(new AttemptRef(task, attempt)))
            {
                continue;
            }

            var key = new AttemptRef(task, attempt);
            if (registered.Kind == EvidenceKind.CodeDiff)
            {
                diffs[key] = registered;
            }
            else if (registered.Title?.StartsWith(AttemptRecordTitlePrefix, StringComparison.Ordinal) == true)
            {
                records[key] = registered;
            }
        }

        var changes = new Dictionary<AttemptRef, AttemptChange>();
        foreach (var key in candidates)
        {
            if (!records.TryGetValue(key, out var record) || await ReadAnchoredAsync(projectId, record, ct).ConfigureAwait(false) is not { } recordText || ChangedFiles(recordText) is not { } files)
            {
                continue;
            }

            int? lines = diffs.TryGetValue(key, out var diff) && await ReadAnchoredAsync(projectId, diff, ct).ConfigureAwait(false) is { } diffText ? ChangedLines(diffText) : files == 0 ? 0 : null;
            changes[key] = new AttemptChange(files, lines);
        }

        return changes;
    }

    /// <summary>The text of a piece of evidence only as the log anchored it; null when it does not match, is too large or cannot be read.</summary>
    private async Task<string?> ReadAnchoredAsync(string projectId, EvidenceRegisteredPayload registered, CancellationToken ct)
    {
        try
        {
            var stream = await evidence.OpenContentAsync(projectId, registered.Evidence, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxEvidenceBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                var content = buffer.ToArray();
                return string.Equals(ContentHash.Sha256Hex(content), registered.Sha256, StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetString(content) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The files an attempt record says the attempt changed; null when it is not an attempt record.</summary>
    internal static int? ChangedFiles(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == AttemptRecordKind
                && root.TryGetProperty("changedPaths", out var paths) && paths.ValueKind == JsonValueKind.Array
                ? paths.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The lines a unified diff adds or removes (not its headers).</summary>
    internal static int ChangedLines(string diff)
    {
        var lines = 0;
        foreach (var line in diff.Split('\n'))
        {
            if ((line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)) || (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)))
            {
                lines++;
            }
        }

        return lines;
    }
}
