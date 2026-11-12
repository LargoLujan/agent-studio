using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// Says in the language in use why the memory was not derived or not exported (docs/17 D-47, ADR-0054): the layers below give the
/// reason as a code (<see cref="UserText"/>) and, for the log and the tests, in Spanish. A reason without a code (one a newer layer
/// adds before its text is written) is shown as that Spanish text, never as an empty line.
/// </summary>
public static class MemoryReasonTexts
{
    /// <summary>The text of <paramref name="code"/> in the language in use, or <paramref name="spanish"/> when there is no code.</summary>
    public static string Say(UserText? code, string? spanish) => Say(Texts.Current, code, spanish);

    /// <summary>The text of <paramref name="code"/> in the language of <paramref name="texts"/>, or <paramref name="spanish"/> when there is no code.</summary>
    public static string Say(IUiTextProvider texts, UserText? code, string? spanish)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return code is not null && texts.Has(code.ResourceKey) ? texts.RenderNested(code) : spanish ?? string.Empty;
    }

    /// <summary>The name of the language in use, the one the export of the memory is written in.</summary>
    public static string ExportLanguage => Texts.Current.Culture.Name;
}
