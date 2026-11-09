using AgentStudio.Domain.Questions;

namespace AgentStudio.Orchestration.Engine.Questions;

/// <summary>
/// The answer of a person to a question (docs/17 D-30), on its way to the loop: like every command, it goes through the one
/// channel the loop reads, so the loop alone records it and moves the task (no lock). <see cref="Done"/> completes once it is
/// in the log, or fails with why it was refused; a run that ends before it gets to it refuses it too.
/// </summary>
internal sealed record AnswerSignal(QuestionAnswer Answer, TaskCompletionSource Done) : Signal;
