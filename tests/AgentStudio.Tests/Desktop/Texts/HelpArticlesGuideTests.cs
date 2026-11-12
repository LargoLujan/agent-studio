using System.IO;
using System.Text.RegularExpressions;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.TextsGuide;

/// <summary>
/// F14-5 (docs/17 D-52 and D-53, ADR-0054 point 9): the guide of texts on the articles of the help (<c>Desktop/Help/es-ES</c> and
/// <c>Desktop/Help/en</c>): sentences of 20 words at most, no jargon of the first level (an article is first level), nothing
/// of the glossary in its old form, and the English article with the same structure as the Spanish one.
/// </summary>
public sealed partial class HelpArticlesGuideTests
{
    private static string HelpFolder(string language) => Path.Combine(DesktopRoot, "Help", language);

    [GeneratedRegex(@"\A---\r?\n(?<header>.*?)\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^title:\s*(?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex TitleLine();

    [GeneratedRegex(@"^\s*(\d+\.\s+|[-*]\s+|#+\s+)", RegexOptions.Multiline)]
    private static partial Regex ListMarker();

    /// <summary>The prose of an article: its title and its body, without the header keys, the markup of lists and headings and the bold marks.</summary>
    private static string Prose(string markdown)
    {
        var header = FrontMatter().Match(markdown);
        var title = TitleLine().Match(header.Groups["header"].Value).Groups["title"].Value.Trim();
        var body = markdown[header.Length..].Replace("**", string.Empty, StringComparison.Ordinal);
        return title + "\n" + ListMarker().Replace(body, string.Empty);
    }

    private static IReadOnlyList<(string Language, string Name, string Prose, string Markdown)> Articles() =>
        [.. new[] { "es-ES", "en" }.SelectMany(language => Directory.GetFiles(HelpFolder(language), "*.md")
            .Order(StringComparer.Ordinal)
            .Select(file =>
            {
                var markdown = File.ReadAllText(file);
                return (language, Path.GetFileName(file), Prose(markdown), markdown);
            }))];

    private static string Report(IEnumerable<string> lines) => "\n" + string.Join('\n', lines);

    [Fact]
    public void The_corpus_has_every_article_in_both_languages_including_work_and_repository()
    {
        var articles = Articles();
        Assert.True(articles.Count >= 24, $"Only {articles.Count} articles were read.");
        foreach (var name in new[] { "work.md", "repository.md" })
        {
            Assert.Contains(articles, a => a.Language == "es-ES" && a.Name == name);
            Assert.Contains(articles, a => a.Language == "en" && a.Name == name);
        }
    }

    [Fact]
    public void No_sentence_of_an_article_has_more_than_20_words()
    {
        var tooLong = new List<string>();
        foreach (var article in Articles())
        {
            var (words, sentence) = SpanishReadability.LongestSentence(article.Prose);
            if (words > TextGuide.MaxWordsPerSentence)
            {
                tooLong.Add($"{article.Language}/{article.Name} ({words} words): {sentence}");
            }
        }

        Assert.True(tooLong.Count == 0, "Sentences with more than 20 words:" + Report(tooLong));
    }

    [Fact]
    public void An_article_has_no_jargon_and_no_old_term_of_the_glossary()
    {
        var found = new List<string>();
        foreach (var article in Articles())
        {
            foreach (var term in TextGuide.Jargon)
            {
                if (term.Pattern.IsMatch(article.Prose))
                {
                    found.Add($"{article.Language}/{article.Name} has «{term.Name}» (say: {term.Say})");
                }
            }

            if (TextGuide.Commit.Pattern.IsMatch(TextGuide.WithoutCommitGloss(article.Prose)))
            {
                found.Add($"{article.Language}/{article.Name} has «commit» outside «versión guardada (commit)»");
            }

            foreach (var term in TextGuide.Glossary)
            {
                var pattern = article.Language == "en" ? term.English : term.Spanish;
                if (pattern is not null && pattern.IsMatch(article.Prose))
                {
                    found.Add($"{article.Language}/{article.Name} has «{term.Name}» (say: {term.Say})");
                }
            }

            if (article.Language == "en")
            {
                foreach (var calque in TextGuide.EnglishCalques)
                {
                    if (calque.Pattern.IsMatch(article.Prose))
                    {
                        found.Add($"{article.Language}/{article.Name} has «{calque.Name}» (say: {calque.Say})");
                    }
                }
            }
        }

        Assert.True(found.Count == 0, "Jargon or old terms in the articles:" + Report(found));
    }

    [Fact]
    public void The_english_article_has_the_same_structure_as_the_spanish_one()
    {
        var different = new List<string>();
        foreach (var es in Articles().Where(a => a.Language == "es-ES"))
        {
            var en = Articles().Single(a => a.Language == "en" && a.Name == es.Name);
            var spanishShape = Shape(es.Markdown);
            var englishShape = Shape(en.Markdown);
            if (spanishShape != englishShape)
            {
                different.Add($"{es.Name}: es {spanishShape} / en {englishShape}");
            }
        }

        Assert.True(different.Count == 0, "Articles whose two languages differ in headings, list items or paragraphs:" + Report(different));
    }

    /// <summary>The skeleton of an article: for each line, H for a heading, L for a list item, P for a paragraph.</summary>
    private static string Shape(string markdown)
    {
        var body = markdown[FrontMatter().Match(markdown).Length..];
        return string.Concat(body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith('#') ? 'H' : ListMarker().IsMatch(line) ? 'L' : 'P'));
    }
}
