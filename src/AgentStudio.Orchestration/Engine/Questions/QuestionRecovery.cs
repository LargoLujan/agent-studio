using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// The questions of a run rebuilt from its log after a restart (docs/17 D-29): what is lived is what is replayed. Every
/// question its executors asked is read back from its evidence, only as <c>QuestionAsked</c> anchored it; every answer, with
/// the person's own text read back the same way; and the ones still waiting are the ones replay says are pending
/// (<see cref="RunReplay.PendingQuestions"/>), with the instant the log recorded them, so the consent of the run counts from
/// the same moment. A question or an answer that cannot be read as anchored leaves the run not resumable (fail closed): it
/// would reach the window and the prompt of the agent as something nobody asked or answered. The answers to the questions the
/// orchestrator asked before it planned (V43) are read the same way, with <see cref="LoadOrchestratorAsync"/>: from the run that
/// planned, which is this one or the one <c>RunStarted</c> names as <see cref="RunStartedPayload.PlannedIn"/>.
/// </summary>
internal static class QuestionRecovery
{
    /// <exception cref="InvalidOperationException">A question or an answer cannot be read back as the log anchored it.</exception>
    public static async Task LoadAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        RunReplay replay,
        QuestionBook book,
        CancellationToken cancellationToken)
    {
        var asked = new Dictionary<string, AskedQuestion>(StringComparer.Ordinal);
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case QuestionAskedPayload { Task: { } task } p:
                    var text = await ReadAsync(recorder, p.QuestionEvidence, p.QuestionSha256, $"question '{p.Question}' of task '{task}'", cancellationToken).ConfigureAwait(false);
                    var question = QuestionDocuments.ReadQuestion(text, p.Question)
                        ?? throw new InvalidOperationException($"The evidence {p.QuestionEvidence} of question '{p.Question}' of task '{task}' is not that question.");
                    asked[p.Question] = new AskedQuestion(p.Question, task, p.Agent, p.Attempt, question, p.QuestionEvidence, p.QuestionSha256, p.Estimates, entry.RecordedAt);
                    book.Ask(asked[p.Question], pending: false);
                    break;
                case QuestionAnsweredPayload { Task: { } task } p:
                    if (!asked.TryGetValue(p.Question, out var of) || of.Task != task)
                    {
                        throw new InvalidOperationException($"Question '{p.Question}' of task '{task}' was answered, but the log never recorded it as asked.");
                    }

                    string? own = null;
                    if (p.AnswerEvidence is { } evidence)
                    {
                        own = await ReadAsync(recorder, evidence, p.AnswerSha256!, $"the answer to question '{p.Question}' of task '{task}'", cancellationToken).ConfigureAwait(false);
                    }

                    book.Answer(new RecordedAnswer(of, p.ChosenOption, own, p.AnswerEvidence, p.AnswerSha256, p.DecidedBy, p.Method, entry.RecordedAt));
                    break;
            }
        }

        foreach (var (task, question) in replay.PendingQuestions)
        {
            if (!asked.TryGetValue(question, out var waiting) || waiting.Task != task)
            {
                throw new InvalidOperationException($"Task '{task}' waits for question '{question}', which the log never recorded as asked by its agent.");
            }

            book.Wait(waiting);
        }
    }

    /// <summary>
    /// The answers to the questions the orchestrator asked before it planned (V43), read from the verified log for a run that
    /// starts or resumes: every executor gets them as data. They are in the entries of the run in which the orchestrator planned
    /// (<paramref name="plannedIn"/>), or in the run's own (<paramref name="entries"/>) when it is null: the questions and the
    /// answers of that planning, no task in either. Only the questions that were answered are read, each answer once (the first
    /// one the log recorded); a question with no answer is of no use, and its evidence is not touched. They are not the
    /// executors' questions, so they count in no cap. Reads only.
    /// </summary>
    /// <param name="log">The whole verified log of the project, to find <paramref name="plannedIn"/> in.</param>
    /// <param name="plannedIn">
    /// The run the orchestrator planned in when it is not this one, as <c>RunStarted</c> or the request says (text, so a value of a
    /// log that is not a run id fails like any other run the log does not have); null for the run's own entries.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="plannedIn"/> is not a run of this log in which the orchestrator proposed a plan (<c>PlanCreated</c> by the
    /// orchestrator), or a question or an answer cannot be read back as the log anchored it.
    /// </exception>
    public static async Task LoadOrchestratorAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        IReadOnlyList<DecisionEntry> log,
        string? plannedIn,
        QuestionBook book,
        CancellationToken cancellationToken)
    {
        var planning = entries;
        if (plannedIn is not null)
        {
            // Another run's entries are trusted for what the orchestrator asked and was answered only if that run is one in which it
            // planned: a wrong id, or the run of a plan written by hand, would hand the executors answers to nothing they were asked.
            planning = RunLog.Of(log, plannedIn);
            if (planning.Count == 0)
            {
                throw new InvalidOperationException($"Run {recorder.Run} says the orchestrator planned in run '{plannedIn}', which the log has no entry of.");
            }

            if (!planning.Any(e => e.Payload is PlanCreatedPayload { Source: PlanSource.OrchestratorAgent }))
            {
                throw new InvalidOperationException($"Run {recorder.Run} says the orchestrator planned in run '{plannedIn}', but the log has no plan the orchestrator proposed in it.");
            }
        }

        var asked = new Dictionary<string, (DecisionEntry Entry, QuestionAskedPayload Payload)>(StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, payload) in planning)
        {
            switch (payload)
            {
                case QuestionAskedPayload { Task: null } p:
                    asked.TryAdd(p.Question, (entry, p));
                    break;
                case QuestionAnsweredPayload { Task: null } p:
                    if (!answered.Add(p.Question))
                    {
                        break; // answered already: the first answer the log recorded is the one that counts
                    }

                    if (!asked.TryGetValue(p.Question, out var question))
                    {
                        throw new InvalidOperationException($"Question '{p.Question}' of the orchestrator was answered, but the log never recorded it as asked.");
                    }

                    var (askedEntry, asking) = question;
                    var json = await ReadAsync(recorder, asking.QuestionEvidence, asking.QuestionSha256, $"question '{p.Question}' of the orchestrator", cancellationToken).ConfigureAwait(false);
                    var read = QuestionDocuments.ReadPlannerQuestion(json)
                        ?? throw new InvalidOperationException($"The evidence {asking.QuestionEvidence} of question '{p.Question}' of the orchestrator is not a question.");
                    string? own = null;
                    if (p.AnswerEvidence is { } evidence)
                    {
                        own = await ReadAsync(recorder, evidence, p.AnswerSha256!, $"the answer to question '{p.Question}' of the orchestrator", cancellationToken).ConfigureAwait(false);
                    }

                    var recorded = new AskedQuestion(p.Question, null, asking.Agent, asking.Attempt, read, asking.QuestionEvidence, asking.QuestionSha256, asking.Estimates, askedEntry.RecordedAt);
                    book.AnswerOrchestrator(new RecordedAnswer(recorded, p.ChosenOption, own, p.AnswerEvidence, p.AnswerSha256, p.DecidedBy, p.Method, entry.RecordedAt));
                    break;
            }
        }
    }

    /// <summary>
    /// The decisions for the judge that an answer recorded just before the app stopped did not capture yet, captured now (after
    /// <c>RunResumed</c>, like the rest of the recovery), so every answered question has one.
    /// </summary>
    public static async Task CaptureMissingDecisionsAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        QuestionBook book,
        IEnumerable<TaskId> tasks,
        CancellationToken cancellationToken)
    {
        var captured = entries
            .Select(e => e.Payload)
            .OfType<EvidenceRegisteredPayload>()
            .Select(p => p.Title)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var answered in tasks.SelectMany(book.AnsweredOf))
        {
            if (!captured.Contains(QuestionDocuments.DecisionTitle(answered.Asked.Id, answered.Asked.AskingTask.Value)))
            {
                await OrchestrationRun.CaptureDecisionAsync(recorder, answered, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadAsync(RunRecorder recorder, EvidenceId evidence, string sha256, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await recorder.ReadTextAsync(evidence, sha256, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException($"The evidence {evidence} of {what} cannot be used as the decision log anchored it: {ex.Message}", ex);
        }
    }
}
