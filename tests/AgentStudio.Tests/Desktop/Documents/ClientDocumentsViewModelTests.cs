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

    private sealed class GatedBuilder : IClientDocumentBuilder
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClientDocumentBuild> BuildAsync(ClientDocumentBuildRequest request, CancellationToken ct)
        {
            await Gate.Task;
            return GoldenModel.Prepared();
        }
    }

    [Fact]
    public async Task The_export_carries_the_approval_of_what_was_previewed_and_the_language_of_that_preview()
    {
        var (vm, exporter, _) = await OpenAsync();
        vm.Destination = @"C:\salida";
        vm.SelectedLanguage = vm.Languages[1];
        await vm.GenerateCommand.ExecuteAsync(null);
        vm.Approved = true;
        await vm.ExportCommand.ExecuteAsync(null);

        var request = Assert.Single(exporter.Requests);
        Assert.Equal("en", request.Language);
        Assert.NotNull(request.Approval);
        Assert.Equal(request.Build.Document.Sha256(), request.Approval.ModelSha256);
        Assert.Equal(request.Build.Disclosure.Sha256(), request.Approval.DisclosureSha256);
        Assert.Equal("user", request.Approval.ApprovedBy);
    }

    [Fact]
    public async Task Changing_the_language_while_the_document_is_generated_drops_that_result_and_mixes_no_languages()
    {
        var texts = new ResourceUserTextRenderer();
        var gated = new GatedBuilder();
        var exporter = new FakeExporter();
        var vm = new ClientDocumentsViewModel(new FakeWorkspace(), gated, exporter, [new MarkdownDocumentWriter(texts, Redactor)], null);
        await vm.SetProjectAsync(@"C:\x\proyecto");
        vm.Destination = @"C:\salida";
        vm.SelectedLanguage = vm.Languages[0];

        var generating = vm.GenerateCommand.ExecuteAsync(null);
        vm.SelectedLanguage = vm.Languages[1];
        gated.Gate.SetResult();
        await generating;

        Assert.False(vm.HasPreview);
        Assert.Equal(string.Empty, vm.Preview);
        vm.Approved = true;
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Empty(exporter.Requests);
    }

    [Fact]
    public async Task A_closed_milestone_offers_its_document_and_builds_and_exports_nothing()
    {
        var (vm, exporter, builder) = await OpenAsync();
        var offered = 0;
        vm.MilestoneOffered += (_, _) => offered++;

        await new ClientDocumentsMilestoneListener(vm).OnClosedAsync(
            new AgentStudio.Application.Work.MilestoneClosedNotice(Project, MilestoneOf("Hito 4"), null!, null!),
            Ct);

        Assert.Equal(1, offered);
        Assert.Equal("Hito 4", vm.Milestone);
        Assert.Equal(0, builder.Builds);
        Assert.Empty(exporter.Requests);
        Assert.False(vm.HasPreview);
        Assert.Contains("Hito 4", vm.StatusMessage, StringComparison.Ordinal);
    }

    private static AgentStudio.Application.Work.MilestoneView MilestoneOf(string name) => new(
        new AgentStudio.Domain.Work.Milestones.MilestoneHeader(new AgentStudio.Domain.Work.Milestones.MilestoneId("mil-1"), name, "objetivo", new string('a', 64), null, GoldenModel.At),
        default,
        new string('a', 64),
        [],
        [],
        [],
        []);

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
