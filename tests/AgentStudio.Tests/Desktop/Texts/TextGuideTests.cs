using System.Globalization;
using static AgentStudio.Tests.Desktop.Localization.LocalizationTestSupport;

namespace AgentStudio.Tests.Desktop.Texts;

/// <summary>
/// F14-5 (docs/17 D-52 and D-53, ADR-0054 point 9): the guide of texts, measured on every text of the closed resource files, in
/// Spanish and in English where the rule applies. One test per rule, and each lists every text that breaks it with its measure.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Length:</b> no sentence of more than 20 words (a sentence ends at «.», «?», «!» or a new line), both languages.</item>
/// <item><b>Jargon:</b> the terms of <see cref="TextGuide.Jargon"/> may not appear in a first-level text. A key is detail when its
/// last segment is <c>Detail</c> or <c>Details</c> (<c>Audit_Hash_Detail</c>); every other key is first level. «commit» is allowed only
/// as «versión guardada (commit)».</item>
/// <item><b>Glossary:</b> the words the glossary translates («cuota», «evidencia»…) may not appear in the old form anywhere.</item>
/// <item><b>INFLESZ:</b> an explanation in Spanish (10 words or more) scores 55 or more.</item>
/// </list>
/// The exceptions, with the reason of each, are in <see cref="TextGuide.Exceptions"/>; the areas not checked yet, in
/// <see cref="TextGuide.PendingExtraction"/>.
/// </remarks>
public sealed class TextGuideTests
{
    private static string Report(IEnumerable<string> lines) => "\n" + string.Join('\n', lines);

    [Fact]
    public void Every_resource_area_is_checked_or_pending_its_extraction()
    {
        var present = ResourceFiles().Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(TextGuide.ClosedAreas.Where(a => !present.Contains(a)));
        Assert.Empty(TextGuide.ClosedAreas.Intersect(TextGuide.PendingExtraction, StringComparer.Ordinal));

        // Unknown areas are skipped, not failed: another package may be adding its file. This only keeps the closed list honest
        // by counting what is checked.
        var texts = TextGuide.Texts();
        Assert.True(texts.Count > 3000, $"The guide checks {texts.Count} texts of {TextGuide.ClosedAreas.Length} areas.");
    }

    [Fact]
    public void Every_exception_of_the_guide_has_a_reason_and_still_applies()
    {
        var texts = TextGuide.Texts().ToDictionary(t => $"{t.Area}/{t.Key}/{t.Language}", t => t);
        var stale = new List<string>();
        foreach (var (key, reason) in TextGuide.Exceptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), key + " has no reason");
            var parts = key.Split('/');
            if (!texts.ContainsKey($"{parts[0]}/{parts[1]}/es") && !texts.ContainsKey($"{parts[0]}/{parts[1]}/en"))
            {
                stale.Add(key);
            }
        }

        Assert.True(stale.Count == 0, "Exceptions of texts that no longer exist:" + Report(stale));
    }

    [Fact]
    public void No_sentence_of_the_texts_has_more_than_20_words_in_either_language()
    {
        var tooLong = new List<string>();
        foreach (var text in TextGuide.Texts())
        {
            var (words, sentence) = SpanishReadability.LongestSentence(text.Value);
            if (words > TextGuide.MaxWordsPerSentence && !TextGuide.IsExcused(text, "length"))
            {
                tooLong.Add($"{text.Language} {text.Id} ({words} words): {sentence}");
            }
        }

        Assert.True(tooLong.Count == 0, $"Sentences with more than {TextGuide.MaxWordsPerSentence} words:" + Report(tooLong));
    }

    [Fact]
    public void The_technical_terms_stay_out_of_the_first_level_texts()
    {
        var found = new List<string>();
        foreach (var text in TextGuide.Texts().Where(t => !t.IsDetail))
        {
            foreach (var term in TextGuide.Jargon)
            {
                if (term.Pattern.IsMatch(text.Value) && !TextGuide.IsExcused(text, term.Name))
                {
                    found.Add($"{text.Language} {text.Id} has «{term.Name}» (say: {term.Say}): {text.Value.ReplaceLineEndings(" / ")}");
                }
            }
        }

        Assert.True(found.Count == 0, "Jargon in first-level texts:" + Report(found));
    }

    [Fact]
    public void The_word_commit_appears_only_as_a_saved_version_with_commit_in_parentheses()
    {
        var found = new List<string>();
        foreach (var text in TextGuide.Texts())
        {
            if (TextGuide.Commit.Pattern.IsMatch(TextGuide.WithoutCommitGloss(text.Value)) && !TextGuide.IsExcused(text, "commit"))
            {
                found.Add($"{text.Language} {text.Id}: {text.Value.ReplaceLineEndings(" / ")}");
            }
        }

        Assert.True(found.Count == 0, "«commit» outside «versión guardada (commit)»:" + Report(found));
    }

    [Fact]
    public void What_the_glossary_translates_does_not_appear_with_the_old_term()
    {
        var found = new List<string>();
        foreach (var text in TextGuide.Texts())
        {
            foreach (var term in TextGuide.Glossary)
            {
                var pattern = text.Language == "es" ? term.Spanish : term.English;
                if (pattern is not null && pattern.IsMatch(text.Value) && !TextGuide.IsExcused(text, term.Name))
                {
                    found.Add($"{text.Language} {text.Id} has «{term.Name}» (say: {term.Say}): {text.Value.ReplaceLineEndings(" / ")}");
                }
            }
        }

        Assert.True(found.Count == 0, "Old terms of the glossary:" + Report(found));
    }

    [Fact]
    public void Every_explanation_in_spanish_reads_at_inflesz_55_or_more()
    {
        var low = new List<string>();
        var checkedTexts = 0;
        foreach (var text in TextGuide.Texts().Where(t => t.IsExplanation))
        {
            checkedTexts++;
            var index = SpanishReadability.Inflesz(text.Value)!.Value;
            if (index < TextGuide.MinInflesz && !TextGuide.IsExcused(text, "inflesz"))
            {
                low.Add($"{text.Id} (INFLESZ {index.ToString("F1", CultureInfo.InvariantCulture)}): {text.Value.ReplaceLineEndings(" / ")}");
            }
        }

        Assert.True(checkedTexts > 300, $"Only {checkedTexts} explanations were measured.");
        Assert.True(low.Count == 0, $"Explanations under INFLESZ {TextGuide.MinInflesz}:" + Report(low));
    }
}
