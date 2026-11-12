using AgentStudio.Application.Localization;
using AgentStudio.Domain.Common;

namespace AgentStudio.Desktop.Localization;

/// <summary>
/// Paints a coded text whose arguments include other coded texts (<see cref="INestedText"/>, Phase 14, ADR-0054 addendum): the
/// pieces first, in the same language, and then the sentence with them as text. <see cref="IUiTextProvider.Render"/> stays as it
/// is: it knows only the arguments a culture can format, so a screen that shows a text with pieces goes through here.
/// </summary>
public static class NestedTextRendering
{
    /// <summary>The text of <paramref name="text"/> in the language of <paramref name="provider"/>, its pieces painted first.</summary>
    public static string RenderNested(this IUiTextProvider provider, UserText text)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Arguments.Any(a => a is INestedText))
        {
            return provider.Render(text);
        }

        var painted = text.Arguments.Select(a => a is UserText piece ? provider.RenderNested(piece) : a).ToList();
        return provider.Render(new UserText(text.Code, painted));
    }
}
