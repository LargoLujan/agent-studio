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
            var record = await world.Instance.Evidence.CaptureAsync(
                id,
                new EvidenceDraft(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, $"dotnet test en {Folder} con token={Secret}", "agent-studio", new TestRunSummary(12, 0, 1, 0), [Rid("R1")]),
                content,
                Ct);
            await AnchorAsync(world, record);
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

    /// <summary>What the engine does with the evidence a run captures: the log announces it (EvidenceRegistered), so the document may count it.</summary>
    private static Task AnchorAsync(MemoryWorld world, EvidenceRecord record) =>
        world.Instance.Recorder.RecordAsync(
            world.Instance.ProjectId,
            new Actor(ActorKind.System, "test"),
            DecisionKind.EvidenceRegistered,
            $"Evidencia {record.Id}",
            EvidenceRegisteredPayload.Anchoring(record, "run-1", null, null),
            cancellationToken: Ct);

    private static ClientDocumentExportRequest Approved(MemoryWorld world, ClientDocumentBuild build, IReadOnlyList<ClientDocumentFormat> formats, string destination, string language = "es-ES") =>
        new(world.Project, build, formats, language, destination, "user", ClientDocumentApproval.Of(build, "user"));

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "as-docs-" + Guid.NewGuid().ToString("N")[..10]);

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
                Approved(world, b, [ClientDocumentFormat.Markdown, ClientDocumentFormat.Html, ClientDocumentFormat.Word, ClientDocumentFormat.Pdf], destination);
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
            Exporter(world).ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown], inside), Ct));
        Assert.False(Directory.Exists(inside));

        world.Activity.Busy = false;
        var lying = build with { Disclosure = build.Disclosure with { AgentWrittenParts = [new ClientAgentPart(ClientDocumentSectionKind.Summary, new RunId("x"), true)] } };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Exporter(world).ExportAsync(Approved(world, lying, [ClientDocumentFormat.Markdown], inside), Ct));
        Assert.Empty(await world.EntriesAsync(DecisionKind.ClientDocumentExported));
    }

    [Fact]
    public async Task Nothing_is_exported_without_the_approval_of_the_person_or_with_the_approval_of_another_preview()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var destination = TempFolder();
        var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct);
        var other = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, "Otro hito", IncludeConsumption: false, []), Ct);
        var formats = new[] { ClientDocumentFormat.Markdown };

        var none = Approved(world, build, formats, destination) with { Approval = null };
        var ofAnother = Approved(world, build, formats, destination) with { Approval = ClientDocumentApproval.Of(other, "user") };
        var otherList = Approved(world, build, formats, destination) with
        {
            Approval = ClientDocumentApproval.Of(build, "user") with { DisclosureSha256 = new string('0', 64) },
        };
        var anonymous = Approved(world, build, formats, destination) with { Approval = ClientDocumentApproval.Of(build, " ") };
        foreach (var request in new[] { none, ofAnother, otherList, anonymous })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Exporter(world).ExportAsync(request, Ct));
        }

        Assert.False(Directory.Exists(destination));
        Assert.Empty(await world.EntriesAsync(DecisionKind.ClientDocumentExported));
    }

    [Fact]
    public async Task A_junction_to_the_project_does_not_dodge_the_check_of_the_run_and_git_and_the_data_are_never_written()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct);
        var outside = TempFolder();
        Directory.CreateDirectory(outside);
        var link = Path.Combine(outside, "enlace");
        try
        {
            RunMklinkJunction(link, world.Root);
            Directory.CreateDirectory(Path.Combine(world.Root, "entrega"));

            world.Activity.Busy = true;
            await Assert.ThrowsAsync<ClientDocumentRunBusyException>(() =>
                Exporter(world).ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown], Path.Combine(link, "entrega")), Ct));
            await Assert.ThrowsAsync<ClientDocumentRunBusyException>(() =>
                Exporter(world).ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown], Path.Combine(link, "nueva", "carpeta")), Ct));
            Assert.Empty(Directory.GetFiles(Path.Combine(world.Root, "entrega")));
            Assert.False(Directory.Exists(Path.Combine(world.Root, "nueva")));

            world.Activity.Busy = false;
            foreach (var protectedFolder in new[] { ".git", ".agentstudio", Path.Combine(".agentstudio", "memory"), Path.Combine("sub", ".git") })
            {
                foreach (var destination in new[] { Path.Combine(world.Root, protectedFolder), Path.Combine(link, protectedFolder) })
                {
                    var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                        Exporter(world).ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown], destination), Ct));
                    Assert.Contains("document.export-destination-protected", error.Message, StringComparison.Ordinal);
                }
            }

            Assert.Empty(await world.EntriesAsync(DecisionKind.ClientDocumentExported));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    private static void RunMklinkJunction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>It says a run is not going the first time it is asked and that one is going from then on, as when it starts while a PDF is printed.</summary>
    private sealed class StartsLater : AgentStudio.Application.Orchestration.IRunActivity
    {
        private int _asked;

        public bool IsBusy(string projectId) => Interlocked.Increment(ref _asked) > 1;
    }

    [Fact]
    public async Task A_run_that_starts_while_the_document_is_rendered_stops_the_export_and_the_folder_it_made_is_removed()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct);
        var inside = Path.Combine(world.Root, "entregas", "cliente");
        var exporter = new ClientDocumentExporter(Writers(world), world.Instance.Evidence, world.Instance.Recorder, new StartsLater(), world.Redactor);

        await Assert.ThrowsAsync<ClientDocumentRunBusyException>(() =>
            exporter.ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown, ClientDocumentFormat.Html], inside), Ct));

        Assert.False(Directory.Exists(inside));
        Assert.False(Directory.Exists(Path.Combine(world.Root, "entregas")));
        Assert.Empty(await world.EntriesAsync(DecisionKind.ClientDocumentExported));
    }

    [Fact]
    public async Task A_folder_that_already_existed_stays_when_the_export_fails()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct);
        var inside = Path.Combine(world.Root, "entregas");
        Directory.CreateDirectory(inside);
        var exporter = new ClientDocumentExporter(Writers(world), world.Instance.Evidence, world.Instance.Recorder, new StartsLater(), world.Redactor);

        await Assert.ThrowsAsync<ClientDocumentRunBusyException>(() =>
            exporter.ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown], inside), Ct));

        Assert.True(Directory.Exists(inside));
    }

    [Fact]
    public async Task A_requirement_is_integrated_only_when_a_passing_run_of_the_delivered_commit_covers_it_and_otherwise_it_says_no_data()
    {
        using var world = await MemoryWorld.CreateAsync();
        var id = world.Instance.ProjectId;
        await world.Instance.Requirements.SaveAsync(
            id,
            FrozenRequirementSet.Freeze(
                [Req("R1", RequirementPriority.Mandatory, "uno", Crit("c1", VerificationMethod.AutomatedTest)), Req("R2", RequirementPriority.Mandatory, "dos", Crit("c2", VerificationMethod.AutomatedTest))],
                T0),
            Ct);
        async Task Capture(string title, string? commit, params string[] covers)
        {
            using var content = new MemoryStream("ok"u8.ToArray());
            var record = await world.Instance.Evidence.CaptureAsync(
                id,
                new EvidenceDraft(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, title, "agent-studio", new TestRunSummary(3, 0, 0, 0), [.. covers.Select(Rid)], SubjectCommit: commit),
                content,
                Ct);
            await AnchorAsync(world, record);
        }

        await Capture("sobre el commit entregado", GoldenModel.Commit, "R1");
        await Capture("sobre otro commit", new string('b', 40), "R2");
        var scopeBefore = (await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct)).Document;
        Assert.DoesNotContain(scopeBefore.Texts(), t => t.Text.System?.Code is ClientDocumentTexts.ScopeDeliveryNote or ClientDocumentTexts.Unknown);
        Assert.Equal([ClientDocumentTexts.No, ClientDocumentTexts.No], IntegratedCodes(scopeBefore));

        await world.Instance.Recorder.RecordAsync(
            id,
            new Actor(ActorKind.System, "test"),
            DecisionKind.IntegrationPerformed,
            "Entrega",
            new IntegrationPerformedPayload(null, "main", ["t1"], GoldenModel.Commit, [], IsDelivery: true) { RunId = "run-1" },
            cancellationToken: Ct);
        var document = (await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct)).Document;

        Assert.Equal([ClientDocumentTexts.Yes, ClientDocumentTexts.Unknown], IntegratedCodes(document));
        var note = Assert.Single(document.Texts(), t => t.Text.System?.Code == ClientDocumentTexts.ScopeDeliveryNote);
        Assert.Equal(GoldenModel.Commit[..12], note.Text.System!.Arguments[0]);
        Assert.Contains(document.Texts(), t => t.Text.System?.Code == ClientDocumentTexts.NotIncludedDeployed);
    }

    [Fact]
    public async Task Evidence_the_log_does_not_announce_such_as_the_tests_of_HEAD_changes_neither_the_figures_nor_the_annexes()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var before = (await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct)).Document;

        // The tests of HEAD: captured by the system and stored, bound to a commit, but announced in no run.
        using (var content = new MemoryStream("ok"u8.ToArray()))
        {
            await world.Instance.Evidence.CaptureAsync(
                world.Instance.ProjectId,
                new EvidenceDraft(EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "Pruebas (test)", "agent-studio", new TestRunSummary(99, 7, 0, 1), SubjectCommit: GoldenModel.Commit),
                content,
                Ct);
        }

        var after = (await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, null, IncludeConsumption: false, []), Ct)).Document;

        static string Figures(ClientDocument d) => string.Join("|", d.Texts().Where(t => t.Text.System?.Code == ClientDocumentTexts.SummaryFigures).SelectMany(t => t.Text.System!.Arguments.Select(a => a?.ToString())));
        static List<string> Annex(ClientDocument d) => [.. d.Sections.SelectMany(s => s.Blocks).OfType<ClientEvidenceList>().SelectMany(l => l.References.Select(r => r.Evidence.Value + r.Sha256))];

        Assert.NotEmpty(Figures(before));
        Assert.Equal(Figures(before), Figures(after));
        Assert.NotEmpty(Annex(before));
        Assert.Equal(Annex(before), Annex(after));
    }

    private static List<string> IntegratedCodes(ClientDocument document) =>
        [.. document.Sections.Single(s => s.Kind == ClientDocumentSectionKind.Scope).Blocks.OfType<ClientTable>().Single().Rows.Select(r => r[5].System!.Code)];

    [Fact]
    public async Task The_properties_of_the_word_and_the_names_of_the_files_hold_no_secret_either()
    {
        using var world = await MemoryWorld.CreateAsync();
        await SeedAsync(world);
        var destination = TempFolder();
        try
        {
            var build = await Builder(world).BuildAsync(new ClientDocumentBuildRequest(world.Project, $"Hito password={Secret}", IncludeConsumption: false, []), Ct);
            var result = await Exporter(world).ExportAsync(Approved(world, build, [ClientDocumentFormat.Markdown, ClientDocumentFormat.Word], destination), Ct);

            Assert.All(result.Files, f => Assert.DoesNotContain("hunter2xyz", f.FileName, StringComparison.OrdinalIgnoreCase));
            var word = result.Files.Single(f => f.Format == ClientDocumentFormat.Word);
            using var zip = new ZipArchive(File.OpenRead(Path.Combine(destination, word.FileName)), ZipArchiveMode.Read);
            var core = zip.Entries.Single(e => e.FullName.EndsWith(".psmdcp", StringComparison.Ordinal));
            using var reader = new StreamReader(core.Open(), Encoding.UTF8);
            var properties = reader.ReadToEnd();
            Assert.Contains("Agent Studio", properties, StringComparison.Ordinal);
            foreach (var forbidden in new[] { Secret, "Clientes Acme", "secretos", @"C:\Users" })
            {
                Assert.DoesNotContain(forbidden, properties, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }
}
