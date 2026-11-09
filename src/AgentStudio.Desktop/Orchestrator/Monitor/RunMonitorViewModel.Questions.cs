using System.Collections.ObjectModel;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Domain.Questions;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

// Phase 13 (docs/17 D-29 to D-31, F13-14, WP8): the questions of the agents of the run. The monitor hears them as the only reader of
// the events of its run (ADR-0036): each one is a card here and, through QuestionRaised, a window that opens by itself.

public sealed partial class RunMonitorViewModel
{
    /// <summary>Who answers a question from the monitor: the person (the registry says <c>user</c>, docs/17 D-29).</summary>
    public const string QuestionDecidedBy = "user";

    /// <summary>The questions of the agents that wait for the answer of the person, oldest first: one card each.</summary>
    public ObservableCollection<QuestionViewModel> PendingQuestions { get; } = [];

    /// <summary>
    /// An agent asked something, on the UI context: the window of the question opens if the app is in view, and otherwise the tray
    /// says «Un agente te pregunta algo», with no data of the project (docs/17 D-31). It is raised once per question, also for a
    /// question that was waiting when the run was attached or resumed.
    /// </summary>
    public event EventHandler<QuestionViewModel>? QuestionRaised;

    /// <summary>The question was answered (by the person, here or elsewhere, or by the consent of the run) or its task ended: its card and window go.</summary>
    public event EventHandler<QuestionViewModel>? QuestionResolved;

    /// <summary>The person asked to see the window of a question again («Responder» on its card).</summary>
    public event EventHandler<QuestionViewModel>? QuestionOpenRequested;

    /// <summary>«Responder» on the card of a question: asks for its window again.</summary>
    [RelayCommand]
    private void OpenQuestion(QuestionViewModel? question)
    {
        if (question is not null && PendingQuestions.Contains(question))
        {
            QuestionOpenRequested?.Invoke(this, question);
        }
    }

    /// <summary>«Responder» on the line of a task that waits for the answer: asks for the window of its question.</summary>
    [RelayCommand]
    private void OpenQuestionOf(MonitorTaskViewModel? task)
    {
        if (task?.Question is { } pending && PendingQuestions.FirstOrDefault(q => q.Id == pending.Id) is { } card)
        {
            QuestionOpenRequested?.Invoke(this, card);
        }
    }

    /// <summary>Sends the answer to the run; the run records it and the task goes on with it as data (it spends no quota by itself, but its next attempt counts).</summary>
    private Task AnswerQuestionAsync(QuestionAnswer answer)
    {
        if (_run is not { } run || IsInspecting)
        {
            throw new InvalidOperationException("The run is not followed live: it takes no answers.");
        }

        return run.AnswerAsync(answer);
    }

    private void AddQuestion(PendingQuestion question, bool announce)
    {
        if (PendingQuestions.Any(q => q.Id == question.Id))
        {
            return;
        }

        var card = new QuestionViewModel(question, AnswerQuestionAsync, QuestionDecidedBy);
        PendingQuestions.Add(card);
        OnPropertyChanged(nameof(HasPendingQuestions));
        if (announce)
        {
            QuestionRaised?.Invoke(this, card);
        }
    }

    /// <summary>At least one question waits for the person.</summary>
    public bool HasPendingQuestions => PendingQuestions.Count > 0;

    private void OnQuestionPending(QuestionPendingEvent asked)
    {
        if (IsInspecting)
        {
            return;
        }

        var question = asked.Question;
        if (question.Task is { } id)
        {
            var task = TaskFor(id);
            task.Question = question;
            Log(asked.At, id, Texts.Format("Monitor_Question_Log", question.Agent.Value));
            MarkLive(task);
        }
        else
        {
            Log(asked.At, null, Texts.Format("Monitor_Question_LogPlanner", question.Agent.Value));
        }

        AddQuestion(question, announce: true);
    }

    private void OnQuestionAnswered(QuestionAnsweredEvent answered)
    {
        if (answered.Task is { } id)
        {
            var task = TaskFor(id);
            task.Question = null;
            Log(answered.At, id, Texts.Get(answered.Method == QuestionAnswerMethod.RecommendedByConsent ? "Monitor_Question_LogAnsweredByConsent" : "Monitor_Question_LogAnswered"));
            MarkLive(task);
        }

        ResolveQuestion(answered.Question);
    }

    /// <summary>The question with this id is no longer waiting: its card goes and whoever shows its window closes it.</summary>
    private void ResolveQuestion(string questionId)
    {
        if (PendingQuestions.FirstOrDefault(q => q.Id == questionId) is not { } card)
        {
            return;
        }

        card.MarkResolved();
        PendingQuestions.Remove(card);
        OnPropertyChanged(nameof(HasPendingQuestions));
        QuestionResolved?.Invoke(this, card);
    }

    private void ResetQuestions()
    {
        var left = PendingQuestions.ToList();
        PendingQuestions.Clear();
        OnPropertyChanged(nameof(HasPendingQuestions));
        foreach (var card in left)
        {
            card.MarkResolved();
            QuestionResolved?.Invoke(this, card);
        }
    }
}
