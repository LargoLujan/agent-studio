namespace AgentStudio.Application.Localization;

/// <summary>
/// The name of a language as the app keeps it (docs/17 D-49): the name of a culture, such as <c>es-ES</c>, <c>en</c> or
/// <c>pt-BR</c>, which «Ajustes» → «Idioma» stores and the corpus of the help and the documents for clients are written in.
/// The layers below the desktop do not know the list of languages (it lives in the resources of the desktop); they only check that
/// a name has the shape of one, so a path or a sentence never travels as a language.
/// </summary>
public static class LanguageTags
{
    /// <summary>The longest name accepted (<c>zh-Hans-CN</c> and the like fit well within it).</summary>
    public const int MaxLength = 16;

    /// <summary>Whether <paramref name="tag"/> has the shape of the name of a culture: a language of two or three letters, then subtags of letters or digits.</summary>
    public static bool IsValid(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > MaxLength)
        {
            return false;
        }

        var parts = tag.Split('-');
        if (parts[0] is not { Length: 2 or 3 } language || !language.All(char.IsAsciiLetter))
        {
            return false;
        }

        return parts.Skip(1).All(p => p is { Length: >= 1 and <= 8 } && p.All(char.IsAsciiLetterOrDigit));
    }
}
