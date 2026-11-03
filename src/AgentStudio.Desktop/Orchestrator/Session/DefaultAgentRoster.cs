using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Desktop.Orchestrator.Session;

/// <summary>
/// The agent template a project gets when it has none: decision 7 of docs/13 as amended (ADR-0014). Claude Code plans
/// and executes on the user's Max subscription, and Codex judges, so the judge's vendor differs from the vendor of
/// everyone who produced what it reviews.
/// <list type="bullet">
/// <item><c>orquestador</c>: Claude Code, plans only, with the read-only tools <c>Read</c>, <c>Grep</c> and <c>Glob</c>.</item>
/// <item><c>ejecutor</c>: Claude Code. Its permissions come from the workspace of its task: a writing task runs in
/// <c>dontAsk</c> with its file tools only (<c>--tools</c>) and Read and Edit rules for its worktree (no shell), and no
/// task gets network in Phase 3.</item>
/// <item><c>juez</c>: Codex CLI, in the <c>-s read-only</c> sandbox the independent review always asks for.</item>
/// </list>
/// The debates of Phase 5 add four (docs/15 D-09 and decision 1 of its section 12): two participants, <c>arquitecto</c>
/// (Claude Code) and <c>critico</c> (Codex), and two judges, <c>juez-claude</c> (Claude Code) and <c>juez-revision</c>
/// (Codex with another model, so a revision of a verdict of <c>juez</c> is judged by another model, D-39). Participants
/// always run read-only, whatever their tools say (D-09); they receive no memory, and a judge never does.
/// The orchestrator and the executor receive the trusted memory of the project (<see cref="MemoryPolicy.ReadSelected"/>,
/// docs/14 section 12, decision 5); the judges never do (ADR-0022).
/// Only <c>juez-revision</c> names a model (<see cref="RevisionJudgeModel"/>); the rest use the default of the user's
/// account. The protocol prompts carry the rules, so no system instructions are added.
/// </summary>
public static class DefaultAgentRoster
{
    /// <summary>The Codex model of <c>juez-revision</c>: one the user's account accepted (the capture of F2-10), other than the default.</summary>
    public const string RevisionJudgeModel = "gpt-6-luna";

    public static AgentId Orchestrator { get; } = new("orquestador");

    public static AgentId Executor { get; } = new("ejecutor");

    public static AgentId Judge { get; } = new("juez");

    public static AgentId Architect { get; } = new("arquitecto");

    public static AgentId Critic { get; } = new("critico");

    public static AgentId ClaudeJudge { get; } = new("juez-claude");

    public static AgentId RevisionJudge { get; } = new("juez-revision");

    public static IReadOnlyList<AgentDefinition> Create() =>
    [
        Agent(Orchestrator, "Orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob"], TimeSpan.FromMinutes(15), 30, new ReviewPolicy(false, false), MemoryPolicy.ReadSelected),
        Agent(Executor, "Ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob", "Edit", "Write"], TimeSpan.FromMinutes(20), 60, new ReviewPolicy(true, true), MemoryPolicy.ReadSelected),
        Agent(Judge, "Juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, [], TimeSpan.FromMinutes(15), 30, new ReviewPolicy(false, false), MemoryPolicy.None),
        Agent(Architect, "Arquitecto", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob"], TimeSpan.FromMinutes(5), 12, new ReviewPolicy(false, false), MemoryPolicy.None, maxRounds: 4),
        Agent(Critic, "Crítico", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, ["Read", "Grep", "Glob"], TimeSpan.FromMinutes(5), 12, new ReviewPolicy(false, false), MemoryPolicy.None, maxRounds: 4),
        Agent(ClaudeJudge, "Juez Claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic, [], TimeSpan.FromMinutes(15), 30, new ReviewPolicy(false, false), MemoryPolicy.None),
        Agent(RevisionJudge, "Juez de revisión", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, [], TimeSpan.FromMinutes(15), 30, new ReviewPolicy(false, false), MemoryPolicy.None, model: RevisionJudgeModel),
    ];

    private static AgentDefinition Agent(
        AgentId id,
        string name,
        AgentRole role,
        ProviderId provider,
        VendorId vendor,
        IReadOnlyList<string> tools,
        TimeSpan maxDuration,
        int maxTurns,
        ReviewPolicy review,
        MemoryPolicy memory,
        int maxRounds = 1,
        string? model = null) =>
        new(
            id,
            name,
            role,
            new ModelBinding(provider, vendor, model),
            SystemInstructions: string.Empty,
            Objectives: [],
            AllowedTools: tools,
            PathGrants: [],
            new ExecutionBudget(maxTurns, maxDuration),
            MaxRounds: maxRounds,
            memory,
            review);
}
