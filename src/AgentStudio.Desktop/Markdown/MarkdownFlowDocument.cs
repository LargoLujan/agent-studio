using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using DocumentList = System.Windows.Documents.List;

namespace AgentStudio.Desktop.Markdown;

/// <summary>The look of a document built from Markdown: the brushes and the fonts of the theme.</summary>
public sealed record MarkdownLook(Brush Foreground, Brush SecondaryForeground, Brush CodeBackground, FontFamily Font, FontFamily CodeFont, double FontSize = 13);

/// <summary>
/// A read-only document from the Markdown of <see cref="MarkdownLite"/> (V31): titles, paragraphs in the secondary colour,
/// lists with their sub-entries, bold, code in the monospaced font over its own background. Built in code, so nothing of the
/// text is ever parsed as XAML.
/// </summary>
public static class MarkdownFlowDocument
{
    public static FlowDocument Build(string? markdown, MarkdownLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        var document = new FlowDocument
        {
            FontFamily = look.Font,
            FontSize = look.FontSize,
            Foreground = look.Foreground,
            Background = Brushes.Transparent,
            PagePadding = new Thickness(14, 10, 14, 14),
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = false,
        };

        // The lists open at each depth: an entry two spaces in goes in a list inside the entry above it.
        var lists = new List<DocumentList>();
        foreach (var block in MarkdownLite.Parse(markdown))
        {
            if (block is MarkdownListItem item)
            {
                AddItem(document, lists, item, look);
                continue;
            }

            lists.Clear();
            document.Blocks.Add(block switch
            {
                MarkdownHeading heading => ToHeading(heading, look),
                MarkdownParagraph paragraph => ToParagraph(paragraph, look),
                MarkdownCodeBlock code => ToCode(code, look),
                _ => throw new InvalidOperationException($"Unknown Markdown block: {block.GetType().Name}."),
            });
        }

        return document;
    }

    private static void AddItem(FlowDocument document, List<DocumentList> lists, MarkdownListItem item, MarkdownLook look)
    {
        // A sub-entry with no entry above it goes one level under the last list, never deeper.
        var depth = Math.Min(item.Depth, lists.Count);
        while (lists.Count > depth + 1)
        {
            lists.RemoveAt(lists.Count - 1);
        }

        if (lists.Count == depth)
        {
            // The sub-entries (a result, a «Hecho» line) in the secondary colour, so each entry reads first.
            var list = new DocumentList
            {
                MarkerStyle = TextMarkerStyle.Disc,
                Padding = new Thickness(18, 0, 0, 0),
                Margin = depth == 0 ? new Thickness(0, 0, 0, 8) : new Thickness(0, 1, 0, 1),
            };
            if (depth == 0)
            {
                document.Blocks.Add(list);
            }
            else
            {
                list.Foreground = look.SecondaryForeground;
                lists[depth - 1].ListItems.LastListItem!.Blocks.Add(list);
            }

            lists.Add(list);
        }

        lists[depth].ListItems.Add(new ListItem(ToLine(item.Spans, look, new Thickness(0, 1, 0, 1))));
    }

    private static Paragraph ToHeading(MarkdownHeading heading, MarkdownLook look)
    {
        var paragraph = ToLine(heading.Spans, look, heading.Level == 1 ? new Thickness(0, 0, 0, 10) : new Thickness(0, 12, 0, 6));
        paragraph.FontSize = heading.Level switch
        {
            1 => look.FontSize + 5,
            2 => look.FontSize + 2.5,
            _ => look.FontSize + 1,
        };
        paragraph.FontWeight = FontWeights.SemiBold;
        return paragraph;
    }

    private static Paragraph ToParagraph(MarkdownParagraph paragraph, MarkdownLook look)
    {
        var block = new Paragraph { Margin = new Thickness(0, 0, 0, 10), Foreground = look.SecondaryForeground };
        for (var i = 0; i < paragraph.Lines.Count; i++)
        {
            if (i > 0)
            {
                block.Inlines.Add(new LineBreak());
            }

            AddSpans(block.Inlines, paragraph.Lines[i], look);
        }

        return block;
    }

    private static Paragraph ToCode(MarkdownCodeBlock code, MarkdownLook look)
    {
        var block = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8, 6, 8, 6),
            Background = look.CodeBackground,
            FontFamily = look.CodeFont,
            FontSize = look.FontSize - 1,
        };
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0)
            {
                block.Inlines.Add(new LineBreak());
            }

            block.Inlines.Add(new Run(code.Lines[i]));
        }

        return block;
    }

    private static Paragraph ToLine(IReadOnlyList<MarkdownSpan> spans, MarkdownLook look, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        AddSpans(paragraph.Inlines, spans, look);
        return paragraph;
    }

    private static void AddSpans(InlineCollection inlines, IReadOnlyList<MarkdownSpan> spans, MarkdownLook look)
    {
        foreach (var span in spans)
        {
            inlines.Add(span.Kind switch
            {
                MarkdownSpanKind.Bold => new Bold(new Run(span.Text)),
                MarkdownSpanKind.Code => new Run(span.Text) { FontFamily = look.CodeFont, FontSize = look.FontSize - 1, Background = look.CodeBackground },
                _ => new Run(span.Text),
            });
        }
    }
}

/// <summary>
/// Markdown text to the document a <see cref="System.Windows.Controls.FlowDocumentScrollViewer"/> shows
/// (<see cref="MarkdownFlowDocument"/>), with the look its properties give (set from the theme where it is declared). One
/// way: the document is read-only.
/// </summary>
public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    public Brush Foreground { get; set; } = Brushes.Black;

    public Brush SecondaryForeground { get; set; } = Brushes.DimGray;

    public Brush CodeBackground { get; set; } = Brushes.Transparent;

    public FontFamily FontFamily { get; set; } = new("Segoe UI");

    public FontFamily CodeFontFamily { get; set; } = new("Consolas");

    public double FontSize { get; set; } = 13;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        MarkdownFlowDocument.Build(value as string, new MarkdownLook(Foreground, SecondaryForeground, CodeBackground, FontFamily, CodeFontFamily, FontSize));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
