using System.IO;
using System.IO.Compression;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Git;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Documents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Integration.Phase9;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase14;

/// <summary>
/// The world of the documentation for clients (F14-9): the app composed as it composes itself, a real reviewed run delivered to the user's
/// branch, and a milestone whose requirement, and a note of the memory, carry a false token and paths of this computer: what a document
/// must never take out of the project.
/// </summary>
public sealed class ClientDocumentsWorld : IAsyncLifetime
{
    public const string Token = "ghp_0123456789abcdefghijklmnopqrstuvwxyzAB";

    public const string MilestoneName = "Entrega de Calc";

    public const string DraftText = "Resumen de lo entregado, redactado por un agente a partir del registro.";

    private readonly TempDirectory _out = new();

    internal AppWorld World { get; private set; } = null!;

    public RunResult Run { get; private set; } = null!;

    public string Destination => _out.Path;

    /// <summary>A path of this computer that is not the project: under the profile of the user who runs the tests.</summary>
    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "clientes", "tienda", "src", "Pedido.cs");

    public async ValueTask InitializeAsync()
    {
        World = await AppWorld.OpenAsync(clis: new Phase5Clis { Executor = Phase9Runs.Executor });
        Run = await Phase9Runs.RunAsync(World, Phase5Runs.Reviewed(), Phase5Runs.Profile());
        var delivered = await World.Get<IDeliveryService>().DeliverAsync(World.App.Project, Run.Id, DeliveryMode.FastForwardOnly, TestContext.Current.CancellationToken);
        Assert.True(delivered.Delivered, delivered.Message);

        // What the person wrote in the project, secrets and paths included.
        var requirements = FrozenRequirementSet.Freeze(
            [
                new Requirement(
                    new RequirementId("RQ-01"),
                    $"Que {World.Repo.Root} no se publique y que {Token} no salga en ningún documento.",
                    RequirementPriority.Mandatory,
                    [new AcceptanceCriterion("c1", "Multiply(2,3) devuelve 6", VerificationMethod.AutomatedTest)]),
            ],
            Start);
        await World.Get<IMilestoneService>().OpenAsync(
            new OpenMilestoneRequest(World.App.Project, MilestoneName, "Que multiplique.", requirements, null, "manuel"),
            TestContext.Current.CancellationToken);
        await World.Get<IProjectMemory>().RecordAsync(
            World.App.Project,
            new MemoryDraft(MemoryKind.Decision, "Acceso", $"Se guarda en {UserPath} con la clave {Token}.", MemoryOrigin.System, "nota:1"),
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (World is not null)
        {
            await World.DisposeAsync();
        }

        _out.Dispose();
    }
}

/// <summary>
/// F14-9 (docs/17 D-58 to D-60, ADR-0057; §9.4 «Integración») end to end, with the real builder, the real writers (Markdown and HTML of the
/// memory module, Word of the infrastructure one), the real exporter over a temporary folder and the log of the project: the document comes
/// out in Markdown, HTML and Word; what an agent wrote is marked in each; the list of what goes out is made; <c>ClientDocumentExported</c> is
/// in the verified log; and a secret and paths of this computer sown in the project are in no format. The PDF is left out: it prints the
/// same HTML through WebView2, which these tests never start.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class ClientDocumentsExportTests(ClientDocumentsWorld fixture) : IClassFixture<ClientDocumentsWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClientDocumentFormat[] Formats = [ClientDocumentFormat.Markdown, ClientDocumentFormat.Html, ClientDocumentFormat.Word];

    /// <summary>The text of a file as a reader sees it: the file itself, or every part of a Word package, unzipped.</summary>
    private static string TextOf(string file)
    {
        if (!file.EndsWith(".docx", StringComparison.Ordinal))
        {
            return File.ReadAllText(file, Encoding.UTF8);
        }

        using var zip = ZipFile.OpenRead(file);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private static IEnumerable<string> Seeds(ClientDocumentsWorld world, AppWorld app) =>
    [
        ClientDocumentsWorld.Token,
        "ghp_",
        ClientDocumentsWorld.UserPath,
        "Pedido.cs",
        "tienda",
        app.Repo.Root,
        Path.GetDirectoryName(app.Repo.Root)!,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        world.Destination,
    ];

    private async Task<(ClientDocumentBuild Build, ClientDocumentExportResult Result, ClientAgentDraft Draft)> ExportAsync(string subfolder)
    {
        var app = fixture.World;
        var folder = Path.Combine(fixture.Destination, subfolder);
        Directory.CreateDirectory(folder);

        // One part written by an agent (checked again by the builder), one with the token that the builder discards, and one with a path.
        var draft = new ClientAgentDraft(ClientDraftKind.Summary, [ClientDocumentsWorld.DraftText, $"La clave es {ClientDocumentsWorld.Token}.", $"Mira {ClientDocumentsWorld.UserPath}."], fixture.Run.Id, ReviewedByPerson: false);
        var build = await app.Get<IClientDocumentBuilder>().BuildAsync(new ClientDocumentBuildRequest(app.App.Project, ClientDocumentsWorld.MilestoneName, false, [draft]), Ct);
        var request = new ClientDocumentExportRequest(app.App.Project, build, Formats, "es-ES", folder, "usuario", ClientDocumentApproval.Of(build, "usuario"));
        var result = await app.Get<IClientDocumentExporter>().ExportAsync(request, Ct);
        return (build, result, draft);
    }

    [Fact]
    public async Task The_document_comes_out_in_markdown_html_and_word_with_what_an_agent_wrote_marked_the_list_of_what_goes_out_and_the_export_in_the_log()
    {
        var app = fixture.World;
        var (build, result, _) = await ExportAsync("exportacion");

        // Three files, one per format, with the fingerprint the exporter says (and the log keeps).
        Assert.Equal(Formats, result.Files.Select(f => f.Format));
        var folder = Path.Combine(fixture.Destination, "exportacion");
        Assert.Equal([".docx", ".html", ".md"], Directory.GetFiles(folder).Select(Path.GetExtension).Order(StringComparer.Ordinal));
        foreach (var file in result.Files)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, file.FileName), Ct);
            Assert.Equal((file.Sha256, file.Bytes), (AgentStudio.Domain.Common.ContentHash.Sha256Hex(bytes), bytes.LongLength));
        }

        // What an agent wrote is in the three, and in each it carries its mark with the run that wrote it.
        var markdown = TextOf(Path.Combine(folder, result.Files.Single(f => f.Format == ClientDocumentFormat.Markdown).FileName));
        var html = TextOf(Path.Combine(folder, result.Files.Single(f => f.Format == ClientDocumentFormat.Html).FileName));
        var word = TextOf(Path.Combine(folder, result.Files.Single(f => f.Format == ClientDocumentFormat.Word).FileName));
        foreach (var text in new[] { markdown, html, word })
        {
            Assert.Contains(ClientDocumentsWorld.DraftText, text, StringComparison.Ordinal);
            Assert.Contains(fixture.Run.Id.Value, text, StringComparison.Ordinal);
        }

        Assert.Contains("class=\"mark\"", html, StringComparison.Ordinal);
        Assert.Contains(markdown.Split('\n'), line => line.Contains(fixture.Run.Id.Value, StringComparison.Ordinal) && line.TrimStart().StartsWith('_') || line.Contains("_(", StringComparison.Ordinal));

        // The list of what goes out: the part of the agent with its run, the paths that were removed, the secrets that were taken out.
        var part = Assert.Single(build.Disclosure.AgentWrittenParts);
        Assert.Equal((ClientDocumentSectionKind.Summary, fixture.Run.Id, false), (part.Section, part.Run, part.ReviewedByPerson));
        Assert.True(build.Disclosure.SecretsRedacted > 0, "The token sown in the project was not counted as redacted.");
        Assert.NotEmpty(build.Disclosure.PathsRemoved);

        // ClientDocumentExported is in the log, outside every run, with that list, and the log still verifies.
        var log = await app.App.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
        var exported = Assert.Single(await app.App.PayloadsAsync<ClientDocumentExportedPayload>(), e => e.Payload.Version == result.Version).Payload;
        Assert.Equal((ClientDocumentsWorld.MilestoneName, "es-ES", "usuario", build.Document.Sha256()), (exported.Milestone, exported.Language, exported.DecidedBy, exported.ModelSha256));
        Assert.Equal(Formats, exported.Files.Select(f => f.Format));
        Assert.Equal(build.Disclosure.SecretsRedacted, exported.SecretsRedacted);
        Assert.Equal(build.Disclosure.PathsRemoved.Count, exported.PathsRemoved);
        var recorded = Assert.Single(exported.AgentParts);
        Assert.Equal(fixture.Run.Id, recorded.Run);
        Assert.NotNull(exported.Commit); // the delivery of the run is the commit of the cover
    }

    [Fact]
    public async Task A_secret_and_the_paths_of_this_computer_sown_in_the_project_are_in_no_format_nor_in_the_log_nor_in_the_list()
    {
        var app = fixture.World;
        var (build, result, _) = await ExportAsync("fuga");
        var folder = Path.Combine(fixture.Destination, "fuga");
        var seeds = Seeds(fixture, app).ToList();

        foreach (var file in result.Files)
        {
            var text = TextOf(Path.Combine(folder, file.FileName));
            foreach (var seed in seeds.Where(s => !string.Equals(s, fixture.Destination, StringComparison.OrdinalIgnoreCase)))
            {
                Assert.False(text.Contains(seed, StringComparison.OrdinalIgnoreCase), $"{file.Format} carries «{seed}».");
            }

            // The path as JSON or XML would write it, doubled backslashes included.
            Assert.DoesNotContain(app.Repo.Root.Replace("\\", "\\\\", StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);
        }

        // Neither the list the person reads nor the log carries the text of a path or of a secret.
        var list = string.Join("\n", build.Disclosure.AgentWrittenParts.Select(p => p.Run.Value));
        Assert.DoesNotContain(ClientDocumentsWorld.Token, list, StringComparison.Ordinal);
        var log = await app.App.LoadLogAsync();
        foreach (var entry in log.Entries.Where(e => e.Kind == AgentStudio.Domain.Decisions.DecisionKind.ClientDocumentExported))
        {
            Assert.DoesNotContain(ClientDocumentsWorld.Token, entry.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Pedido.cs", entry.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("clientes", entry.PayloadJson, StringComparison.Ordinal);
        }

        // The token that an agent put in a paragraph never entered the model (the builder checks again what an agent drafted).
        Assert.DoesNotContain(
            build.Document.Texts(),
            t => t.Text.Value?.Contains(ClientDocumentsWorld.Token, StringComparison.Ordinal) == true || t.Text.Value?.Contains("Pedido.cs", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Through_the_screen_the_person_generates_reviews_approves_and_exports_and_nothing_goes_out_before_the_approval()
    {
        var app = fixture.World;
        var folder = Path.Combine(fixture.Destination, "pantalla");
        Directory.CreateDirectory(folder);
        var screen = app.Get<ClientDocumentsViewModel>();
        await screen.SetProjectAsync(app.Repo.Root);
        screen.Milestone = ClientDocumentsWorld.MilestoneName;
        screen.Destination = folder;
        Assert.True(screen.HasMarkdown && screen.HasHtml && screen.HasWord);
        screen.ExportMarkdown = true;
        screen.ExportHtml = true;
        screen.ExportWord = true;
        screen.ExportPdf = false;

        // Without the preview and the approval, no export.
        Assert.False(screen.ExportCommand.CanExecute(null));
        await screen.GenerateCommand.ExecuteAsync(null);
        Assert.Null(screen.ErrorMessage);
        Assert.True(screen.HasPreview);
        Assert.False(screen.ExportCommand.CanExecute(null));
        Assert.Empty(Directory.GetFiles(folder));
        Assert.DoesNotContain(ClientDocumentsWorld.Token, screen.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Pedido.cs", screen.Preview, StringComparison.Ordinal);

        screen.Approved = true;
        await screen.ExportCommand.ExecuteAsync(null);
        Assert.Null(screen.ErrorMessage);
        Assert.Equal(3, screen.ExportedFiles.Count);
        Assert.Equal(3, Directory.GetFiles(folder).Length);
        foreach (var file in Directory.GetFiles(folder))
        {
            var text = TextOf(file);
            Assert.DoesNotContain(ClientDocumentsWorld.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(app.Repo.Root, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(await app.App.PayloadsAsync<ClientDocumentExportedPayload>(), e => e.Payload.Files.Count == 3 && e.Payload.Milestone == ClientDocumentsWorld.MilestoneName);
    }
}
