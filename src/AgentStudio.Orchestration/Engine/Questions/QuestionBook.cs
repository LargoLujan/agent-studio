using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// A question an agent asked, as the log recorded it (docs/17 D-29): its id in the run, the task, agent and attempt that
/// asked, the question as validated and redacted, its evidence (the whole question as the agent wrote it, after redaction),
/// what the system estimated for each option and when the log recorded it. The task is null for a question of the
/// orchestrator before it plans (D-28): no task owns it, and its answer goes to the executors of every task (V43).
/// </summary>
internal sealed record AskedQuestion(
    string Id,
    TaskId? Task,
    AgentId Agent,
    int Attempt,
    AgentQuestion Question,
    EvidenceId Evidence,
    string Sha256,
    IReadOnlyList<OptionEstimateSnapshot> Estimates,
    DateTimeOffset AskedAt)
{
    /// <summary>The task whose executor asked.</summary>
    /// <exception cref="InvalidOperationException">The orchestrator asked it, before it planned: no task owns it.</exception>
    public TaskId AskingTask => Task ?? throw new InvalidOperationException($"Question '{Id}' was asked by the orchestrator, not by the executor of a task.");

    /// <summary>
    /// The question as the monitor and its window see it (<see cref="PendingQuestion"/>), with the estimates exactly as
    /// <c>QuestionAsked</c> records them, so a restart shows the same.
    /// </summary>
    public PendingQuestion ToPending() => new(Id, Task, Agent, Attempt, Question, Evidence, Estimates, AskedAt);
}

/// <summary>
/// A question with its answer (docs/17 D-30), which every later attempt of its task receives as data: the option chosen, or
/// the person's own text (redacted) with its evidence. The answer to a question of the orchestrator (no task) goes to the
/// executors of every task instead.
/// </summary>
/// <param name="AnsweredAt">When the log recorded the answer (<c>QuestionAnswered</c>).</param>
internal sealed record RecordedAnswer(
    AskedQuestion Asked,
    string? Option,
    string? OwnText,
    EvidenceId? OwnTextEvidence,
    string? OwnTextSha256,
    string DecidedBy,
    QuestionAnswerMethod Method,
    DateTimeOffset AnsweredAt)
{
    /// <summary>The label of the option chosen, as the agent wrote it (redacted); null for an answer in the person's own words.</summary>
    public string? Label => Option is { } option ? Asked.Question.Option(option)?.Label : null;
}

/// <summary>
/// The questions of a run, as the loop keeps them (docs/17 D-29 and D-30): the one each task waits for, the answered ones
/// its later attempts receive, the answered ones of the orchestrator that every task receives, and how many were asked, for
/// the caps of <see cref="QuestionRules"/>. Only the loop touches it; a resume rebuilds it from the log
/// (<see cref="QuestionRecovery"/>).
/// </summary>
internal sealed class QuestionBook
{
    private readonly Dictionary<TaskId, AskedQuestion> _pending = [];
    private readonly Dictionary<TaskId, List<RecordedAnswer>> _answered = [];
    private readonly Dictionary<TaskId, int> _askedByTask = [];
    private readonly List<RecordedAnswer> _answeredByOrchestrator = [];

    /// <summary>The questions recorded in the run by its executors (the cap of <see cref="QuestionRules.MaxQuestionsPerRun"/>).</summary>
    public int AskedInRun { get; private set; }

    /// <summary>The questions waiting for their answer, by task.</summary>
    public IReadOnlyDictionary<TaskId, AskedQuestion> Pending => _pending;

    /// <summary>How many questions the agent of <paramref name="task"/> asked in the run.</summary>
    public int AskedBy(TaskId task) => _askedByTask.GetValueOrDefault(task);

    /// <summary>Whether one more question of <paramref name="task"/> would pass a cap (D-29): three per task, ten per run.</summary>
    public bool Capped(TaskId task) => AskedBy(task) >= QuestionRules.MaxQuestionsPerTask || AskedInRun >= QuestionRules.MaxQuestionsPerRun;

    /// <summary>
    /// The id of the next question of <paramref name="task"/>, unique in the run: the task and its number (<c>T3-q1</c>). The
    /// number is always what follows the last <c>-q</c>, so two tasks never give the same id (<c>A-q1</c> is the first of
    /// task <c>A</c>; the first of task <c>A-q1</c> is <c>A-q1-q1</c>).
    /// </summary>
    public string NextId(TaskId task) => string.Create(CultureInfo.InvariantCulture, $"{task.Value}-q{AskedBy(task) + 1}");

    /// <summary>The questions of <paramref name="task"/> answered so far, in the order they were asked.</summary>
    public IReadOnlyList<RecordedAnswer> AnsweredOf(TaskId task) => _answered.TryGetValue(task, out var list) ? list : [];

    /// <summary>
    /// The questions the orchestrator asked before it planned and the person answered, in the order of the log: data for the
    /// executor of every task (V43). They are not the executors' questions, so they count in no cap.
    /// </summary>
    public IReadOnlyList<RecordedAnswer> AnsweredByOrchestrator => _answeredByOrchestrator;

    /// <summary>
    /// Every answer the next attempt of <paramref name="task"/> receives as data: the ones to the questions of the
    /// orchestrator first (they were asked before the run), then the ones to its own.
    /// </summary>
    public IReadOnlyList<RecordedAnswer> AnswersFor(TaskId task) => [.. _answeredByOrchestrator, .. AnsweredOf(task)];

    /// <summary>The question <paramref name="task"/> waits for, as the monitor sees it; null when it waits for none.</summary>
    public PendingQuestion? PendingView(TaskId task) => _pending.TryGetValue(task, out var asked) ? asked.ToPending() : null;

    /// <summary>A question was recorded (<c>QuestionAsked</c>); <paramref name="pending"/> false for one of a task that no longer waits for it.</summary>
    public void Ask(AskedQuestion question, bool pending = true)
    {
        var task = question.AskingTask;
        _askedByTask[task] = AskedBy(task) + 1;
        AskedInRun++;
        if (pending)
        {
            _pending[task] = question;
        }
    }

    /// <summary>The question of a task got its answer (<c>QuestionAnswered</c>): it is no longer pending.</summary>
    public void Answer(RecordedAnswer answered)
    {
        var task = answered.Asked.AskingTask;
        _pending.Remove(task);
        if (!_answered.TryGetValue(task, out var list))
        {
            _answered[task] = list = [];
        }

        list.Add(answered);
    }

    /// <summary>A question of the orchestrator got its answer (<c>QuestionAnswered</c> with no task): every task receives it from now on.</summary>
    /// <exception cref="ArgumentException">The question was asked by the executor of a task.</exception>
    public void AnswerOrchestrator(RecordedAnswer answered)
    {
        if (answered.Asked.Task is not null)
        {
            throw new ArgumentException($"Question '{answered.Asked.Id}' was asked by the executor of a task, not by the orchestrator.", nameof(answered));
        }

        _answeredByOrchestrator.Add(answered);
    }

    /// <summary>After a restart: the task waits for <paramref name="question"/>, as replay found it (<see cref="QuestionRecovery"/>).</summary>
    public void Wait(AskedQuestion question) => _pending[question.AskingTask] = question;

    /// <summary>The task no longer waits for its question (it was cancelled): the question is let go, unanswered.</summary>
    public void Drop(TaskId task) => _pending.Remove(task);
}
