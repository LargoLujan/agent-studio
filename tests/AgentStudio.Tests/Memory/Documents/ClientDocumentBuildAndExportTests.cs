using System.IO;
using System.IO.Compression;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Desktop.Documents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Infrastructure.Documents.Docx;
using AgentStudio.Memory.Documents;
using AgentStudio.Tests.Memory.Store;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Documents;

/// <summary>
/// The builder and the exporter of the documentation for clients over a real project (docs/17 D-58 to D-60; F14-9): what is built, that
/// no secret and no path of the machine reaches any of the four formats, that the list of what goes out is the document's own, that the
/// export records its fingerprints and never overwrites.
/// </summary>
public sealed class ClientDocumentBuildAndExportTests
{
    private const string Secret = "hunter2xyz";
    private const string Folder = @"D:\Clientes Acme\Proyecto Uno";

    private static readonly ResourceUserTextRenderer Texts = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakePrinter : IHtmlPdfPrinter
    {
        public Task<byte[]> PrintAsync(byte[] html, CancellationToken ct) => Task.FromResult(html);
    }

    private static IReadOnlyList<IClientDocumentFormatWriter> Writers(MemoryWorld world) =>
    [
        new MarkdownDocumentWriter(Texts, world.Redactor),
        new HtmlDocumentWriter(Texts, world.Redactor),
        new WordDocumentWriter(Texts, world.Redactor),
        new PdfDocumentWriter(new HtmlDocumentWriter(Texts, world.Redactor), new FakePrinter()),
    ];

    private static ClientDocumentBuilder Builder(MemoryWorld world) =>
        new(world.Instance.Recorder, world.Memory, world.Instance.Requirements, world.Instance.Evidence, world.Instance.Verdicts, world.Redactor, world.Clock);

    private static ClientDocumentExporter Exporter(MemoryWorld world) =>
        new(Writers(world), world.Instance.Evidence, world.Instance.Recorder, world.Activity, world.Redactor);

    /// <summary>A project with a requirement, a test run, a delivery, trusted memory with a secret and a path, and a title with a secret.</summary>
    private static async Task SeedAsync(MemoryWorld world)
    {
        var id = world.Instance.ProjectId;
        await world.Instance.Requirements.SaveAsync(
            id,
            FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Mandatory, $"Pagar con tarjeta, clave password={Secret}", Crit("c1", VerificationMethod.AutomatedTest))], T0),
            Ct);
        using (var content = new MemoryStream("ok"u8.ToArray()))
        {
            await world.Instance.Evidence.CaptureAsync(
                id,
                new EvidenceDraft(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, $"dotnet test en {Folder} con token={Secret}", "agent-studio", new TestRunSummary(12, 0, 1, 0), [Rid("R1")]),
                content,
                Ct);
        }

        await world.Memory.RecordAsync(
            world.Project,
            new MemoryDraft(MemoryKind.Decision, $"Despliegue de {Folder}", $"Se usa password={Secret} y el fichero {Folder}\\datos.csv; también C:\\Users\\persona\\secretos\\clave.txt.", MemoryOrigin.User, "nota:1"),
            Ct);
        await world.Instance.Recorder.RecordAsync(
            id,
            new Actor(ActorKind.System, "test"),
            DecisionKind.IntegrationPerformed,
            "Entrega",
            new IntegrationPerformedPayload(null, "main", ["t1"], GoldenModel.Commit, [], IsDelivery: true) { RunId = "run-1" },
            cancellationToken: Ct);
    }

    private static ClientAgentDraft Draft(bool reviewed) => new(
        ClientDraftKind.Summary,
        ["Un resumen limpio del trabajo.", $"Ver {Folder}\\notas.txt y password={Secret}"],
        new RunId("aux-1"),
        reviewed);

    private static string AllText(ClientDocumentFormat format, byte[] bytes)
    {
        if (format != ClientDocumentFormat.Word)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    [Fact]
    public async Task No_secret_and_no_path_of_the_machine_reach_any_of_the_four_formats()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var build = await Builder(world).BuildAsync(
            new ClientDocumentBuildRequest(world.Project, $"Hito {Folder} password={Secret}", IncludeConsumption: false, [Draft(reviewed: false)]),
            Ct);

        Assert.Empty(ClientDocumentRules.Validate(build.Document, world.Redactor));
        Assert.True(build.Disclosure.SecretsRedacted >= 3);
        Assert.True(build.Disclosure.PathsRemoved.Count >= 3);
        foreach (var writer in Writers(world))
        {
            var text = AllText(writer.Format, await writer.RenderAsync(build.Document, "es-ES", Ct));
            foreach (var forbidden in new[] { Secret, "Clientes Acme", "Proyecto Uno", "secretos", @"C:\Users", "datos.csv", "clave.txt", "notas.txt" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{writer.Format} contains '{forbidden}'.");
            }
        }
    }

    [Fact]
    public async Task The_list_of_what_goes_out_is_the_documents_own_and_the_draft_with_a_secret_is_left_out()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, [Draft(reviewed: true)]), Ct);

        var parts = ClientDocumentDisclosure.AgentPartsOf(build.Document);
        Assert.Equal(parts, build.Disclosure.AgentWrittenParts);
        Assert.Equal([new ClientAgentPart(ClientDocumentSectionKind.Summary, new RunId("aux-1"), true)], parts);
        var paragraphs = build.Document.Texts().Where(t => t.Text.Source == ClientTextSource.Agent).Select(t => t.Text.Value).ToList();
        Assert.Equal(["Un resumen limpio del trabajo."], paragraphs);
        Assert.False(build.Disclosure.IncludesConsumption);
        Assert.Empty(build.Disclosure.Validate());
        Assert.Equal(GoldenModel.Commit, build.Document.Cover.Commit);
        Assert.Equal(1, build.Document.Cover.Version);
        Assert.Null(build.Document.Cover.Milestone);
    }

    [Fact]
    public async Task The_model_is_deterministic_for_the_same_state_and_clock()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var request = new ClientDocumentBuildRequest(world.Project, "Hito 1", IncludeConsumption: false, []);
        var first = await Builder(world).BuildAsync(request, Ct);
        var second = await Builder(world).BuildAsync(request, Ct);
        Assert.Equal(first.Document.Sha256(), second.Document.Sha256());
    }

    [Fact]
    public async Task The_export_records_the_fingerprints_stores_the_markdown_and_never_overwrites_and_the_version_goes_up()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var destination = Path.Combine(Path.GetTempPath(), "as-docs-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, "Hito 1", IncludeConsumption: false, []), Ct);
            ClientDocumentExportRequest Request(ClientDocumentBuild b) =>
                new(world.Project, b, [ClientDocumentFormat.Markdown, ClientDocumentFormat.Html, ClientDocumentFormat.Word, ClientDocumentFormat.Pdf], "es-ES", destination, "user");
            var exporter = Exporter(world);

            var first = await exporter.ExportAsync(Request(build), Ct);
            var second = await exporter.ExportAsync(Request(build), Ct);

            Assert.Equal(4, first.Files.Count);
            Assert.Equal("proyecto-n-hito-1-v1.md", first.Files[0].FileName);
            Assert.Equal("proyecto-n-hito-1-v1-2.md", second.Files[0].FileName);
            foreach (var file in first.Files.Concat(second.Files))
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(destination, file.FileName), Ct);
                Assert.Equal(file.Sha256, ContentHash.Sha256Hex(bytes));
                Assert.Equal(file.Bytes, bytes.Length);
            }

            Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
            Assert.Equal(8, Directory.GetFiles(destination).Length);

            using (var stored = await world.Instance.Evidence.OpenContentAsync(world.Instance.ProjectId, first.MarkdownEvidence, Ct))
            using (var copy = new MemoryStream())
            {
                await stored.CopyToAsync(copy, Ct);
                Assert.Equal(first.MarkdownSha256, ContentHash.Sha256Hex(copy.ToArray()));
            }

            var exported = (await world.EntriesAsync(DecisionKind.ClientDocumentExported))
                .Select(e => DecisionPayloadSerializer.Deserialize<ClientDocumentExportedPayload>(e.PayloadJson)).ToList();
            Assert.Equal(2, exported.Count);
            Assert.Equal(first.Files.Select(f => (f.FileName, f.Sha256)), exported[0].Files.Select(f => (f.FileName, f.Sha256)));
            Assert.Equal(build.Disclosure.SecretsRedacted, exported[0].SecretsRedacted);
            Assert.Equal(build.Disclosure.PathsRemoved.Count, exported[0].PathsRemoved);
            Assert.All(exported, p => Assert.DoesNotContain(destination, p.DecidedBy, StringComparison.Ordinal));

            var next = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, "Hito 1", IncludeConsumption: false, []), Ct);
            Assert.Equal(2, next.Document.Cover.Version);
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Nothing_is_written_inside_the_project_while_a_run_goes_on_or_when_the_request_breaks_a_rule()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct);
        var inside = Path.Combine(world.Root, "entrega");
        world.Activity.Busy = true;
        await Assert.ThrowsAsync<ClientDocumentRunBusyException>(() =>
            Exporter(world).ExportAsync(new ClientDocumentExportRequest(world.Project, build, [ClientDocumentFormat.Markdown], "es-ES", inside, "user"), Ct));
        Assert.False(Directory.Exists(inside));

        world.Activity.Busy = false;
        var lying = build with { Disclosure = build.Disclosure with { AgentWrittenParts = [new ClientAgentPart(ClientDocumentSectionKind.Summary, new RunId("x"), true)] } };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Exporter(world).ExportAsync(new ClientDocumentExportRequest(world.Project, lying, [ClientDocumentFormat.Markdown], "es-ES", inside, "user"), Ct));
        Assert.Empty(await world.EntriesAsync(DecisionKind.ClientDocumentExported));
    }
}
