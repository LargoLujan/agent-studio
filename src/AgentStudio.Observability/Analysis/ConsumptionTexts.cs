using AgentStudio.Application.Analysis;
using AgentStudio.Application.Localization;

namespace AgentStudio.Observability.Analysis;

/// <summary>
/// How a finding of the analysis of consumption is said (docs/17 D-34 and D-47, ADR-0054): its sentence is the coded text of its
/// code with its arguments, under <see cref="TextPrefix"/> (<c>Text_consumption_ctx_pack_omitted</c>…), and what it suggests is
/// the coded text of <see cref="SuggestionCode"/> without arguments; <c>work.discarded</c> only informs. The prefix keeps the
/// texts apart from those of the analyzer of the team, whose codes may be the same (<c>model.oversized</c> is in both, with
/// other arguments). The desktop paints both from its
/// resources (<c>Resources/ConsumptionStrings.resx</c>); the cost goes apart, with its origin
/// (<see cref="ConsumptionFinding.Tokens"/>).
/// </summary>
/// <remarks>
/// The arguments of each code, in order (numbers are <see cref="int"/> or <see cref="long"/>, waits <see cref="TimeSpan"/>):
/// <list type="table">
/// <item><term><c>ctx.pack-omitted</c></term><description>{0} agent, {1} entries left out, {2} attempts.</description></item>
/// <item><term><c>ctx.high-peak</c></term><description>{0} attempt (<c>T1#2</c>), {1} percent of the window, {2} model.</description></item>
/// <item><term><c>ctx.compacted</c></term><description>{0} attempt, {1} compactions.</description></item>
/// <item><term><c>ctx.heavy-preamble</c></term><description>{0} agent, {1} tokens of skills, {2} tokens of memory (estimated), {3} attempts.</description></item>
/// <item><term><c>retry.same-class</c></term><description>{0} task, {1} attempts, {2} class of the end (the name of <c>AttemptEndClass</c>).</description></item>
/// <item><term><c>retry.same-failing-tests</c></term><description>{0} task, {1} attempts, {2} tests failing, {3} step of the verification.</description></item>
/// <item><term><c>plan.corrected</c></term><description>{0} corrections.</description></item>
/// <item><term><c>verdict.corrected</c></term><description>{0} corrections.</description></item>
/// <item><term><c>model.oversized</c></term><description>{0} agent, {1} model, {2} attempts.</description></item>
/// <item><term><c>cache.low-reuse</c></term><description>{0} attempt, {1} percent of its input read from the cache.</description></item>
/// <item><term><c>work.discarded</c></term><description>{0} attempts, {1} tasks.</description></item>
/// <item><term><c>wait.queue</c></term><description>{0} provider, {1} attempts, {2} total wait, {3} longest wait.</description></item>
/// </list>
/// </remarks>
public static class ConsumptionTexts
{
    /// <summary>The suffix of the code of what a finding suggests.</summary>
    public const string SuggestionSuffix = ".suggestion";

    /// <summary>What the code of a text of this analysis starts with, before the code of its finding.</summary>
    public const string TextPrefix = "consumption.";

    /// <summary>The codes that only inform: they suggest nothing.</summary>
    public static IReadOnlyList<string> InformOnly { get; } = [ConsumptionFindingCodes.WorkDiscarded];

    /// <summary>The sentence of a finding: its code and its arguments.</summary>
    public static UserText Sentence(ConsumptionFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new UserText(TextPrefix + finding.Code, finding.Arguments);
    }

    /// <summary>The code of what a finding of <paramref name="code"/> suggests; null for one that only informs.</summary>
    public static string? SuggestionCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return InformOnly.Contains(code) ? null : code + SuggestionSuffix;
    }

    /// <summary>What a finding suggests, as a coded text without arguments; null for one that only informs.</summary>
    public static UserText? Suggestion(ConsumptionFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return SuggestionCode(finding.Code) is { } code ? new UserText(TextPrefix + code) : null;
    }
}
