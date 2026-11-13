using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Memory.Consistency;
using AgentStudio.Memory.Diagrams;
using AgentStudio.Tests.Memory.Diagrams;

namespace AgentStudio.Tests.Memory.Consistency;

/// <summary>
/// F6-11 (docs/20 D-27 to D-29, ADR-0045): the four deterministic checks, each over a prepared repository (the files of a commit in memory) with one failure of
/// its kind and one without, the cases of D-28 (anchor, URL, block of code, case, pattern with <c>*</c>), and Markdown files that are hostile data.
/// </summary>
public sealed class ConsistencyChecksTests
{
    private static ConsistencyScope Scope(FakeSnapshot snapshot, CodeGraph? graph = null) => new(DiagramFakes.Project(@"C:\work\project"), snapshot, graph);

    private static async Task<IReadOnlyList<ConsistencyFinding>> Links(FakeSnapshot snapshot) =>
        await new LinkBrokenCheck().RunAsync(Scope(snapshot), CancellationToken.None) ?? throw new InvalidOperationException("applies");

    private static async Task<IReadOnlyList<ConsistencyFinding>> Adrs(FakeSnapshot snapshot) =>
        await new AdrCitesMissingCheck().RunAsync(Scope(snapshot), CancellationToken.None) ?? throw new InvalidOperationException("applies");

    private static string Fact(ConsistencyFinding finding, string name) => finding.Facts.Single(f => f.Name == name).Value;

    // ---- doc.link-broken ----

    [Fact]
    public async Task A_repository_with_only_good_links_has_no_finding()
    {
        var snapshot = new FakeSnapshot(
            ("README.md", "# Inicio\nVer [la guía](docs/guia.md) y ![logo](img/logo.png).\n"),
            ("docs/guia.md", "Volver a [inicio](../README.md#inicio), a [la carpeta](../img/) o a [la raíz](/README.md).\n"),
            ("img/logo.png", "png"));

        Assert.Empty(await Links(snapshot));
    }

    [Fact]
    public async Task A_link_to_a_path_that_does_not_exist_is_a_finding_with_its_file_and_line()
    {
        var snapshot = new FakeSnapshot(
            ("README.md", "uno\ndos [roto](docs/no-existe.md) tres\n\n![falta](img/falta.png)\n"),
            ("docs/guia.md", "ok"));

        var findings = await Links(snapshot);

        Assert.Collection(
            findings,
            first =>
            {
                Assert.Equal(ConsistencyCodes.LinkBroken, first.Code);
                Assert.Equal("README.md", first.Path);
                Assert.Equal(2, first.Line);
                Assert.Equal("docs/no-existe.md", Fact(first, "target"));
                Assert.Equal("missing", Fact(first, "reason"));
            },
            second =>
            {
                Assert.Equal(4, second.Line);
                Assert.Equal("img/falta.png", Fact(second, "target"));
            });
    }

    [Fact]
    public async Task The_cases_of_the_rules_anchor_url_code_title_spaces_and_the_anchor_of_a_good_path()
    {
        var snapshot = new FakeSnapshot(
            ("docs/a.md", string.Join(
                '\n',
                "[solo ancla](#seccion)",
                "[web](https://example.test/x) [correo](mailto:a@b.test) [red](//cdn.test/x.js) [ftp](ftp://h/x)",
                "[buena con ancla](b.md#parte) [con consulta](b.md?plain=1) [con título](b.md \"un título\")",
                "[con espacios](<con espacios.md>) [codificado](con%20espacios.md) [carpeta](./) [raíz](..)",
                "`[en código](no-existe.md)` y [rota](no.md)",
                "```",
                "[en bloque](no-existe-bloque.md)",
                "```",
                "~~~~",
                "[en bloque con tilde](no-existe-tilde.md)",
                "~~~~",
                "\\[escapado\\](no-existe-escapado.md)",
                "[ancla rota en rota](nada.md#x)")),
            ("docs/b.md", "ok"),
            ("docs/con espacios.md", "ok"));

        var findings = await Links(snapshot);

        Assert.Equal(["no.md", "nada.md#x"], findings.Select(f => Fact(f, "target")).ToArray());
        Assert.Equal([5, 13], findings.Select(f => f.Line!.Value).ToArray());
    }

    [Fact]
    public async Task A_link_that_leaves_the_repository_is_a_finding_and_so_is_a_drive_of_windows()
    {
        var snapshot = new FakeSnapshot(
            ("a.md", "[arriba](../fuera.md) [más](docs/../../x.md) [disco](C:/x/y.md) [disco2](D:\\x.md)"),
            ("docs/b.md", "[sube](../../afuera.md)"));

        var findings = await Links(snapshot);

        Assert.Equal(5, findings.Count);
        Assert.All(findings, f => Assert.Equal("outside", Fact(f, "reason")));
    }

    [Fact]
    public async Task A_link_that_only_fails_by_case_is_said_so_because_on_github_it_is_broken()
    {
        var snapshot = new FakeSnapshot(("README.md", "[guía](Docs/Guia.MD)"), ("docs/guia.md", "ok"));

        var finding = Assert.Single(await Links(snapshot));

        Assert.Equal("case", Fact(finding, "reason"));
        Assert.Equal("docs/guia.md", Fact(finding, "found"));
    }

    [Fact]
    public async Task The_folder_of_the_export_is_skipped()
    {
        var snapshot = new FakeSnapshot(
            ("docs/generado/diagramas/solucion.md", "[roto](no.md)"),
            ("docs/generado/otro.md", "[roto](no.md)"),
            ("docs/guia.md", "[roto](no.md)"));

        var findings = await Links(snapshot);

        Assert.Equal("docs/guia.md", Assert.Single(findings).Path);
    }

    [Fact]
    public async Task A_folder_this_computer_exported_diagrams_to_is_skipped_too()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.ExportAsync(world.Project, new DiagramExportRequest([new DiagramRef(DiagramKind.Solution, string.Empty)], "docs/otros", DiagramExportConflictChoice.Ask), CancellationToken.None);
        var snapshot = new FakeSnapshot(("docs/otros/solucion.md", "[roto](no.md)"), ("docs/otros/mio.md", "[roto](no.md)"), ("docs/guia.md", "[roto](no.md)"));

        var findings = await new LinkBrokenCheck(world.Catalog.Store).RunAsync(new ConsistencyScope(world.Project, snapshot), CancellationToken.None);

        Assert.Equal("docs/guia.md", Assert.Single(findings!).Path);
    }

    [Fact]
    public async Task Markdown_files_are_data_that_cannot_hang_or_overflow_the_reader()
    {
        // Lines of the longest length that is read, of the patterns that make a reader with backtracking or a quadratic scan crawl; under the limit of a file.
        var hostile = new StringBuilder();
        foreach (var pattern in new[] { "[", "`", "](", "[a](", "`a``b```c", "[a](((((((", "![", "[[a]](", "`[`](" })
        {
            var line = string.Concat(Enumerable.Repeat(pattern, MarkdownScanner.MaxLineLength / pattern.Length));
            for (var i = 0; i < 40; i++)
            {
                hostile.Append(line).Append('\n');
            }
        }

        hostile.Append("\0\u202E\uD800 [x](y.md)\n");
        Assert.True(hostile.Length < MarkdownFiles.MaxFileBytes);
        var files = new List<(string, string)> { ("docs/hostil.md", hostile.ToString()) };
        for (var i = 0; i < 40; i++)
        {
            files.Add(($"docs/muchos/{i:D2}.md", new string('x', 100) + "[roto](no.md)\n"));
        }

        var watch = Stopwatch.StartNew();
        var findings = await Links(new FakeSnapshot([.. files]));

        // A generous bound: it only has to tell a linear scan (well under a second) from one that grows with the square of the line (minutes), also on a computer that is busy.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), watch.Elapsed.ToString());
        Assert.Contains(findings, f => f.Path == "docs/muchos/00.md");
    }

    [Fact]
    public async Task A_file_over_the_limit_is_skipped_and_only_the_first_five_thousand_files_are_read()
    {
        var files = new List<(string, string)> { ("docs/enorme.md", "[roto](no.md)" + new string(' ', (int)MarkdownFiles.MaxFileBytes)) };

        // The huge file counts as one of the five thousand. The files after them are not read: their broken link is never found.
        for (var i = 0; i < MarkdownFiles.MaxFiles + 10; i++)
        {
            files.Add(($"z/{i:D5}.md", i == 1 || i >= MarkdownFiles.MaxFiles - 1 ? "[roto](no.md)" : "ok"));
        }

        var findings = await Links(new FakeSnapshot([.. files]));

        Assert.Equal("z/00001.md", Assert.Single(findings).Path);
    }

    [Fact]
    public async Task More_findings_than_the_cap_are_returned_so_the_service_can_say_it_cut_the_list()
    {
        var links = string.Join('\n', Enumerable.Range(0, ConsistencyReport.MaxFindings + 50).Select(i => $"[x](no-{i}.md)"));

        var findings = await Links(new FakeSnapshot(("a.md", links)));

        Assert.Equal(ConsistencyReport.MaxFindings + 1, findings.Count);
    }

    // ---- doc.adr-cites-missing ----

    [Fact]
    public async Task An_adr_that_cites_a_file_that_is_gone_is_a_finding_and_one_that_cites_what_exists_is_not()
    {
        var snapshot = new FakeSnapshot(
            ("src/Foo/Bar.cs", "x"),
            ("docs/adr/0001-uno.md", "Decide en `src/Foo/Bar.cs` y en `src/Foo/`.\nPero `src/Borrado/Viejo.cs` ya no está.\nOtra vez `src/Borrado/Viejo.cs`.\n"),
            ("docs/adr/0002-dos.md", "Todo existe: `src/Foo/Bar.cs`.\n"));

        var findings = await Adrs(snapshot);

        var finding = Assert.Single(findings);
        Assert.Equal(ConsistencyCodes.AdrCitesMissing, finding.Code);
        Assert.Equal("docs/adr/0001-uno.md", finding.Path);
        Assert.Equal(2, finding.Line);
        Assert.Equal("src/Borrado/Viejo.cs", Fact(finding, "cited"));
    }

    [Fact]
    public async Task What_looks_like_a_path_follows_the_rules_of_d28()
    {
        var snapshot = new FakeSnapshot(
            ("src/Foo/Bar.cs", "x"),
            ("docs/adr/0003-reglas.md", string.Join(
                '\n',
                "`src/**/*.cs` con prefijo que existe",
                "`nada/**/*.cs` con prefijo que no",
                "`<proyecto>/x.cs` plantilla",
                "`{a}/y.md` variable",
                "`%LOCALAPPDATA%/z.json` variable de entorno",
                "`Bar.cs` sin barra",
                "`una frase con / y espacios.md` con espacios",
                "`C:/x/y.md` con unidad",
                "`..\\fuera\\x.md` sube",
                "`src\\Foo\\Bar.cs` con barras de Windows",
                "`./src/Foo/Bar.cs` con punto",
                "`src/falta` sin extensión",
                "`src/Falta/` carpeta que falta",
                "`src/Foo/` carpeta que está",
                "`SRC/FOO/BAR.CS` solo por mayúsculas",
                "```",
                "`src/en/bloque.cs`",
                "```")),
            ("docs/otro.md", "`src/no/existe.cs` pero no es un ADR"),
            ("docs/adr/README.md", "`src/no/existe.cs` pero no es NNNN"),
            ("docs/adr/0004-sin-adr.txt", "`src/no/existe.cs`"));

        var findings = await Adrs(snapshot);

        Assert.Equal(["nada/**/*.cs", "src/Falta/"], findings.Select(f => Fact(f, "cited")).ToArray());
        Assert.Equal([2, 13], findings.Select(f => f.Line!.Value).ToArray());
    }

    [Theory]
    [InlineData("docs/adr/0045-x.md", true)]
    [InlineData("adr/0001-a.md", true)]
    [InlineData("a/ADR/0001-a.MD", true)]
    [InlineData("docs/adr/045-x.md", false)]
    [InlineData("docs/adr/0045x.md", false)]
    [InlineData("docs/adr/README.md", false)]
    [InlineData("0001-a.md", false)]
    [InlineData("docs/0001-a.md", false)]
    public void An_adr_is_a_file_nnnn_dash_md_under_a_folder_called_adr(string path, bool expected)
    {
        Assert.Equal(expected, AdrCitesMissingCheck.IsAdr(path));
    }

    // ---- doc.diagram-stale ----

    [Fact]
    public async Task A_diagram_whose_model_changed_is_a_finding_without_file_or_line_and_it_does_not_apply_without_a_model()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var staleness = new DiagramStaleness(world.Catalog.Store, world.Runs, world.Clock);
        var check = new DiagramStaleCheck(staleness);
        var snapshot = new FakeSnapshot(("README.md", "x"));

        var fresh = await check.RunAsync(new ConsistencyScope(world.Project, snapshot, DiagramFixtures.Graph()), CancellationToken.None);
        var changed = await check.RunAsync(new ConsistencyScope(world.Project, snapshot, DiagramFixtures.Graph(appName: "Renamed")), CancellationToken.None);
        var without = await check.RunAsync(new ConsistencyScope(world.Project, snapshot), CancellationToken.None);

        Assert.Empty(fresh!);
        var finding = Assert.Single(changed!);
        Assert.Equal(ConsistencyCodes.DiagramStale, finding.Code);
        Assert.Null(finding.Path);
        Assert.Null(finding.Line);
        Assert.Equal("solucion", Fact(finding, "diagram"));
        Assert.Equal(DiagramFixtures.Commit, Fact(finding, "describes"));
        Assert.Null(without);
    }

    [Fact]
    public async Task A_file_that_was_exported_is_never_read_by_the_check_of_the_diagrams()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.ExportAsync(world.Project, new DiagramExportRequest([new DiagramRef(DiagramKind.Solution, string.Empty)], DiagramExportRequest.DefaultFolder, DiagramExportConflictChoice.Ask), CancellationToken.None);
        File.WriteAllText(Path.Combine(world.Root, "docs", "generado", "diagramas", "solucion.md"), "garbage\n");
        var check = new DiagramStaleCheck(new DiagramStaleness(world.Catalog.Store, world.Runs, world.Clock));

        var findings = await check.RunAsync(new ConsistencyScope(world.Project, new FakeSnapshot(("README.md", "x")), DiagramFixtures.Graph()), CancellationToken.None);

        Assert.Empty(findings!);
    }

    // ---- requirement.uncovered ----

    [Fact]
    public async Task A_requirement_of_an_open_milestone_that_no_task_covers_is_a_finding_and_without_open_milestones_the_check_does_not_apply()
    {
        var open = new MilestoneId("mil-uno");
        var closed = new MilestoneId("mil-dos");
        var milestones = new FakeMilestones(
            View(open, "Primer hito", MilestoneStatus.Open),
            View(closed, "Hito cerrado", MilestoneStatus.Closed));
        var board = new FakeBoard(new Dictionary<string, MilestoneBoard>
        {
            [open.Value] = new(open, 1, new string('a', 64), [new BoardCard(BoardCardKind.Requirement, "R1", "Sumar", BoardColumn.Backlog, null, null), new BoardCard(BoardCardKind.Requirement, "R2", "Restar", BoardColumn.Done, "run-1", null)], []),
            [closed.Value] = new(closed, 1, new string('a', 64), [new BoardCard(BoardCardKind.Requirement, "R9", "Cerrado", BoardColumn.Backlog, null, null)], []),
        });

        var findings = await new RequirementUncoveredCheck(milestones, board).RunAsync(Scope(new FakeSnapshot()), CancellationToken.None);

        var finding = Assert.Single(findings!);
        Assert.Equal(ConsistencyCodes.RequirementUncovered, finding.Code);
        Assert.Null(finding.Path);
        Assert.Equal("Primer hito", Fact(finding, "milestone"));
        Assert.Equal("R1", Fact(finding, "requirement"));
        Assert.Equal("Sumar", Fact(finding, "title"));
        Assert.Equal([open.Value], board.Asked);

        var none = await new RequirementUncoveredCheck(new FakeMilestones(View(closed, "Hito cerrado", MilestoneStatus.Closed)), board).RunAsync(Scope(new FakeSnapshot()), CancellationToken.None);
        Assert.Null(none);
        Assert.Null(await new RequirementUncoveredCheck(new FakeMilestones(), board).RunAsync(Scope(new FakeSnapshot()), CancellationToken.None));
    }

    // ---- the service ----

    [Fact]
    public async Task The_service_joins_the_findings_marks_what_does_not_apply_and_orders_by_the_codes_of_the_screen()
    {
        var snapshot = new FakeSnapshot(("README.md", "[roto](no.md)"), ("docs/adr/0001-a.md", "`src/gone/x.cs`"));
        var service = new ConsistencyService([new AdrCitesMissingCheck(), new LinkBrokenCheck(), new Scripted(ConsistencyCodes.RequirementUncovered, null)]);

        var report = await service.RunAsync(DiagramFakes.Project(@"C:\work\p"), snapshot, CancellationToken.None);

        Assert.Equal(snapshot.Commit, report.Commit);
        Assert.Equal([ConsistencyCodes.LinkBroken, ConsistencyCodes.AdrCitesMissing], report.Findings.Select(f => f.Code).ToArray());
        Assert.Equal([ConsistencyCodes.RequirementUncovered], report.NotApplicable);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task A_check_that_throws_is_a_finding_that_names_it_and_never_quotes_the_exception_nor_a_silent_not_applicable()
    {
        var service = new ConsistencyService([new Scripted(ConsistencyCodes.RequirementUncovered, new InvalidOperationException("secret detail C:\\Users\\x")), new LinkBrokenCheck()]);

        var report = await service.RunAsync(DiagramFakes.Project(@"C:\work\p"), new FakeSnapshot(("README.md", "ok")), CancellationToken.None);

        var failed = Assert.Single(report.Findings);
        Assert.Equal(ConsistencyCodes.CheckFailed, failed.Code);
        Assert.Equal(ConsistencyCodes.RequirementUncovered, Fact(failed, "check"));
        Assert.DoesNotContain("secret", string.Join(' ', failed.Facts.Select(f => f.Value)), StringComparison.Ordinal);
        Assert.Empty(report.NotApplicable);
    }

    [Fact]
    public async Task A_cancellation_goes_through_the_service()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = new ConsistencyService([new Scripted(ConsistencyCodes.LinkBroken, new OperationCanceledException())]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(DiagramFakes.Project(@"C:\work\p"), new FakeSnapshot(), cancelled.Token));
    }

    [Fact]
    public async Task More_than_two_thousand_findings_are_cut_and_the_report_says_so()
    {
        var many = Enumerable.Range(0, ConsistencyReport.MaxFindings + 1).Select(i => new ConsistencyFinding(ConsistencyCodes.LinkBroken, $"a{i:D5}.md", 1, [])).ToList();
        var service = new ConsistencyService([new Scripted(ConsistencyCodes.LinkBroken, null, many)]);

        var report = await service.RunAsync(DiagramFakes.Project(@"C:\work\p"), new FakeSnapshot(), CancellationToken.None);

        Assert.True(report.Truncated);
        Assert.Equal(ConsistencyReport.MaxFindings, report.Findings.Count);
    }

    [Fact]
    public async Task A_finding_of_a_code_that_is_not_the_one_of_its_check_is_left_out()
    {
        var foreign = new ConsistencyFinding(ConsistencyCodes.AdrCitesMissing, "a.md", 1, []);
        var service = new ConsistencyService([new Scripted(ConsistencyCodes.LinkBroken, null, [foreign])]);

        var report = await service.RunAsync(DiagramFakes.Project(@"C:\work\p"), new FakeSnapshot(), CancellationToken.None);

        Assert.Empty(report.Findings);
    }

    // ---- doubles ----

    private static MilestoneView View(MilestoneId id, string name, MilestoneStatus status) => new(
        new MilestoneHeader(id, name, "objetivo", new string('c', 64), null, DiagramFixtures.At),
        status,
        new string('c', 64),
        [],
        [],
        [],
        []);

    private sealed class Scripted(string code, Exception? throws, IReadOnlyList<ConsistencyFinding>? findings = null) : IConsistencyCheck
    {
        public string Code => code;

        public Task<IReadOnlyList<ConsistencyFinding>?> RunAsync(ConsistencyScope scope, CancellationToken ct) =>
            throws is not null ? throw throws : Task.FromResult(findings);
    }

    private sealed class FakeMilestones(params MilestoneView[] views) : IMilestoneService
    {
        public Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MilestoneView>>(views);

        public Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, AgentStudio.Domain.Requirements.FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, AgentStudio.Domain.Common.EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeBoard(Dictionary<string, MilestoneBoard> boards) : IBoardProjector
    {
        public List<string> Asked { get; } = [];

        public Task<MilestoneBoard> ProjectAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken)
        {
            Asked.Add(milestone.Value);
            return Task.FromResult(boards[milestone.Value]);
        }

        public Task<IReadOnlyList<BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>The files of a commit, in memory: the paths in ordinal order and their text. It refuses a path it does not have and a file over the limit, like the real copy.</summary>
internal sealed class FakeSnapshot : IRepositorySnapshot
{
    private readonly Dictionary<string, byte[]> _files;

    public FakeSnapshot(params (string Path, string Text)[] files)
    {
        _files = files.ToDictionary(f => f.Path, f => new UTF8Encoding(false).GetBytes(f.Text), StringComparer.Ordinal);
        Paths = [.. _files.Keys.Order(StringComparer.Ordinal)];
    }

    public string Commit { get; } = DiagramFixtures.Commit;

    public string Root => @"C:\work\copy";

    public IReadOnlyList<string> Paths { get; }

    public Stream OpenRead(string relativePath, long maxBytes)
    {
        if (!_files.TryGetValue(relativePath, out var bytes))
        {
            throw new ArgumentException("Not a path of the copy.", nameof(relativePath));
        }

        return bytes.Length > maxBytes ? throw new IOException("Over the limit.") : new MemoryStream(bytes);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
