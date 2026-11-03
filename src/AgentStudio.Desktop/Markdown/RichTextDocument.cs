using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AgentStudio.Desktop.Markdown;

/// <summary>
/// The document of a <see cref="RichTextBox"/> as a value a binding can set: <see cref="RichTextBox.Document"/> is not a
/// dependency property. The journal (V31) binds it to its Markdown through <see cref="MarkdownToFlowDocumentConverter"/>.
/// </summary>
public static class RichTextDocument
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.RegisterAttached(
        "Document", typeof(FlowDocument), typeof(RichTextDocument), new PropertyMetadata(null, OnDocumentChanged));

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

    private static void OnDocumentChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is RichTextBox box)
        {
            // A document belongs to one box: the converter builds a new one for each text.
            box.Document = e.NewValue as FlowDocument ?? new FlowDocument();
        }
    }
}
