using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Application.Knowledge;

// Phase 4 contracts of the context packing (docs/14 D-23 and D-24).

/// <summary>The tiers of a pack, in the order they enter it (D-24). A tier goes in whole before the next one.</summary>
public enum ContextTier
{
    /// <summary>Mandatory requirements of the frozen set (or those the task covers): if they do not fit, the agent is not launched.</summary>
    MandatoryRequirement,

    /// <summary>Trusted decisions, architecture and conventions; the oldest are left out first.</summary>
    Decision,

    /// <summary>Pending tasks; the oldest are left out first.</summary>
    PendingTask,

    /// <summary>Desirable requirements.</summary>
    DesirableRequirement,

    /// <summary>Known errors and solutions, by relevance.</summary>
    KnownIssue,

    /// <summary>Everything else, by relevance.</summary>
    Other,
}

/// <summary>
/// One item a prompt may be given: its id (a requirement or a memory entry), its tier, its category and origin as the
/// block labels them, its literal text (quoted by <c>PromptData</c> in the block, so it can never close it) and its SHA-256.
/// </summary>
public sealed record ContextItem(string Id, ContextTier Tier, string Kind, string Origin, string Text, string Sha256);

/// <summary>
/// What to pack for an agent (D-24): its definition (its <c>MemoryPolicy</c> decides whether memory goes in at all), the
/// focus memory is recalled for (the request, or the objective of the task), the mandatory requirements and the budget in
/// characters. <see cref="Desirable"/> holds the desirable requirements.
/// </summary>
public sealed record ContextPackRequest(AgentDefinition Agent, string Focus, IReadOnlyList<ContextItem> Mandatory, int BudgetCharacters)
{
    public IReadOnlyList<ContextItem> Desirable { get; init; } = [];
}

/// <summary>
/// A pack: the items that went in, in order, those left out for size («posibles pérdidas»), the characters (exact), the
/// tokens (characters divided by 4, «estimado por Agent Studio») and the SHA-256 of the <c>&lt;project_memory&gt;</c>
/// block. Deterministic: the same request and memory give the same pack.
/// </summary>
public sealed record ContextPack(IReadOnlyList<ContextItem> Items, IReadOnlyList<ContextItem> Omitted, int Characters, MetricValue<long> EstimatedTokens, string Sha256)
{
    /// <summary>What <c>AgentRunStarted</c> version 2 records of this pack: hashes and ids, never the text.</summary>
    public ContextPackReference ToReference() => new(Sha256, [.. Items.Select(i => i.Id)], [.. Omitted.Select(i => i.Id)], Characters);
}

/// <summary>Packs the context of an agent by tiers and fails closed (D-24).</summary>
public interface IContextPacker
{
    /// <exception cref="ContextBudgetTooSmallException">The mandatory requirements do not fit: the agent is not launched and the task is blocked with the reason.</exception>
    Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct);
}

/// <summary>The mandatory requirements need more characters than the budget (D-24): nothing is launched.</summary>
public sealed class ContextBudgetTooSmallException : InvalidOperationException
{
    public ContextBudgetTooSmallException(int required, int budget)
        : base($"The mandatory requirements need {required} characters, but the context budget is {budget}: the agent is not launched.")
    {
        Required = required;
        Budget = budget;
    }

    public ContextBudgetTooSmallException()
    {
    }

    public ContextBudgetTooSmallException(string message)
        : base(message)
    {
    }

    public ContextBudgetTooSmallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The characters the mandatory requirements need.</summary>
    public int Required { get; }

    /// <summary>The budget the request had.</summary>
    public int Budget { get; }
}
