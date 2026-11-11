using System.Collections.Concurrent;
using System.Globalization;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Documents;

/// <summary>
/// Paints the sentences of the system of a document for clients (<see cref="UserText"/>) in the language chosen at export, from the
/// resources of the desktop (docs/17 D-47 and D-59; ADR-0057): the writers of Markdown, HTML and Word live below the desktop and
/// cannot read them, so this is their port. Numbers and dates take the culture of the language, not the one of the interface; a
/// text missing in the language comes in the source language, Spanish of Spain, as everywhere.
/// </summary>
public sealed class ResourceUserTextRenderer : IUserTextRenderer
{
    private readonly ConcurrentDictionary<string, ResourceTextProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public string Render(UserText text, string language)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!LanguageTags.IsValid(language))
        {
            throw new ArgumentException("The language is the name of a culture, such as es-ES or en.", nameof(language));
        }

        return _providers.GetOrAdd(language, tag => new ResourceTextProvider(CultureOf(tag))).Render(text);
    }

    private static CultureInfo CultureOf(string tag)
    {
        try
        {
            return CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            // A name with the shape of a culture that Windows does not know reads the source language.
            return CultureInfo.GetCultureInfo(UiLanguages.SourceCulture);
        }
    }
}
