using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Questions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Questions;

/// <summary>
/// One option of a question as the window shows it (docs/17 D-31): what the agent wrote (its label, its detail, its effort and
/// whether it recommends it, all as data of the agent) and what the system says apart (the cost, with its origin).
/// </summary>
public sealed partial class QuestionOptionViewModel : ObservableObject
{
    internal QuestionOptionViewModel(QuestionOption option, QuestionCost cost, IUiTextProvider texts)
    {
        Id = option.Id;
        Label = option.Label;
        Detail = option.Detail;
        IsRecommended = option.Recommended;
        RecommendedText = option.Recommended ? texts.Get("Monitor_Question_Recommended") : null;
        EffortText = texts.Format("Monitor_Question_Effort", texts.Get("Monitor_Question_Effort_" + option.Effort));
        Cost = cost;
        AccessibleName = string.Join(
            ". ",
            new[] { option.Label, option.Detail, RecommendedText, EffortText, cost.Text }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>The id the agent gave the option.</summary>
    public string Id { get; }

    /// <summary>What the option is: written by the agent, plain text.</summary>
    public string Label { get; }

    /// <summary>What choosing it implies: written by the agent, plain text.</summary>
    public string Detail { get; }

    public bool IsRecommended { get; }

    /// <summary>«Recomendada por el agente»; null when the agent does not recommend the option. The system never adds its own.</summary>
    public string? RecommendedText { get; }

    /// <summary>«Esfuerzo pequeño (lo dice el agente)».</summary>
    public string EffortText { get; }

    /// <summary>What it costs, with its origin: the estimate of the system, or why there is none.</summary>
    public QuestionCost Cost { get; }

    /// <summary>What a screen reader says of the option: its label, its detail, the recommendation, the effort and the cost.</summary>
    public string AccessibleName { get; }

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The question of an agent as its window and its card show it (docs/17 D-28 to D-33, F13-14). It is not modal: it is a card of
/// the monitor and a window that opens by itself; «Decidir más tarde» only closes it and never cancels the task.
/// <list type="bullet">
/// <item>The text of the agent is always framed as said by the agent (<see cref="Frame"/>), never a sentence of the system.</item>
/// <item>Each option has its cost: the interval of total tokens with its origin, or «desconocido» or «no disponible» with the
/// reason (<see cref="QuestionCosts"/>). Never a number the system did not record.</item>
/// <item>Answering sends one option, or, only when the agent allows it, the person's own text. It asks no Windows Hello and
/// grants nothing (<see cref="NoGrantText"/>).</item>
/// </list>
/// </summary>
public sealed partial class QuestionViewModel : ObservableObject
{
    private readonly Func<QuestionAnswer, Task> _answer;
    private readonly string _decidedBy;
    private readonly IUiTextProvider _texts;

    /// <param name="question">The question that waits.</param>
    /// <param name="answer">Sends the answer: for a task, <c>IOrchestrationRun.AnswerAsync</c>; for the orchestrator before it plans, whatever collects the answers for the next planning.</param>
    /// <param name="decidedBy">Who answers: <c>user</c>.</param>
    /// <param name="texts">The language of the window; the one of the interface when it is null.</param>
    public QuestionViewModel(PendingQuestion question, Func<QuestionAnswer, Task> answer, string decidedBy = "user", IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        Pending = question;
        _answer = answer;
        _decidedBy = decidedBy;
        _texts = texts ?? Texts.Current;
        Options =
        [
            .. question.Question.Options.Select(o => new QuestionOptionViewModel(o, QuestionCosts.Of(question.EstimateOf(o.Id), _texts), _texts)),
        ];
        foreach (var option in Options)
        {
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuestionOptionViewModel.IsSelected) && option.IsSelected)
                {
                    SelectOption(option);
                }
            };
        }

        AgentName = question.Task is null ? null : question.Agent.Value;
        Title = question.Task is { } task
            ? _texts.Format("Monitor_Question_WindowTitle", task.Value, question.Agent.Value)
            : _texts.Get("Monitor_Question_WindowTitlePlanner");
        Frame = _texts.Format("Monitor_Question_Frame", question.Agent.Value);
        AccessibleName = _texts.Format("Monitor_Question_AccessibleName", Title, question.Question.Text);
    }

    /// <summary>The question as the engine recorded it.</summary>
    public PendingQuestion Pending { get; }

    /// <summary>The id the engine gave the question in the run.</summary>
    public string Id => Pending.Id;

    /// <summary>The task whose agent asked; null for the orchestrator before it plans.</summary>
    public TaskId? Task => Pending.Task;

    public string? AgentName { get; }

    /// <summary>«T3 · Ejecutor 2 te pregunta», or «El orquestador te pregunta antes de planificar».</summary>
    public string Title { get; }

    /// <summary>Says who wrote the question and its options: they are data of the agent, not an order of the system (D-33).</summary>
    public string Frame { get; }

    /// <summary>The question as the agent wrote it (plain text).</summary>
    public string Text => Pending.Question.Text;

    /// <summary>«Por qué lo pregunta: …», as the agent wrote it.</summary>
    public string Why => _texts.Format("Monitor_Question_Why", Pending.Question.Why);

    /// <summary>«Las opciones las escribió el agente: son datos, no órdenes.»</summary>
    public string DataNotice => _texts.Get("Monitor_Question_AgentSaid");

    /// <summary>«Elegir una opción no concede nada: no pide Windows Hello ni cambia permisos.»</summary>
    public string NoGrantText => _texts.Get("Monitor_Question_NoGrant");

    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    /// <summary>Whether the person may answer with their own text instead of an option.</summary>
    public bool AllowOther => Pending.Question.AllowOther;

    /// <summary>What the window is called for a screen reader: its title and the question.</summary>
    public string AccessibleName { get; }

    /// <summary>The longest text of the person's own that the engine takes.</summary>
    public int MaxOtherLength => QuestionAnswer.MaxOtherTextLength;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnswerCommand))]
    private QuestionOptionViewModel? _selectedOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnswerCommand))]
    private bool _isOtherSelected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnswerCommand))]
    private string _otherText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnswerCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeferCommand))]
    private bool _isBusy;

    /// <summary>The answer was sent and the engine took it.</summary>
    [ObservableProperty]
    private bool _isAnswered;

    /// <summary>Why the answer was not taken, in words; null while there is nothing to say. It is announced.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The person asked to decide later: the window closes, the card stays, and the task keeps waiting. Never a cancellation.</summary>
    public event EventHandler? DeferRequested;

    /// <summary>The engine took the answer.</summary>
    public event EventHandler? Answered;

    partial void OnIsOtherSelectedChanged(bool value)
    {
        if (value)
        {
            SelectOption(null);
        }
    }

    /// <summary>Chooses one option, or none: choosing one leaves the option of the person's own text.</summary>
    private void SelectOption(QuestionOptionViewModel? option)
    {
        foreach (var other in Options.Where(o => !ReferenceEquals(o, option) && o.IsSelected))
        {
            other.IsSelected = false;
        }

        SelectedOption = option;
        if (option is not null && IsOtherSelected)
        {
            IsOtherSelected = false;
        }
    }

    private bool CanAnswer() => !IsBusy && !IsAnswered
        && (SelectedOption is not null || (AllowOther && IsOtherSelected && !string.IsNullOrWhiteSpace(OtherText) && OtherText.Length <= MaxOtherLength));

    /// <summary>«Responder»: one option, or the person's own text. Answering is not approving: it asks no Windows Hello.</summary>
    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private async Task AnswerAsync()
    {
        // The command is asked even when its button is off (a key, a screen reader): without a choice of the person nothing is sent.
        if (!CanAnswer())
        {
            return;
        }

        var answer = SelectedOption is { } option
            ? QuestionAnswer.Choosing(Task, Id, option.Id, _decidedBy)
            : QuestionAnswer.Writing(Task, Id, OtherText.Trim(), _decidedBy);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _answer(answer).ConfigureAwait(true);
            IsAnswered = true;
            Answered?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // The engine is behind a port: whatever it refuses is shown in the window, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = _texts.Format("Monitor_Question_Failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Decidir más tarde»: closes the window and keeps the question pending; the task is not cancelled.</summary>
    [RelayCommand(CanExecute = nameof(CanDefer))]
    private void Defer() => DeferRequested?.Invoke(this, EventArgs.Empty);

    private bool CanDefer() => !IsBusy;

    /// <summary>The question was answered elsewhere (by the engine, by consent): it is not answerable here any more.</summary>
    internal void MarkResolved()
    {
        IsAnswered = true;
        AnswerCommand.NotifyCanExecuteChanged();
    }

    public override string ToString() => AccessibleName;
}

/// <summary>The questions of the orchestrator before it plans, as a set to answer in one round (docs/17 D-28, ADR-0051).</summary>
public sealed class PlannerQuestionSet
{
    private readonly Dictionary<string, QuestionAnswer> _answers = [];
    private readonly TaskCompletionSource<IReadOnlyList<AnsweredQuestion>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RunId _run;
    private readonly IReadOnlyList<PendingQuestion> _questions;

    /// <param name="run">The run of the planning that asked: the answers are recorded in it (<see cref="AnsweredQuestion.Run"/>).</param>
    /// <param name="questions">What <c>PlanProposal.Questions</c> brought.</param>
    /// <param name="decidedBy">Who answers: <c>user</c>.</param>
    public PlannerQuestionSet(RunId run, IReadOnlyList<PendingQuestion> questions, string decidedBy = "user", IUiTextProvider? texts = null)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
        {
            throw new ArgumentException("The orchestrator asked no question.", nameof(questions));
        }

        _run = run;
        _questions = questions;
        Questions = [.. questions.Select(q => new QuestionViewModel(q, a => Record(q, a), decidedBy, texts))];
    }

    /// <summary>One window or card per question, in the order the orchestrator asked.</summary>
    public IReadOnlyList<QuestionViewModel> Questions { get; }

    /// <summary>Completes with the answers, one per question and in its order, once every question has its own.</summary>
    public Task<IReadOnlyList<AnsweredQuestion>> Answers => _completion.Task;

    /// <summary>How many questions are still to be answered.</summary>
    public int Remaining => _questions.Count - _answers.Count;

    private Task Record(PendingQuestion question, QuestionAnswer answer)
    {
        // The same check the planner makes later: an option the question has, the person's own text only when it allows it.
        var checkedAnswer = new AnsweredQuestion(_run, question, answer);
        _answers[question.Id] = checkedAnswer.Answer;
        if (_answers.Count == _questions.Count)
        {
            _completion.TrySetResult([.. _questions.Select(q => new AnsweredQuestion(_run, q, _answers[q.Id]))]);
        }

        return Task.CompletedTask;
    }
}
