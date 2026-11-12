using System.Globalization;
using System.Text;
using AgentStudio.Application.Localization;

namespace AgentStudio.Desktop.Localization;

/// <summary>
/// The pseudolocalization of the interface (ADR-0054, adenda de la Fase 14): the texts of another provider with their letters
/// accented and about 40 % longer, so a screen shows at a glance, and a test finds, every text that will not fit when a real
/// translation is longer than the source. Placeholders (<c>{0}</c>, <c>{1:N1}</c>) and escaped braces are kept as they are, so
/// <see cref="Format"/> still fills them. It is used by the tests of the inventory of the interface and never by the app.
/// </summary>
public sealed class PseudoTextProvider : IUiTextProvider
{
    /// <summary>The culture the pseudolocalization is named by, as Windows names it.</summary>
    public const string CultureName = "qps-ploc";

    private const string From = "aceinouyAEIOUCNY";
    private const string To = "\u00E0\u00E7\u00E9\u00EF\u00F1\u00F6\u00FB\u00FD\u00C0\u00C9\u00CE\u00D6\u00DB\u00C7\u00D1\u00DD";
    private const string Padding = " ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~ ~";

    private readonly IUiTextProvider _inner;

    public PseudoTextProvider(IUiTextProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The culture of the source: dates and numbers are written as it does, only the letters change.</summary>
    public CultureInfo Culture => _inner.Culture;

    public bool Has(string key) => _inner.Has(key);

    /// <summary>The text of <paramref name="key"/> pseudolocalized; a key that does not exist stays <c>[key]</c>, so it is still found.</summary>
    public string Get(string key) => _inner.Has(key) ? Pseudo(_inner.Get(key)) : _inner.Get(key);

    public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments ?? []);

    public string Render(UserText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Format(text.ResourceKey, [.. text.Arguments]);
    }

    /// <summary>Accents the letters that have an accented twin and adds about 40 % of padding at the end, never inside a placeholder.</summary>
    public static string Pseudo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length * 2);
        var depth = 0;
        var letters = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '{' || c == '}') && depth == 0 && i + 1 < text.Length && text[i + 1] == c)
            {
                builder.Append(c).Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && char.IsLetter(c))
            {
                letters++;
                var index = From.IndexOf(c, StringComparison.Ordinal);
                builder.Append(index >= 0 ? To[index] : c);
                continue;
            }

            builder.Append(c);
        }

        if (letters > 0)
        {
            var extra = (int)Math.Ceiling(letters * 0.4);
            builder.Append(Padding.AsSpan(0, Math.Min(extra, Padding.Length)));
        }

        return builder.ToString();
    }
}
