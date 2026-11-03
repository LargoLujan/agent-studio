using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Observability.Usage;

/// <summary>
/// What the log of one project says about its agent attempts (docs/14 D-01 to D-04): the attempts that ended, in the
/// order they ended; what could not be counted, in Spanish; when each attempt started (the journal records a prompt at
/// that moment, D-08); and the first start recorded with version 2 (Phase 4), from which the journal is reconciled.
/// </summary>
public sealed record UsageProjection(IReadOnlyList<UsageAttempt> Attempts, IReadOnlyList<string> Warnings, IReadOnlyList<DateTimeOffset> Starts, DateTimeOffset? Phase4Since);

/// <summary>
/// The usage of agent attempts as a projection of verified decision logs (docs/14 D-01, ADR-0019): there is no usage
/// table, and every figure keeps who gave it.
/// <list type="bullet">
/// <item>An <c>AgentRunFinished</c> closes the last open <c>AgentRunStarted</c> of the same run, task and attempt
/// (docs/04 section 16.2). The start says who and where; the end, how it went.</item>
/// <item>The CLIs report the total of the session (D-02), and a retry, the planner's correction and the judge's resume
/// it. A session is the pair (provider, session id), and an attempt that resumed one under a new id still shares it.
/// The own usage of an attempt is its snapshot minus that of the last earlier attempt of its session that ended with a
/// result; when a figure goes down the CLI did not accumulate, and the whole snapshot counts as an estimate of Agent
/// Studio. An attempt without a result (interrupted, cancelled, timed out) has no figure.</item>
/// <item>A Codex usage of all zeros is <c>Usage::default()</c>: no usage notification came, so it is no figure
/// (docs/14 section 3.2), never a zero.</item>
/// <item>A version 1 end has no cache writes, reasoning, breakdown, launch time, context peak nor compactions: those
/// are missing (D-03), never zero.</item>
/// </list>
/// </summary>
public sealed partial class UsageProjector(IProjectLogReader logs, IProjectStorageReader storage) : IUsageReport
{
    /// <summary>Why a figure of a version 1 end is missing.</summary>
    public const string Version1Reason = "registro v1 (anterior a la Fase 4): no guarda esta cifra";

    /// <summary>Why the figures of an attempt that ended without a result are missing.</summary>
    public const string NoResultReason = "intento sin resultado: su gasto no se recupera";

    /// <summary>Why the figures of a Codex attempt whose usage is all zeros are missing.</summary>
    public const string CodexZeroReason = "codex: uso a cero (no llegó ninguna notificación de uso)";

    /// <summary>How many entries are read at a time: only one chunk of payloads is alive at once.</summary>
    private const int ChunkSize = 8192;

    /// <summary>How many projections of unchanged logs are kept (the open project and a few recent ones).</summary>
    private const int MaxKeptProjections = 4;

    private readonly Lock _keptGate = new();
    private readonly List<(KeptKey Key, UsageProjection Projection)> _kept = [];

    /// <summary>From this many entries on, the payloads of a chunk are read in parallel.</summary>
    private const int ParallelReadThreshold = 4096;

    /// <summary>
    /// Reads every project's log through <see cref="IProjectLogReader"/> (a root given twice is read once), projects
    /// the trusted ones, keeps the attempts of the period that pass the filter, groups them and reconciles the journal.
    /// A log that does not verify, a history no anchor recognises and a project that cannot be read are listed as
    /// excluded with their reason, and nothing of them is counted. The warnings of the projections reach
    /// <see cref="UsageReport.Journal"/>, the only place of the report for them.
    /// </summary>
    /// <exception cref="ArgumentException">The period ends before it starts.</exception>
    public async Task<UsageReport> QueryAsync(IReadOnlyList<string> projectRoots, UsageQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectRoots);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Zone);
        ArgumentNullException.ThrowIfNull(query.GroupBy);
        if (query.To < query.From)
        {
            throw new ArgumentException("The period of a usage query ends before it starts.", nameof(query));
        }

        var excluded = new List<ExcludedProject>();
        var trusted = new List<ProjectLogSnapshot>();
        foreach (var root in Distinct(projectRoots, excluded))
        {
            ct.ThrowIfCancellationRequested();
            ProjectLogSnapshot snapshot;
            try
            {
                snapshot = await logs.ReadAsync(root, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or DbException)
            {
                excluded.Add(new ExcludedProject(root, null, $"No se puede leer el registro: {ex.Message}"));
                continue;
            }

            if (snapshot.IsTrusted)
            {
                trusted.Add(snapshot with { Root = root });
            }
            else
            {
                excluded.Add(new ExcludedProject(root, snapshot.ProjectId, WhyNotTrusted(snapshot)));
            }
        }

        // The projection is CPU work: never on the caller's thread, which may be the UI's.
        return await Task.Run(() => Compose(trusted, excluded, query, ct), ct).ConfigureAwait(false);
    }

    private UsageReport Compose(List<ProjectLogSnapshot> trusted, List<ExcludedProject> excluded, UsageQuery query, CancellationToken ct)
    {
        var projected = new List<(string Root, UsageProjection Projection)>();
        var warnings = new List<string>();
        foreach (var snapshot in trusted)
        {
            ct.ThrowIfCancellationRequested();
            var projection = ProjectionOf(snapshot);
            warnings.AddRange(projection.Warnings.Select(w => $"{snapshot.Root}: {w}"));
            projected.Add((snapshot.Root, projection));
        }

        var attempts = UsageAggregator.Select(projected.SelectMany(p => p.Projection.Attempts).OrderBy(a => a.FinishedAt), query);
        var groups = UsageAggregator.Group(attempts, query);
        var journal = JournalReconciler.Reconcile(storage, projected, query, warnings, ct);
        return new UsageReport(attempts, groups, excluded, journal);
    }

    /// <summary>
    /// The projection of a trusted log, kept while the log does not change. The measurement of WP2 decided it: a
    /// projection grows with the whole log while a query is often only a new grouping, filter or period of the same one.
    /// A verified log is append-only and chained by hash, so the same project, length and last hash mean the same entries.
    /// Only the last few logs are kept.
    /// </summary>
    private UsageProjection ProjectionOf(ProjectLogSnapshot snapshot)
    {
        if (snapshot.Entries.Count == 0)
        {
            return new UsageProjection([], [], [], null);
        }

        var head = snapshot.Entries.MaxBy(e => e.Sequence)!;
        var key = new KeptKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.Root)), snapshot.ProjectId, snapshot.Entries.Count, head.Sequence, head.Hash);
        lock (_keptGate)
        {
            var index = _kept.FindIndex(k => k.Key == key);
            if (index >= 0)
            {
                var hit = _kept[index];
                _kept.RemoveAt(index);
                _kept.Add(hit);
                return hit.Projection;
            }
        }

        var projection = Project(snapshot.ProjectId, snapshot.Entries);
        lock (_keptGate)
        {
            _kept.RemoveAll(k => string.Equals(k.Key.Root, key.Root, StringComparison.OrdinalIgnoreCase));
            _kept.Add((key, projection));
            if (_kept.Count > MaxKeptProjections)
            {
                _kept.RemoveAt(0);
            }
        }

        return projection;
    }

    /// <summary>Why a log read from a project is not counted (D-05), in Spanish.</summary>
    private static string WhyNotTrusted(ProjectLogSnapshot snapshot) =>
        !snapshot.Verification.IsValid
            ? snapshot.Verification.FirstInvalidSequence is { } sequence
                ? string.Create(CultureInfo.InvariantCulture, $"El registro no verifica (entrada {sequence}): {snapshot.Verification.Problem}")
                : $"El registro no verifica: {snapshot.Verification.Problem}"
            : snapshot.Anchor switch
            {
                AnchorStatus.Unanchored => "Ningún ancla reconoce este historial en su ubicación (se movió, se copió o vino con el repositorio): no se cuenta hasta que se acepte como nuevo origen.",
                AnchorStatus.AnotherProject => "El ancla de esta ubicación es de otro proyecto: no se cuenta.",
                AnchorStatus.Unreadable => "No se puede leer el ancla del registro: no se cuenta.",
                _ => $"El registro no es de confianza ({snapshot.Anchor}): no se cuenta.",
            };

    /// <summary>The roots in their order, each once whatever its case or final separator; a root that is no path is excluded.</summary>
    private static List<string> Distinct(IReadOnlyList<string> roots, List<ExcludedProject> excluded)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<string>();
        foreach (var root in roots)
        {
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                excluded.Add(new ExcludedProject(root ?? string.Empty, null, $"La ruta del proyecto no es válida: {ex.Message}"));
                continue;
            }

            if (seen.Add(full))
            {
                distinct.Add(root);
            }
        }

        return distinct;
    }

    /// <summary>Projects the attempts of one project's log, which must have verified (the reader checks it).</summary>
    public static UsageProjection Project(string projectId, IReadOnlyList<DecisionEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(entries);

        var warnings = new List<string>();
        var starts = new List<DateTimeOffset>();
        DateTimeOffset? phase4 = null;
        var open = new Dictionary<AttemptKey, Stack<(DecisionEntry Entry, AgentRunStartedPayload Payload)>>();
        var sessions = new Dictionary<(ProviderId Provider, string Session), Session>();
        var attempts = new List<UsageAttempt>();
        var sessionOf = new List<Session?>();

        // The log is read in chunks so that only one chunk of payloads is alive at a time: every attempt is closed (and
        // its own usage worked out) as soon as its end is read, and its payloads are left to the collector.
        var ordered = InSequence(entries);
        var chunk = new (DecisionPayload? Payload, string? Failure)[Math.Min(ChunkSize, ordered.Count)];
        for (var offset = 0; offset < ordered.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, ordered.Count - offset);
            ReadAgentRuns(ordered, offset, chunk.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                var entry = ordered[offset + i];
                var (payload, failure) = chunk[i];
                chunk[i] = default;
                if (failure is not null)
                {
                    warnings.Add(failure);
                }

                switch (payload)
                {
                    case AgentRunStartedPayload started:
                        starts.Add(entry.RecordedAt);
                        if (phase4 is null && started.RecordsPurpose())
                        {
                            phase4 = entry.RecordedAt;
                        }

                        var key = new AttemptKey(started.RunId, started.Task?.Value, started.Attempt);
                        if (!open.TryGetValue(key, out var stack))
                        {
                            open[key] = stack = new Stack<(DecisionEntry, AgentRunStartedPayload)>();
                        }

                        stack.Push((entry, started));
                        break;

                    case AgentRunFinishedPayload finished:
                        if (!open.TryGetValue(new AttemptKey(finished.RunId, finished.Task?.Value, finished.Attempt), out var starting) || starting.Count == 0)
                        {
                            warnings.Add(string.Create(
                                CultureInfo.InvariantCulture,
                                $"La entrada {entry.Sequence} cierra el intento {finished.Attempt} de {finished.Task?.Value ?? "la ejecución"} ({finished.RunId}) sin su AgentRunStarted: no se cuenta."));
                            break;
                        }

                        var (startEntry, startPayload) = starting.Pop();
                        var (attempt, session) = Close(projectId, new Closed(startEntry, startPayload, entry, finished), sessions, warnings);
                        attempts.Add(attempt);
                        sessionOf.Add(session);
                        break;
                }
            }
        }

        WithSessionTotals(attempts, sessionOf);
        return new UsageProjection(attempts, warnings, starts, phase4);
    }

    private static UsageAttempt Build(string projectId, Closed attempt, UsageFigures own, IReadOnlyList<ModelUsageFigures> perModel)
    {
        var started = attempt.Started;
        var finished = attempt.Finished;
        return new UsageAttempt(
            projectId,
            finished.RunId,
            started.Task?.Value,
            started.Attempt,
            started.RecordsPurpose() ? started.Purpose : null,
            started.Agent,
            started.Provider,
            started.Vendor,
            started.Model,
            finished.ReportedModel,
            finished.SessionId ?? finished.ResumedSession,
            attempt.StartEntry.RecordedAt,
            finished.LaunchedAt,
            attempt.FinishEntry.RecordedAt,
            finished.Status,
            own,
            null,
            perModel,
            PeakOf(attempt),
            finished.Compactions)
        {
            RecordedVersion = finished.V,
        };
    }

    /// <summary>The step with the largest context; when there is none, why (never a zero).</summary>
    private static ContextPeakFigures PeakOf(Closed attempt)
    {
        var finished = attempt.Finished;
        if (finished.ContextPeak is { } peak)
        {
            return new ContextPeakFigures(peak.Model, Long(peak.UsedTokens, attempt, null), Long(peak.ContextWindow, attempt, null));
        }

        var (origin, reason) =
            finished.Status == AttemptOutcome.Interrupted ? (MetricOrigin.Missing, "intento interrumpido: no se sabe")
            : !finished.RecordsUsageWithOrigins() ? (MetricOrigin.Missing, Version1Reason)
            : finished.LaunchedAt is null ? (MetricOrigin.Missing, "el proceso no llegó a arrancar")
            : finished.Compactions is null ? (MetricOrigin.NotExposed, "el CLI no informa de la ocupación del contexto")
            : (MetricOrigin.Missing, "no llegó ningún paso");
        var none = MetricValue<long>.Of(origin, null, reason);
        return new ContextPeakFigures(null, none, none);
    }

    private static IReadOnlyList<DecisionEntry> InSequence(IReadOnlyList<DecisionEntry> entries)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].Sequence < entries[i - 1].Sequence)
            {
                return [.. entries.OrderBy(e => e.Sequence)];
            }
        }

        return entries;
    }

    /// <summary>
    /// The payloads of the agent attempts of <paramref name="count"/> entries from <paramref name="offset"/>, by position,
    /// read with <see cref="DecisionPayloadSerializer"/> (the one way payloads are read), or why one cannot be read.
    /// Reading is most of the cost of a projection and every entry is read on its own, so a large chunk is read in
    /// parallel; neither the result nor the order of the warnings depends on it.
    /// </summary>
    private static void ReadAgentRuns(IReadOnlyList<DecisionEntry> entries, int offset, Span<(DecisionPayload? Payload, string? Failure)> chunk)
    {
        if (chunk.Length < ParallelReadThreshold)
        {
            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = ReadOne(entries[offset + i]);
            }

            return;
        }

        var results = new (DecisionPayload?, string?)[chunk.Length];
        Parallel.For(0, chunk.Length, i => results[i] = ReadOne(entries[offset + i]));
        results.CopyTo(chunk);
    }

    private static (DecisionPayload? Payload, string? Failure) ReadOne(DecisionEntry entry)
    {
        var type = entry.Kind switch
        {
            DecisionKind.AgentRunStarted => typeof(AgentRunStartedPayload),
            DecisionKind.AgentRunFinished => typeof(AgentRunFinishedPayload),
            _ => null,
        };
        if (type is null)
        {
            return default;
        }

        try
        {
            return (DecisionPayloadSerializer.Deserialize(type, entry.PayloadJson), null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"La entrada {entry.Sequence} ({entry.Kind}) no se puede leer y no se cuenta: {ex.Message}"));
        }
    }

    /// <summary>A figure of a snapshot as a metric; one that contradicts itself is missing, and says so.</summary>
    private static MetricValue<long> Long(MetricSnapshot? snapshot, Closed attempt, List<string>? warnings)
    {
        if (snapshot is null)
        {
            return MetricValue<long>.Missing(attempt.Finished.RecordsUsageWithOrigins() ? "el registro no guarda esta cifra" : Version1Reason);
        }

        try
        {
            return snapshot.ToInt64();
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            warnings?.Add(Unreadable(attempt, snapshot, ex));
            return MetricValue<long>.Missing(UnreadableReason(attempt));
        }
    }

    private static MetricValue<decimal> Decimal(MetricSnapshot? snapshot, Closed attempt, List<string>? warnings)
    {
        if (snapshot is null)
        {
            return MetricValue<decimal>.Missing(attempt.Finished.RecordsUsageWithOrigins() ? "el registro no guarda esta cifra" : Version1Reason);
        }

        try
        {
            return snapshot.ToDecimal();
        }
        catch (ArgumentException ex)
        {
            warnings?.Add(Unreadable(attempt, snapshot, ex));
            return MetricValue<decimal>.Missing(UnreadableReason(attempt));
        }
    }

    private static string Unreadable(Closed attempt, MetricSnapshot snapshot, Exception ex) =>
        string.Create(CultureInfo.InvariantCulture, $"La entrada {attempt.FinishEntry.Sequence} tiene una cifra ilegible ({snapshot.Source}): {ex.Message}");

    private static string UnreadableReason(Closed attempt) =>
        string.Create(CultureInfo.InvariantCulture, $"cifra ilegible en la entrada {attempt.FinishEntry.Sequence}");

    private readonly record struct AttemptKey(string Run, string? Task, int Attempt);

    /// <summary>What identifies a log already projected: where it is, whose it is, how long it is and its head.</summary>
    private readonly record struct KeptKey(string Root, string ProjectId, int Count, long HeadSequence, string HeadHash);

    private sealed record Closed(DecisionEntry StartEntry, AgentRunStartedPayload Started, DecisionEntry FinishEntry, AgentRunFinishedPayload Finished);
}
