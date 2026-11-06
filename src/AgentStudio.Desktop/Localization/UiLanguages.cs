using System.Globalization;

namespace AgentStudio.Desktop.Localization;

/// <summary>Who reviewed the translation of a language (docs/17 D-49 and D-50), as the list of languages says it.</summary>
public enum TranslationReview
{
    /// <summary>«Revisado por una persona»: the source language and English.</summary>
    ReviewedByPerson,

    /// <summary>«Traducción automática revisada»: the first wave of Phase 15, translated by agents and reviewed by one of another vendor.</summary>
    MachineTranslatedReviewed,
}

/// <summary>
/// A language of the interface (docs/17 D-49): the culture of its resources, its name in its own language («Deutsch», never
/// translated) and who reviewed it.
/// </summary>
public sealed record UiLanguage(string CultureName, string NativeName, TranslationReview Review)
{
    /// <summary>The culture of the language.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(CultureName);

    /// <summary>The key of the text of its status in the list («Revisado por una persona»…).</summary>
    public string ReviewKey => Review == TranslationReview.ReviewedByPerson ? "Language_Review_Person" : "Language_Review_Machine";

    /// <summary>Shown as an item of a list: its name in its own language.</summary>
    public override string ToString() => NativeName;
}

/// <summary>
/// The languages Agent Studio has, and which one the interface uses (docs/17 D-49, ADR-0054). The person chooses in «Ajustes»
/// → «Idioma» (<c>AppSettings.Language</c>); without a choice, the language of the interface of Windows if it is in the list,
/// and English if not. A text missing in a language falls back to the source language, Spanish of Spain (the neutral resources).
/// Right-to-left languages are postponed (D-50).
/// </summary>
public static class UiLanguages
{
    /// <summary>The language of <c>Strings.resx</c>, the source: Spanish of Spain.</summary>
    public const string SourceCulture = "es-ES";

    /// <summary>The language used when the one of Windows is not in the list (D-49): English.</summary>
    public const string FallbackCulture = "en";

    /// <summary>The languages of the interface, the source first. Phase 15 adds the first wave.</summary>
    public static IReadOnlyList<UiLanguage> All { get; } =
    [
        new(SourceCulture, "Español (España)", TranslationReview.ReviewedByPerson),
        new(FallbackCulture, "English", TranslationReview.ReviewedByPerson),
    ];

    /// <summary>
    /// The language of the list for a culture name: the same name (without case), else the first of the same language
    /// («es-MX» is Spanish of Spain, «en-GB» is English); null for a name that is not a culture or a language not in the list.
    /// </summary>
    public static UiLanguage? Find(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        var exact = All.FirstOrDefault(l => string.Equals(l.CultureName, cultureName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(cultureName.Trim());
        }
        catch (CultureNotFoundException)
        {
            return null;
        }

        return All.FirstOrDefault(l => string.Equals(l.Culture.TwoLetterISOLanguageName, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The language the interface uses (D-49): the one the person chose when it is in the list; else the language of the
    /// interface of Windows (<paramref name="windowsUiCulture"/>) when it is; else English. A choice that is not in the list
    /// (a hand-edited file, a language removed) is no choice.
    /// </summary>
    public static UiLanguage Resolve(string? chosen, CultureInfo windowsUiCulture)
    {
        ArgumentNullException.ThrowIfNull(windowsUiCulture);
        return Find(chosen)
            ?? Find(windowsUiCulture.Name)
            ?? All.First(l => l.CultureName == FallbackCulture);
    }
}
