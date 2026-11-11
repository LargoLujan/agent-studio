using System.Globalization;
using System.Text;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Security;
using AgentStudio.Memory.Export;

namespace AgentStudio.Memory.Documents;

/// <summary>
/// Writes the model of a document for clients as Markdown (docs/17 D-59; ADR-0057), with no library: UTF-8 without BOM and <c>\n</c>,
/// no time of writing, so the same model and language always give the same bytes. Everything that is data (what a person or an agent
/// wrote, names, hashes) is escaped as in the export of the memory, so it can never become a heading, a link, a tag or a list of its
/// own; what an agent wrote carries its mark. The Markdown that is exported also stays as evidence.
/// </summary>
public sealed class MarkdownDocumentWriter(IUserTextRenderer texts, ISecretRedactor redactor) : IClientDocumentFormatWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public ClientDocumentFormat Format => ClientDocumentFormat.Markdown;

    public Task<byte[]> RenderAsync(ClientDocument document, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ClientDocumentReading.EnsureWritable(document, redactor);
        var reading = new ClientDocumentReading(texts, language);
        var text = new StringBuilder();
        text.Append("# ").Append(Escape(reading.Title(document))).Append("\n\n");
        foreach (var (label, value) in reading.CoverLines(document))
        {
            text.Append("- **").Append(Escape(label)).Append(":** ").Append(Escape(value)).Append('\n');
        }

        foreach (var section in document.Sections)
        {
            ct.ThrowIfCancellationRequested();
            text.Append("\n## ").Append(Escape(reading.SectionTitle(section.Kind))).Append('\n');
            foreach (var block in section.Blocks)
            {
                text.Append('\n');
                Append(text, block, reading);
            }
        }

        return Task.FromResult(Utf8.GetBytes(text.ToString()));
    }

    private static string Escape(string value)
    {
        var escaped = MarkdownText.Escape(value);
        // What the escape leaves alone but a line may start a list or a heading with.
        if (escaped.StartsWith("- ", StringComparison.Ordinal) || escaped.StartsWith("+ ", StringComparison.Ordinal) || escaped.StartsWith('~'))
        {
            return "\\" + escaped;
        }

        var digits = 0;
        while (digits < escaped.Length && char.IsAsciiDigit(escaped[digits]))
        {
            digits++;
        }

        return digits > 0 && digits < escaped.Length && escaped[digits] is '.' or ')' ? escaped[..digits] + "\\" + escaped[digits..] : escaped;
    }

    private static string Inline(RenderedClientText text) =>
        Escape(text.Value.ReplaceLineEndings(" ")) + (text.Mark is null ? string.Empty : " _(" + MarkdownText.Escape(text.Mark) + ")_");

    private static void Append(StringBuilder text, ClientDocumentBlock block, ClientDocumentReading reading)
    {
        switch (block)
        {
            case ClientParagraph paragraph:
                var rendered = reading.Text(paragraph.Text);
                text.Append(string.Join("  \n", rendered.Value.Split('\n').Select(Escape))).Append('\n');
                if (rendered.Mark is not null)
                {
                    text.Append("\n_").Append(MarkdownText.Escape(rendered.Mark)).Append("_\n");
                }

                break;
            case ClientList list:
                for (var i = 0; i < list.Items.Count; i++)
                {
                    text.Append(list.Ordered ? (i + 1).ToString(CultureInfo.InvariantCulture) + ". " : "- ").Append(Inline(reading.Text(list.Items[i]))).Append('\n');
                }

                break;
            case ClientTable table:
                text.Append("| ").Append(string.Join(" | ", table.Header.Select(h => Inline(reading.Text(h))))).Append(" |\n");
                text.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", table.Header.Count))).Append('\n');
                foreach (var row in table.Rows)
                {
                    text.Append("| ").Append(string.Join(" | ", row.Select(c => Inline(reading.Text(c))))).Append(" |\n");
                }

                break;
            case ClientEvidenceList evidence:
                foreach (var reference in evidence.References)
                {
                    text.Append("- ").Append(Escape(reference.Evidence.Value)).Append(" · ").Append(Inline(reading.Text(reference.Label)))
                        .Append(" · SHA-256: `").Append(reference.Sha256).Append("`\n");
                }

                break;
            default:
                throw new InvalidOperationException($"{block.GetType().Name} is not a block of a document for clients.");
        }
    }
}
