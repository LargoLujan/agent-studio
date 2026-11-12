using System.IO;
using System.Reflection;
using AgentStudio.Application.Help;
using AgentStudio.Application.Localization;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Help;

/// <summary>
/// The corpus of the help (docs/17 D-55, ADR-0056): the articles the app ships in <c>Help/&lt;language&gt;/&lt;id&gt;.md</c>, embedded in the
/// assembly, written in plain language and one per screen or section. Each begins with a header between <c>---</c> lines:
/// <c>title:</c> (the title), <c>screens:</c> (the codes of the screens whose <c>F1</c> opens it) and <c>info:</c> (the keys of the
/// ⓘ buttons whose «Más ayuda» opens it), the last two as lists separated by commas. An article that is not written in a language is
/// read in Spanish of Spain, the source, as any text missing from the interface is (ADR-0054). It reads files shipped with the app
/// and nothing else.
/// </summary>
public sealed class HelpCorpus : IHelpCorpus
{
    /// <summary>The language the articles are written in first; the others fall back to it.</summary>
    public const string SourceLanguage = UiLanguages.SourceCulture;

    private const string ResourcePrefix = "Help.";

    private const string ResourceSuffix = ".md";

    private readonly Dictionary<string, IReadOnlyList<HelpArticle>> _byLanguage;

    /// <param name="articles">Every article of every language; the ones of a language not written yet come from the source language.</param>
    public HelpCorpus(IEnumerable<HelpArticle> articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        var all = articles.ToList();
        var source = all.Where(a => Same(a.Language, SourceLanguage)).ToList();
        _byLanguage = new Dictionary<string, IReadOnlyList<HelpArticle>>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in all.Select(a => a.Language).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var own = all.Where(a => Same(a.Language, language)).ToList();
            var missing = source.Where(s => own.All(o => o.Id != s.Id));
            _byLanguage[language] = [.. own.Concat(missing).OrderBy(a => a.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>The languages some article is written in, the source first.</summary>
    public IReadOnlyList<string> Languages => [.. _byLanguage.Keys.OrderBy(l => Same(l, SourceLanguage) ? 0 : 1).ThenBy(l => l, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The corpus embedded in <paramref name="assembly"/> (the desktop's by default).</summary>
    /// <exception cref="InvalidDataException">An article has no valid header, or two have the same language and id.</exception>
    public static HelpCorpus FromAssembly(Assembly? assembly = null)
    {
        var from = assembly ?? typeof(HelpCorpus).Assembly;
        var articles = new List<HelpArticle>();
        foreach (var name in from.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            // «Help.es-ES.preparation.md»: the language, then the id, which may have dots.
            var rest = name[ResourcePrefix.Length..^ResourceSuffix.Length].Replace('\\', '.').Replace('/', '.');
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0)
            {
                throw new InvalidDataException($"The help resource '{name}' has no language folder.");
            }

            using var stream = from.GetManifestResourceStream(name) ?? throw new InvalidDataException($"The help resource '{name}' cannot be opened.");
            using var reader = new StreamReader(stream);
            articles.Add(Parse(rest[(dot + 1)..], rest[..dot], reader.ReadToEnd()));
        }

        var duplicate = articles.GroupBy(a => (a.Language.ToUpperInvariant(), a.Id)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"The help has two articles '{duplicate.Key.Id}' in '{duplicate.Key.Item1}'.");
        }

        return new HelpCorpus(articles);
    }

    /// <summary>One article from the text of its file: the header between <c>---</c> lines and the Markdown after it.</summary>
    /// <exception cref="InvalidDataException">The header is missing or has a line that is not <c>name: value</c>.</exception>
    public static HelpArticle Parse(string id, string language, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            throw new InvalidDataException($"The article '{id}' ({language}) does not begin with its header.");
        }

        string? title = null;
        IReadOnlyList<string> screens = [];
        IReadOnlyList<string> info = [];
        var index = 1;
        for (; index < lines.Length && lines[index].Trim() != "---"; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidDataException($"The header of the article '{id}' ({language}) has a line without a name: '{line}'.");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (name)
            {
                case "title":
                    title = value;
                    break;
                case "screens":
                    screens = List(value);
                    break;
                case "info":
                    info = List(value);
                    break;
                default:
                    throw new InvalidDataException($"The header of the article '{id}' ({language}) has an unknown field '{name}'.");
            }
        }

        if (index >= lines.Length)
        {
            throw new InvalidDataException($"The header of the article '{id}' ({language}) is not closed.");
        }

        var body = string.Join('\n', lines.Skip(index + 1)).Trim();
        return new HelpArticle(id, language, title ?? string.Empty, body, screens, info);
    }

    private static IReadOnlyList<string> List(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<HelpArticle> Articles(string language) =>
        _byLanguage.TryGetValue(Resolve(language), out var articles) ? articles : [];

    public HelpArticle? Find(string id, string language) => Articles(language).FirstOrDefault(a => a.Id == id);

    public HelpArticle? ForScreen(string screen, string language) => Articles(language).FirstOrDefault(a => a.Screens.Contains(screen, StringComparer.Ordinal));

    public HelpArticle? ForInfoKey(string infoKey, string language) => Articles(language).FirstOrDefault(a => a.InfoKeys.Contains(infoKey, StringComparer.Ordinal));

    /// <summary>
    /// The language the articles are read in for <paramref name="language"/>: the same name, else the same language without its
    /// region («en-GB» reads «en»), else the source language.
    /// </summary>
    public string Resolve(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            if (_byLanguage.ContainsKey(language))
            {
                return _byLanguage.Keys.First(k => Same(k, language));
            }

            var neutral = language.Split('-')[0];
            if (_byLanguage.Keys.FirstOrDefault(k => Same(k, neutral)) is { } found)
            {
                return found;
            }

            if (UiLanguages.Find(language) is { } known && _byLanguage.Keys.FirstOrDefault(k => Same(k, known.CultureName)) is { } mapped)
            {
                return mapped;
            }
        }

        return SourceLanguage;
    }

    /// <summary>The name of the language the articles of the current interface language are read in.</summary>
    public string CurrentLanguage => Resolve(Texts.Current.Culture.Name);
}
