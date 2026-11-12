using AgentStudio.Application.Localization;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>
/// Paints in the language in use what Orchestration gives as coded texts (docs/17 D-47, ADR-0054 addendum of 2026-11-12): the
/// warnings of the review of a plan and the lines of the steps of the verification profile. What the approval carries and the run
/// compares (<c>VerificationStepText.PlanWarning</c>) is not painted here: it is the Spanish sentence that was anchored (D-48).
/// </summary>
public static class OrchestratorCodedText
{
    /// <summary>The sentences painted one after another, separated by a space; null when there is none.</summary>
    public static string? Sentences(IReadOnlyList<UserText> texts, IUiTextProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        provider ??= Texts.Current;
        return texts.Count == 0 ? null : string.Join(' ', texts.Select(provider.RenderNested));
    }

    /// <summary>A step of the profile as the person reads it before approving: id, title, command, what it requires and its lack of isolation.</summary>
    public static string StepLine(VerificationStep step, IUiTextProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        provider ??= Texts.Current;
        var parts = VerificationStepText.LineParts(step);
        return string.Join(
            " · ",
            new[] { parts.Plain, parts.Requires is { } requires ? provider.RenderNested(requires) : null, parts.WithoutIsolation is { } open ? provider.RenderNested(open) : null }
                .OfType<string>());
    }
}
