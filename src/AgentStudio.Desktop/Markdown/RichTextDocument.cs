using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AgentStudio.Desktop.Markdown;

/// <summary>
/// The document of a <see cref="RichTextBox"/> as a value a binding can set: <see cref="RichTextBox.Document"/> is not a
/// dependency property. The journal (V31) binds it to its Markdown through <see cref="MarkdownToFlowDocumentConverter"/>.
/// <para>
/// V32: the journal is read again by itself while a run writes it, and each read is a new document. Whoever reads it keeps
/// their place, while the document is of the same <see cref="ScopeProperty"/> (the same project and day) and the one before
/// had something to read: at the end it follows the new entries; further up, the entry at the top of the view stays at the
/// top, even when a long day drops its oldest entries above it. A document of another scope, or the first one, starts at the
/// top.
/// </para>
/// </summary>
public static class RichTextDocument
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.RegisterAttached(
        "Document", typeof(FlowDocument), typeof(RichTextDocument), new PropertyMetadata(null, OnDocumentChanged));

    /// <summary>What the documents are of (the project and the day of the journal): the place is kept only within one.</summary>
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(string), typeof(RichTextDocument), new PropertyMetadata(null));

    /// <summary>The scope of the document shown now.</summary>
    private static readonly DependencyProperty ShownScopeProperty = DependencyProperty.RegisterAttached(
        "ShownScope", typeof(string), typeof(RichTextDocument), new PropertyMetadata(null));

    public static FlowDocument? GetDocument(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (FlowDocument?)element.GetValue(DocumentProperty);
    }

    public static void SetDocument(DependencyObject element, FlowDocument? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(DocumentProperty, value);
    }

    public static string? GetScope(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(ScopeProperty);
    }

    public static void SetScope(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ScopeProperty, value);
    }

    private static void OnDocumentChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not RichTextBox box)
        {
            return;
        }

        var scope = GetScope(box);
        var keep = scope is not null
            && string.Equals(scope, (string?)box.GetValue(ShownScopeProperty), StringComparison.Ordinal)
            && e.OldValue is FlowDocument old && HasText(old)
            && box.ExtentHeight > 0;
        var offset = box.VerticalOffset;
        var atEnd = offset + box.ViewportHeight >= box.ExtentHeight - 1;
        var anchor = keep && !atEnd ? AnchorAtTop(box) : null;

        // A document belongs to one box: the converter builds a new one for each text.
        box.Document = e.NewValue as FlowDocument ?? new FlowDocument();
        box.SetValue(ShownScopeProperty, scope);
        if (!keep)
        {
            box.ScrollToHome();
            return;
        }

        box.UpdateLayout();
        if (atEnd)
        {
            box.ScrollToEnd();
            return;
        }

        // The pixels first; then, when the entry that was at the top is still there, the shift that puts it back at the top.
        box.ScrollToVerticalOffset(offset);
        box.UpdateLayout();
        if (anchor is { } kept && Find(box.Document, kept) is { } paragraph)
        {
            var shift = Top(paragraph) - kept.Top;
            if (Math.Abs(shift) > 0.5)
            {
                box.ScrollToVerticalOffset(offset + shift);
            }
        }
    }

    /// <summary>The paragraph at the top of the view: its text, which of the paragraphs with that text it is, and where it is.</summary>
    private static Anchor? AnchorAtTop(RichTextBox box)
    {
        if (box.GetPositionFromPoint(new Point(box.Padding.Left + 4, box.Padding.Top + 2), snapToText: true)?.Paragraph is not { } top)
        {
            return null;
        }

        var text = TextOf(top);
        var occurrence = Paragraphs(box.Document.Blocks).TakeWhile(p => !ReferenceEquals(p, top)).Count(p => TextOf(p) == text);
        return new Anchor(text, occurrence, Top(top));
    }

    private static Paragraph? Find(FlowDocument document, Anchor anchor) =>
        Paragraphs(document.Blocks).Where(p => TextOf(p) == anchor.Text).Skip(anchor.Occurrence).FirstOrDefault();

    private static double Top(Paragraph paragraph) => paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;

    private static string TextOf(Paragraph paragraph) => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;

    private static bool HasText(FlowDocument document) =>
        !string.IsNullOrWhiteSpace(new TextRange(document.ContentStart, document.ContentEnd).Text);

    /// <summary>Every paragraph of the blocks, in order, those of lists and sections included.</summary>
    private static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case List list:
                    foreach (var nested in list.ListItems.SelectMany(item => Paragraphs(item.Blocks)))
                    {
                        yield return nested;
                    }

                    break;
                case Section section:
                    foreach (var nested in Paragraphs(section.Blocks))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    /// <summary>The entry at the top of the view, to find it again in the next document.</summary>
    private sealed record Anchor(string Text, int Occurrence, double Top);
}
