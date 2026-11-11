using System.IO;
using System.IO.Compression;
using System.Text;
using AgentStudio.Application.Documents;
using AgentStudio.Desktop.Documents;
using AgentStudio.Infrastructure.Documents.Docx;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Memory.Documents;
using DocumentFormat.OpenXml.Packaging;

namespace AgentStudio.Tests.Infrastructure.Documents;

/// <summary>The writer of Word (docs/17 D-59; ADR-0057): a valid package, golden content, the same bytes for the same model, and the mark of an agent.</summary>
public sealed class WordDocumentWriterTests
{
    private static readonly SecretRedactor Redactor = new(@"C:\Users\persona");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WordDocumentWriter Writer() => new(new ResourceUserTextRenderer(), Redactor);

    private static string Entry(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en")]
    public async Task The_content_of_the_package_matches_its_golden_files(string language)
    {
        var docx = await Writer().RenderAsync(GoldenModel.Build(), language, Ct);
        Golden.Check("Infrastructure/Documents", $"cliente-{language}.document.xml", Entry(docx, "word/document.xml"));
        Golden.Check("Infrastructure/Documents", $"cliente-{language}.core.xml", Entry(docx, "package/services/metadata/core-properties/agentstudio-core.psmdcp"));
    }

    [Fact]
    public async Task The_package_opens_with_the_sdk_and_has_no_time_of_the_machine()
    {
        var docx = await Writer().RenderAsync(GoldenModel.Build(), "es-ES", Ct);
        using (var package = WordprocessingDocument.Open(new MemoryStream(docx), false))
        {
            Assert.Contains("Alcance", package.MainDocumentPart!.Document!.InnerText, StringComparison.Ordinal);
        }

        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        Assert.Equal("[Content_Types].xml", zip.Entries[0].FullName);
        Assert.All(zip.Entries, e => Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), e.LastWriteTime.DateTime));
    }

    [Fact]
    public async Task The_same_model_gives_the_same_bytes_even_when_the_clock_has_moved()
    {
        var first = await Writer().RenderAsync(GoldenModel.Build(), "es-ES", Ct);
        await Task.Delay(2300, Ct);
        var second = await Writer().RenderAsync(GoldenModel.Build(), "es-ES", Ct);
        using var one = new ZipArchive(new MemoryStream(first), ZipArchiveMode.Read);
        using var two = new ZipArchive(new MemoryStream(second), ZipArchiveMode.Read);
        Assert.Equal(one.Entries.Select(e => e.FullName), two.Entries.Select(e => e.FullName));
        foreach (var entry in one.Entries)
        {
            Assert.Equal(Entry(first, entry.FullName), Entry(second, entry.FullName));
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task What_an_agent_wrote_carries_its_mark()
    {
        var docx = await Writer().RenderAsync(GoldenModel.Build(), "es-ES", Ct);
        var xml = Entry(docx, "word/document.xml");
        Assert.Contains("Redactado por un agente (ejecución aux-7)", xml, StringComparison.Ordinal);
        Assert.Contains("revisado por la persona responsable: no", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_breaks_its_rules_is_not_written()
    {
        var dirty = GoldenModel.Build() with
        {
            Sections = [new(ClientDocumentSectionKind.Summary, [new ClientParagraph(ClientText.Written("password=hunter2xyz"))])],
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().RenderAsync(dirty, "es-ES", Ct));
    }
}
