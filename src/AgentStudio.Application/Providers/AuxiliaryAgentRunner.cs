using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Application.Providers;

/// <summary>An agent run outside an orchestration (docs/14 D-08): what it is for, and the request to launch.</summary>
public sealed record AuxiliaryRunRequest(AgentRunPurpose Purpose, AgentRunRequest Request);

/// <summary>
/// How an auxiliary run went: the run id it was recorded under (never an orchestration run), the outcome, the usage the
/// provider reported (null when none came) and the evidence of the prompt and of the answer (null without an answer).
/// </summary>
public sealed record AuxiliaryRunResult(RunId Run, AgentRunOutcome Outcome, TokenUsage? Usage, EvidenceId PromptEvidence, EvidenceId? AnswerEvidence);

/// <summary>Launches one agent outside an orchestration and records it in the decision log like the judge (docs/14 D-08).</summary>
public interface IAuxiliaryAgentRunner
{
    /// <exception cref="ArgumentException">The purpose is one of the engine or the review, or the domain refuses the agent.</exception>
    /// <exception cref="InvalidOperationException">No provider of the project runs the agent's CLI.</exception>
    /// <exception cref="DecisionLogIntegrityException">The decision log does not verify: nothing was captured nor launched.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled; the attempt is recorded as cancelled first.</exception>
    Task<AuxiliaryRunResult> RunAsync(ProjectContext project, AuxiliaryRunRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);
}

/// <summary>
/// Runs the auxiliary agents (the assisted skill recommendation, the narrative daily summary) the way every agent run is
/// recorded (docs/14 D-08):
/// <list type="number">
/// <item>The decision log must verify, or nothing is captured nor launched (fail closed, ADR-0010).</item>
/// <item>The run gets an id of its own, <c>aux-yyyyMMdd-xxxxxx</c>, that no orchestration run can have and that is not in
/// <c>orchestration_runs</c>: the usage panel counts it and the list of runs never shows it.</item>
/// <item>The prompt is captured as evidence, and <c>AgentRunStarted</c> version 2 (with the purpose; version 3 when the agent
/// sets a reasoning effort, which it then records, docs/18 P47) is recorded before the launch: a launch that cannot be
/// recorded does not happen.</item>
/// <item>The events are passed on and read to the end; <c>AgentRunFinished</c> version 2 records the outcome, the usage
/// with origins and breakdown, the time the process started (after any wait in the provider's queue), the model of the
/// steps, the session it resumed, the step with the largest context and the compactions (null when the provider does not
/// report context), with the answer as evidence reported by the agent. The auxiliary runs of Phase 13 (<see cref="RecordsEndClass"/>)
/// record version 3, with the class of their end (docs/17 §8.2).</item>
/// </list>
/// The reported model is the one of the last step (<see cref="ContextObserved.Model"/>) or, without steps, the only model
/// of the usage breakdown; never the model of <see cref="SessionStarted"/>, which a CLI may announce for an auxiliary model
/// (docs/14 D-13).
/// </summary>
public sealed class AuxiliaryAgentRunner(IDecisionRecorder decisions, IEvidenceStore evidence, IProjectAgentProviders providers, TimeProvider timeProvider) : IAuxiliaryAgentRunner
{
    /// <summary>The prefix of every auxiliary run id: an orchestration run id starts with its date (<c>yyyyMMdd-</c>).</summary>
    public const string RunPrefix = "aux-";

    /// <summary>The actor and producer the system records itself as.</summary>
    public const string SystemId = "agent-studio";

    private static readonly Actor SystemActor = new(ActorKind.System, SystemId);

    /// <summary>
    /// Whether <paramref name="purpose"/> is run as an auxiliary run: the assisted skill recommendation, the narrative daily
    /// summary and, from Phase 13 (docs/17 §8.2), «Revisar el orden», the analysis of consumption with an agent and the chat of
    /// preparation and, from Phase 14, the agent of the help and the draft of a document for clients, each with the person's OK.
    /// </summary>
    public static bool IsAuxiliary(AgentRunPurpose purpose) =>
        purpose is AgentRunPurpose.SkillRecommendation or AgentRunPurpose.DailySummary
            or AgentRunPurpose.OrderReview or AgentRunPurpose.ConsumptionAnalysis or AgentRunPurpose.PreparationChat
            or AgentRunPurpose.Help or AgentRunPurpose.ClientDocumentDraft;

    /// <summary>
    /// Whether the end of a run of <paramref name="purpose"/> is recorded with its class (<c>AgentRunFinished</c> version 3,
    /// docs/17 §8.2): the auxiliary runs of Phases 13 and 14 do; the earlier ones keep version 2, as they always wrote it.
    /// </summary>
    public static bool RecordsEndClass(AgentRunPurpose purpose) =>
        purpose is AgentRunPurpose.OrderReview or AgentRunPurpose.ConsumptionAnalysis or AgentRunPurpose.PreparationChat
            or AgentRunPurpose.Help or AgentRunPurpose.ClientDocumentDraft;

    /// <summary>The only tools a read-only auxiliary run of Phase 14 may be allowed.</summary>
    private static readonly string[] ReadOnlyTools = ["Read", "Grep", "Glob"];

    /// <summary>
    /// Whether the runner holds <paramref name="purpose"/> to a read-only launch in an empty folder: the agent of the help and the
    /// draft of a document for clients (docs/17 D-56 and D-59). The other purposes leave the launch to their service.
    /// </summary>
    public static bool RequiresReadOnlyLaunch(AgentRunPurpose purpose) => purpose is AgentRunPurpose.Help or AgentRunPurpose.ClientDocumentDraft;

    /// <summary>
    /// What a read-only launch (<see cref="RequiresReadOnlyLaunch"/>) must not have, in English; empty when it is one: no file writes,
    /// shell or network; allowed tools only Read, Grep and Glob; no additional directories; no MCP servers; no skills; no session to
    /// resume; and a working directory that exists, is empty and is not a link (neither it nor any of its parents is a reparse point:
    /// a link could point the agent at a folder with files). The allowed tools cannot be an empty list: the launch names the three
    /// tools, so a profile that restricts nothing is never read as «Read, Grep and Glob». It is checked before anything is captured,
    /// recorded or launched, and the folder again just before the provider starts (<see cref="WorkingDirectoryProblems"/>).
    /// </summary>
    public static IReadOnlyList<string> ReadOnlyViolations(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        var permissions = request.Permissions;
        if (permissions is null || permissions.AllowFileWrites || permissions.AllowShell || permissions.AllowNetwork)
        {
            problems.Add("permissions must deny file writes, shell and network");
        }

        if (permissions is not null)
        {
            if (permissions.AllowedTools is not { Count: > 0 })
            {
                problems.Add("allowed tools cannot be empty: name Read, Grep and Glob");
            }

            if (permissions.AllowedTools is not null && permissions.AllowedTools.Any(t => !ReadOnlyTools.Contains(t, StringComparer.Ordinal)))
            {
                problems.Add("allowed tools can only be Read, Grep and Glob");
            }

            if (permissions.AdditionalDirectories.Count > 0)
            {
                problems.Add("no additional directories");
            }
        }

        if (request.McpServers is { Count: > 0 })
        {
            problems.Add("no MCP servers");
        }

        if (!string.IsNullOrWhiteSpace(request.SkillsDirectory))
        {
            problems.Add("no skills");
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            problems.Add("no session to resume");
        }

        problems.AddRange(WorkingDirectoryProblems(request.WorkingDirectory));
        return problems;
    }

    /// <summary>
    /// What is wrong with the working directory of a read-only launch, in English: it does not exist or is not empty, or it (or one of
    /// its parents) is a link (<see cref="FileAttributes.ReparsePoint"/>). The runner asks it twice: when the request arrives and again
    /// just before the provider starts, so a file or a link that appeared in between does not reach the agent.
    /// </summary>
    public static IReadOnlyList<string> WorkingDirectoryProblems(string? workingDirectory)
    {
        var problems = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
                || Directory.EnumerateFileSystemEntries(workingDirectory).Any())
            {
                problems.Add("the working directory must exist and be empty");
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) && IsOrHasLinkParent(workingDirectory))
            {
                problems.Add("the working directory cannot be a link, nor inside one");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problems.Add("the working directory cannot be read");
        }

        return problems;
    }

    private static bool IsOrHasLinkParent(string workingDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory)); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<AuxiliaryRunResult> RunAsync(ProjectContext project, AuxiliaryRunRequest request, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        if (!IsAuxiliary(request.Purpose))
        {
            throw new ArgumentException($"A {request.Purpose} run is recorded by the engine, the review, the debate or the audit, never as an auxiliary run.", nameof(request));
        }

        if (RequiresReadOnlyLaunch(request.Purpose) && ReadOnlyViolations(request.Request) is { Count: > 0 } violations)
        {
            throw new ArgumentException($"A {request.Purpose} run is read-only in an empty folder, and this launch is not: {string.Join("; ", violations)}.", nameof(request));
        }

        var agent = request.Request.Agent;
        var problems = agent.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"Agent '{agent.Id}' cannot run: {string.Join(", ", problems)}.", nameof(request));
        }

        var provider = providers.For(project.RootPath).FirstOrDefault(p => p.Provider == agent.Model.Provider)
            ?? throw new InvalidOperationException($"There is no provider '{agent.Model.Provider}' for agent '{agent.Id}' in project '{project.ProjectId}'.");

        // 1: fail closed before anything is captured.
        var log = await decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, log.Verification);
        }

        // 2 and 3: an id of its own, the prompt as evidence and AgentRunStarted before the launch.
        var run = NewRunId(log.Entries);
        var what = Title(request.Purpose);
        var prompt = await CaptureAsync(project, run, EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, $"Prompt de {what}", SystemId, request.Request.Prompt, "text/markdown", ct).ConfigureAwait(false);
        await decisions.RecordAsync(
            project.ProjectId,
            SystemActor,
            DecisionKind.AgentRunStarted,
            $"Arranca {agent.Id} ({provider.Provider}) para {what}",
            new AgentRunStartedPayload(null, 1, agent.Id, provider.Provider, provider.Vendor, agent.Model.Model, request.Request.WorkingDirectory, prompt.Id, prompt.ContentSha256, [], request.Purpose, Effort: agent.Effort)
            {
                RunId = run.Value,
                V = agent.Effort is null ? AgentRunStartedPayload.PurposeVersion : AgentRunStartedPayload.ContextPartsVersion,
            },
            cancellationToken: ct).ConfigureAwait(false);

        // The folder of a read-only launch is checked again now: what was recorded is a start, and an agent never starts in a folder
        // that gained a file or became a link after the first check. The attempt is closed as failed, like a launch that cannot happen.
        if (RequiresReadOnlyLaunch(request.Purpose) && WorkingDirectoryProblems(request.Request.WorkingDirectory) is { Count: > 0 } late)
        {
            var refused = new AgentRunOutcome(AgentRunStatus.Failed, null, null, null, null, "No se lanzó el agente: la carpeta de trabajo ya no está vacía o es un enlace (" + string.Join("; ", late) + ").");
            await RecordFinishedAsync(project, run, request.Purpose, agent.Id.Value, what, refused, null, null, new AgentRunObservations(reportsContext: false)).ConfigureAwait(false);
            return new AuxiliaryRunResult(run, refused, null, prompt.Id, null);
        }

        IAgentRun agentRun;
        try
        {
            // The request carries the start just recorded, so the journal can name it (docs/17 D-02).
            agentRun = await provider.StartAsync(request.Request with { Anchor = new AgentRunAnchor(run.Value, null, 1) }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordFinishedAsync(project, run, request.Purpose, agent.Id.Value, what, new AgentRunOutcome(AgentRunStatus.Cancelled, null, null, null, null, "Se canceló antes de arrancar."), null, null, new AgentRunObservations(reportsContext: false)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var failed = new AgentRunOutcome(AgentRunStatus.Failed, null, null, null, null, "No se pudo lanzar el agente: " + ex.Message);
            await RecordFinishedAsync(project, run, request.Purpose, agent.Id.Value, what, failed, null, null, new AgentRunObservations(reportsContext: false)).ConfigureAwait(false);
            return new AuxiliaryRunResult(run, failed, null, prompt.Id, null);
        }

        // 4: the events to the end, then the outcome; once the agent ran, its end is recorded even if the caller cancels.
        var launchedAt = timeProvider.GetUtcNow();
        await using (agentRun.ConfigureAwait(false))
        {
            using var cancelOnRequest = ct.Register(() => _ = agentRun.CancelAsync());
            var seen = new AgentRunObservations(provider.Capabilities.ReportsContextOccupancy, request.Request.ResumeSessionId, launchedAt);
            try
            {
                await foreach (var agentEvent in agentRun.Events.WithCancellation(ct).ConfigureAwait(false))
                {
                    seen.Observe(agentEvent, timeProvider.GetUtcNow());
                    progress?.Report(agentEvent);
                }
            }
            catch (Exception)
            {
                // A broken or cancelled event stream, whoever cancelled it, does not decide the outcome: the completion
                // does, and the end of the attempt is recorded either way.
            }

            var outcome = await agentRun.Completion.ConfigureAwait(false);
            EvidenceId? answer = null;
            var text = outcome.StructuredOutputJson ?? outcome.FinalText;
            if (!string.IsNullOrEmpty(text))
            {
                var mediaType = outcome.StructuredOutputJson is null ? "text/markdown" : "application/json";
                answer = (await CaptureAsync(project, run, EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent, $"Respuesta de {what}", agent.Id.Value, text, mediaType, CancellationToken.None).ConfigureAwait(false)).Id;
            }

            await RecordFinishedAsync(project, run, request.Purpose, agent.Id.Value, what, outcome, seen.Usage, answer, seen).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new AuxiliaryRunResult(run, outcome, seen.Usage, prompt.Id, answer);
        }
    }

    private Task<DecisionEntry> RecordFinishedAsync(ProjectContext project, RunId run, AgentRunPurpose purpose, string agent, string what, AgentRunOutcome outcome, TokenUsage? usage, EvidenceId? answer, AgentRunObservations seen) =>
        decisions.RecordAsync(
            project.ProjectId,
            SystemActor,
            DecisionKind.AgentRunFinished,
            $"Termina {agent} para {what}: {AttemptOutcomes.From(outcome.Status)}",
            new AgentRunFinishedPayload(
                null,
                1,
                AttemptOutcomes.From(outcome.Status),
                outcome.ExitCode,
                outcome.SessionId ?? seen.SessionId,
                outcome.Error,
                usage is null ? null : UsageSnapshot.WithBreakdown(usage),
                answer,
                seen.LaunchedAt,
                seen.ReportedModel(seen.Usage),
                seen.ResumedSession,
                seen.PeakSnapshot(usage),
                seen.Compactions,
                usage?.Turns,
                RecordsEndClass(purpose) ? AttemptEndSnapshot.From(outcome.Classified()) : null)
            {
                RunId = run.Value,
                V = RecordsEndClass(purpose) ? AgentRunFinishedPayload.EndClassVersion : AgentRunFinishedPayload.UsageVersion,
                Journal = outcome.Journal,
            },
            cancellationToken: CancellationToken.None);

    private async Task<EvidenceRecord> CaptureAsync(ProjectContext project, RunId run, EvidenceKind kind, EvidenceOrigin origin, string title, string producedBy, string content, string mediaType, CancellationToken ct)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await evidence.CaptureAsync(
            project.ProjectId,
            new EvidenceDraft(kind, origin, title, producedBy, RunId: run.Value, Attempt: 1, MediaType: mediaType),
            stream,
            ct).ConfigureAwait(false);
    }

    /// <summary>A new auxiliary run id: the prefix, the date and six random hex digits, none already in the log.</summary>
    private RunId NewRunId(IReadOnlyList<DecisionEntry> entries)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            try
            {
                if (DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson) is { } payload)
                {
                    taken.Add(payload.RunId);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // A payload that cannot be read names no run (the chain verified, so it was written that way).
            }
        }

        var day = timeProvider.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var i = 0; i < 64; i++)
        {
            var id = $"{RunPrefix}{day}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            if (!taken.Contains(id))
            {
                return new RunId(id);
            }
        }

        throw new InvalidOperationException("Could not allocate a free auxiliary run id.");
    }

    private static string Title(AgentRunPurpose purpose) => purpose switch
    {
        AgentRunPurpose.SkillRecommendation => "la recomendación de skills",
        AgentRunPurpose.DailySummary => "el resumen diario",
        AgentRunPurpose.OrderReview => "la revisión del orden",
        AgentRunPurpose.ConsumptionAnalysis => "el análisis del consumo",
        AgentRunPurpose.PreparationChat => "el chat de preparación",
        AgentRunPurpose.Help => "la ayuda",
        AgentRunPurpose.ClientDocumentDraft => "el borrador del documento para clientes",
        _ => purpose.ToString(),
    };
}
