using AgentStudio.Application.Localization;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Prompts;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Team;
using AgentStudio.Orchestration.Engine;
using static AgentStudio.Orchestration.Planning.StrictJsonSchema;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// The chat with the orchestrator in the band of preparation (docs/17 D-44, WP1). Each message is an auxiliary run of the
/// orchestrator (<see cref="IAuxiliaryAgentRunner"/>, purpose <c>PreparationChat</c>), read-only like the planning (Read, Grep
/// and Glob in a <c>_base</c> of the conversation, without writes, shell nor network), with the person's OK for that message.
/// While the conversation lasts, the session of the CLI is resumed, and each message and answer stays as evidence. The
/// message goes through <see cref="ISecretRedactor"/> before it leaves (what is sent reaches the vendor of the agent). The
/// answer has a strict schema: a text, drafts of requirements, suggestions for the team as typed fixes the system checks
/// against the template, risks and questions. Nothing is applied by itself: the person writes and freezes the requirements,
/// and applies a fix like any other.
/// </summary>
public sealed class PreparationChatService : IPreparationChat
{
    /// <summary>The prefix of the run of a conversation: no orchestration run (a date) nor auxiliary run (<c>aux-</c>) has it.</summary>
    public const string WorkspacePrefix = "chat-";

    /// <summary>How long the person's OK for a message lasts.</summary>
    public static readonly TimeSpan ConsentValidity = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
    };

    private readonly IAuxiliaryAgentRunner _runner;
    private readonly IWorktreeManager _worktrees;
    private readonly ISecretRedactor _redactor;
    private readonly TimeProvider _time;
    private readonly IQuotaConsentLedger _consents;
    private readonly IResponseLanguage? _language;

    /// <param name="consents">The register of the OKs spent that the whole app shares (one instance, from the composition).</param>
    public PreparationChatService(IAuxiliaryAgentRunner runner, IWorktreeManager worktrees, ISecretRedactor redactor, IQuotaConsentLedger consents, TimeProvider time, IResponseLanguage? language = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(consents);
        ArgumentNullException.ThrowIfNull(time);
        _runner = runner;
        _worktrees = worktrees;
        _redactor = redactor;
        _consents = consents;
        _time = time;
        _language = language;
    }

    /// <summary>The kinds of suggestions for the team the answer may carry, each a typed fix (docs/17 D-44).</summary>
    public static IReadOnlyList<string> SuggestionKinds { get; } = ["changeModel", "memoryOn", "memoryOff", "addJudge", "removeCapability"];

    /// <summary>The strict schema of the answer, with the agents of the roster as an enum.</summary>
    public static string Schema(IEnumerable<string> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var ids = agents.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return ToCompactJson(Object(
            ("text", String("La respuesta a la persona, en castellano y en texto plano.")),
            ("requirements", Array(Object(
                ("statement", String()),
                ("priority", Enum([nameof(RequirementPriority.Mandatory), nameof(RequirementPriority.Desirable)])),
                ("criteria", Array(String()))), "Borradores de requisitos: los escribe y congela la persona.")),
            ("team", Array(Object(
                ("kind", Enum(SuggestionKinds)),
                ("agent", NullableEnum(ids)),
                ("model", Nullable(String())),
                ("tool", Nullable(String())),
                ("reason", String())), "Sugerencias para el equipo; la persona decide.")),
            ("risks", Array(String())),
            ("questions", Array(String(), "Preguntas para la persona."))));
    }

    public async Task<PreparationChatReply> SendAsync(PreparationChatRequest request, IProgress<AgentEvent>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentNullException.ThrowIfNull(request.Roster);
        ArgumentNullException.ThrowIfNull(request.Message);
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > PreparationChatLimits.MaxMessageLength)
        {
            throw new ArgumentException($"A message has from 1 to {PreparationChatLimits.MaxMessageLength} characters.", nameof(request));
        }

        var orchestrator = request.Roster.FirstOrDefault(a => a.Id == request.Orchestrator && a.Role == AgentRole.Orchestrator)
            ?? throw new ArgumentException($"'{request.Orchestrator}' is not the orchestrator of the roster.", nameof(request));

        // The copy of a conversation is only ever one of its own: never the _base of a run, which a session could otherwise name.
        if (request.Session is { } previous && !IsChatWorkspace(previous.Workspace))
        {
            throw new ArgumentException($"'{previous.Workspace}' is not the copy of a chat of preparation (it starts with '{WorkspacePrefix}').", nameof(request));
        }

        QuotaConsentCheck.Spend(_consents, request.Consent, _time.GetUtcNow(), ConsentValidity);

        var workspace = request.Session?.Workspace ?? new RunId($"{WorkspacePrefix}{_time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}");
        var folder = await BaseOfAsync(request.Project, workspace, ct).ConfigureAwait(false);
        var message = _redactor.Redact(request.Message);
        var resume = request.Session?.AgentSession;
        var prompt = resume is null ? FirstPrompt(request.Roster, message, ResponseLanguage.Resolve(_language)) : NextPrompt(message);
        var launch = new AgentRunRequest(
            orchestrator,
            prompt,
            folder,
            AgentRunner.ReadOnly(Planner.Tools),
            OutputJsonSchema: Schema(request.Roster.Select(a => a.Id.Value)),
            ResumeSessionId: resume,
            Purpose: "Chat de preparación con el orquestador",
            Tags: ["#orquestacion", "#chat-preparacion", $"#{workspace}"]);
        var result = await _runner.RunAsync(request.Project, new AuxiliaryRunRequest(AgentRunPurpose.PreparationChat, launch), progress, ct).ConfigureAwait(false);
        var session = new PreparationChatSession(workspace, result.Outcome.SessionId ?? resume, (request.Session?.Messages ?? 0) + 1);
        if (result.Outcome.Status != AgentRunStatus.Succeeded || string.IsNullOrWhiteSpace(result.Outcome.StructuredOutputJson))
        {
            return Empty(result, session, [], result.Outcome.Error ?? "The orchestrator gave no answer.", result.Outcome.Error is null ? UserText.Of(OrchestrationUserTexts.ChatNoAnswer) : null);
        }

        AnswerDocument? answer;
        try
        {
            answer = JsonSerializer.Deserialize<AnswerDocument>(result.Outcome.StructuredOutputJson, Options);
        }
        catch (JsonException ex)
        {
            return Empty(result, session, [ex.Message], "The answer of the orchestrator does not follow the schema of the chat.", UserText.Of(OrchestrationUserTexts.ChatSchemaMismatch));
        }

        if (answer is null)
        {
            return Empty(result, session, [], "The answer of the orchestrator is null.", UserText.Of(OrchestrationUserTexts.ChatAnswerNull));
        }

        var discarded = new List<string>();
        return new PreparationChatReply(
            result.Run,
            result.Outcome.Status,
            result.AnswerEvidence,
            Clean(answer.Text, PreparationChatLimits.MaxTextLength, "the text", discarded),
            Drafts(answer.Requirements, discarded),
            Suggestions(answer.Team, request.Roster, orchestrator, request.Catalog, discarded),
            Items(answer.Risks, "a risk", discarded),
            Items(answer.Questions, "a question", discarded),
            discarded,
            session,
            null);
    }

    /// <summary>Whether <paramref name="workspace"/> is the run of the copy of a conversation (<see cref="WorkspacePrefix"/>).</summary>
    public static bool IsChatWorkspace(RunId workspace) => workspace.Value.StartsWith(WorkspacePrefix, StringComparison.Ordinal);

    /// <exception cref="ArgumentException">The session names a copy that is not of a chat: nothing is removed.</exception>
    public async Task EndAsync(ProjectContext project, PreparationChatSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(session);
        if (!IsChatWorkspace(session.Workspace))
        {
            throw new ArgumentException($"'{session.Workspace}' is not the copy of a chat of preparation: it is not removed.", nameof(session));
        }

        var owned = await _worktrees.ListOwnedAsync(project, ct).ConfigureAwait(false);
        if (owned.FirstOrDefault(l => l.Run == session.Workspace && l.Name == "_base") is { } lease)
        {
            await _worktrees.RemoveAsync(lease, discardChanges: false, ct).ConfigureAwait(false);
        }
    }

    private static PreparationChatReply Empty(AuxiliaryRunResult result, PreparationChatSession session, IReadOnlyList<string> discarded, string error, UserText? errorText = null) =>
        new(result.Run, result.Outcome.Status, result.AnswerEvidence, string.Empty, [], [], [], [], discarded, session, error, errorText);

    private async Task<string> BaseOfAsync(ProjectContext project, RunId workspace, CancellationToken ct)
    {
        var owned = await _worktrees.ListOwnedAsync(project, ct).ConfigureAwait(false);
        if (owned.FirstOrDefault(l => l.Run == workspace && l.Name == "_base") is { } lease)
        {
            return lease.Path;
        }

        var head = (await _worktrees.InspectAsync(project.RootPath, ct).ConfigureAwait(false)).HeadCommit;
        return (await _worktrees.EnsureAsync(new WorktreeRequest(project, workspace, "_base", head), ct).ConfigureAwait(false)).Path;
    }

    private static string FirstPrompt(IReadOnlyList<AgentDefinition> roster, string message, string language)
    {
        var text = new StringBuilder();
        text.AppendLine("# Chat de preparación de Agent Studio");
        text.AppendLine();
        text.AppendLine("Eres el agente orquestador. La persona prepara el trabajo de su proyecto y habla contigo antes de planificar. No modificas ficheros ni ejecutas órdenes: puedes leer el repositorio (Read, Grep, Glob). Respondes con el esquema: un texto breve, borradores de requisitos (los escribe y congela la persona), sugerencias para el equipo, riesgos y preguntas para la persona. Nada de lo que propones se aplica solo.");
        text.AppendLine(CultureInfo.InvariantCulture, $"El texto, {PreparationChatLimits.MaxTextLength} caracteres como mucho; cada elemento de una lista, {PreparationChatLimits.MaxItemLength}; como mucho {PreparationChatLimits.MaxItems} de cada. Todo en texto plano, sin enlaces.");
        text.AppendLine("Sugerencias para el equipo: changeModel (agent y model), memoryOn o memoryOff (agent), addJudge (un juez de otra empresa) y removeCapability (agent y tool), cada una con su reason.");
        text.AppendLine();
        ResponseLanguage.Append(text, language);
        text.AppendLine("## El equipo");
        text.AppendLine("Va como datos entre las marcas.");
        text.AppendLine("<team>");
        text.AppendLine(PromptData.Json(roster.Select(a => new TeamAgentData(a.Id.Value, a.Role.ToString(), a.Model.Provider.Value, a.Model.Model, a.Memory.ToString(), a.AllowedTools)).ToList()));
        text.AppendLine("</team>");
        text.AppendLine();
        AppendMessage(text, message);
        return text.ToString();
    }

    private static string NextPrompt(string message)
    {
        var text = new StringBuilder();
        AppendMessage(text, message);
        return text.ToString();
    }

    private static void AppendMessage(StringBuilder text, string message)
    {
        text.AppendLine("## Mensaje de la persona");
        text.AppendLine("Es lo que te pide. Va como dato entre las marcas, en forma de cadena JSON. Devuelve solo el objeto JSON del esquema.");
        text.AppendLine("<person_message>");
        text.AppendLine(PromptData.Quote(message));
        text.AppendLine("</person_message>");
    }

    /// <summary>Text of an agent as the chat shows it: without control or direction characters (but line breaks), cut to <paramref name="max"/>.</summary>
    private static string Clean(string text, int max, string what, List<string> discarded)
    {
        var kept = new string([.. text.Where(c => c is '\n' or '\t' || (!QuestionRules.HasControlCharacter(c.ToString()) && !QuestionRules.HasBidiCharacter(c.ToString())))]).Trim();
        if (kept.Length > max)
        {
            discarded.Add($"{what} was cut to {max} characters.");
            kept = kept[..max];
        }

        return kept;
    }

    private static List<string> Items(IReadOnlyList<string> items, string what, List<string> discarded)
    {
        var kept = new List<string>();
        foreach (var item in items)
        {
            var clean = Clean(item, PreparationChatLimits.MaxItemLength, what, discarded);
            if (clean.Length == 0)
            {
                discarded.Add($"{what} is blank.");
            }
            else if (kept.Count >= PreparationChatLimits.MaxItems)
            {
                discarded.Add($"{what} is one more than {PreparationChatLimits.MaxItems}.");
            }
            else
            {
                kept.Add(clean);
            }
        }

        return kept;
    }

    private static List<RequirementDraft> Drafts(IReadOnlyList<RequirementDocument> drafts, List<string> discarded)
    {
        var kept = new List<RequirementDraft>();
        foreach (var draft in drafts)
        {
            var statement = Clean(draft.Statement, PreparationChatLimits.MaxItemLength, "a draft of a requirement", discarded);
            if (statement.Length == 0 || !System.Enum.TryParse<RequirementPriority>(draft.Priority, ignoreCase: false, out var priority) || !System.Enum.IsDefined(priority) || kept.Count >= PreparationChatLimits.MaxItems)
            {
                discarded.Add("A draft of a requirement is blank, has an unknown priority or is one too many.");
                continue;
            }

            kept.Add(new RequirementDraft(statement, priority, Items(draft.Criteria, "a criterion", discarded)));
        }

        return kept;
    }

    /// <summary>
    /// Each suggestion as a fix, only when it names what the template has: an agent of it, a model the catalog classifies for
    /// the CLI of that agent (as <c>ModelUndersizedRule</c> asks), a tool it has, a judge of a vendor no producer is of. A
    /// suggestion that does not fit is left out, never the answer.
    /// </summary>
    private static List<TeamSuggestion> Suggestions(IReadOnlyList<TeamDocument> team, IReadOnlyList<AgentDefinition> roster, AgentDefinition orchestrator, ModelCatalog? catalog, List<string> discarded)
    {
        var kept = new List<TeamSuggestion>();
        foreach (var suggestion in team)
        {
            var reason = Clean(suggestion.Reason, PreparationChatLimits.MaxItemLength, "a reason", discarded);
            var agent = suggestion.Agent is { } id ? roster.FirstOrDefault(a => a.Id.Value == id) : null;
            var model = suggestion.Model is { } named ? Clean(named, PreparationChatLimits.MaxItemLength, "a model", discarded) : string.Empty;
            Fix? fix = suggestion.Kind switch
            {
                "changeModel" when agent is not null && model.Length > 0 && catalog?.Find(agent.Model.Provider, model) is not null => new ChangeModelFix(agent.Id, model),
                "memoryOn" when agent is not null && !TeamRoles.Judges(agent.Role) && agent.Memory == MemoryPolicy.None => new SetMemoryFix(agent.Id, MemoryPolicy.ReadSelected),
                "memoryOff" when agent is not null && agent.Memory != MemoryPolicy.None => new SetMemoryFix(agent.Id, MemoryPolicy.None),
                "addJudge" => OtherVendor(roster, orchestrator),
                "removeCapability" when agent is not null && suggestion.Tool is { } tool && agent.AllowedTools.Contains(tool, StringComparer.Ordinal) => new RemoveCapabilityFix(agent.Id, tool),
                _ => null,
            };
            if (fix is null || reason.Length == 0 || kept.Count >= PreparationChatLimits.MaxItems)
            {
                discarded.Add($"The suggestion '{suggestion.Kind}' for '{suggestion.Agent}' does not fit the template, gives no reason or is one too many.");
                continue;
            }

            kept.Add(new TeamSuggestion(reason, fix));
        }

        return kept;
    }

    /// <summary>A judge of the vendor none of the orchestrator and the agents that write is of; null when both produce.</summary>
    private static AddJudgeFix? OtherVendor(IReadOnlyList<AgentDefinition> roster, AgentDefinition orchestrator)
    {
        var producers = roster.Where(a => TeamRoles.Writes(a.Role)).Select(a => a.Model.Vendor).Append(orchestrator.Model.Vendor).ToHashSet();
        (ProviderId Provider, VendorId Vendor)[] judges = [(ProviderId.ClaudeCode, VendorId.Anthropic), (ProviderId.CodexCli, VendorId.OpenAI)];
        return judges.Where(j => !producers.Contains(j.Vendor)).Select(j => new AddJudgeFix(j.Provider, j.Vendor)).FirstOrDefault();
    }

    private sealed record TeamAgentData(string Id, string Role, string Program, string? Model, string Memory, IReadOnlyList<string> Tools);

    private sealed record AnswerDocument(string Text, IReadOnlyList<RequirementDocument> Requirements, IReadOnlyList<TeamDocument> Team, IReadOnlyList<string> Risks, IReadOnlyList<string> Questions);

    private sealed record RequirementDocument(string Statement, string Priority, IReadOnlyList<string> Criteria);

    private sealed record TeamDocument(string Kind, string? Agent, string? Model, string? Tool, string Reason);
}
