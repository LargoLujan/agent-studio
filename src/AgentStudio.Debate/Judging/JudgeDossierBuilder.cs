using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// Builds the dossier of an orchestration run straight from Agent Studio's stores, through their ports, and writes its
/// package of files under the dossier root. Callers pass identifiers only, so neither the engine nor the orchestrator can
/// choose or filter what the judge sees.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The decision log is the source of truth: the entries of the run, the head of the whole log (which the verdict
/// must echo), the hashes of the plan and of the frozen requirements (from <c>RunStarted</c>), the producers' vendors
/// (<see cref="RunLog.ProducerVendors"/>) and the state of the tasks, replayed with <see cref="RunStateReplayer"/>.</item>
/// <item>The run header (<see cref="IRunRegistry"/>) gives the evidence that holds the literal original request and the
/// instant the run started; a header that disagrees with the log is reported, and the log wins. The header is a row of
/// the store, so it must also hash to what <c>RunStarted</c> anchored (<see cref="RunHeaderHash"/>, version 2): one that
/// does not, or a run whose <c>RunStarted</c> anchors no hash (version 1), is an integrity problem.</item>
/// <item>The commit under review is the result of the run's last clean integration in the log
/// (<see cref="RunLog.CandidateCommit"/>): only test runs bound to it can support an approval.</item>
/// <item>Every problem found (the checks below) is an integrity problem of the dossier
/// (<see cref="JudgeDossier.IntegrityProblems"/>): the judge sees it and no approval can pass the validator.</item>
/// <item>Only the evidence the verified log registered is consumed (<see cref="RunLog.RegisteredEvidence"/>), and as the
/// log anchored it: a row of the store that no <c>EvidenceRegistered</c> entry announced with its id, SHA-256, kind and
/// origin (and, from version 2, its title, author, capture time, test counters and commit) is left out and reported, and
/// so is the original request when the log did not register it. A row announced only by version 1 is reported too: its
/// metadata is unanchored, so it can never support an approval.</item>
/// <item>Every evidence of the dossier is copied into the package and checked against the SHA-256 recorded when it was
/// captured. A mismatch, or content that cannot be read, is a system check the judge sees: never silently left out.</item>
/// <item>The original request is read only against the SHA-256 its <c>EvidenceRegistered</c> entry anchored, never against
/// the row of the store (R3-08): content rewritten with the hash of its row, or that the log never anchored, is withheld
/// from the judge, with a system check.</item>
/// <item>The package goes to <c>&lt;root&gt;/&lt;project&gt;/&lt;run&gt;/&lt;first 16 characters of the head&gt;</c>, so
/// building again at the same head rewrites the same package and a later state of the log gets a new one. Nothing is
/// deleted nor written through a link (junction or symbolic link) from the root down, and the files of the package
/// are always new files.</item>
/// <item>Phase 13: the prompts of the executors (with the brief, the instructions of the agents and the answers of the
/// person), the prompt of the orchestrator's planning (in every attempt, with questions or without them), the questions as
/// the agents wrote them (with their recommended option and efforts), every report of an executor or of the orchestrator's
/// planning that brings a question or cannot be read, and the <c>QuestionAsked</c> entries (with the recommendation and the
/// estimates) never reach the judge: of every question, those of the executors and those of the orchestrator alike, the
/// judge gets the decision of the person, only the choice, with no text of the agent (docs/17 D-33, docs/18 P54).</item>
/// </list>
/// Phase 3 only reviews runs: a request without a run id is refused (the whole-project audit is Phase 5).
/// </remarks>
public sealed class JudgeDossierBuilder(
    IDecisionRecorder decisions,
    IRunRegistry runs,
    IPlanStore plans,
    IRequirementStore requirements,
    IEvidenceStore evidence,
    JudgingOptions options) : IJudgeDossierBuilder, IJudgeDossierPackageBuilder
{
    private const int HeadPrefixLength = 16;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<JudgeDossier> BuildAsync(DossierRequest request, CancellationToken cancellationToken) =>
        (await BuildPackageAsync(request, cancellationToken).ConfigureAwait(false)).Dossier;

    /// <exception cref="ArgumentException">The request names no run, or the run id is not valid.</exception>
    /// <exception cref="InvalidOperationException">The run never started, is not registered, or its plan, requirements or original request are missing.</exception>
    public async Task<JudgeDossierPackage> BuildPackageAsync(DossierRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectId);
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ArgumentException("Phase 3 builds the dossier of one orchestration run: the request must name the run.", nameof(request));
        }

        var projectId = request.ProjectId;
        var run = new RunId(request.RunId);
        var checks = new List<string>();

        // A critical moment: the judge rests on the whole log, verified from its first entry (ADR-0010, addendum of 2026-11-04).
        var log = await decisions.LoadAsync(projectId, LogLoad.Full, cancellationToken).ConfigureAwait(false);
        if (!log.Verification.IsValid)
        {
            checks.Add($"The decision log does not verify (entry {log.Verification.FirstInvalidSequence}): {log.Verification.Problem}");
        }

        var runEntries = RunLog.EntriesOf(log.Entries, run.Value);
        var started = RunLog.RunStarted(runEntries)
            ?? throw new InvalidOperationException($"The decision log of project '{projectId}' has no RunStarted for run '{run}'.");
        var header = await runs.FindAsync(projectId, run, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Run '{run}' of project '{projectId}' is not registered.");
        if (!string.Equals(header.PlanHash, started.PlanHash, StringComparison.Ordinal))
        {
            checks.Add($"The run header names plan {header.PlanHash}, but RunStarted in the log names {started.PlanHash}; the log is used.");
        }

        if (!string.Equals(header.RequirementSetHash, started.RequirementSetHash, StringComparison.Ordinal))
        {
            checks.Add($"The run header names requirement set {header.RequirementSetHash}, but RunStarted in the log names {started.RequirementSetHash}; the log is used.");
        }

        if (HeaderProblem(run, header, started) is { } unverified)
        {
            checks.Add(unverified);
        }

        var frozen = await requirements.FindAsync(projectId, started.RequirementSetHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The frozen requirement set {started.RequirementSetHash} of run '{run}' is not stored.");
        var plan = await plans.FindAsync(projectId, started.PlanHash, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The plan {started.PlanHash} of run '{run}' is not stored.");
        var originalRequest = await ReadOriginalRequestAsync(projectId, header.OriginalRequest, RunLog.AnchoredSha256(runEntries, header.OriginalRequest), checks, cancellationToken).ConfigureAwait(false);
        var stored = await evidence.ListByRunAsync(projectId, run.Value, cancellationToken).ConfigureAwait(false);
        var withheld = await WithheldFromTheJudgeAsync(projectId, runEntries, plan, checks, cancellationToken).ConfigureAwait(false);
        var runEvidence = RunLog.RegisteredEvidence(runEntries, stored, checks).Where(e => !withheld.Contains(e.Id)).ToList();
        if (!runEvidence.Any(e => e.Id == header.OriginalRequest))
        {
            checks.Add($"The original request (evidence '{header.OriginalRequest}') named by the run header is not evidence the decision log registered for this run.");
        }

        // A run that came from a planning of another run (the plan was edited after the orchestrator proposed it) has not the
        // answers of the person to the orchestrator's questions among its entries: only those decisions come from that planning.
        var planned = await PlannedInAsync(projectId, run, started, log.Entries, checks, cancellationToken).ConfigureAwait(false);
        runEvidence.AddRange(planned.Evidence);

        var replayProblems = new List<string>();
        var events = RunLog.ReplayEvents(runEntries, replayProblems);
        var replay = RunStateReplayer.Replay(plan, header.CreatedAt, events, started.PlanHash);
        checks.AddRange(replayProblems.Concat(replay.Problems).Select(p => "Replay: " + p));

        var dossier = new JudgeDossier(
            originalRequest,
            frozen,
            PlanJson.Render(plan),
            [.. runEntries.Where(e => e.Kind != DecisionKind.QuestionAsked).Concat(planned.Answers).OrderBy(e => e.Sequence)],
            log.HeadHash,
            runEvidence,
            [.. replay.Graph.Tasks.Select(t => t.Snapshot())],
            RunLog.ProducerVendors(runEntries))
        {
            CandidateCommit = RunLog.CandidateCommit(runEntries),
        };

        var folder = PackageFolder(projectId, run, log.HeadHash);
        return await WritePackageAsync(folder, projectId, run, dossier, checks, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the dossier takes from the planning of another run: the answers of the person and the evidence of their decisions.</summary>
    private sealed record PlannedDecisions(IReadOnlyList<DecisionEntry> Answers, IReadOnlyList<EvidenceRecord> Evidence)
    {
        public static PlannedDecisions None { get; } = new([], []);
    }

    /// <summary>
    /// P54 (docs/17 D-33): the decisions of the person about the questions the orchestrator asked before it planned, when this run
    /// did not plan (<c>RunStarted.PlannedIn</c> names the run that did: the plan was edited after the orchestrator proposed it, so
    /// the run got an id of its own). From the verified entries of that planning, and only from them, the dossier takes:
    /// <list type="bullet">
    /// <item>each <c>QuestionAnswered</c> with no task (the first one of each question, as the engine reads them);</item>
    /// <item>the document the system captured for it (found by the title <see cref="QuestionEvidenceTitles.OrchestratorDecision"/>
    /// among the evidence the log registered, <c>CapturedBySystem</c>, with no task) and the person's own text when there is one
    /// (the evidence the answer cites), each only as <see cref="RunLog.RegisteredEvidence"/> admits it.</item>
    /// </list>
    /// Nothing else of that run reaches the judge: not its prompts, its reports, its <c>QuestionAsked</c> nor any other evidence.
    /// Fail closed: a <c>PlannedIn</c> the log has no entry of, an entry that cannot be read, and a document of a decision or a text
    /// of the person that cannot be admitted as the log anchored it are system checks, so no approval can rest on the dossier. A
    /// question answered before the planner captured decisions has none: the judge only gets its <c>QuestionAnswered</c>, as for
    /// any run.
    /// </summary>
    private async Task<PlannedDecisions> PlannedInAsync(
        string projectId,
        RunId run,
        RunStartedPayload started,
        IReadOnlyList<DecisionEntry> log,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        if (started.PlannedIn is not { } plannedIn || string.Equals(plannedIn, run.Value, StringComparison.Ordinal))
        {
            return PlannedDecisions.None; // a run that planned itself has those entries among its own
        }

        IReadOnlyList<DecisionEntry> planning = string.IsNullOrWhiteSpace(plannedIn) ? [] : RunLog.EntriesOf(log, plannedIn);
        if (planning.Count == 0)
        {
            checks.Add($"RunStarted of run '{run}' says the orchestrator planned in run '{plannedIn}', which the decision log has no entry of: the decisions of the person about the orchestrator's questions cannot be read, so no approval can rest on the dossier.");
            return PlannedDecisions.None;
        }

        var answers = new List<DecisionEntry>();
        var asked = new List<(string Question, EvidenceId? OwnText)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in planning.Where(e => e.Kind == DecisionKind.QuestionAnswered))
        {
            QuestionAnsweredPayload answer;
            try
            {
                answer = DecisionPayloadSerializer.Deserialize<QuestionAnsweredPayload>(entry.PayloadJson);
            }
            catch (System.Text.Json.JsonException ex)
            {
                checks.Add($"Entry {entry.Sequence} (QuestionAnswered) of run '{plannedIn}': its payload cannot be read, so the decision of the person it records cannot be given to the judge; the dossier cannot support an approval: {ex.Message}");
                continue;
            }

            if (answer.Task is null && seen.Add(answer.Question))
            {
                answers.Add(entry);
                asked.Add((answer.Question, answer.AnswerEvidence));
            }
        }

        var registered = new List<EvidenceRegisteredPayload>();
        foreach (var entry in planning.Where(e => e.Kind == DecisionKind.EvidenceRegistered))
        {
            try
            {
                registered.Add(DecisionPayloadSerializer.Deserialize<EvidenceRegisteredPayload>(entry.PayloadJson));
            }
            catch (System.Text.Json.JsonException ex)
            {
                checks.Add($"Entry {entry.Sequence} (EvidenceRegistered) of run '{plannedIn}': its payload cannot be read, so the document of a decision of the person cannot be told from it; the dossier cannot support an approval: {ex.Message}");
            }
        }

        var wanted = new List<(EvidenceId Evidence, EvidenceOrigin Origin, string What)>();
        foreach (var (question, ownText) in asked)
        {
            var title = QuestionEvidenceTitles.OrchestratorDecision(question);
            if (registered.FirstOrDefault(p => p.Task is null && p.Kind == EvidenceKind.Document && p.Origin == EvidenceOrigin.CapturedBySystem && string.Equals(p.Title, title, StringComparison.Ordinal)) is { } decision)
            {
                wanted.Add((decision.Evidence, EvidenceOrigin.CapturedBySystem, $"the decision about question '{question}' of the orchestrator"));
            }

            if (ownText is { } text)
            {
                wanted.Add((text, EvidenceOrigin.ProvidedByHuman, $"the person's own answer to question '{question}' of the orchestrator"));
            }
        }

        var records = new List<EvidenceRecord>();
        if (wanted.Count > 0)
        {
            var stored = await evidence.ListByRunAsync(projectId, plannedIn, cancellationToken).ConfigureAwait(false);
            var problems = new List<string>(); // of all the evidence of that run: only what concerns the wanted evidence is the dossier's
            var admitted = RunLog.RegisteredEvidence(planning, stored, problems);
            foreach (var (id, origin, what) in wanted)
            {
                if (admitted.FirstOrDefault(e => e.Id == id) is { } record && record.Origin == origin)
                {
                    // Admitted with a problem (anchored only by version 1 of EvidenceRegistered, say): it reaches the judge, and the
                    // problem with it, as it would from the run's own evidence.
                    records.Add(record);
                    checks.AddRange(problems.Where(p => p.Contains($"'{id}'", StringComparison.Ordinal)));
                    continue;
                }

                var why = problems.Where(p => p.Contains($"'{id}'", StringComparison.Ordinal)).ToList();
                checks.Add($"Evidence '{id}' of run '{plannedIn}' ({what}) cannot be admitted as the decision log anchored it{(why.Count == 0 ? string.Empty : ": " + string.Join(" ", why))}; it is left out of the dossier and proves nothing.");
            }
        }

        return new PlannedDecisions(answers, records);
    }

    /// <summary>
    /// Phase 13 (docs/17 D-10, D-33 and D-42; F13-4 and F13-13): what of the run never reaches the judge, quietly, as the prompts
    /// with memory never do (ADR-0022). The judge gets the decisions of the person, and only the choice:
    /// <list type="bullet">
    /// <item>the prompt of every executor (<c>InstructionsIssued</c>): it carries the brief of the project, the instructions of
    /// its agent and the answers of the person in their blocks of data, which guide the agent and say nothing of whether the
    /// requirements are met;</item>
    /// <item>the prompt of the orchestrator's planning, in every attempt (P54): the run that planned has no
    /// <c>InstructionsIssued</c> for it, but its <c>AgentRunStarted</c> names it and says what the launch is for
    /// (<see cref="AgentRunPurpose.Planner"/>), so it is found by that and never by the title of its evidence. It carries the
    /// brief, the memory and, after a question, the questions with their options and the answers of the person, and says
    /// nothing of whether the requirements are met. A start written before the purpose was recorded (version 1) that has no
    /// task is the planner's or the judge's, and neither prompt is evidence of the work: it is withheld too;</item>
    /// <item>the question as the agent wrote it (<c>QuestionAsked</c>, an executor's or the orchestrator's), with the option it
    /// recommended and the effort of each option; the entry itself also stays out of the decisions of the dossier, for it names
    /// the recommended option and the estimates of the system;</item>
    /// <item>every report of an executor that brings a question, whether the question was recorded or not (one beyond the caps,
    /// one that broke its rules): it carries the question as the agent wrote it, its recommendation included. The same rule
    /// for the reports of the orchestrator's planning (the <c>AgentRunFinished</c> with no task whose <c>AgentRunStarted</c>, of
    /// the same attempt, says <see cref="AgentRunPurpose.Planner"/>): the one with <c>questions</c> is withheld, the one that
    /// only has the plan may reach the judge. A report that cannot be read as the log anchored it, or as JSON, is withheld too
    /// (fail closed).</item>
    /// </list>
    /// A payload of these kinds that cannot be read is a system check: what of it the judge must not get cannot be told, so no
    /// approval can rest on the dossier. What the judge gets of a question is its decision: the document the system captured
    /// when it was answered (ids, hashes and instants, no text of the agent; for a question of the orchestrator, with no task),
    /// the <c>QuestionAnswered</c> entry, and the person's own text when there is one.
    /// </summary>
    private async Task<HashSet<EvidenceId>> WithheldFromTheJudgeAsync(
        string projectId,
        IReadOnlyList<DecisionEntry> runEntries,
        PlanDefinition plan,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        var withheld = new HashSet<EvidenceId>();
        var executors = plan.Tasks.Where(t => t.Kind == WorkTaskKind.AgentWork).Select(t => t.Id).ToHashSet();
        var reports = new List<(EvidenceId Report, ReportOf Of)>();

        // What each launch with no task is for, as the latest AgentRunStarted of its attempt said (the entries are in log order,
        // and a launch records its start before its end). AgentRunFinished carries neither the purpose nor the agent, and the
        // judge's own launches have no task either, so an end is told apart by the start of the same attempt that came before.
        var launchedFor = new Dictionary<int, AgentRunPurpose?>();
        foreach (var entry in runEntries)
        {
            try
            {
                switch (entry.Kind)
                {
                    case DecisionKind.InstructionsIssued:
                        withheld.Add(DecisionPayloadSerializer.Deserialize<InstructionsIssuedPayload>(entry.PayloadJson).PromptEvidenceId);
                        break;
                    case DecisionKind.EvidenceRegistered:
                        // The prompt of the planning also by its anchored title: its AgentRunStarted comes after the guard of the user's
                        // repository, so a launch it refused leaves the prompt registered with no start to name it. A payload that
                        // cannot be read is reported by RegisteredEvidence.
                        if (TryReadRegistered(entry) is { Task: null, Kind: EvidenceKind.Document, Origin: EvidenceOrigin.CapturedBySystem } registered && PlannerEvidenceTitles.IsPrompt(registered.Title))
                        {
                            withheld.Add(registered.Evidence);
                        }

                        break;
                    case DecisionKind.AgentRunStarted:
                        var started =DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(entry.PayloadJson);
                        if (started.Task is null)
                        {
                            launchedFor[started.Attempt] = started.Purpose;
                            if (started.Purpose is null or AgentRunPurpose.Planner)
                            {
                                withheld.Add(started.PromptEvidenceId);
                            }
                        }

                        break;
                    case DecisionKind.AgentRunFinished:
                        var finished = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(entry.PayloadJson);
                        if (finished.ReportEvidenceId is { } report)
                        {
                            if (finished.Task is { } task)
                            {
                                if (executors.Contains(task))
                                {
                                    reports.Add((report, ReportOf.Executor));
                                }
                            }
                            else if (launchedFor.GetValueOrDefault(finished.Attempt) is AgentRunPurpose.Planner)
                            {
                                reports.Add((report, ReportOf.Planner));
                            }
                        }

                        break;
                    case DecisionKind.QuestionAsked:
                        withheld.Add(DecisionPayloadSerializer.Deserialize<QuestionAskedPayload>(entry.PayloadJson).QuestionEvidence);
                        break;
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                checks.Add($"Entry {entry.Sequence} ({entry.Kind}): its payload cannot be read, so what of it the judge must not get (a prompt, a question, a report) cannot be told; the dossier cannot support an approval: {ex.Message}");
            }
        }

        foreach (var (report, of) in reports.Distinct())
        {
            switch (await ReadReportAsync(projectId, report, of, RunLog.AnchoredSha256(runEntries, report), cancellationToken).ConfigureAwait(false))
            {
                case ReportRead.WithoutQuestion:
                    break;
                case ReportRead.Unreadable:
                    // Fail closed twice over: its content never reaches the judge, and the check makes an approval impossible.
                    withheld.Add(report);
                    checks.Add($"Evidence '{report}': the report of {(of == ReportOf.Planner ? "the orchestrator's planning" : "an executor")} cannot be read as the decision log anchored it, so it cannot be told whether it brings a question; it is withheld from the judge and proves nothing.");
                    break;
                default:
                    withheld.Add(report);
                    break;
            }
        }

        return withheld;
    }

    /// <summary>Whose report it is: the question it may bring has not the same shape.</summary>
    private enum ReportOf
    {
        /// <summary>An executor's: a <c>question</c> object, or none.</summary>
        Executor,

        /// <summary>The orchestrator's planning (P54): <c>questions</c>, an array that is empty when it plans.</summary>
        Planner,
    }

    /// <summary>What reading the report of an executor or of the orchestrator's planning found.</summary>
    private enum ReportRead
    {
        /// <summary>The content the log anchored, a JSON object without a question: it may reach the judge.</summary>
        WithoutQuestion,

        /// <summary>It brings a question, or it is not a JSON object (the agent's output, not a report): withheld.</summary>
        Withheld,

        /// <summary>It cannot be read as the log anchored it (missing, unreadable, rewritten): withheld, with a system check.</summary>
        Unreadable,
    }

    /// <summary>
    /// Whether the report of an executor, or of the orchestrator's planning, may reach the judge: its content is the one the log
    /// anchored, it is a JSON object, and it brings no question. An executor's brings none when it has no <c>question</c> or it is
    /// <c>null</c>; the planner's, when it has no <c>questions</c> or it is <c>null</c> or an empty array (any other value there
    /// is not a plan). Anything else is withheld (<see cref="ReportRead"/>).
    /// </summary>
    private async Task<ReportRead> ReadReportAsync(string projectId, EvidenceId report, ReportOf of, string? anchoredSha256, CancellationToken cancellationToken)
    {
        if (anchoredSha256 is null)
        {
            return ReportRead.Unreadable;
        }

        try
        {
            byte[] content;
            var stream = await evidence.OpenContentAsync(projectId, report, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                content = buffer.ToArray();
            }

            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), anchoredSha256, StringComparison.OrdinalIgnoreCase))
            {
                return ReportRead.Unreadable;
            }

            using var document = System.Text.Json.JsonDocument.Parse(content);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && BringsNoQuestion(document.RootElement, of)
                ? ReportRead.WithoutQuestion
                : ReportRead.Withheld;
        }
        catch (System.Text.Json.JsonException)
        {
            return ReportRead.Withheld; // the agent's output is not a report: nobody can say it brings no question
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReportRead.Unreadable;
        }
    }

    private static EvidenceRegisteredPayload? TryReadRegistered(DecisionEntry entry)
    {
        try
        {
            return DecisionPayloadSerializer.Deserialize<EvidenceRegisteredPayload>(entry.PayloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool BringsNoQuestion(System.Text.Json.JsonElement report, ReportOf of)
    {
        if (of == ReportOf.Executor)
        {
            return !report.TryGetProperty("question", out var question) || question.ValueKind == System.Text.Json.JsonValueKind.Null;
        }

        return !report.TryGetProperty("questions", out var questions)
            || questions.ValueKind == System.Text.Json.JsonValueKind.Null
            || (questions.ValueKind == System.Text.Json.JsonValueKind.Array && questions.GetArrayLength() == 0);
    }

    /// <summary>
    /// Why the stored header is not the one the run started with, or null when it hashes to what <c>RunStarted</c>
    /// anchored (<see cref="RunHeaderHash"/>, version 2). A version-1 <c>RunStarted</c> anchors no hash, so its header
    /// cannot be checked at all.
    /// </summary>
    private static string? HeaderProblem(RunId run, RunRecord header, RunStartedPayload started)
    {
        if (started.HeaderHash is not { } anchored)
        {
            return string.Create(CultureInfo.InvariantCulture, $"RunStarted of run '{run}' is version {started.V}, which does not anchor the hash of the run header: the header (the roster with the judge, the options, the base commit, the original request and the start of the run) cannot be checked against what was approved.");
        }

        var stored = RunHeaderHash.Of(header);
        return string.Equals(stored, anchored, StringComparison.Ordinal)
            ? null
            : $"The header stored for run '{run}' hashes to {stored}, but RunStarted anchored {anchored}: its roster (the judge included), options, base commit, original request or start changed after the run started, so what the dossier takes from it is not what was approved.";
    }

    /// <summary>
    /// The literal original request, only as the decision log anchored it (R3-08): its bytes must hash to
    /// <paramref name="anchoredSha256"/>, the SHA-256 of its <c>EvidenceRegistered</c> entry, and the text is decoded from
    /// those very bytes. Content the log anchored nothing for, or that hashes to anything else (rewritten together with
    /// the hash of its row, say), is withheld: the judge gets a note in its place and the dossier an integrity problem.
    /// </summary>
    /// <exception cref="InvalidOperationException">The content cannot be read at all.</exception>
    private async Task<string> ReadOriginalRequestAsync(string projectId, EvidenceId id, string? anchoredSha256, List<string> checks, CancellationToken cancellationToken)
    {
        if (anchoredSha256 is null)
        {
            return Withheld(id); // the check that it is not registered evidence of the run comes with the evidence of the run
        }

        byte[] content;
        try
        {
            var stream = await evidence.OpenContentAsync(projectId, id, cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                content = buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"The original request (evidence '{id}') cannot be read: {ex.Message}", ex);
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(actual, anchoredSha256, StringComparison.OrdinalIgnoreCase))
        {
            checks.Add($"The original request (evidence '{id}') hashes to {actual}, not to {anchoredSha256}, the SHA-256 the decision log anchored for it: it was rewritten after it was registered, so it is withheld from the judge and proves nothing.");
            return Withheld(id);
        }

        using var reader = new StreamReader(new MemoryStream(content, writable: false), Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the judge gets in place of an original request whose content the log does not vouch for.</summary>
    private static string Withheld(EvidenceId id) =>
        $"[Withheld by Agent Studio: the content of the original request (evidence '{id}') is not the one the decision log anchored, or the log anchored none. The system checks say why.]";

    private string PackageFolder(string projectId, RunId run, string headHash)
    {
        var root = Path.GetFullPath(options.DossierRoot);
        var folder = Path.GetFullPath(Path.Combine(root, Segment(projectId, 64), run.Value, headHash[..Math.Min(HeadPrefixLength, headHash.Length)]));
        if (!folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The dossier folder '{folder}' would fall outside the dossier root '{root}'.");
        }

        return folder;
    }

    /// <summary>
    /// Copies and checks every evidence first, so the problems found while copying are part of the dossier the validator
    /// checks (<see cref="JudgeDossier.IntegrityProblems"/>), and then writes the request and the dossier itself.
    /// </summary>
    private async Task<JudgeDossierPackage> WritePackageAsync(string folder, string projectId, RunId run, JudgeDossier dossier, List<string> checks, CancellationToken cancellationToken)
    {
        // An agent with a shell runs as the same user and can plant a junction on the way to the package: nothing is
        // deleted nor written through one, so the package can never land on (or wipe) a folder elsewhere.
        RefuseLinks(folder);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true); // Same head, same content: the package is rebuilt, never merged with an old one.
        }

        var evidenceFolder = Path.Combine(folder, "evidence");
        Directory.CreateDirectory(evidenceFolder);
        RefuseLinks(evidenceFolder); // again, now that the folders exist: one could have been swapped meanwhile

        var copies = new List<DossierEvidenceFile>();
        for (var i = 0; i < dossier.Evidence.Count; i++)
        {
            var record = dossier.Evidence[i];
            var name = $"{i + 1:D3}-{Segment(record.Id.Value, 48)}{(record.Kind == EvidenceKind.CodeDiff ? ".diff" : ".txt")}";
            copies.Add(await CopyEvidenceAsync(projectId, record, evidenceFolder, name, checks, cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyList<string> problems = [.. checks];
        var checkedDossier = dossier with { IntegrityProblems = problems };
        var files = new DossierFiles(folder, projectId, run.Value, copies, problems);
        await WriteNewTextAsync(Path.Combine(folder, "request.txt"), checkedDossier.OriginalRequest, cancellationToken).ConfigureAwait(false);
        await WriteNewTextAsync(Path.Combine(folder, "dossier.json"), DossierDocument.ToJson(checkedDossier, files), cancellationToken).ConfigureAwait(false);
        return new JudgeDossierPackage(checkedDossier, files);
    }

    /// <summary>
    /// Refuses a path that goes through a link (a junction or a symbolic link) anywhere from the dossier root, included,
    /// down to <paramref name="path"/>, included. The folders above the root are the user's own and are not checked.
    /// </summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    private void RefuseLinks(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DossierRoot));
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (IsLink(current))
            {
                throw new InvalidOperationException(
                    $"The dossier folder '{path}' goes through '{current.FullName}', which is a link (a junction or a symbolic link): the package is never written nor deleted through one.");
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>A link, even one whose target is gone; a folder that does not exist is not one.</summary>
    private static bool IsLink(DirectoryInfo folder) =>
        folder.LinkTarget is not null || (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint));

    /// <summary>Writes a file that must not exist yet, so a link planted in its place is never followed.</summary>
    private static async Task WriteNewTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(Utf8NoBom.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DossierEvidenceFile> CopyEvidenceAsync(
        string projectId,
        EvidenceRecord record,
        string evidenceFolder,
        string name,
        List<string> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await evidence.OpenContentAsync(projectId, record.Id, cancellationToken).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var (size, written, sha256) = await CopyAsync(source, Path.Combine(evidenceFolder, name), options.MaxEvidenceFileBytes, cancellationToken).ConfigureAwait(false);
                var verified = string.Equals(sha256, record.ContentSha256, StringComparison.Ordinal);
                if (!verified)
                {
                    checks.Add($"Evidence '{record.Id}': its content does not match the SHA-256 recorded at capture; it changed afterwards and proves nothing.");
                }

                return new DossierEvidenceFile(record.Id, "evidence/" + name, verified, written < size, size);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add($"Evidence '{record.Id}': its content cannot be read ({ex.Message}); it proves nothing.");
            return new DossierEvidenceFile(record.Id, null, false, false, 0);
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes, but hashes the whole content.</summary>
    private static async Task<(long Size, long Written, string Sha256)> CopyAsync(Stream source, string path, long limit, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long size = 0;
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                var room = (int)Math.Min(read, limit - written);
                if (room > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, room), cancellationToken).ConfigureAwait(false);
                    written += room;
                }

                size += read;
            }

            return (size, written, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    /// <summary>A safe folder or file name part: <c>[A-Za-z0-9_-]</c>, at most <paramref name="maxLength"/> characters.</summary>
    private static string Segment(string text, int maxLength)
    {
        var safe = new string([.. text.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_')]);
        safe = safe.Length > maxLength ? safe[..maxLength] : safe;
        return safe.Length == 0 ? "_" : safe;
    }
}
