using System.Globalization;
using System.IO;
using System.Text;

namespace AgentStudio.Tests.Desktop.Texts;

/// <summary>
/// F14-5, «y tu lectura»: the report for the reading with the person. For each resource area, the ten explanations in Spanish
/// with the lowest INFLESZ and the ten with the longest sentence, to read. It is a measurement, outside the CI: it never fails.
/// </summary>
/// <remarks>
/// Run it with <c>dotnet test -- --filter-trait "Category=Measurement" --filter-class "*TextGuideReportTests"</c>. It writes
/// <c>TextGuideReport.md</c> next to the test assembly, or to the path in the environment variable <c>AGENTSTUDIO_TEXT_REPORT</c>,
/// and the test output names the file.
/// </remarks>
public sealed class TextGuideReportTests
{
    private const int PerArea = 10;

    [Fact]
    [Trait("Category", "Measurement")]
    public void The_report_for_the_reading_with_the_person_is_written()
    {
        var texts = TextGuide.Texts().Where(t => t.Language == "es" && t.IsExplanation).ToList();
        var report = new StringBuilder();
        report.AppendLine("# Informe de claridad de los textos (F14-5)");
        report.AppendLine();
        report.AppendLine($"Umbral: INFLESZ {TextGuide.MinInflesz} o más; frases de {TextGuide.MaxWordsPerSentence} palabras como mucho. "
            + $"Se mide cada explicación en castellano de {TextGuide.ExplanationMinWords} palabras o más. Por área, los {PerArea} textos con el INFLESZ más bajo.");
        foreach (var area in texts.Select(t => t.Area).Distinct().OrderBy(a => a, StringComparer.Ordinal))
        {
            var ofArea = texts.Where(t => t.Area == area)
                .Select(t => (Text: t, Index: SpanishReadability.Inflesz(t.Value)!.Value, Longest: SpanishReadability.LongestSentence(t.Value).Words))
                .OrderBy(x => x.Index)
                .ToList();
            report.AppendLine();
            report.AppendLine($"## {area} ({ofArea.Count} explicaciones, INFLESZ medio {ofArea.Average(x => x.Index).ToString("F1", CultureInfo.InvariantCulture)})");
            report.AppendLine();
            foreach (var x in ofArea.Take(PerArea))
            {
                report.AppendLine($"- `{x.Text.Key}` · INFLESZ {x.Index.ToString("F1", CultureInfo.InvariantCulture)} · frase más larga {x.Longest} palabras");
                foreach (var line in x.Text.Value.ReplaceLineEndings("\n").Split('\n'))
                {
                    report.AppendLine($"  > {line}");
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("AGENTSTUDIO_TEXT_REPORT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "TextGuideReport.md");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine("Text guide report: " + path);
        Assert.True(File.Exists(path));
    }
}
