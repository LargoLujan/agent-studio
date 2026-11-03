using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Observability.Usage;
using AgentStudio.Tests.Providers.Live;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// Reads back what a demonstration left in the stores and the log, for its capture: never what the test meant to do, only
/// what the app recorded. Every reading is defensive: the quota is spent by then, and a figure that cannot be read becomes
/// a note instead of losing the evidence.
/// </summary>
internal static class DemoFacts
{
    /// <summary>The CLIs as the app's probes see them: only the installation, the version and whether there is a session.</summary>
    public static async Task<IReadOnlyList<DemoTool>> ToolsAsync(DemoApp app, CancellationToken ct)
    {
        var probes = await app.ProbeAsync(ct);
        return [.. probes
            .Where(p => p.Provider == ProviderId.ClaudeCode || p.Provider == ProviderId.CodexCli)
            .Select(p => new DemoTool(p.DisplayName, p.Installation.ToString(), p.Version, p.Authentication.ToString()))];
    }

    public static IReadOnlyList<DemoRequirement> Requirements(FrozenRequirementSet? requirements) =>
        requirements is null
            ? []
            : [.. requirements.Requirements.Select(r => new DemoRequirement(
                r.Id.Value,
                r.Statement,
                r.Priority.ToString(),
                [.. r.Criteria.Select(c => new DemoCriterion(c.Id, c.Method.ToString(), c.Description))]))];

    /// <summary>The entries of the units (runs, debates, audits), in order, with the alerts among them.</summary>
    public static (IReadOnlyList<DemoEntryFacts> Entries, IReadOnlyList<string> Alerts) Log(IReadOnlyList<DecisionEntry> log, IReadOnlySet<string> units)
    {
        var entries = new List<DemoEntryFacts>();
        var alerts = new List<string>();
        foreach (var entry in log)
        {
            DecisionPayload? payload;
            try
            {
                payload = DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                continue;
            }

            if (payload is null || !units.Contains(payload.RunId))
            {
                continue;
            }

            entries.Add(new DemoEntryFacts(entry.Sequence, entry.RecordedAt, $"{entry.Actor.Kind}:{entry.Actor.Id}", entry.Kind.ToString(), entry.Summary));
            if (payload is AuditAlertPayload alert)
            {
                alerts.Add($"{alert.Code}: {alert.Message}");
            }
        }

        return (entries, alerts);
    }

    /// <summary>The attempts of the units as the usage projection of Phase 4 counts them (the «Consumo» panel reads the same).</summary>
    public static IReadOnlyList<DemoAttemptFacts> Attempts(string projectId, IReadOnlyList<DecisionEntry> log, IReadOnlySet<string> units)
    {
        var projection = UsageProjector.Project(projectId, log);
        return [.. projection.Attempts
            .Where(a => units.Contains(a.RunId))
            .Select(a => new DemoAttemptFacts(
                a.RunId,
                a.TaskId,
                a.Attempt,
                a.Purpose?.ToString(),
                a.Agent.Value,
                a.Provider.Value,
                a.Vendor.Value,
                a.ConfiguredModel,
                a.ReportedModel,
                a.Outcome.ToString(),
                a.LaunchedAt is { } launched ? Math.Round((a.FinishedAt - launched).TotalSeconds, 1) : null,
                DemoFigure.Of(a.Own.InputTokens),
                DemoFigure.Of(a.Own.CacheReadInputTokens),
                DemoFigure.Of(a.Own.CacheWriteInputTokens),
                DemoFigure.Of(a.Own.OutputTokens),
                DemoFigure.Of(a.Own.ReasoningOutputTokens),
                DemoFigure.Of(a.Own.CostUsd)))];
    }

    /// <summary>
    /// The verdicts kept in <paramref name="unit"/> (all of them, or those in <paramref name="only"/>), as the judge gave them,
    /// with what the validator found and the note of their <c>VerdictIssued</c> (the disposition and the waivers).
    /// </summary>
    public static async Task<IReadOnlyList<DemoVerdictFacts>> VerdictsAsync(DemoApp app, IReadOnlyList<DecisionEntry> log, string unit, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        var notes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in log.Where(e => e.Kind == DecisionKind.VerdictIssued))
        {
            try
            {
                if (DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson) is VerdictIssuedPayload issued)
                {
                    notes[issued.VerdictId] = issued.VendorNote;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // An entry that cannot be read adds no note.
            }
        }

        var stored = await app.Get<IVerdictStore>().ListAsync(app.Project.ProjectId, unit, ct);
        return [.. stored
            .Where(v => only is null || only.Contains(v.Id))
            .Select(v => Verdict(v, notes.GetValueOrDefault(v.Id)))];
    }

    public static DemoVerdictFacts Verdict(StoredVerdict stored, string? note)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var verdict = stored.Verdict;
        return new DemoVerdictFacts
        {
            Id = stored.Id,
            Unit = stored.RunId,
            Scope = stored.Scope.ToString(),
            Judge = new DemoJudge(verdict.Judge.Agent.Value, verdict.Judge.Model.Provider.Value, verdict.Judge.Model.Vendor.Value, verdict.Judge.Model.Model),
            Decision = verdict.Decision.ToString(),
            ChosenProposal = verdict.Resolution?.ChosenProposal,
            Confidence = verdict.Confidence?.Level.ToString() ?? "-",
            ConfidenceJustification = verdict.Confidence?.Justification ?? string.Empty,
            Valid = stored.Valid,
            Supersedes = stored.Supersedes,
            Issues = [.. stored.Issues.Select(i => new DemoIssue(i.Code.ToString(), i.Message))],
            Summary = verdict.Summary ?? string.Empty,
            Assessments = [.. (verdict.Assessments ?? []).Where(a => a is not null).Select(a => new DemoAssessment(a.Requirement.Value, a.Status.ToString(), a.Evidence?.Count ?? 0, a.Rationale ?? string.Empty))],
            Findings = [.. (verdict.Findings ?? []).Where(f => f is not null).Select(f => new DemoFinding(f.Category.ToString(), f.Severity.ToString(), f.Description ?? string.Empty))],
            Comparison = [.. (verdict.Resolution?.Comparison ?? []).Where(c => c is not null).Select(c => new DemoComparison(c.Proposal, c.Strengths ?? string.Empty, c.Weaknesses ?? string.Empty))],
            Risks = [.. verdict.Risks ?? []],
            CorrectiveActions = [.. verdict.CorrectiveActions ?? []],
            PendingObjections = [.. verdict.PendingObjections ?? []],
            RequiresHumanIntervention = verdict.RequiresHumanIntervention,
            RequirementSetHash = verdict.RequirementSetHash ?? string.Empty,
            DecisionLogHeadHash = verdict.DecisionLogHeadHash ?? string.Empty,
            IssuedAt = verdict.IssuedAt,
            VendorNote = note,
        };
    }

    /// <summary>The latest observation of each window of quota in the user's record (docs/14 D-14), whitelisted.</summary>
    public static async Task<IReadOnlyList<DemoQuotaFacts>> QuotaAsync(DemoApp app, CancellationToken ct)
    {
        var quota = app.Services.GetService(typeof(IQuotaObservations)) as IQuotaObservations;
        if (quota is null)
        {
            return [];
        }

        var latest = await quota.LatestAsync(ct);
        return [.. latest.Select(q => new DemoQuotaFacts(q.Provider.Value, q.Window, q.Label, DemoFigure.Of(q.Utilization), q.ResetsAt, q.ObservedAt, q.Source, q.Reached))];
    }

    /// <summary>The lines of the project's prompt journal that name <paramref name="unit"/>: one per launch, with its tags.</summary>
    public static IReadOnlyList<string> Journal(string projectRoot, string unit) =>
        [.. JournalFiles.ReadLines(projectRoot).Where(l => l.Contains(unit, StringComparison.Ordinal))];

    /// <summary>
    /// The launches the project's prompt journal recorded since <paramref name="since"/> (its id, local time and summary):
    /// the entries of an orchestration run name their task, not the run, so they are told by when they were written.
    /// </summary>
    public static IReadOnlyList<string> JournalSince(string projectRoot, DateTimeOffset since)
    {
        var from = TimeZoneInfo.ConvertTime(since, TimeZoneInfo.Local).DateTime.AddSeconds(-1);
        return [.. JournalFiles.Read(projectRoot).Entries
            .Where(e => e.LocalTime >= from)
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Id} · {e.LocalTime:HH:mm:ss} · «{e.Summary}»"))];
    }

    public static IReadOnlyList<DemoApprovalFacts> Approvals(DemoPerson person) =>
        [.. person.Asked.Select(a => new DemoApprovalFacts(a.Request.Action, a.Approved, a.Request.Description))];

    /// <summary>A piece of evidence the system captured, with the isolation and source its header declares (ADR-0017).</summary>
    public static async Task<DemoEvidence> EvidenceAsync(DemoApp app, EvidenceRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        string? isolation = null;
        try
        {
            await using var stream = await app.Get<IEvidenceStore>().OpenContentAsync(app.Project.ProjectId, record.Id, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            for (var i = 0; i < 40 && await reader.ReadLineAsync(ct) is { } line; i++)
            {
                if (line.StartsWith("# isolation:", StringComparison.Ordinal))
                {
                    isolation = line["# isolation:".Length..].Trim();
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            isolation = "no se pudo leer: " + ex.Message;
        }

        return new DemoEvidence(
            record.Id.Value,
            record.Kind.ToString(),
            record.Origin.ToString(),
            record.Title,
            record.SubjectCommit,
            isolation,
            record.TestRun?.Passed,
            record.TestRun?.Failed,
            record.TestRun?.Skipped,
            record.TestRun?.ExitCode);
    }

    /// <summary>
    /// A debate as the app keeps it: its header and the approval it was opened with, its rounds as <c>DebateRoundClosed</c>
    /// recorded them, its contributions read against their anchored hashes, and how it ended. A revision debate keeps its
    /// verdicts in the unit of the verdict it challenges, so those are read by the caller.
    /// </summary>
    public static async Task<DemoDebateFacts> DebateAsync(DemoApp app, DebateProposal proposal, RunId debate, DebateWatch watch, IReadOnlyList<DemoEvidence> tests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(watch);
        var project = app.Project;
        var problems = new List<string>();
        DebateTranscript? transcript = null;
        try
        {
            transcript = await app.Get<IDebateCoordinator>().ReadAsync(project, debate, ct);
            problems.AddRange(transcript.Problems);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            problems.Add("No se pudo leer el debate: " + ex.Message);
        }

        var record = await app.Get<IDebateRegistry>().FindAsync(project.ProjectId, debate, ct);
        var requirements = record is null ? null : await app.Get<IRequirementStore>().FindAsync(project.ProjectId, record.RequirementSetHash, ct);
        var log = await app.LoadLogAsync(ct);
        var debateLog = DebateLog.Of(log.Entries, debate);
        problems.AddRange(debateLog.Problems);
        problems.AddRange(watch.Snapshot?.Problems ?? []);

        var result = watch.Result;
        var request = proposal.Request;
        return new DemoDebateFacts
        {
            Id = debate.Value,
            Mode = request.Mode.ToString(),
            Subject = (record?.Subject.Kind ?? request.Subject.Kind).ToString(),
            SubjectReference = record?.Subject.Reference ?? request.Subject.Reference,
            SnapshotCommit = record?.SnapshotCommit,
            Question = transcript?.Summary.Question ?? request.Question,
            RequirementSetHash = record?.RequirementSetHash ?? request.Requirements.ContentHash,
            Requirements = Requirements(requirements ?? request.Requirements),
            Participants = transcript is { Participants.Count: > 0 }
                ? [.. transcript.Participants.Select(p => new DemoParticipant(p.Label, p.Role.ToString(), p.Agent.Value, p.Provider.Value, p.Vendor.Value, p.Model))]
                : [.. (record?.Participants ?? []).Select(p => new DemoParticipant(p.Label, p.Role.ToString(), p.Agent.Id.Value, p.Agent.Model.Provider.Value, p.Agent.Model.Vendor.Value, p.Agent.Model.Model))],
            Judges = [.. (record?.Judges ?? []).Select(j => new DemoJudge(j.Id.Value, j.Model.Provider.Value, j.Model.Vendor.Value, j.Model.Model))],
            Judging = (record?.Judging ?? request.Judging).ToString(),
            WaivedVendors = [.. (record?.WaivedParticipantVendors ?? [.. proposal.Judging.WaivedParticipantVendors ?? new HashSet<VendorId>()]).Select(v => v.Value).Order(StringComparer.Ordinal)],
            ProducerVendors = [.. (record?.ProducerVendors ?? []).Select(v => v.Value).Order(StringComparer.Ordinal)],
            MaxRounds = record?.Limits.MaxRounds ?? request.Limits.MaxRounds,
            MaxMinutes = (record?.Limits.MaxDuration ?? request.Limits.MaxDuration).TotalMinutes,
            MaxTokens = record?.Limits.MaxTotalTokens ?? request.Limits.MaxTotalTokens,
            MaxAgentRuns = record?.MaxAgentRuns ?? proposal.Estimate.MaxAgentRuns,
            EstimatedTokens = proposal.Estimate.Tokens.Value,
            EstimateNotes = [.. proposal.Estimate.Notes],
            ApprovalText = proposal.ApprovalText,
            Rounds = [.. debateLog.RoundsClosed.Select(r => new DemoRound(
                r.Payload.Round,
                r.Payload.Next.ToString(),
                r.Payload.Judgment?.ToString(),
                r.Payload.Limit.ToString(),
                DemoFigure.Of(r.Payload.TokensUsed),
                r.Payload.TokensIncludeEstimates,
                r.Payload.ElapsedSeconds,
                r.Payload.Reason))],
            Contributions = [.. (transcript?.Contributions ?? []).Select(Contribution)],
            Phase = (result?.Phase ?? transcript?.Summary.Phase)?.ToString() ?? "-",
            Outcome = (result?.Outcome ?? transcript?.Summary.Outcome)?.ToString() ?? "-",
            Rule = result?.Panel?.Rule ?? transcript?.Outcome?.Rule ?? debateLog.Closed?.Payload.Rule,
            Decision = (result?.Panel?.Decision ?? transcript?.Outcome?.Decision)?.ToString(),
            ChosenProposal = result?.Panel?.ChosenProposal ?? transcript?.Outcome?.ChosenProposal ?? debateLog.Closed?.Payload.ChosenProposal,
            JudgmentReason = (result?.JudgmentReason ?? transcript?.JudgmentReason)?.ToString(),
            LimitReached = (result?.LimitReached ?? transcript?.LimitReached ?? DebateLimitReached.None).ToString(),
            ResultSummary = result?.Summary ?? debateLog.Closed?.Payload.Reason ?? string.Empty,
            Stopped = watch.Stopped,
            AwaitingReason = watch.Awaiting?.Reason.ToString(),
            AwaitingMessage = watch.Awaiting?.Message,
            Tokens = watch.Snapshot is { } snapshot ? Tokens(snapshot) : null,
            ReportedTurns = watch.Snapshot?.ReportedTurns ?? 0,
            AgentRuns = watch.Snapshot?.AgentRuns ?? 0,
            Problems = [.. problems.Distinct(StringComparer.Ordinal)],
            Timeline = watch.Timeline,
            Tests = tests,
        };
    }

    /// <summary>The tokens of the participants the room showed at the end, with their origin («parcial (n de m)» when some turn reported none).</summary>
    private static DemoFigure Tokens(DebateSnapshot snapshot)
    {
        var tokens = snapshot.Tokens;
        var source = tokens.Missing > 0
            ? string.Create(CultureInfo.InvariantCulture, $"debate tokens: {tokens.Reported} of {tokens.Count} attempts reported")
            : "debate tokens: input + output of the participants (DebateBudget)";
        return new DemoFigure(tokens.Sum, tokens.Origin.ToString(), source);
    }

    private static DemoContribution Contribution(DebateContributionView view)
    {
        string? summary = null;
        string? argument = null;
        var claims = 0;
        var citations = 0;
        var concedes = new List<string>();
        if (view.Text is { Length: > 0 } text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                summary = String(root, "summary");
                argument = String(root, "argument");
                claims = Count(root, "claims");
                citations = Count(root, "citations");
                if (root.TryGetProperty("concedes", out var conceded) && conceded.ValueKind == JsonValueKind.Array)
                {
                    concedes.AddRange(conceded.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!));
                }
            }
            catch (JsonException ex)
            {
                summary = "(el texto de la contribución no es JSON: " + ex.Message + ")";
            }
        }
        else
        {
            summary = "(el texto no se pudo leer contra su hash)";
        }

        return new DemoContribution(
            view.Label,
            view.Round,
            view.Author,
            view.Kind.ToString(),
            view.RepliesTo,
            summary,
            argument,
            claims,
            citations,
            view.UnresolvedCitations,
            concedes,
            view.Finished,
            view.Redactions,
            view.AnsweredAt,
            view.Sha256);
    }

    private static string? String(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Count(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
}
