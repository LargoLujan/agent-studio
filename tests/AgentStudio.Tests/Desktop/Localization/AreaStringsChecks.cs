using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AgentStudio.Desktop.Localization;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Localization;

/// <summary>
/// The checks every area of texts of Phase 14 (docs/17 D-47, D-51 and D-54; F14-1, F14-3 and F14-6) makes on its own files,
/// written once: a key made of a prefix, a plural or a condition is never seen by the test of keys of Phase 13, which only reads
/// <c>Get("Key")</c>, so each area asks here that every key its screens name exists in both languages, that its English has no
/// Spanish letters or signs, and that each ⓘ of its screens has its three texts, in short sentences.
/// </summary>
internal static partial class AreaStringsChecks
{
    internal static IUiTextProvider Spanish { get; } = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));

    internal static IUiTextProvider English { get; } = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"InfoKey=""(Info_[A-Za-z_]+)""")]
    private static partial Regex InfoKeyAttribute();

    [GeneratedRegex(@"\{l:T ([A-Za-z_]+)\}")]
    private static partial Regex XamlKey();

    /// <summary>A key written as a literal in code: <c>"Memory_Kind_"</c>, <c>"Skills_Count_One"</c>… for the given prefixes.</summary>
    private static Regex KeyLiteral(IEnumerable<string> prefixes) =>
        new("\"((?:" + string.Join('|', prefixes.Select(Regex.Escape)) + ")[A-Za-z_]*)\"", RegexOptions.CultureInvariant);

    /// <summary>The files of the area: the C# of its folders and the XAML of its views, relative to the desktop.</summary>
    internal static IReadOnlyList<string> Sources(IEnumerable<string> codeFolders, IEnumerable<string> viewFolders) =>
    [
        .. codeFolders.SelectMany(f => Directory.GetFiles(Path.Combine(DesktopRoot, f), "*.cs", SearchOption.AllDirectories)),
        .. viewFolders.SelectMany(f => Directory.GetFiles(Path.Combine(DesktopRoot, "Views", f), "*.xaml", SearchOption.AllDirectories)),
    ];

    /// <summary>
    /// Every key the <paramref name="sources"/> name (a literal that starts with one of the <paramref name="prefixes"/>, or
    /// <c>{l:T Key}</c> in XAML) that is missing in a language. A prefix that ends in <c>_</c> needs one key that starts with it; a
    /// base of a plural needs its <c>_One</c> and <c>_Other</c>; the base of an information button, its three texts.
    /// </summary>
    internal static IReadOnlyList<string> MissingKeys(IEnumerable<string> sources, IEnumerable<string> prefixes)
    {
        var files = ResourceFiles();
        var spanish = files.Values.SelectMany(f => Read(f.Neutral).Keys).ToHashSet(StringComparer.Ordinal);
        var english = files.Values.SelectMany(f => Read(f.Satellites["en"]).Keys).ToHashSet(StringComparer.Ordinal);
        var literal = KeyLiteral(prefixes);
        var missing = new List<string>();
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            var named = literal.Matches(text).Select(m => m.Groups[1].Value)
                .Concat(XamlKey().Matches(text).Select(m => m.Groups[1].Value))
                .Distinct(StringComparer.Ordinal);
            foreach (var key in named)
            {
                bool Found(HashSet<string> keys) =>
                    keys.Contains(key)
                    || (key.EndsWith('_') && keys.Any(k => k.StartsWith(key, StringComparison.Ordinal)))
                    || (keys.Contains(key + "_One") && keys.Contains(key + "_Other"))
                    || (keys.Contains(key + "_Title") && keys.Contains(key + "_What") && keys.Contains(key + "_Actions"));
                if (!Found(spanish) || !Found(english))
                {
                    missing.Add($"{Path.GetFileName(source)}: {key}");
                }
            }
        }

        return missing;
    }

    /// <summary>The texts of the English file of <paramref name="baseName"/> with a letter or a sign that only Spanish uses.</summary>
    internal static IReadOnlyList<string> SpanishInEnglish(string baseName)
    {
        var untranslated = new List<string>();
        foreach (var (key, text) in Read(ResourceFiles()[baseName].Satellites["en"]))
        {
            if (text.Any(c => "áéíóúüñÁÉÍÓÚÑ¿¡«»".Contains(c, StringComparison.Ordinal)))
            {
                untranslated.Add($"{baseName}.{key}: {text}");
            }
        }

        return untranslated;
    }

    /// <summary>Every <c>InfoKey</c> the XAML of the <paramref name="views"/> (relative to <c>Views</c>) names, in order.</summary>
    internal static IReadOnlyList<string> InfoKeysOf(params string[] views) =>
    [
        .. views
            .SelectMany(view => InfoKeyAttribute().Matches(File.ReadAllText(Path.Combine(DesktopRoot, "Views", view))).Select(m => m.Groups[1].Value))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// What is wrong with the information buttons <paramref name="sections"/>: a text missing in a language, an explanation that is
    /// the same in both, fewer than three actions, or a sentence of more than 20 words (D-52).
    /// </summary>
    internal static IReadOnlyList<string> InfoProblems(IEnumerable<string> sections)
    {
        var problems = new List<string>();
        foreach (var section in sections)
        {
            foreach (var part in new[] { "_Title", "_What", "_Actions" })
            {
                foreach (var (language, provider) in new[] { ("es", Spanish), ("en", English) })
                {
                    if (!provider.Has(section + part) || provider.Get(section + part).StartsWith('[') || provider.Get(section + part).Trim().Length == 0)
                    {
                        problems.Add($"{section}{part} ({language}) is missing");
                    }
                }
            }

            if (Spanish.Get(section + "_What") == English.Get(section + "_What") || Spanish.Get(section + "_Actions") == English.Get(section + "_Actions"))
            {
                problems.Add($"{section}: the explanation is the same in both languages");
            }

            if (Spanish.Get(section + "_Actions").Split('\n').Length < 3 || English.Get(section + "_Actions").Split('\n').Length < 3)
            {
                problems.Add($"{section}: fewer than three actions");
            }

            foreach (var part in new[] { "_What", "_Actions" })
            {
                foreach (var provider in new[] { Spanish, English })
                {
                    problems.AddRange(LongSentences(provider.Get(section + part)).Select(s => $"{section}{part}: {s}"));
                }
            }
        }

        return problems;
    }

    /// <summary>The sentences of <paramref name="text"/> with more than 20 words.</summary>
    internal static IEnumerable<string> LongSentences(string text) =>
        Regex.Split(text, @"(?<=[.!?:])\s+|\n").Where(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 20);

    /// <summary>The keys of <paramref name="baseName"/> (neutral file) whose Spanish text has a sentence of more than 20 words.</summary>
    internal static IReadOnlyList<string> KeysWithLongSentences(string baseName, IReadOnlySet<string> except)
    {
        var tooLong = new List<string>();
        foreach (var language in new[] { Spanish, English })
        {
            foreach (var key in Read(ResourceFiles()[baseName].Neutral).Keys.Where(k => !except.Contains(k)))
            {
                tooLong.AddRange(LongSentences(language.Get(key)).Select(s => $"{key}: {s}"));
            }
        }

        return tooLong;
    }
}
