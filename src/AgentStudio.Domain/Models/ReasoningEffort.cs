using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

/// <summary>
/// How hard a model reasons before it answers (docs/17 D-22 and D-25): the closed list of levels the two CLIs accept, from
/// their local help. Claude Code takes <c>--effort</c> with <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>;
/// Codex takes <c>-c model_reasoning_effort=…</c> from <c>minimal</c> to <c>xhigh</c>. Which levels each model accepts is the
/// catalog's (<see cref="ModelCatalogEntry.Efforts"/>); a CLI only ever receives a value of this list, never free text.
/// </summary>
/// <remarks>
/// The names are part of the hash of a template that sets an effort (<c>RosterHash</c>) and of its stored JSON: they are
/// never renamed, and a new level is appended.
/// </remarks>
public enum ReasoningEffort
{
    /// <summary>Codex only (<c>minimal</c>).</summary>
    Minimal,

    /// <summary><c>low</c> in both CLIs.</summary>
    Low,

    /// <summary><c>medium</c> in both CLIs.</summary>
    Medium,

    /// <summary><c>high</c> in both CLIs.</summary>
    High,

    /// <summary><c>xhigh</c> in both CLIs.</summary>
    XHigh,

    /// <summary>Claude Code only (<c>max</c>).</summary>
    Max,
}

/// <summary>The value each CLI takes for an effort (docs/17 D-25), so its adapter never writes one by hand.</summary>
public static class ReasoningEfforts
{
    private static readonly IReadOnlyList<ReasoningEffort> Claude =
        [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max];

    private static readonly IReadOnlyList<ReasoningEffort> Codex =
        [ReasoningEffort.Minimal, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh];

    /// <summary>
    /// The levels each CLI takes (docs/17 D-22, from the local help of each): Claude Code (<c>--effort</c>) from <c>low</c> to
    /// <c>max</c>, Codex (<c>model_reasoning_effort</c>) from <c>minimal</c> to <c>xhigh</c>; any other provider, none. Which
    /// of them each model accepts is the catalog's (<see cref="ModelCatalogEntry.Efforts"/>), within these.
    /// </summary>
    public static IReadOnlyList<ReasoningEffort> For(ProviderId provider) =>
        provider == ProviderId.ClaudeCode ? Claude
        : provider == ProviderId.CodexCli ? Codex
        : [];

    /// <summary>Whether the CLI of <paramref name="provider"/> takes <paramref name="effort"/>: Claude never <c>minimal</c>, Codex never <c>max</c>.</summary>
    public static bool Accepts(ProviderId provider, ReasoningEffort effort) => For(provider).Contains(effort);

    /// <summary>The token of the effort as both CLIs spell it: <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the list.</exception>
    public static string Token(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        ReasoningEffort.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort."),
    };
}
