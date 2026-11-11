using System.Globalization;
using System.Reflection;
using AgentStudio.Application.Documents;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application.Documents;

/// <summary>
/// docs/17 §5.5 (D-58 to D-60, P0 of Phase 14, ADR-0057): the contracts of the documentation for clients. The model has the sections
/// of D-58 in order and no language (the system's text is a code); what an agent wrote cannot lose its mark; no path of the machine and
/// no hidden character go in what people or agents wrote; the hash of the model is deterministic in any culture; every export is a
/// version with its own file names. Nothing is built, written or launched.
/// </summary>
public sealed class ClientDocumentContractsTests
{
    private const string Commit = "9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7";

    private static readonly DateTimeOffset At = new(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(1));

    private static readonly RunId DraftRun = new("aux-20261111-a1b2c3");

    private static ClientDocumentCover Cover(string project = "Mi tienda", string? milestone = "Hito 1", int version = 2, string? commit = Commit) =>
        new(project, milestone, At, version, commit);

    private static ClientDocument Complete(ClientText? summary = null) => new(
        Cover(),
        [
            new(ClientDocumentSectionKind.Summary, [new ClientParagraph(summary ?? ClientText.OfSystem("document.summary-template", 12, 3, 0.75m))]),
            new(
                ClientDocumentSectionKind.Scope,
                [
                    new ClientTable(
                        [ClientText.OfSystem("document.column-requirement"), ClientText.OfSystem("document.column-state")],
                        [[ClientText.Written("R1: calcular el IVA de cada línea"), ClientText.OfSystem("document.state-integrated")]]),
                ]),
            new(ClientDocumentSectionKind.Verification, [new ClientList(false, [ClientText.OfSystem("document.tests-passed", 120, 0, At)])]),
            new(ClientDocumentSectionKind.Decisions, [new ClientList(true, [ClientText.Written("Usar SQLite como almacén local")])]),
            new(ClientDocumentSectionKind.Changes, [new ClientParagraph(ClientText.OfSystem("document.changes-count", 14, TimeSpan.FromMinutes(90)))]),
            new(ClientDocumentSectionKind.RisksAndPending, [new ClientList(false, [ClientText.Written("R4: el redondeo queda sin probar")])]),
            new(ClientDocumentSectionKind.Annexes, [new ClientEvidenceList([new ClientEvidenceReference(Eid("EV-7"), HashA, ClientText.OfSystem("document.evidence-test-run"))])]),
        ]);

    // ---- the formats (D-59) ----

    [Fact]
    public void There_are_four_formats_each_with_its_extension_and_its_media_type()
    {
        Assert.Equal(["Markdown", "Html", "Pdf", "Word"], Enum.GetNames<ClientDocumentFormat>());
        Assert.Equal(Enum.GetValues<ClientDocumentFormat>(), ClientDocumentFormats.All);
        Assert.Equal([".md", ".html", ".pdf", ".docx"], ClientDocumentFormats.All.Select(ClientDocumentFormats.Extension));
        Assert.Equal(
            ["text/markdown", "text/html", "application/pdf", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            ClientDocumentFormats.All.Select(ClientDocumentFormats.MediaType));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientDocumentFormats.Extension((ClientDocumentFormat)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientDocumentFormats.MediaType((ClientDocumentFormat)9));
    }

    // ---- the sections (D-58) ----

    [Fact]
    public void The_sections_after_the_cover_are_the_seven_of_d58_in_their_order_each_with_a_code_for_its_title()
    {
        Assert.Equal(["Summary", "Scope", "Verification", "Decisions", "Changes", "RisksAndPending", "Annexes"], Enum.GetNames<ClientDocumentSectionKind>());
        Assert.Equal(Enum.GetValues<ClientDocumentSectionKind>(), ClientDocumentSections.Order);
        var codes = ClientDocumentSections.Order.Select(ClientDocumentSections.TitleCode).ToList();
        Assert.All(codes, code => Assert.True(UserText.IsCode(code), code));
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal("Text_document_section_risks", UserText.KeyOf(ClientDocumentSections.TitleCode(ClientDocumentSectionKind.RisksAndPending)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientDocumentSections.TitleCode((ClientDocumentSectionKind)9));
    }

    // ---- the texts: by who wrote them (D-58 and D-59) ----

    [Fact]
    public void A_text_of_the_system_is_a_code_with_arguments_and_one_of_a_person_or_an_agent_is_verbatim()
    {
        var system = ClientText.OfSystem("document.tests-passed", 120, "x");
        var written = ClientText.Written("R1: calcular el IVA");
        var agent = ClientText.ByAgent("Resumen del proyecto.", new ClientAgentMark(DraftRun, ReviewedByPerson: true));

        Assert.Equal(ClientTextSource.System, system.Source);
        Assert.Equal(UserText.Of("document.tests-passed", 120, "x"), system.System);
        Assert.Null(system.Value);
        Assert.Null(system.Mark);
        Assert.Equal(ClientTextSource.Written, written.Source);
        Assert.Equal("R1: calcular el IVA", written.Value);
        Assert.Null(written.System);
        Assert.Null(written.Mark);
        Assert.Equal(ClientTextSource.Agent, agent.Source);
        Assert.Equal("Resumen del proyecto.", agent.Value);
        Assert.Equal(new ClientAgentMark(DraftRun, true), agent.Mark);
        Assert.Equal(["System", "Written", "Agent"], Enum.GetNames<ClientTextSource>());
    }

    [Fact]
    public void An_agents_text_cannot_be_made_without_its_mark_and_no_text_is_blank()
    {
        Assert.Throws<ArgumentNullException>(() => ClientText.ByAgent("Texto.", null!));
        Assert.Throws<ArgumentException>(() => ClientText.ByAgent(" ", new ClientAgentMark(DraftRun, false)));
        Assert.Throws<ArgumentException>(() => ClientText.Written(""));
        Assert.Throws<ArgumentNullException>(() => ClientText.OfSystem((UserText)null!));
        Assert.Throws<ArgumentException>(() => ClientText.OfSystem("Not A Code"));
        Assert.Throws<ArgumentException>(() => ClientText.OfSystem("document.x", new object()));
        Assert.Empty(typeof(ClientText).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    // ---- the rules of the model (D-58, «Lo que no va») ----

    [Fact]
    public void A_document_with_every_section_in_order_breaks_no_rule()
    {
        Assert.Empty(ClientDocumentRules.Validate(Complete()));
    }

    [Fact]
    public void A_document_may_leave_a_section_out_but_never_reorder_or_repeat_them()
    {
        var full = Complete();

        Assert.Empty(ClientDocumentRules.Validate(full with { Sections = [.. full.Sections.Where(s => s.Kind != ClientDocumentSectionKind.Decisions)] }));
        Assert.Equal(["document.sections-order"], ClientDocumentRules.Validate(full with { Sections = [.. full.Sections.Reverse()] }));
        Assert.Equal(["document.sections-order"], ClientDocumentRules.Validate(full with { Sections = [.. full.Sections, full.Sections[0]] }));
        Assert.Equal(["document.sections-order"], ClientDocumentRules.Validate(full with { Sections = [.. full.Sections, new ClientDocumentSection((ClientDocumentSectionKind)9, [full.Sections[0].Blocks[0]])] }));
    }

    [Fact]
    public void Each_rule_of_the_cover_has_its_own_code()
    {
        var sections = Complete().Sections;

        Assert.Equal(["document.cover-project-required"], ClientDocumentRules.Validate(new ClientDocument(Cover(project: " "), sections)));
        Assert.Equal(["document.cover-version-invalid"], ClientDocumentRules.Validate(new ClientDocument(Cover(version: 0), sections)));
        Assert.Equal(["document.cover-commit-invalid"], ClientDocumentRules.Validate(new ClientDocument(Cover(commit: "HEAD"), sections)));
        Assert.Empty(ClientDocumentRules.Validate(new ClientDocument(Cover(milestone: null, commit: null), sections)));
        Assert.Equal(["document.local-path"], ClientDocumentRules.Validate(new ClientDocument(Cover(project: @"C:\Users\ana\tienda"), sections)));
        Assert.Equal(["document.hidden-character"], ClientDocumentRules.Validate(new ClientDocument(Cover(milestone: "Hito\u202E1"), sections)));
        Assert.Equal(["document.cover-required"], ClientDocumentRules.Validate(new ClientDocument(null!, sections)));
    }

    [Fact]
    public void A_path_of_the_machine_or_a_hidden_character_in_what_people_or_agents_wrote_is_a_problem()
    {
        var mark = new ClientAgentMark(DraftRun, false);

        Assert.Equal(["document.local-path"], ClientDocumentRules.Validate(Complete(ClientText.Written(@"Mira C:\Users\ana\repo\plan.json"))));
        Assert.Equal(["document.local-path"], ClientDocumentRules.Validate(Complete(ClientText.ByAgent("Está en ~/repo/plan.json", mark))));
        Assert.Equal(["document.hidden-character"], ClientDocumentRules.Validate(Complete(ClientText.Written("a\u202Eb"))));
        Assert.Empty(ClientDocumentRules.Validate(Complete(ClientText.Written("Una línea\ny otra,\ncon tildes: áéíóú ñ."))));
        Assert.Empty(ClientDocumentRules.Validate(Complete(ClientText.Written("Se tocó src/Tienda/Iva.cs y https://example.com/ayuda"))));
    }

    [Fact]
    public void A_path_in_a_text_of_the_system_is_not_checked_because_it_is_a_code_and_its_arguments()
    {
        // The arguments of a coded text are ids, names and numbers the builder chose: a path would be a bug of the builder, found by
        // its tests (F14-9); the model only judges what people and agents wrote.
        Assert.Empty(ClientDocumentRules.Validate(Complete(ClientText.OfSystem("document.summary-template", @"C:\Users\ana"))));
    }

    [Fact]
    public void Blocks_must_be_well_formed()
    {
        ClientDocument With(ClientDocumentBlock block) => new(Cover(), [new ClientDocumentSection(ClientDocumentSectionKind.Scope, [block])]);

        Assert.Equal(["document.list-empty"], ClientDocumentRules.Validate(With(new ClientList(false, []))));
        Assert.Equal(["document.table-malformed"], ClientDocumentRules.Validate(With(new ClientTable([], []))));
        Assert.Equal(
            ["document.table-malformed"],
            ClientDocumentRules.Validate(With(new ClientTable([ClientText.OfSystem("document.a"), ClientText.OfSystem("document.b")], [[ClientText.Written("una celda")]]))));
        Assert.Equal(
            ["document.evidence-fingerprint-invalid"],
            ClientDocumentRules.Validate(With(new ClientEvidenceList([new ClientEvidenceReference(Eid("EV-1"), "abc", ClientText.OfSystem("document.evidence-test-run"))]))));
        Assert.Equal(["document.block-unknown"], ClientDocumentRules.Validate(With(new UnknownBlock())));
        Assert.Equal(["document.section-empty"], ClientDocumentRules.Validate(new ClientDocument(Cover(), [new ClientDocumentSection(ClientDocumentSectionKind.Scope, [])])));
        Assert.Throws<ArgumentNullException>(() => ClientDocumentRules.Validate(null!));
    }

    private sealed record UnknownBlock : ClientDocumentBlock;

    [Theory]
    [InlineData("9c4da5d", true)]
    [InlineData("9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7", true)]
    [InlineData("9C4DA5D", false)]
    [InlineData("9c4da5", false)]
    [InlineData("main", false)]
    [InlineData("", false)]
    public void A_commit_is_seven_to_sixty_four_lowercase_hex_characters(string value, bool valid)
    {
        Assert.Equal(valid, ClientDocumentRules.IsCommit(value));
    }

    // ---- the hash of the model: deterministic (D-58) ----

    [Fact]
    public void The_hash_of_the_model_is_the_sha256_of_its_canonical_form_written_apart()
    {
        var document = new ClientDocument(
            new ClientDocumentCover("Mi tienda", null, At, 1, null),
            [new ClientDocumentSection(ClientDocumentSectionKind.Summary, [new ClientParagraph(ClientText.Written("Hola"))])]);

        var expected = ContentHash.Sha256Hex(
            "30:agent-studio/client-document/1;9:Mi tienda;0:;33:2026-11-11T09:00:00.0000000+00:00;1:1;0:;7:section;7:Summary;9:paragraph;7:Written;4:Hola;");

        Assert.Equal(expected, document.Sha256());
        Assert.Equal("agent-studio/client-document/1", ClientDocument.CanonicalVersion);
    }

    [Fact]
    public void The_same_model_always_has_the_same_hash_in_any_culture_and_any_change_gives_another()
    {
        var hash = Complete().Sha256();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "tr-TR", "de-DE", "ar-SA", "es-ES" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.Equal(hash, Complete().Sha256());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.True(ContentHash.IsSha256Hex(hash));
        var changes = new[]
        {
            Complete(ClientText.OfSystem("document.summary-template", 12, 3, 0.76m)),
            Complete(ClientText.Written("Otro resumen")),
            Complete(ClientText.ByAgent("Otro resumen", new ClientAgentMark(DraftRun, false))),
            new ClientDocument(Cover(version: 3), Complete().Sections),
            new ClientDocument(Cover(milestone: null), Complete().Sections),
            new ClientDocument(Cover(project: "Otra"), Complete().Sections),
            new ClientDocument(Cover(commit: null), Complete().Sections),
            new ClientDocument(Cover(), [.. Complete().Sections.Take(3)]),
        };
        Assert.Equal(changes.Length, changes.Select(c => c.Sha256()).Append(hash).Distinct().Count() - 1);
    }

    [Fact]
    public void An_agents_text_with_and_without_review_has_two_hashes_and_one_of_a_person_with_the_same_words_a_third()
    {
        var reviewed = Complete(ClientText.ByAgent("Resumen.", new ClientAgentMark(DraftRun, true))).Sha256();
        var notReviewed = Complete(ClientText.ByAgent("Resumen.", new ClientAgentMark(DraftRun, false))).Sha256();
        var person = Complete(ClientText.Written("Resumen.")).Sha256();

        Assert.Equal(3, new[] { reviewed, notReviewed, person }.Distinct().Count());
    }

    [Fact]
    public void The_texts_of_the_document_are_listed_with_their_section_in_reading_order()
    {
        var texts = Complete().Texts().ToList();

        Assert.Equal(ClientDocumentSectionKind.Summary, texts[0].Section);
        Assert.Equal(ClientDocumentSectionKind.Annexes, texts[^1].Section);
        Assert.Equal(10, texts.Count);
        Assert.Equal(["R1: calcular el IVA de cada línea", "Usar SQLite como almacén local", "R4: el redondeo queda sin probar"], texts.Where(t => t.Text.Source == ClientTextSource.Written).Select(t => t.Text.Value));
    }

    // ---- the list of what goes out (D-60) ----

    [Fact]
    public void The_list_of_what_goes_out_takes_the_parts_agents_wrote_from_the_document_itself()
    {
        var reviewed = ClientText.ByAgent("Resumen.", new ClientAgentMark(DraftRun, true));
        var document = Complete(reviewed) with { };
        var annex = new ClientDocumentSection(ClientDocumentSectionKind.Annexes, [new ClientParagraph(ClientText.ByAgent("Cómo se usa.", new ClientAgentMark(new RunId("aux-20261111-d4e5f6"), false)))]);
        document = document with { Sections = [.. document.Sections.Take(6), annex] };

        var disclosure = ClientDocumentDisclosure.Of(document, secretsRedacted: 2, pathsRemoved: [@"C:\Users\ana\repo", "~/x"], risksIncluded: 1, includesConsumption: false);

        Assert.Equal(
            [new ClientAgentPart(ClientDocumentSectionKind.Summary, DraftRun, true), new ClientAgentPart(ClientDocumentSectionKind.Annexes, new RunId("aux-20261111-d4e5f6"), false)],
            disclosure.AgentWrittenParts);
        Assert.Equal((2, 1, false), (disclosure.SecretsRedacted, disclosure.RisksIncluded, disclosure.IncludesConsumption));
        Assert.Equal([@"C:\Users\ana\repo", "~/x"], disclosure.PathsRemoved);
        Assert.Empty(disclosure.Validate());
        Assert.Empty(ClientDocumentDisclosure.Of(Complete(), 0, [], 0, includesConsumption: true).AgentWrittenParts);
        Assert.Equal(["document.disclosure-count-negative"], (disclosure with { SecretsRedacted = -1 }).Validate());
    }

    // ---- the drafts of an agent (D-59) ----

    [Fact]
    public void An_agent_writes_the_summary_or_an_explanation_of_use_and_each_has_its_place()
    {
        Assert.Equal(["Summary", "UsageExplanation"], Enum.GetNames<ClientDraftKind>());
        Assert.Equal(ClientDocumentSectionKind.Summary, ClientDraftKinds.SectionOf(ClientDraftKind.Summary));
        Assert.Equal(ClientDocumentSectionKind.Annexes, ClientDraftKinds.SectionOf(ClientDraftKind.UsageExplanation));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientDraftKinds.SectionOf((ClientDraftKind)9));
    }

    [Fact]
    public void A_draft_keeps_clean_paragraphs_and_leaves_out_what_a_document_for_clients_cannot_have()
    {
        var check = ClientDraftRules.Check(
        [
            "  Primer párrafo.  ",
            "Segundo\u202E párrafo\u0007.",
            @"Este menciona C:\Users\ana\repo y no puede ir.",
            " ",
            null,
            new string('p', ClientDocumentLimits.MaxDraftParagraphLength + 20),
        ]);

        Assert.Equal(["Primer párrafo.", "Segundo párrafo.", new string('p', ClientDocumentLimits.MaxDraftParagraphLength)], check.Paragraphs);
        Assert.Equal(
            ["a paragraph names a path of the machine.", $"a paragraph is longer than {ClientDocumentLimits.MaxDraftParagraphLength} characters: it was cut."],
            check.Discarded);
        Assert.Empty(ClientDraftRules.Check(null).Paragraphs);
    }

    [Fact]
    public void A_draft_has_at_most_six_paragraphs()
    {
        var check = ClientDraftRules.Check(Enumerable.Range(1, 8).Select(i => $"Párrafo {i}."));

        Assert.Equal(ClientDocumentLimits.MaxDraftParagraphs, check.Paragraphs.Count);
        Assert.Equal(6, ClientDocumentLimits.MaxDraftParagraphs);
        Assert.Equal(2, check.Discarded.Count);
        Assert.All(check.Discarded, d => Assert.Contains("one more than 6", d, StringComparison.Ordinal));
    }

    // ---- the files of an export (D-60) ----

    [Theory]
    [InlineData("Mi tienda", "Hito 1", 2, ClientDocumentFormat.Word, "mi-tienda-hito-1-v2.docx")]
    [InlineData("Mi tienda", null, 1, ClientDocumentFormat.Markdown, "mi-tienda-v1.md")]
    [InlineData("Ñandú & Cía. (2026)", "Versión «1»", 12, ClientDocumentFormat.Pdf, "nandu-cia-2026-version-1-v12.pdf")]
    [InlineData("  ", "  ", 3, ClientDocumentFormat.Html, "documento-v3.html")]
    [InlineData("..\\..\\Windows", null, 1, ClientDocumentFormat.Html, "windows-v1.html")]
    [InlineData("C:\\Users\\ana\\tienda", null, 1, ClientDocumentFormat.Markdown, "c-users-ana-tienda-v1.md")]
    public void Every_export_is_a_version_and_its_files_are_named_with_it_in_plain_ascii(string project, string? milestone, int version, ClientDocumentFormat format, string expected)
    {
        var name = ClientDocumentFiles.FileName(project, milestone, version, format);

        Assert.Equal(expected, name);
        Assert.True(ClientDocumentFiles.IsPlainFileName(name));
    }

    [Fact]
    public void The_name_of_a_file_is_cut_and_a_version_below_one_is_refused()
    {
        var name = ClientDocumentFiles.FileName(new string('x', 200), new string('y', 200), 1, ClientDocumentFormat.Word);

        Assert.True(name.Length <= 120, name);
        Assert.EndsWith("-v1.docx", name, StringComparison.Ordinal);
        Assert.True(ClientDocumentFiles.IsPlainFileName(name));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientDocumentFiles.FileName("a", null, 0, ClientDocumentFormat.Word));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientDocumentFiles.FileName("a", null, 1, (ClientDocumentFormat)9));
    }

    [Theory]
    [InlineData("mi-tienda-v2.docx", true)]
    [InlineData("informe final v2.pdf", true)]
    [InlineData("", false)]
    [InlineData("a/b.md", false)]
    [InlineData("a\\b.md", false)]
    [InlineData("C:informe.md", false)]
    [InlineData("..", false)]
    [InlineData("a..b.md", false)]
    [InlineData("informe.", false)]
    [InlineData("informe ", false)]
    [InlineData(" informe", false)]
    [InlineData("in*forme.md", false)]
    [InlineData("in\tforme.md", false)]
    public void A_plain_file_name_has_no_folder_no_drive_no_parent_and_no_character_windows_refuses(string name, bool plain)
    {
        Assert.Equal(plain, ClientDocumentFiles.IsPlainFileName(name));
    }

    // ---- the request to export (D-60) ----

    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000ab", @"C:\repo");

    private static ClientDocumentExportRequest Export(
        IReadOnlyList<ClientDocumentFormat>? formats = null,
        string language = "es-ES",
        string destination = @"C:\Entregas\tienda",
        string decidedBy = "user",
        ClientDocumentBuild? build = null) =>
        new(Project, build ?? new ClientDocumentBuild(Complete(), ClientDocumentDisclosure.Of(Complete(), 0, [], 1, false)), formats ?? [ClientDocumentFormat.Markdown, ClientDocumentFormat.Word], language, destination, decidedBy);

    [Fact]
    public void An_export_of_a_valid_build_in_chosen_formats_to_a_folder_by_a_person_breaks_no_rule()
    {
        Assert.Empty(ClientDocumentExportRules.Validate(Export()));
        Assert.Empty(ClientDocumentExportRules.Validate(Export(formats: [.. ClientDocumentFormats.All], language: "en")));
    }

    [Fact]
    public void Each_rule_of_an_export_has_its_own_code()
    {
        Assert.Equal(["document.export-formats-required"], ClientDocumentExportRules.Validate(Export(formats: [])));
        Assert.Equal(["document.export-format-unknown"], ClientDocumentExportRules.Validate(Export(formats: [(ClientDocumentFormat)9])));
        Assert.Equal(["document.export-format-duplicate"], ClientDocumentExportRules.Validate(Export(formats: [ClientDocumentFormat.Pdf, ClientDocumentFormat.Pdf])));
        Assert.Equal(["document.export-language-invalid"], ClientDocumentExportRules.Validate(Export(language: "castellano")));
        Assert.Equal(["document.export-destination-invalid"], ClientDocumentExportRules.Validate(Export(destination: @"entregas\tienda")));
        Assert.Equal(["document.export-destination-invalid"], ClientDocumentExportRules.Validate(Export(destination: " ")));
        Assert.Equal(["document.export-decider-required"], ClientDocumentExportRules.Validate(Export(decidedBy: " ")));
        Assert.Equal(["document.export-build-required"], ClientDocumentExportRules.Validate(Export() with { Build = null! }));
        Assert.Equal(
            ["document.local-path"],
            ClientDocumentExportRules.Validate(Export(build: new ClientDocumentBuild(Complete(ClientText.Written(@"Ver C:\Users\ana\x")), ClientDocumentDisclosure.Of(Complete(), 0, [], 0, false)))));
        Assert.Equal(
            ["document.disclosure-count-negative"],
            ClientDocumentExportRules.Validate(Export(build: new ClientDocumentBuild(Complete(), ClientDocumentDisclosure.Of(Complete(), -1, [], 0, false)))));
    }

    // ---- the surface of the contracts ----

    [Fact]
    public void The_ports_of_the_documents_are_the_documented_ones()
    {
        Assert.Equal(["BuildAsync"], MethodNames(typeof(IClientDocumentBuilder)));
        Assert.Equal(["ExportAsync"], MethodNames(typeof(IClientDocumentExporter)));
        Assert.Equal(["DraftAsync"], MethodNames(typeof(IClientDocumentDrafter)));
        Assert.Equal(["Render"], MethodNames(typeof(IUserTextRenderer)));
        Assert.Equal(["RenderAsync", "get_Format"], MethodNames(typeof(IClientDocumentFormatWriter)));
        Assert.Equal(typeof(Task<byte[]>), typeof(IClientDocumentFormatWriter).GetMethod("RenderAsync")!.ReturnType);
    }

    [Fact]
    public void The_model_has_no_language_and_no_text_of_the_log_the_prompts_or_the_consumption_unless_asked()
    {
        // D-58, «Lo que no va»: the request of the build cannot ask for prompts, transcripts or the journal; the consumption only with its flag.
        Assert.Equal(["Project", "Milestone", "IncludeConsumption", "AgentDrafts"], typeof(ClientDocumentBuildRequest).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));
        Assert.DoesNotContain(typeof(ClientDocument).GetProperties(), p => p.Name.Contains("Language", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ClientDocumentCover).GetProperties(), p => p.Name.Contains("Language", StringComparison.Ordinal) || p.Name.Contains("Path", StringComparison.Ordinal) || p.Name.Contains("User", StringComparison.Ordinal));
    }

    private static string[] MethodNames(Type type) =>
        [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).Order(StringComparer.Ordinal)];
}
