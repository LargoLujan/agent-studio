using System.Net;
using System.Text;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Security;

namespace AgentStudio.Memory.Documents;

/// <summary>
/// Writes the model of a document for clients as one self-contained HTML page (docs/17 D-59; ADR-0057), with no library: the styles
/// are inline, there is no script (and a policy that forbids it, and any network request), and the same model and language always give
/// the same bytes. Everything that is data is HTML-encoded. It is also the page the PDF is printed from, so what the leak test sees in
/// the HTML is what the PDF holds.
/// </summary>
public sealed class HtmlDocumentWriter(IUserTextRenderer texts, ISecretRedactor redactor) : IClientDocumentFormatWriter
{
    private const string Style =
        "body{font-family:Segoe UI,Arial,sans-serif;line-height:1.5;max-width:60rem;margin:2rem auto;padding:0 1rem;color:#1b1b1b}"
        + "h1{font-size:1.8rem;margin-bottom:.5rem}h2{font-size:1.3rem;margin-top:2rem;border-bottom:1px solid #bbb;padding-bottom:.2rem}"
        + "dl.cover{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem}dl.cover dt{font-weight:600}dl.cover dd{margin:0}"
        + "table{border-collapse:collapse;width:100%;margin:.5rem 0}th,td{border:1px solid #999;padding:.3rem .5rem;text-align:left;vertical-align:top}"
        + "th{background:#eee}tr{page-break-inside:avoid}.agent{border-left:3px solid #8a6d00;padding-left:.5rem}.mark{color:#5a4700;font-style:italic;font-size:.9em}"
        + "code{font-family:Consolas,monospace;font-size:.9em;word-break:break-all}@page{margin:18mm}";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public ClientDocumentFormat Format => ClientDocumentFormat.Html;

    public Task<byte[]> RenderAsync(ClientDocument document, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ClientDocumentReading.EnsureWritable(document, redactor);
        var reading = new ClientDocumentReading(texts, language);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"").Append(Encode(reading.Language)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Encode(reading.Title(document))).Append("</title>\n<style>").Append(Style).Append("</style>\n</head>\n<body>\n")
            .Append("<h1>").Append(Encode(reading.Title(document))).Append("</h1>\n<dl class=\"cover\">\n");
        foreach (var (label, value) in reading.CoverLines(document))
        {
            html.Append("<dt>").Append(Encode(label)).Append("</dt><dd>").Append(Encode(value)).Append("</dd>\n");
        }

        html.Append("</dl>\n");
        foreach (var section in document.Sections)
        {
            ct.ThrowIfCancellationRequested();
            html.Append("<section>\n<h2>").Append(Encode(reading.SectionTitle(section.Kind))).Append("</h2>\n");
            foreach (var block in section.Blocks)
            {
                Append(html, block, reading);
            }

            html.Append("</section>\n");
        }

        html.Append("</body>\n</html>\n");
        return Task.FromResult(Utf8.GetBytes(html.ToString()));
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Inline(RenderedClientText text) => text.Mark is null
        ? Encode(text.Value).Replace("\n", "<br>", StringComparison.Ordinal)
        : "<span class=\"agent\">" + Encode(text.Value).Replace("\n", "<br>", StringComparison.Ordinal) + "</span> <span class=\"mark\">(" + Encode(text.Mark) + ")</span>";

    private static void Append(StringBuilder html, ClientDocumentBlock block, ClientDocumentReading reading)
    {
        switch (block)
        {
            case ClientParagraph paragraph:
                html.Append("<p>").Append(Inline(reading.Text(paragraph.Text))).Append("</p>\n");
                break;
            case ClientList list:
                var tag = list.Ordered ? "ol" : "ul";
                html.Append('<').Append(tag).Append(">\n");
                foreach (var item in list.Items)
                {
                    html.Append("<li>").Append(Inline(reading.Text(item))).Append("</li>\n");
                }

                html.Append("</").Append(tag).Append(">\n");
                break;
            case ClientTable table:
                html.Append("<table>\n<thead><tr>");
                foreach (var cell in table.Header)
                {
                    html.Append("<th>").Append(Inline(reading.Text(cell))).Append("</th>");
                }

                html.Append("</tr></thead>\n<tbody>\n");
                foreach (var row in table.Rows)
                {
                    html.Append("<tr>");
                    foreach (var cell in row)
                    {
                        html.Append("<td>").Append(Inline(reading.Text(cell))).Append("</td>");
                    }

                    html.Append("</tr>\n");
                }

                html.Append("</tbody>\n</table>\n");
                break;
            case ClientEvidenceList evidence:
                html.Append("<ul>\n");
                foreach (var reference in evidence.References)
                {
                    html.Append("<li>").Append(Encode(reference.Evidence.Value)).Append(" · ").Append(Inline(reading.Text(reference.Label)))
                        .Append(" · SHA-256: <code>").Append(Encode(reference.Sha256)).Append("</code></li>\n");
                }

                html.Append("</ul>\n");
                break;
            default:
                throw new InvalidOperationException($"{block.GetType().Name} is not a block of a document for clients.");
        }
    }
}
