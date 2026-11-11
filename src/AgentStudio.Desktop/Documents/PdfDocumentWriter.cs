using AgentStudio.Application.Documents;

namespace AgentStudio.Desktop.Documents;

/// <summary>
/// Prints a page of HTML to PDF (docs/17 D-59). It is a port so the writer of PDF and everything above it are tested without a
/// window: the real one is <see cref="WebView2PdfPrinter"/>, which needs the WebView2 runtime and a desktop.
/// </summary>
public interface IHtmlPdfPrinter
{
    /// <summary>The PDF of <paramref name="html"/> (UTF-8), in memory: nothing is saved by the printer, so nothing is left half written.</summary>
    Task<byte[]> PrintAsync(byte[] html, CancellationToken ct);
}

/// <summary>
/// The writer of PDF (docs/17 D-59; ADR-0057): the HTML of the same model, printed with WebView2 (<see cref="IHtmlPdfPrinter"/>), one
/// document at a time. The HTML is the one the leak test checks, so what the PDF holds is what that test saw. The bytes of a PDF
/// carry the date it was printed: what is the same for the same model and language is the HTML it comes from.
/// </summary>
public sealed class PdfDocumentWriter : IClientDocumentFormatWriter
{
    private readonly IClientDocumentFormatWriter _html;
    private readonly IHtmlPdfPrinter _printer;

    /// <param name="html">The writer of HTML the page comes from.</param>
    /// <param name="printer">Prints the page.</param>
    /// <exception cref="ArgumentException"><paramref name="html"/> does not write HTML.</exception>
    public PdfDocumentWriter(IClientDocumentFormatWriter html, IHtmlPdfPrinter printer)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(printer);
        if (html.Format != ClientDocumentFormat.Html)
        {
            throw new ArgumentException("The page of the PDF comes from the writer of HTML.", nameof(html));
        }

        _html = html;
        _printer = printer;
    }

    public ClientDocumentFormat Format => ClientDocumentFormat.Pdf;

    public async Task<byte[]> RenderAsync(ClientDocument document, string language, CancellationToken ct)
    {
        var page = await _html.RenderAsync(document, language, ct).ConfigureAwait(false);
        return await _printer.PrintAsync(page, ct).ConfigureAwait(false);
    }
}
