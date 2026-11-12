using System.Text;
using AgentStudio.Application.Documents;
using AgentStudio.Desktop.Documents;
using AgentStudio.Memory.Documents;
using AgentStudio.Security.Redaction;

namespace AgentStudio.Tests.Memory.Documents;

/// <summary>The writers of Markdown and HTML (docs/17 D-59; ADR-0057): golden files in both languages, determinism and the rules they keep.</summary>
public sealed class ClientDocumentWritersTests
{
    private static readonly SecretRedactor Redactor = new(@"C:\Users\persona");
    private static readonly ResourceUserTextRenderer Texts = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> RenderAsync(IClientDocumentFormatWriter writer, string language) =>
        Encoding.UTF8.GetString(await writer.RenderAsync(GoldenModel.Build(), language, Ct));

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en")]
    public async Task The_markdown_matches_its_golden_file(string language)
    {
        Golden.Check("Memory/Documents", $"cliente-{language}.md", await RenderAsync(new MarkdownDocumentWriter(Texts, Redactor), language));
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("en")]
    public async Task The_html_matches_its_golden_file(string language)
    {
        Golden.Check("Memory/Documents", $"cliente-{language}.html", await RenderAsync(new HtmlDocumentWriter(Texts, Redactor), language));
    }

    [Fact]
    public async Task The_same_model_and_language_give_the_same_bytes_and_the_language_changes_only_the_system_text()
    {
        IClientDocumentFormatWriter[] writers = [new MarkdownDocumentWriter(Texts, Redactor), new HtmlDocumentWriter(Texts, Redactor)];
        foreach (var writer in writers)
        {
            var first = await writer.RenderAsync(GoldenModel.Build(), "es-ES", Ct);
            var second = await writer.RenderAsync(GoldenModel.Build(), "es-ES", Ct);
            Assert.Equal(first, second);
            var english = await RenderAsync(writer, "en");
            Assert.Contains("Pagar con tarjeta", english, StringComparison.Ordinal);
            Assert.DoesNotContain("Alcance", english, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task What_an_agent_wrote_carries_its_mark_in_both_formats_and_data_is_escaped()
    {
        var markdown = await RenderAsync(new MarkdownDocumentWriter(Texts, Redactor), "es-ES");
        var html = await RenderAsync(new HtmlDocumentWriter(Texts, Redactor), "es-ES");
        Assert.Contains("Redactado por un agente \\(ejecución aux-7\\)", markdown, StringComparison.Ordinal);
        Assert.Contains("Revisado por la persona responsable: no", markdown, StringComparison.Ordinal);
        Assert.Contains("class=\"mark\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>etiqueta</b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;etiqueta&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>etiqueta</b>", markdown, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_with_a_secret_or_a_path_of_the_machine_is_never_written()
    {
        var dirty = GoldenModel.Build() with
        {
            Sections =
            [
                new(ClientDocumentSectionKind.Summary, [new ClientParagraph(ClientText.Written("Clave password=hunter2xyz en D:\\Clientes Acme\\datos.csv"))]),
            ],
        };
        foreach (IClientDocumentFormatWriter writer in new IClientDocumentFormatWriter[] { new MarkdownDocumentWriter(Texts, Redactor), new HtmlDocumentWriter(Texts, Redactor) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => writer.RenderAsync(dirty, "es-ES", Ct));
        }
    }

    [Fact]
    public async Task Every_code_of_the_system_has_its_text_in_both_languages()
    {
        foreach (var language in new[] { "es-ES", "en" })
        {
            foreach (var code in ClientDocumentTexts.All)
            {
                var text = Texts.Render(AgentStudio.Application.Localization.UserText.Of(code, "x", "x", "x", "x", "x", "x"), language);
                Assert.False(text.StartsWith('['), $"{code} has no text in {language}.");
            }
        }

        await Task.CompletedTask;
    }
}
