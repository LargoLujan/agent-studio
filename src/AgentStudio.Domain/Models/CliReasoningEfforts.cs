using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

/// <summary>
/// The efforts each CLI takes at all (docs/17 D-22 and D-25), whatever its model: the launch passes an effort only when its CLI
/// is listed here with it, and otherwise passes none, so the CLI uses its own default (an effort it does not take never breaks
/// a run). Which of them each model accepts is the catalog's (<see cref="ModelCatalogEntry.Efforts"/>), always a subset of this.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Claude Code 2.1.290, <c>claude --help</c>: <c>--effort &lt;level&gt;</c> «Effort level for the current session (low,
/// medium, high, xhigh, max)».</item>
/// <item>Codex 0.160.0: <c>-c model_reasoning_effort=…</c>, from <c>minimal</c> to <c>xhigh</c> as the official sample
/// configuration said when docs/17 was written (2026-10-30). <c>codex exec --help</c> lists no values; on 2026-11-08 the
/// sample says only that the levels «vary by model and client», and the page of models names Max (for GPT-6 Luna) and Ultra
/// but never Minimal. Max stays out until it is measured with this version (ADR-0050, «Sin verificar»).</item>
/// <item>Gemini (parked) takes none.</item>
/// </list>
/// </remarks>
public static class CliReasoningEfforts
{
    private static readonly ReasoningEffort[] Claude =
        [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max];

    private static readonly ReasoningEffort[] Codex =
        [ReasoningEffort.Minimal, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh];

    /// <summary>The efforts <paramref name="provider"/> takes, in the order of the list; none for a CLI that takes no effort.</summary>
    public static IReadOnlyList<ReasoningEffort> Of(ProviderId provider) =>
        provider == ProviderId.ClaudeCode ? Claude
        : provider == ProviderId.CodexCli ? Codex
        : [];

    /// <summary>
    /// Whether <paramref name="provider"/> takes <paramref name="effort"/>: false for a value outside the closed list (a stored
    /// template that was tampered with) and for a level the CLI does not have.
    /// </summary>
    public static bool Accepts(ProviderId provider, ReasoningEffort effort) =>
        Enum.IsDefined(effort) && Of(provider).Contains(effort);

    /// <summary>
    /// The effort the launch passes for <paramref name="effort"/>: the same one when the CLI takes it, otherwise null (the CLI
    /// uses its own default, and <see cref="ModelNotices"/> says so).
    /// </summary>
    public static ReasoningEffort? ForLaunch(ProviderId provider, ReasoningEffort? effort) =>
        effort is { } value && Accepts(provider, value) ? value : null;
}
