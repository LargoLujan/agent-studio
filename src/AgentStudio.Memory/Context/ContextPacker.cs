using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Memory.Context;

/// <summary>
/// Packs the context Agent Studio gives an agent, by tiers (docs/14 D-23 and D-24; ADR-0023). Deterministic: the same
/// request and the same memory give the same pack.
/// <list type="number">
/// <item><b>Mandatory requirements</b> go in whole, first and in their order. If they need more characters than the
/// budget, nothing is packed and <see cref="ContextBudgetTooSmallException"/> says how many: the agent is not launched.
/// Memory is not even read.</item>
/// <item>Then the tiers in order, each read as a list: <b>decisions</b> (decisions, architecture and conventions, newest
/// first), <b>pending tasks</b> (newest first; the planner only), <b>desirable requirements</b> (in their order),
/// <b>known errors and solutions</b> and <b>the rest</b> (changes, verdicts and debate summaries), both by relevance to
/// the focus and only what is relevant. An item goes in while it fits in the characters left; the first that does not fit
/// is left out, and so is everything after it: a tier goes in whole before the next one, so a decision or a pending task
/// is never left out while something of a lower tier goes in, and the oldest of a tier go first. What is left out is in
/// <see cref="ContextPack.Omitted"/>, in that order («posibles pérdidas»).</item>
/// </list>
/// <list type="bullet">
/// <item><b>Memory</b> only for an agent whose policy is <see cref="MemoryPolicy.ReadSelected"/>, and never for a judge or
/// an auditor whatever its definition says. Only trusted entries, without what a trusted entry supersedes, and never a
/// daily summary (D-20). At most <see cref="MaxCandidatesPerTier"/> entries are considered per tier.</item>
/// <item><b>An item of memory</b> is its topic and content (<c>"topic: content"</c>), with the canonical SHA-256 that
/// <c>MemoryRecorded</c> anchored.</item>
/// <item><b>Characters</b> are those of the texts of the items, exact; the tokens are characters divided by four, rounded up,
/// «estimado por Agent Studio». The markup of the block a prompt puts them in is not counted.</item>
/// <item><b>The SHA-256 of the pack</b> is that of the canonical list of its items, in order: id, tier, category, origin,
/// SHA-256 and text of each, with length prefixes. It does not depend on how a prompt writes the block.</item>
/// </list>
/// </summary>
public sealed class ContextPacker(IProjectMemory memory) : IContextPacker
{
    /// <summary>How many entries of memory a tier considers at most.</summary>
    public const int MaxCandidatesPerTier = 50;

    /// <summary>Where the estimate of the tokens comes from.</summary>
    public const string TokenEstimateSource = "estimate: 4 chars/token";

    private static readonly MemoryKind[] Decisions = [MemoryKind.Decision, MemoryKind.Architecture, MemoryKind.Convention];
    private static readonly MemoryKind[] PendingTasks = [MemoryKind.PendingTask];
    private static readonly MemoryKind[] KnownIssues = [MemoryKind.KnownError, MemoryKind.Solution];
    private static readonly MemoryKind[] Others = [MemoryKind.Change, MemoryKind.Verdict, MemoryKind.DebateSummary];

    public async Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Agent);
        ArgumentNullException.ThrowIfNull(request.Mandatory);
        ArgumentNullException.ThrowIfNull(request.Desirable);
        ArgumentOutOfRangeException.ThrowIfNegative(request.BudgetCharacters);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mandatory = Distinct(request.Mandatory, seen);
        var required = mandatory.Sum(i => i.Text.Length);
        if (required > request.BudgetCharacters)
        {
            throw new ContextBudgetTooSmallException(required, request.BudgetCharacters);
        }

        var candidates = new List<ContextItem>();
        var readsMemory = request.Agent.Memory == MemoryPolicy.ReadSelected && request.Agent.Role is not (AgentRole.Judge or AgentRole.Auditor);
        if (readsMemory)
        {
            candidates.AddRange(await RecallAsync(project, ContextTier.Decision, Decisions, null, ct).ConfigureAwait(false));
            if (request.Agent.Role == AgentRole.Orchestrator)
            {
                candidates.AddRange(await RecallAsync(project, ContextTier.PendingTask, PendingTasks, null, ct).ConfigureAwait(false));
            }
        }

        candidates.AddRange(request.Desirable);
        if (readsMemory && !string.IsNullOrWhiteSpace(request.Focus))
        {
            candidates.AddRange(await RecallAsync(project, ContextTier.KnownIssue, KnownIssues, request.Focus, ct).ConfigureAwait(false));
            candidates.AddRange(await RecallAsync(project, ContextTier.Other, Others, request.Focus, ct).ConfigureAwait(false));
        }

        var items = new List<ContextItem>(mandatory);
        var omitted = new List<ContextItem>();
        var characters = required;
        foreach (var candidate in Distinct(candidates, seen))
        {
            if (omitted.Count == 0 && characters + candidate.Text.Length <= request.BudgetCharacters)
            {
                items.Add(candidate);
                characters += candidate.Text.Length;
            }
            else
            {
                omitted.Add(candidate);
            }
        }

        return new ContextPack(items, omitted, characters, MetricValue<long>.EstimatedBySystem((characters + 3L) / 4, TokenEstimateSource), HashOf(items));
    }

    /// <summary>The trusted entries of <paramref name="kinds"/> as items of <paramref name="tier"/>: by relevance to <paramref name="focus"/>, or newest first without one.</summary>
    private async Task<IReadOnlyList<ContextItem>> RecallAsync(ProjectContext project, ContextTier tier, IReadOnlyList<MemoryKind> kinds, string? focus, CancellationToken ct)
    {
        var entries = await memory.RecallAsync(project, new MemoryQuery(focus, kinds, MaxEntries: MaxCandidatesPerTier, TrustedOnly: true), ct).ConfigureAwait(false);
        return [.. entries.Where(e => e.Trust == MemoryTrust.Trusted && e.SupersededBy is null && kinds.Contains(e.Kind))
            .Select(e => new ContextItem(e.Id, tier, e.Kind.ToString(), e.Origin.ToString(), $"{e.Topic}: {e.Content}", e.ContentSha256))];
    }

    /// <summary>The items whose id was not seen yet, each checked, in order; the first of an id is the one that counts.</summary>
    private static List<ContextItem> Distinct(IEnumerable<ContextItem> items, HashSet<string> seen)
    {
        var distinct = new List<ContextItem>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
            ArgumentNullException.ThrowIfNull(item.Text);
            if (seen.Add(item.Id))
            {
                distinct.Add(item);
            }
        }

        return distinct;
    }

    private static string HashOf(IReadOnlyList<ContextItem> items) =>
        ContentHash.Sha256Hex(ContentHash.Canonical(
        [
            "agent-studio/context-pack/1",
            .. items.SelectMany(i => new[] { i.Id, i.Tier.ToString(), i.Kind, i.Origin, i.Sha256, i.Text }),
        ]));
}
