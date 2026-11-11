using System.Globalization;
using System.IO.Compression;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Security;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AgentStudio.Infrastructure.Documents.Docx;

/// <summary>
/// Writes the model of a document for clients as Word (.docx) with the Open XML SDK (docs/17 D-59; ADR-0057). It is deterministic: the
/// same model and language give the same bytes. The dates of the package are the date of the cover (never the clock), the relationships
/// have fixed ids, and the package is repacked with the entries in a fixed order and a fixed time, because the SDK writes the time of
/// the machine in the entries of the archive. What an agent wrote carries its mark. No external link, macro or image goes in.
/// </summary>
public sealed class WordDocumentWriter(IUserTextRenderer texts, ISecretRedactor redactor) : IClientDocumentFormatWriter
{
    /// <summary>The time every entry of the archive carries: the earliest the format can say.</summary>
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string MonoFont = "Consolas";

    public ClientDocumentFormat Format => ClientDocumentFormat.Word;

    public Task<byte[]> RenderAsync(ClientDocument document, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ClientDocumentReading.EnsureWritable(document, redactor);
        var reading = new ClientDocumentReading(texts, language);
        using var raw = new MemoryStream();
        using (var package = WordprocessingDocument.Create(raw, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>("rId1");
            styles.Styles = BuildStyles(reading.Language);
            var body = new Body();
            main.Document = new Document(body);

            body.Append(Paragraph(reading.Title(document), "Title"));
            foreach (var (label, value) in reading.CoverLines(document))
            {
                body.Append(new Paragraph(Run(label + ": ", bold: true), Run(value)));
            }

            foreach (var section in document.Sections)
            {
                ct.ThrowIfCancellationRequested();
                body.Append(Paragraph(reading.SectionTitle(section.Kind), "Heading1"));
                foreach (var block in section.Blocks)
                {
                    Append(body, block, reading);
                }
            }

            body.Append(new SectionProperties(new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134 }));
            main.Document.Save();

            package.PackageProperties.Title = reading.Title(document);
            package.PackageProperties.Creator = "Agent Studio";
            package.PackageProperties.LastModifiedBy = "Agent Studio";
            package.PackageProperties.Created = document.Cover.GeneratedAt.UtcDateTime;
            package.PackageProperties.Modified = document.Cover.GeneratedAt.UtcDateTime;
        }

        return Task.FromResult(Repack(raw.ToArray()));
    }

    /// <summary>The same archive, entries in a fixed order (<c>[Content_Types].xml</c> first) and with a fixed time.</summary>
    private static byte[] Repack(byte[] source)
    {
        using var input = new MemoryStream(source, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var packed = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in archive.Entries
                .OrderBy(e => e.FullName == "[Content_Types].xml" ? 0 : 1)
                .ThenBy(e => e.FullName, StringComparer.Ordinal))
            {
                var copy = packed.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = EntryTime;
                using var from = entry.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
        }

        return output.ToArray();
    }

    private static Styles BuildStyles(string language) => new(
        new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                new FontSize { Val = "22" },
                new Languages { Val = language })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
        new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
        new Style(
            new StyleName { Val = "Title" }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
            new StyleRunProperties(new Bold(), new FontSize { Val = "40" }))
        { Type = StyleValues.Paragraph, StyleId = "Title" },
        new Style(
            new StyleName { Val = "heading 1" }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "360", After = "120" }, new OutlineLevel { Val = 0 }),
            new StyleRunProperties(new Bold(), new FontSize { Val = "30" }))
        { Type = StyleValues.Paragraph, StyleId = "Heading1" },
        new Style(
            new StyleName { Val = "Agent mark" }, new BasedOn { Val = "Normal" },
            new StyleRunProperties(new Italic(), new Color { Val = "5A4700" }, new FontSize { Val = "20" }))
        { Type = StyleValues.Paragraph, StyleId = "AgentMark" });

    private static Paragraph Paragraph(string text, string style) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), Run(text));

    private static Run Run(string text, bool bold = false, bool italic = false, string? font = null)
    {
        var properties = new RunProperties();
        if (font is not null)
        {
            properties.Append(new RunFonts { Ascii = font, HighAnsi = font });
        }

        if (bold)
        {
            properties.Append(new Bold());
        }

        if (italic)
        {
            properties.Append(new Italic());
        }

        var run = new Run();
        if (properties.HasChildren)
        {
            run.Append(properties);
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                run.Append(new Break());
            }

            run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return run;
    }

    private static Run Mark(string mark) => Run(" (" + mark + ")", italic: true);

    /// <summary>The runs of a text: the words and, for an agent's, its mark.</summary>
    private static IEnumerable<Run> Runs(RenderedClientText text, bool bold = false)
    {
        yield return Run(text.Value, bold);
        if (text.Mark is not null)
        {
            yield return Mark(text.Mark);
        }
    }

    private static TableCell Cell(IEnumerable<Run> runs, bool header)
    {
        var properties = new TableCellProperties();
        if (header)
        {
            properties.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "EEEEEE" });
        }

        var paragraph = new Paragraph();
        paragraph.Append(runs);
        return new TableCell(properties, paragraph);
    }

    private static void Append(Body body, ClientDocumentBlock block, ClientDocumentReading reading)
    {
        switch (block)
        {
            case ClientParagraph paragraph:
                var rendered = reading.Text(paragraph.Text);
                body.Append(new Paragraph(Run(rendered.Value)));
                if (rendered.Mark is not null)
                {
                    body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "AgentMark" }), Run(rendered.Mark, italic: true)));
                }

                break;
            case ClientList list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    var item = new Paragraph(new ParagraphProperties(new Indentation { Left = "425", Hanging = "283" }));
                    item.Append(Run((list.Ordered ? (i + 1).ToString(CultureInfo.InvariantCulture) + "." : "•") + "\t"));
                    item.Append(Runs(reading.Text(list.Items[i])));
                    body.Append(item);
                }

                break;
            case ClientTable table:
                var grid = new Table(new TableProperties(
                    new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                    new TableBorders(
                        new TopBorder { Val = BorderValues.Single, Size = 4 },
                        new BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new LeftBorder { Val = BorderValues.Single, Size = 4 },
                        new RightBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
                grid.Append(new TableRow(table.Header.Select(h => Cell(Runs(reading.Text(h), bold: true), header: true))));
                foreach (var row in table.Rows)
                {
                    grid.Append(new TableRow(row.Select(c => Cell(Runs(reading.Text(c)), header: false))));
                }

                body.Append(grid);
                body.Append(new Paragraph());
                break;
            case ClientEvidenceList evidence:
                foreach (var reference in evidence.References)
                {
                    var item = new Paragraph(new ParagraphProperties(new Indentation { Left = "425", Hanging = "283" }));
                    item.Append(Run("•\t" + reference.Evidence.Value + " · "));
                    item.Append(Runs(reading.Text(reference.Label)));
                    item.Append(Run(" · SHA-256: "));
                    item.Append(Run(reference.Sha256, font: MonoFont));
                    body.Append(item);
                }

                break;
            default:
                throw new InvalidOperationException($"{block.GetType().Name} is not a block of a document for clients.");
        }
    }
}
