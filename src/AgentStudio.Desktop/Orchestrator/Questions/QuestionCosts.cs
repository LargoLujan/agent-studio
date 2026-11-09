using AgentStudio.Application.Decisions;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Desktop.Orchestrator.Questions;

/// <summary>How much an option of a question is known to cost (docs/17 D-32).</summary>
public enum QuestionCostKind
{
    /// <summary>The system has no estimate for the option at all.</summary>
    None,

    /// <summary>An interval of total tokens, from the 25th to the 75th percentile of similar attempts of the project, with its origin.</summary>
    Estimated,

    /// <summary>«desconocido»: fewer than three similar attempts. No number is made up.</summary>
    Unknown,

    /// <summary>«no disponible»: the references do not give a total of tokens. No number is made up either.</summary>
    Unavailable,
}

/// <summary>The cost of an option as the window says it: the words, what kind of figure it is and the technical reason, apart.</summary>
/// <param name="Text">The line under the option: «~9.000 a 15.000 tokens · estimado por Agent Studio · 6 referencias», or why there is no figure.</param>
/// <param name="Detail">The reason as the system recorded it, for the tooltip (technical, in English as the log keeps it); null when there is none.</param>
public sealed record QuestionCost(QuestionCostKind Kind, string Text, string? Detail);

/// <summary>
/// The cost of each option of a question as the window shows it (docs/17 D-31 and D-32, F13-14): the interval of total tokens of
/// <see cref="OptionEstimateSnapshot"/> with its origin and its references, or «desconocido» or «no disponible» with the reason.
/// Never a number the system did not record: nothing is summed, rounded up or filled in.
/// </summary>
public static class QuestionCosts
{
    /// <summary>Fewer references than this make an estimate «desconocido» (docs/17 D-32).</summary>
    public const int MinimumReferences = 3;

    public static QuestionCost Of(OptionEstimateSnapshot? estimate, IUiTextProvider? texts = null)
    {
        var t = texts ?? Texts.Current;
        if (estimate is null)
        {
            return new QuestionCost(QuestionCostKind.None, t.Get("Monitor_Question_EstimateNone"), null);
        }

        if (estimate.Low.Value is { } low && estimate.High.Value is { } high)
        {
            var kind = estimate.Low.ResolvedOrigin();
            var origin = kind == MetricOrigin.EstimatedBySystem ? t.Get("Monitor_Question_EstimateOrigin") : MetricText.Origin(kind);
            var text = t.Format(
                "Monitor_Question_Estimate",
                Number(low, t),
                Number(high, t),
                origin,
                estimate.References.ToString(t.Culture));
            return new QuestionCost(QuestionCostKind.Estimated, text, estimate.Low.Source);
        }

        // «desconocido» when there are fewer than three similar attempts; «no disponible» when there are enough but they give no total.
        var unknown = estimate.References < MinimumReferences;
        var why = unknown
            ? t.Format("Monitor_Question_WhyFewReferences", estimate.References.ToString(t.Culture))
            : t.Get("Monitor_Question_WhyNoTotal");
        return new QuestionCost(
            unknown ? QuestionCostKind.Unknown : QuestionCostKind.Unavailable,
            t.Format(unknown ? "Monitor_Question_EstimateUnknown" : "Monitor_Question_EstimateUnavailable", why),
            estimate.Low.Source);
    }

    private static string Number(decimal tokens, IUiTextProvider texts) => decimal.Round(tokens, 0, MidpointRounding.AwayFromZero).ToString("N0", texts.Culture);
}
