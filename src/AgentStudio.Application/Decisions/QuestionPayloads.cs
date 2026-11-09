using AgentStudio.Domain.Common;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Application.Decisions;

// Phase 13 (docs/17 D-29): the question of an agent and the answer of the person, in the log. As every payload, they carry no
// text of an agent (ADR-0010, point 5): the whole question is evidence (AgentStatement, reported by the agent), and the
// person's own answer is evidence they provided. They are written once in the fixed format of DecisionPayloadSerializer.

/// <summary>
/// What the system estimates it would cost to go on with one option (docs/17 D-32): the 25th and 75th percentiles of the
/// tokens of the similar attempts of the project (same agent, same model, the effort the agent declares), each with its
/// origin, and how many attempts it rests on. With fewer than three references both figures are «desconocido»: a snapshot
/// without a value, never a number made up.
/// </summary>
public sealed record OptionEstimateSnapshot(string Option, MetricSnapshot Low, MetricSnapshot High, int References);

/// <summary>
/// An agent asked the person a question (docs/17 D-29), recorded once the system validated it (<see cref="QuestionRules"/>)
/// and registered it as evidence. The task stays <c>Blocked</c> with the reason <c>AwaitingAnswer</c>, which replay rebuilds
/// after a restart.
/// </summary>
/// <param name="Task">The task whose executor asked; null for the orchestrator asking before it plans (D-28).</param>
/// <param name="Attempt">The attempt that asked.</param>
/// <param name="Agent">The agent that asked.</param>
/// <param name="Question">The id the engine gave the question, unique in the run (<see cref="QuestionAnswer.QuestionId"/>).</param>
/// <param name="QuestionEvidence">The evidence of the whole question, as the agent wrote it after redaction.</param>
/// <param name="QuestionSha256">The SHA-256 of that evidence.</param>
/// <param name="Options">The ids of its options, in its order.</param>
/// <param name="RecommendedByAgent">The option the agent recommends, or null.</param>
/// <param name="Estimates">What the system estimates for each option, with its origin; empty when it estimated nothing.</param>
public sealed record QuestionAskedPayload(
    TaskId? Task,
    int Attempt,
    AgentId Agent,
    string Question,
    EvidenceId QuestionEvidence,
    string QuestionSha256,
    IReadOnlyList<string> Options,
    string? RecommendedByAgent,
    IReadOnlyList<OptionEstimateSnapshot> Estimates) : DecisionPayload;

/// <summary>
/// The answer to a question (docs/17 D-29): the option chosen (<paramref name="ChosenOption"/>) or the evidence of the
/// person's own text (<paramref name="AnswerEvidence"/> and its SHA-256), exactly one of the two; who decided and how. A new
/// attempt follows with the answer as data (D-30). The judge sees the question and this choice, never the recommendation nor
/// the estimates (D-33).
/// </summary>
public sealed record QuestionAnsweredPayload(
    TaskId? Task,
    string Question,
    string? ChosenOption,
    EvidenceId? AnswerEvidence,
    string? AnswerSha256,
    string DecidedBy,
    QuestionAnswerMethod Method) : DecisionPayload
{
    /// <summary>The answer as the log records it, in the run <paramref name="runId"/>.</summary>
    /// <exception cref="ArgumentException">Neither or both of an option and an evidence, a hash that is not a SHA-256, or no one decided.</exception>
    public static QuestionAnsweredPayload Answering(
        string runId,
        TaskId? task,
        string question,
        string? chosenOption,
        EvidenceId? answerEvidence,
        string? answerSha256,
        string decidedBy,
        QuestionAnswerMethod method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown way of answering a question.");
        }

        var chose = !string.IsNullOrWhiteSpace(chosenOption);
        var wrote = answerEvidence is not null;
        if (chose == wrote)
        {
            throw new ArgumentException("An answer is an option or the person's own text, exactly one of the two.", nameof(chosenOption));
        }

        if (wrote != (answerSha256 is not null) || (answerSha256 is not null && !ContentHash.IsSha256Hex(answerSha256)))
        {
            throw new ArgumentException("The person's own text is cited by its evidence and the SHA-256 of it.", nameof(answerSha256));
        }

        if (method == QuestionAnswerMethod.RecommendedByConsent && !chose)
        {
            throw new ArgumentException("The system only ever answers with the option the agent recommended.", nameof(method));
        }

        return new QuestionAnsweredPayload(task, question, chosenOption, answerEvidence, answerSha256, decidedBy, method) { RunId = runId };
    }
}
