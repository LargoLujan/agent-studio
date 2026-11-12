using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Application.Orchestration;

/// <summary>The limits of the chat of preparation (docs/17 D-44): what the person sends and what the system keeps of an answer.</summary>
public static class PreparationChatLimits
{
    /// <summary>The longest text of an answer the system shows.</summary>
    public const int MaxTextLength = 1200;

    /// <summary>The longest message of the person.</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>The longest item of a list (a draft, a criterion, a risk, a question, a reason).</summary>
    public const int MaxItemLength = 400;

    /// <summary>The most drafts of requirements, suggestions for the team, risks and questions of one answer, each.</summary>
    public const int MaxItems = 8;
}

/// <summary>
/// A conversation of the chat of preparation (docs/17 D-44): the read-only copy of the repository it reads (a <c>_base</c> under
/// a run of its own, <c>chat-…</c>, that no orchestration run can have) and the session of the CLI it resumes, null until the
/// CLI gives one.
/// </summary>
public sealed record PreparationChatSession(RunId Workspace, string? AgentSession, int Messages);

/// <summary>
/// One message of the person to the orchestrator in the band of preparation (docs/17 D-44): the team as data, the conversation
/// so far (null for the first message) and the person's OK for this message, with the estimate in front.
/// </summary>
public sealed record PreparationChatRequest(
    ProjectContext Project,
    IReadOnlyList<AgentDefinition> Roster,
    AgentId Orchestrator,
    string Message,
    PreparationChatSession? Session,
    QuotaConsent Consent)
{
    /// <summary>
    /// The local catalog of models (docs/17 D-23): a suggestion to change the model of an agent is offered only for a model it
    /// classifies for the CLI of that agent. Without a catalog, no such suggestion is offered.
    /// </summary>
    public Domain.Models.ModelCatalog? Catalog { get; init; }
}

/// <summary>A draft of a requirement the orchestrator suggests: the person copies it to the editor and freezes it, or not (docs/03 §3).</summary>
public sealed record RequirementDraft(string Statement, RequirementPriority Priority, IReadOnlyList<string> Criteria);

/// <summary>A suggestion for the team, as a typed fix the system checked against the template, with the agent's reason (text of an agent).</summary>
public sealed record TeamSuggestion(string Reason, Fix Fix);

/// <summary>
/// The answer of the orchestrator to one message (docs/17 D-44): the auxiliary run it was recorded under (purpose
/// <c>PreparationChat</c>) and the evidence of the answer; its text (<see cref="PreparationChatLimits.MaxTextLength"/> at most),
/// drafts of requirements, suggestions for the team as typed fixes, risks and questions for the person, all text of an agent
/// and none applied; what the system left out and why (in English); the conversation to send the next message in; and
/// <see cref="Error"/> when the run failed or its answer could not be read.
/// </summary>
public sealed record PreparationChatReply(
    RunId Run,
    AgentRunStatus Status,
    EvidenceId? AnswerEvidence,
    string Text,
    IReadOnlyList<RequirementDraft> Requirements,
    IReadOnlyList<TeamSuggestion> Team,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> Discarded,
    PreparationChatSession Session,
    string? Error,
    Localization.UserText? ErrorText = null);

/// <summary>
/// The chat with the orchestrator to prepare the work (docs/17 D-44): read-only, with the person's OK for each message;
/// nothing it suggests is applied by itself.
/// </summary>
public interface IPreparationChat
{
    /// <exception cref="ArgumentNullException">There is no consent, project, roster or message.</exception>
    /// <exception cref="ArgumentException">A blank or too long message, or an orchestrator that is not one of the roster.</exception>
    /// <exception cref="InvalidOperationException">The consent is too old, dated in the future or spent already: nothing is launched.</exception>
    Task<PreparationChatReply> SendAsync(PreparationChatRequest request, IProgress<AgentEvent>? progress, CancellationToken ct);

    /// <summary>Ends a conversation: its read-only copy of the repository is removed. Nothing else changes.</summary>
    Task EndAsync(ProjectContext project, PreparationChatSession session, CancellationToken ct);
}
