using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Planning;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// The questions of a run rebuilt from its log after a restart (docs/17 D-29): what is lived is what is replayed. Every
/// question its executors asked is read back from its evidence, only as <c>QuestionAsked</c> anchored it; every answer, with
/// the person's own text read back the same way; and the ones still waiting are the ones replay says are pending
/// (<see cref="RunReplay.PendingQuestions"/>), with the instant the log recorded them, so the consent of the run counts from
/// the same moment. A question or an answer that cannot be read as anchored leaves the run not resumable (fail closed): it
/// would reach the window and the prompt of the agent as something nobody asked or answered. The questions the orchestrator
/// asked before it planned (no task) and their answers are read back the same way, and go to the executor of every task
/// (V43): a run that starts after those questions (<see cref="LoadOrchestratorAsync"/>) and one that resumes keep them.
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
        var asked = await ReadQuestionsAsync(recorder, entries, book, orchestratorOnly: false, cancellationToken).ConfigureAwait(false);
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
    /// The answers to the questions the orchestrator asked before it planned (V43), read from the log of a run that is about to
    /// start: the planning that asked and the one that went on with the answers recorded them in the same run. Reads only.
    /// </summary>
    /// <exception cref="InvalidOperationException">A question or an answer cannot be read back as the log anchored it.</exception>
    public static Task LoadOrchestratorAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        QuestionBook book,
        CancellationToken cancellationToken) =>
        ReadQuestionsAsync(recorder, entries, book, orchestratorOnly: true, cancellationToken);

    private static async Task<Dictionary<string, AskedQuestion>> ReadQuestionsAsync(
        RunRecorder recorder,
        IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)> entries,
        QuestionBook book,
        bool orchestratorOnly,
        CancellationToken cancellationToken)
    {
        var asked = new Dictionary<string, AskedQuestion>(StringComparer.Ordinal);
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case QuestionAskedPayload { Task: null } p:
                    // The orchestrator's question is the evidence of PlannerQuestions, not the document of an executor's. It counts in no cap.
                    var planned = await ReadAsync(recorder, p.QuestionEvidence, p.QuestionSha256, $"question '{p.Question}' of the orchestrator", cancellationToken).ConfigureAwait(false);
                    var plannedQuestion = PlannerQuestions.ReadEvidence(planned)
                        ?? throw new InvalidOperationException($"The evidence {p.QuestionEvidence} of question '{p.Question}' of the orchestrator is not a question.");
                    asked[p.Question] = new AskedQuestion(p.Question, null, p.Agent, p.Attempt, plannedQuestion, p.QuestionEvidence, p.QuestionSha256, p.Estimates, entry.RecordedAt);
                    break;
                case QuestionAnsweredPayload { Task: null } p:
                    if (!asked.TryGetValue(p.Question, out var orchestratorAsked) || orchestratorAsked.Task is not null)
                    {
                        throw new InvalidOperationException($"Question '{p.Question}' was answered with no task, but the log never recorded it as asked by the orchestrator.");
                    }

                    string? orchestratorOwn = null;
                    if (p.AnswerEvidence is { } orchestratorEvidence)
                    {
                        orchestratorOwn = await ReadAsync(recorder, orchestratorEvidence, p.AnswerSha256!, $"the answer to question '{p.Question}' of the orchestrator", cancellationToken).ConfigureAwait(false);
                    }

                    book.AnswerOrchestrator(new RecordedAnswer(orchestratorAsked, p.ChosenOption, orchestratorOwn, p.AnswerEvidence, p.AnswerSha256, p.DecidedBy, p.Method, entry.RecordedAt));
                    break;
                case QuestionAskedPayload { Task: { } task } p when !orchestratorOnly:
                    var text = await ReadAsync(recorder, p.QuestionEvidence, p.QuestionSha256, $"question '{p.Question}' of task '{task}'", cancellationToken).ConfigureAwait(false);
                    var question = QuestionDocuments.ReadQuestion(text, p.Question)
                        ?? throw new InvalidOperationException($"The evidence {p.QuestionEvidence} of question '{p.Question}' of task '{task}' is not that question.");
                    asked[p.Question] = new AskedQuestion(p.Question, task, p.Agent, p.Attempt, question, p.QuestionEvidence, p.QuestionSha256, p.Estimates, entry.RecordedAt);
                    book.Ask(asked[p.Question], pending: false);
                    break;
                case QuestionAnsweredPayload { Task: { } task } p when !orchestratorOnly:
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

        return asked;
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
