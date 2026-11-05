using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Memory.Context;

/// <summary>
/// Packs the context Agent Studio gives an agent, by tiers (docs/14 D-23 and D-24; ADR-0023, amended by ADR-0047).
/// Deterministic: the same request, the same memory and the same index give the same pack.
/// <list type="number">
/// <item><b>Mandatory requirements</b> go in whole, first and in their order. If they need more characters than the
/// budget, nothing is packed and <see cref="ContextBudgetTooSmallException"/> says how many: the agent is not launched.
/// Memory is not even read.</item>
/// <item>Then the tiers in order, each read as a list: <b>decisions</b> (decisions, architecture and conventions, newest
/// first), <b>pending tasks</b> (newest first; the planner only), <b>desirable requirements</b> (in their order),
/// <b>known errors and solutions</b>, <b>the rest</b> (changes, verdicts and debate summaries) and <b>the journal</b>
/// (<see cref="ContextTier.History"/>: its anchored entries, <see cref="MemoryKind.JournalEntry"/>), the last three by
/// relevance to the focus and only what is relevant. The journal is the last tier, so it is the first left out when there
/// is no room and it never displaces a requirement nor a decision (docs/17 D-02). An item goes in while it fits in the
/// characters left; the first that does not fit is left out, and so is everything after it: a tier goes in whole before
/// the next one, so a decision or a pending task is never left out while something of a lower tier goes in, and the
/// oldest of a tier go first. What is left out is in <see cref="ContextPack.Omitted"/>, in that order («posibles
/// pérdidas»).</item>
/// </list>
/// <list type="bullet">
/// <item><b>Relevance</b> (docs/17 D-06, F12-7): with the semantic index, the tiers by relevance are ranked by its hybrid
/// search (words and meaning; one batch for the three tiers, <see cref="ISemanticIndex.SearchManyAsync"/>, so what is
/// anchored is read once and the focus embedded once), which goes by words only when there is no model; without an index,
/// by the full-text search of the memory (<c>bm25</c>), as before. If the index fails for any reason but a log that does
/// not verify, the tiers fall back to the full-text search: the index is a cache that is rebuilt at will, and it never
/// decides what may go in. In each tier, what contains exactly an identifier the focus names (a run, a number of the
/// journal, a day, a memory id) goes first (ADR-0047, second addendum of 2026-11-04). The pack says how they were ranked
/// (<see cref="ContextPack.Relevance"/>: hybrid, or by words only and why, <see cref="WordsOnlyReason.IndexUnavailable"/>
/// when the index was not used, and <see cref="ContextRelevance.CutNotice"/> when the model has no cut), beside the ids,
/// tiers and categories of what went in and of what was left out (<see cref="ContextPack.ToReference"/>, anchored by
/// <c>AgentRunStarted</c>).</item>
/// <item><b>Memory</b> only for an agent whose policy is <see cref="MemoryPolicy.ReadSelected"/>, and never for a judge or
/// an auditor whatever its definition says: neither the memory nor the index is asked anything for them (F12-8). Only
/// trusted entries, without what a trusted entry supersedes, and never a daily summary (D-20). At most
/// <see cref="MaxCandidatesPerTier"/> entries are considered per tier.</item>
/// <item><b>An item of memory</b> is its topic and content (<c>"topic: content"</c>), with the canonical SHA-256 that
/// <c>MemoryRecorded</c> anchored: always the text of the anchored entry, never the fragment the index ranked.</item>
/// <item><b>Characters</b> are those of the texts of the items, exact; the tokens are characters divided by four, rounded up,
/// «estimado por Agent Studio». The markup of the block a prompt puts them in is not counted.</item>
/// <item><b>The SHA-256 of the pack</b> is that of the canonical list of its items, in order: id, tier, category, origin,
/// SHA-256 and text of each, with length prefixes. It does not depend on how a prompt writes the block.</item>
/// </list>
/// </summary>
/// <param name="index">The semantic index of the projects; null to rank by the full-text search of the memory only.</param>
public sealed class ContextPacker(IProjectMemory memory, ISemanticIndex? index = null) : IContextPacker
{
    /// <summary>How many entries of memory a tier considers at most.</summary>
    public const int MaxCandidatesPerTier = 50;

    /// <summary>Where the estimate of the tokens comes from.</summary>
    public const string TokenEstimateSource = "estimate: 4 chars/token";

    private static readonly MemoryKind[] Decisions = [MemoryKind.Decision, MemoryKind.Architecture, MemoryKind.Convention];
    private static readonly MemoryKind[] PendingTasks = [MemoryKind.PendingTask];
    private static readonly MemoryKind[] KnownIssues = [MemoryKind.KnownError, MemoryKind.Solution];
    private static readonly MemoryKind[] Others = [MemoryKind.Change, MemoryKind.Verdict, MemoryKind.DebateSummary];
    private static readonly MemoryKind[] Journal = [MemoryKind.JournalEntry];

    /// <summary>The tiers by relevance, in their order, each with its categories.</summary>
    private static readonly (ContextTier Tier, MemoryKind[] Kinds)[] ByRelevance =
    [
        (ContextTier.KnownIssue, KnownIssues),
        (ContextTier.Other, Others),
        (ContextTier.History, Journal),
    ];

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
        ContextRelevance? relevance = null;
        if (readsMemory && !string.IsNullOrWhiteSpace(request.Focus))
        {
            (var relevant, relevance) = await RelevantAsync(project, request.Focus, ct).ConfigureAwait(false);
            candidates.AddRange(relevant);
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

        return new ContextPack(items, omitted, characters, MetricValue<long>.EstimatedBySystem((characters + 3L) / 4, TokenEstimateSource), HashOf(items))
        {
            Relevance = relevance,
        };
    }

    /// <summary>The trusted entries of <paramref name="kinds"/> as items of <paramref name="tier"/>: by relevance to <paramref name="focus"/>, or newest first without one.</summary>
    private async Task<IReadOnlyList<ContextItem>> RecallAsync(ProjectContext project, ContextTier tier, IReadOnlyList<MemoryKind> kinds, string? focus, CancellationToken ct)
    {
        var entries = await memory.RecallAsync(project, new MemoryQuery(focus, kinds, MaxEntries: MaxCandidatesPerTier, TrustedOnly: true), ct).ConfigureAwait(false);
        return Items(entries, tier, kinds);
    }

    /// <summary>
    /// The trusted entries relevant to <paramref name="focus"/> as items of the tiers by relevance, in their order, and how
    /// they were ranked: by one batch of searches of the index (one reading, the focus embedded once), or by the full-text
    /// search of the memory without an index or when it fails (<see cref="WordsOnlyReason.IndexUnavailable"/>).
    /// </summary>
    private async Task<(List<ContextItem> Items, ContextRelevance Relevance)> RelevantAsync(ProjectContext project, string focus, CancellationToken ct)
    {
        var items = new List<ContextItem>();
        if (index is not null)
        {
            IReadOnlyList<SemanticSearchResult>? found = null;
            try
            {
                found = await index.SearchManyAsync(
                    project,
                    [.. ByRelevance.Select(t => new SemanticQuery(focus, MaxCandidatesPerTier) { Kinds = t.Kinds, IncludeLog = false, TrustedOnly = true })],
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or DecisionLogIntegrityException))
            {
                // The index is a cache: the memory still ranks the tiers by its words, and the pack says so.
            }

            if (found is not null && found.Count == ByRelevance.Length)
            {
                for (var i = 0; i < ByRelevance.Length; i++)
                {
                    var (tier, kinds) = ByRelevance[i];
                    items.AddRange(Items(found[i].Hits.Select(h => h.Entry).OfType<MemoryEntry>().DistinctBy(e => e.Id, StringComparer.Ordinal), tier, kinds));
                }

                return (items, found.FirstOrDefault(r => r.Reason is not null)?.Reason is { } reason
                    ? ContextRelevance.WordsOnly(reason)
                    : ContextRelevance.Hybrid with { Cut = found[0].Cut });
            }
        }

        foreach (var (tier, kinds) in ByRelevance)
        {
            items.AddRange(await RecallAsync(project, tier, kinds, focus, ct).ConfigureAwait(false));
        }

        return (items, ContextRelevance.WordsOnly(WordsOnlyReason.IndexUnavailable));
    }

    /// <summary>
    /// The entries that may go in (<see cref="SemanticQuery.MayReachAPrompt"/>: trusted, not superseded, never a daily
    /// summary; and of <paramref name="kinds"/>) as items of <paramref name="tier"/>, in their order.
    /// </summary>
    private static List<ContextItem> Items(IEnumerable<MemoryEntry> entries, ContextTier tier, IReadOnlyList<MemoryKind> kinds) =>
        [.. entries.Where(e => SemanticQuery.MayReachAPrompt(e) && kinds.Contains(e.Kind))
            .Select(e => new ContextItem(e.Id, tier, e.Kind.ToString(), e.Origin.ToString(), $"{e.Topic}: {e.Content}", e.ContentSha256))];

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
