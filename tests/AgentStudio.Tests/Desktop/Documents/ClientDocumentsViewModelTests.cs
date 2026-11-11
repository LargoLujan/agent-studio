using AgentStudio.Application.Documents;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Documents;
using AgentStudio.Desktop.Services;
using AgentStudio.Memory.Documents;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Memory.Documents;

namespace AgentStudio.Tests.Desktop.Documents;

/// <summary>«Documentación para clientes» (docs/17 D-60): no export without the approval, the preview and the list, and the PDF behind its port.</summary>
public sealed class ClientDocumentsViewModelTests
{
    private static readonly SecretRedactor Redactor = new(@"C:\Users\persona");
    private static readonly ProjectContext Project = new("p-1", @"C:\x\proyecto");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeWorkspace : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(Project);
    }

    private sealed class FakeBuilder : IClientDocumentBuilder
    {
        public int Builds { get; private set; }

        public Task<ClientDocumentBuild> BuildAsync(ClientDocumentBuildRequest request, CancellationToken ct)
        {
            Builds++;
            return Task.FromResult(GoldenModel.Prepared());
        }
    }

    private sealed class FakeExporter : IClientDocumentExporter
    {
        public List<ClientDocumentExportRequest> Requests { get; } = [];

        public Task<ClientDocumentExportResult> ExportAsync(ClientDocumentExportRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new ClientDocumentExportResult(2, [new ExportedClientFile(ClientDocumentFormat.Markdown, "a-v2.md", new string('b', 64), 10)], new("ev-1"), new string('c', 64)));
        }
    }

    private sealed class FakePrinter : IHtmlPdfPrinter
    {
        public byte[]? Printed { get; private set; }

        public Task<byte[]> PrintAsync(byte[] html, CancellationToken ct)
        {
            Printed = html;
            return Task.FromResult("%PDF"u8.ToArray());
        }
    }

    private static async Task<(ClientDocumentsViewModel Vm, FakeExporter Exporter, FakeBuilder Builder)> OpenAsync()
    {
        var texts = new ResourceUserTextRenderer();
        var builder = new FakeBuilder();
        var exporter = new FakeExporter();
        var vm = new ClientDocumentsViewModel(new FakeWorkspace(), builder, exporter, [new MarkdownDocumentWriter(texts, Redactor)], null);
        await vm.SetProjectAsync(@"C:\x\proyecto");
        return (vm, exporter, builder);
    }

    [Fact]
    public async Task Nothing_is_exported_without_the_approval_a_destination_and_a_preview()
    {
        var (vm, exporter, _) = await OpenAsync();
        vm.Destination = @"C:\salida";
        Assert.False(vm.ExportCommand.CanExecute(null));

        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreview);
        Assert.False(vm.ExportCommand.CanExecute(null));

        vm.Approved = true;
        Assert.True(vm.ExportCommand.CanExecute(null));
        await vm.ExportCommand.ExecuteAsync(null);
        var request = Assert.Single(exporter.Requests);
        Assert.Equal([ClientDocumentFormat.Markdown], request.Formats);
        Assert.Equal("user", request.DecidedBy);
        Assert.Equal(["a-v2.md"], vm.ExportedFiles);
        Assert.False(vm.HasPreview);
        Assert.False(vm.Approved);
    }

    [Fact]
    public async Task The_preview_and_the_list_of_what_goes_out_show_and_a_change_takes_the_approval_back()
    {
        var (vm, _, builder) = await OpenAsync();
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Contains("Mi tienda", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("1", vm.SecretsText, StringComparison.Ordinal);
        Assert.Equal(["~\\x"], vm.PathsRemoved);
        Assert.Equal(2, vm.AgentParts.Count);
        vm.Approved = true;

        vm.Milestone = "Hito 2";
        Assert.False(vm.Approved);
        Assert.False(vm.HasPreview);
        Assert.Equal(string.Empty, vm.Preview);

        await vm.GenerateForMilestoneAsync("Hito 3");
        Assert.Equal(2, builder.Builds);
        Assert.True(vm.HasPreview);
        vm.SelectedLanguage = vm.Languages[1];
        Assert.False(vm.HasPreview);
    }

    [Fact]
    public async Task The_pdf_is_the_printed_html_of_the_same_model()
    {
        var texts = new ResourceUserTextRenderer();
        var printer = new FakePrinter();
        var writer = new PdfDocumentWriter(new HtmlDocumentWriter(texts, Redactor), printer);
        var pdf = await writer.RenderAsync(GoldenModel.Build(), "es-ES", Ct);
        Assert.Equal("%PDF"u8.ToArray(), pdf);
        Assert.Contains("<h1>Documentación del proyecto Mi tienda</h1>", System.Text.Encoding.UTF8.GetString(printer.Printed!), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PdfDocumentWriter(new MarkdownDocumentWriter(texts, Redactor), printer));
    }
}
