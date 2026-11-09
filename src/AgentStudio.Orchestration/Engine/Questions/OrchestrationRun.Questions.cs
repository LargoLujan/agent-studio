using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine.Questions;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// Phase 13 (docs/17 D-28 to D-33): the questions of the executors in the loop. Only the loop records them and moves their
/// tasks, as it does every transition. In the log, always in this order:
/// <list type="number">
/// <item>asking: the question as evidence (redacted), <c>QuestionAsked</c> while the attempt that asked is still
/// <c>Running</c>, then <c>Running → Blocked(AwaitingAnswer)</c>, and the monitor hears <see cref="QuestionPendingEvent"/>;</item>
/// <item>answering: the person's own text as evidence (when it is not an option), <c>QuestionAnswered</c> (the person's, or
/// the system's under the consent of the run), the decision for the judge as evidence, then
/// <c>Blocked(AwaitingAnswer) → Pending</c>, and <see cref="QuestionAnsweredEvent"/>. The next attempt counts in
/// <c>MaxAgentRuns</c> and gets the answer as data.</item>
/// </list>
/// Replay (<see cref="Domain.Orchestration.RunStateReplayer"/>) checks this order; a resume rebuilds the rest
/// (<see cref="QuestionRecovery"/>).
/// </summary>
internal sealed partial class OrchestrationRun
{
    /// <summary>The questions of the executors waiting for their answer, for the monitor once the loop starts (a resumed run asks them again).</summary>
    private void AnnouncePendingQuestions()
    {
        foreach (var asked in _ledger.Questions.Pending.Values.OrderBy(q => q.AskedAt).ThenBy(q => q.Id, StringComparer.Ordinal))
        {
            Emit(new QuestionPendingEvent(Now, asked.ToPending()));
        }
    }

    /// <summary>
    /// The agent of <paramref name="id"/> ended blocked with a valid question (D-28 and D-29): within the caps it is recorded and
    /// the task waits for the answer; beyond them the task waits for a person with «demasiadas preguntas: revisa los
    /// requisitos», and nothing of the question is shown.
    /// </summary>
    private async Task AskAsync(TaskId id, int attempt, AttemptEnd end)
    {
        var book = _ledger.Questions;
        if (book.Capped(id))
        {
            var cap = book.AskedBy(id) >= QuestionRules.MaxQuestionsPerTask
                ? string.Create(CultureInfo.InvariantCulture, $"las {QuestionRules.MaxQuestionsPerTask} que admite una tarea")
                : string.Create(CultureInfo.InvariantCulture, $"las {QuestionRules.MaxQuestionsPerRun} que admite una ejecución");
            await MoveAsync(id, WorkTaskStatus.Blocked, $"El agente de {id} quiso hacer una pregunta más de {cap}: demasiadas preguntas, revisa los requisitos.", end.Evidence, BlockReason.AwaitingHuman).ConfigureAwait(false);
            return;
        }

        // The secrets out (D-28), and the rules once more on what is shown: a placeholder may make a text too long.
        var question = QuestionDocuments.Redacted(end.Question!, _ports.Redactor);
        if (QuestionRules.Validate(question) is { Count: > 0 } broken)
        {
            await FailAsync(id, attempt, $"El informe del agente no es válido: su pregunta deja de cumplir las reglas al quitarle los secretos ({string.Join(", ", broken)}).", end.Evidence).ConfigureAwait(false);
            return;
        }

        var agent = _ctx.Specs[id].Agent!.Value;
        var questionId = book.NextId(id);
        var evidence = await _recorder.CaptureAsync(
            EvidenceKind.AgentStatement,
            EvidenceOrigin.ReportedByAgent,
            QuestionDocuments.QuestionTitle(questionId, id.Value, attempt),
            agent.Value,
            QuestionDocuments.QuestionJson(questionId, id.Value, attempt, agent.Value, question),
            QuestionDocuments.MediaType,
            id,
            attempt,
            Token).ConfigureAwait(false);
        var entry = await _recorder.RecordAsync(
            DecisionKind.QuestionAsked,
            $"El agente de {id} hace la pregunta {questionId} (intento {attempt})",
            new QuestionAskedPayload(id, attempt, agent, questionId, evidence.Id, evidence.ContentSha256, [.. question.Options.Select(o => o.Id)], question.RecommendedOption?.Id, end.QuestionEstimates) { RunId = _recorder.RunValue },
            Token).ConfigureAwait(false);
        var asked = new AskedQuestion(questionId, id, agent, attempt, question, evidence.Id, evidence.ContentSha256, end.QuestionEstimates, entry.RecordedAt);
        book.Ask(asked);

        // The attempt with the answer renews the session only for a question the log recorded, as a resume rebuilds it from
        // QuestionAsked: one beyond the caps leaves the session as any other block does.
        if (end.QuestionRenew is { } renew)
        {
            _ledger.Renew[id] = renew;
        }

        await MoveAsync(id, WorkTaskStatus.Blocked, $"Espera tu respuesta: el agente hace la pregunta {questionId}.", [.. end.Evidence, evidence.Id], BlockReason.AwaitingAnswer).ConfigureAwait(false);
        Emit(new QuestionPendingEvent(Now, asked.ToPending()));
    }

    private async Task OnAnswerAsync(AnswerSignal signal)
    {
        string? refusal;
        try
        {
            refusal = await AnswerQuestionAsync(signal.Answer, byConsent: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The caller must never wait forever: it learns why, and the loop handles the failure (the log is the truth).
            signal.Done.TrySetException(ex);
            throw;
        }

        if (refusal is null)
        {
            signal.Done.TrySetResult();
        }
        else
        {
            signal.Done.TrySetException(new InvalidOperationException(refusal));
        }
    }

    /// <summary>
    /// Records the answer to the question a task waits for (D-29 and D-30) and puts the task back to pending; returns why it is
    /// refused, or null. Only the system answers with the recommended option, and only under the consent of the run.
    /// </summary>
    private async Task<string?> AnswerQuestionAsync(QuestionAnswer answer, bool byConsent)
    {
        TaskId? of = answer.Task;
        if (of is not { } id)
        {
            return "The engine answers only the questions its executors ask while it runs; the orchestrator's are answered before it plans.";
        }

        if (!_ctx.Specs.ContainsKey(id))
        {
            return $"Task '{id}' is not in run {Id}.";
        }

        if (_cancelling)
        {
            return $"Run {Id} is being cancelled: its questions are no longer answered.";
        }

        var book = _ledger.Questions;
        if (_graph[id].Status != WorkTaskStatus.Blocked || _ledger.Blocks.GetValueOrDefault(id)?.Kind != BlockKind.AwaitingAnswer
            || !book.Pending.TryGetValue(id, out var pending))
        {
            return $"Task '{id}' is not waiting for the answer to a question.";
        }

        if (!string.Equals(pending.Id, answer.QuestionId, StringComparison.Ordinal))
        {
            return $"Task '{id}' waits for the answer to question '{pending.Id}', not to '{answer.QuestionId}'.";
        }

        if (answer.Method == QuestionAnswerMethod.RecommendedByConsent && !byConsent)
        {
            return "Only the system answers with the option the agent recommended, under the consent of the run.";
        }

        if ((answer.OptionId is null) == (answer.OtherText is null))
        {
            return "An answer is one of the options or the person's own text, exactly one of the two.";
        }

        if (answer.OptionId is { } option && pending.Question.Option(option) is null)
        {
            return $"Question '{pending.Id}' has no option '{option}'.";
        }

        if (answer.OtherText is { } other && (!pending.Question.AllowOther || string.IsNullOrWhiteSpace(other) || other.Length > QuestionAnswer.MaxOtherTextLength))
        {
            return pending.Question.AllowOther
                ? string.Create(CultureInfo.InvariantCulture, $"An answer in the person's own words has from 1 to {QuestionAnswer.MaxOtherTextLength} characters.")
                : $"Question '{pending.Id}' takes one of its options only.";
        }

        // The person's own text is evidence they provided (D-29), with the secrets out; an option is only its id.
        EvidenceRecord? own = null;
        string? ownText = null;
        if (answer.OtherText is { } text)
        {
            ownText = _ports.Redactor?.Redact(text) ?? text;
            own = await _recorder.CaptureAsync(
                EvidenceKind.HumanNote,
                EvidenceOrigin.ProvidedByHuman,
                QuestionDocuments.OwnAnswerTitle(pending.Id, id.Value),
                answer.DecidedBy,
                ownText,
                "text/plain",
                id,
                pending.Attempt,
                Token).ConfigureAwait(false);
        }

        var actor = byConsent ? RunRecorder.System : new Actor(ActorKind.User, answer.DecidedBy);
        var answeredEntry = await _recorder.RecordAsync(
            actor,
            DecisionKind.QuestionAnswered,
            byConsent ? $"Agent Studio responde {pending.Id} de {id} con la opción recomendada, por el consentimiento de la ejecución" : $"{answer.DecidedBy} responde {pending.Id} de {id}",
            QuestionAnsweredPayload.Answering(_recorder.RunValue, id, pending.Id, answer.OptionId, own?.Id, own?.ContentSha256, answer.DecidedBy, answer.Method),
            Token).ConfigureAwait(false);
        var answered = new RecordedAnswer(pending, answer.OptionId, ownText, own?.Id, own?.ContentSha256, answer.DecidedBy, answer.Method, answeredEntry.RecordedAt);
        book.Answer(answered);
        var decision = await CaptureDecisionAsync(_recorder, answered, Token).ConfigureAwait(false);
        var how = answer.OptionId is { } chosen
            ? byConsent ? $"la opción recomendada {chosen}, por el consentimiento de la ejecución" : $"la opción {chosen}"
            : "una respuesta con sus palabras";
        await MoveAsync(id, WorkTaskStatus.Pending, $"Respondida la pregunta {pending.Id} con {how}: sigue con un intento nuevo.", [decision.Id, .. own is null ? Array.Empty<EvidenceId>() : [own.Id]], actor: actor).ConfigureAwait(false);
        Emit(new QuestionAnsweredEvent(Now, pending.Id, id, answer.Method));
        return null;
    }

    /// <summary>The decision of the person about a question, as evidence the system captures: what the judge gets of it (D-33).</summary>
    internal static Task<EvidenceRecord> CaptureDecisionAsync(RunRecorder recorder, RecordedAnswer answered, CancellationToken cancellationToken) =>
        recorder.CaptureAsync(
            EvidenceKind.Document,
            EvidenceOrigin.CapturedBySystem,
            QuestionDocuments.DecisionTitle(answered.Asked.Id, answered.Asked.Task.Value),
            RunRecorder.ProducedBySystem,
            QuestionDocuments.DecisionJson(answered),
            QuestionDocuments.MediaType,
            answered.Asked.Task,
            answered.Asked.Attempt,
            cancellationToken);

    /// <summary>
    /// The consent of the run (D-33, decision 4 (a)): a question with a recommended option that nobody answered within
    /// <see cref="RunOptions.AnswerWithRecommendedAfter"/> is answered by the system with that option. Never without the
    /// consent, never for a question without a recommended option, and not while the run is cancelling or stopped for its
    /// integrity.
    /// </summary>
    private async Task<bool> AnswerByConsentAsync()
    {
        if (_ctx.Options.AnswerWithRecommendedAfter is not { } wait || _cancelling || _integrityBreached)
        {
            return false;
        }

        var changed = false;
        var due = _ledger.Questions.Pending.Values
            .Where(q => q.Question.RecommendedOption is not null && q.AskedAt + wait <= Now)
            .OrderBy(q => q.AskedAt)
            .ThenBy(q => q.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var asked in due)
        {
            var answer = QuestionAnswer.RecommendedByConsent(asked.Task, asked.Id, asked.Question.RecommendedOption!.Id);
            changed |= await AnswerQuestionAsync(answer, byConsent: true).ConfigureAwait(false) is null;
        }

        return changed;
    }

    /// <summary>When the consent of the run next answers a question by itself, or null.</summary>
    private DateTimeOffset? ConsentWake() =>
        _ctx.Options.AnswerWithRecommendedAfter is { } wait
            ? _ledger.Questions.Pending.Values
                .Where(q => q.Question.RecommendedOption is not null)
                .Select(q => (DateTimeOffset?)(q.AskedAt + wait))
                .Min()
            : null;
}
