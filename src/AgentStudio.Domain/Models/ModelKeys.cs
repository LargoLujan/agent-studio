using AgentStudio.Domain.Providers;

namespace AgentStudio.Domain.Models;

/// <summary>
/// The one rule of which names are the same model (docs/17 D-23, ADR-0050): learning from the log, looking up what was learned
/// and estimating all go through <see cref="Of"/>, so an alias and its full name, with or without the date the CLI appends,
/// in any case, are one model wherever the question is asked.
/// </summary>
public static class ModelKeys
{
    /// <summary>The suffix of the variant of a model with a window of one million tokens; it says the window and is kept.</summary>
    public const string OneMillionSuffix = "[1m]";

    /// <summary>
    /// The key of <paramref name="model"/> of <paramref name="provider"/>: the name of its entry in <paramref name="catalog"/>
    /// (looked up as it is, then without the date and without <c>[1m]</c>), or else the id in lower case without the date the
    /// CLI appends; null for none. Without a catalog, the id.
    /// </summary>
    public static string? Of(ModelCatalog? catalog, ProviderId provider, string? model)
    {
        if (Normalize(model) is not { } normalized)
        {
            return null;
        }

        var trimmed = model!.Trim();
        var withoutWindow = normalized.EndsWith(OneMillionSuffix, StringComparison.Ordinal) ? normalized[..^OneMillionSuffix.Length] : normalized;
        var entry = catalog?.Find(provider, trimmed) ?? catalog?.Find(provider, normalized) ?? catalog?.Find(provider, withoutWindow);
        return entry?.Name ?? normalized;
    }

    /// <summary>
    /// <paramref name="model"/> trimmed, in lower case and without the date the CLI appends (<c>-20251001</c> or
    /// <c>@20251001</c>); the suffix <c>[1m]</c> is kept. Null for none. The same as the base table of windows does.
    /// </summary>
    public static string? Normalize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var id = model.Trim().ToLowerInvariant();
        var oneMillion = id.EndsWith(OneMillionSuffix, StringComparison.Ordinal);
        if (oneMillion)
        {
            id = id[..^OneMillionSuffix.Length];
        }

        if (id.Length > 9 && id[^9] is '-' or '@' && id[^8..].All(char.IsAsciiDigit))
        {
            id = id[..^9];
        }

        return oneMillion ? id + OneMillionSuffix : id;
    }
}
