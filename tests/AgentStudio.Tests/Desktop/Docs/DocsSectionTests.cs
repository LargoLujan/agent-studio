using System.Globalization;
using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Memory.Consistency;
using AgentStudio.Tests.Memory.Diagrams;

namespace AgentStudio.Tests.Desktop.Docs;

/// <summary>
/// F6-10 and F6-11 on the screen (docs/20 D-26 and D-30): the list of diagrams with their states, their text, generating, regenerating, undoing, copying and exporting, with the
/// question about a file that was changed outside; and the discrepancies by group, which are read from what is saved and never start the analysis by themselves. The sections
/// work over the real catalog and the real checks of Memory, with models and a log prepared and temporary folders.
/// </summary>
public sealed class DocsSectionTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly DiagramRef Solution = new(DiagramKind.Solution, string.Empty);

    private sealed class Clipboard : IDocsClipboard
    {
        public List<string> Copied { get; } = [];

        public void Copy(string text) => Copied.Add(text);
    }

    private static DocsBackend Backend(DiagramFakes.World world, Clipboard? clipboard = null) =>
        new(world.Catalog, world.Catalog, world.Analysis, world.History, clipboard);

    private static async Task<(DocsDiagramsSection Section, GraphDocumentViewModel Document)> OpenDiagramsAsync(DiagramFakes.World world, Clipboard? clipboard = null)
    {
        var section = new DocsDiagramsSection(Backend(world, clipboard), Spanish);
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(world.Project, CancellationToken.None);
        return (section, document);
    }

    private static async Task<DocsDiscrepanciesSection> OpenDiscrepanciesAsync(DiagramFakes.World world)
    {
        var section = new DocsDiscrepanciesSection(Backend(world), Spanish);
        await new GraphDocumentViewModel([section]).SetProjectAsync(world.Project, CancellationToken.None);
        return section;
    }

    // ---- «Diagramas» ----

    [Fact]
    public async Task The_list_shows_every_diagram_with_its_state_and_the_first_is_chosen_with_a_notice_that_it_is_not_generated()
    {
        using var world = new DiagramFakes.World();

        var (section, _) = await OpenDiagramsAsync(world);

        Assert.Equal(6, section.Rows.Count);
        Assert.Equal("La solución", section.Rows[0].Name);
        Assert.Equal("Sin generar", section.Rows[0].StateText);
        Assert.Same(section.Rows[0], section.SelectedRow);
        Assert.Equal("Todavía no hay texto para este diagrama. Pulsa Generar.", section.SourceNotice);
        Assert.False(section.HasSource);
        Assert.Equal("Generar", section.GenerateLabel);
        Assert.True(section.GenerateCommand.CanExecute(null));
        Assert.False(section.UndoCommand.CanExecute(null));
        Assert.False(section.ExportCommand.CanExecute(null));
        Assert.False(section.CopyCommand.CanExecute(null));
        Assert.All(section.Rows, r => Assert.Contains(r.Name, r.AutomationName, StringComparison.Ordinal));
        Assert.Contains(section.Rows, r => r.Name == "Espacios de nombres de src/libs/Core/Core.csproj");
    }

    [Fact]
    public async Task Generating_shows_the_text_and_pressing_it_again_changes_nothing_and_says_so()
    {
        using var world = new DiagramFakes.World();
        var (section, _) = await OpenDiagramsAsync(world);

        await section.GenerateCommand.ExecuteAsync(null);
        await section.SourceShown;

        Assert.Equal("Al día", section.SelectedRow!.StateText);
        Assert.Equal("Regenerar", section.GenerateLabel);
        Assert.Equal("Diagrama generado.", section.Message);
        Assert.True(section.HasSource);
        Assert.StartsWith("%% Generado por Agent Studio.", section.SourceText, StringComparison.Ordinal);
        Assert.Null(section.SourceNotice);

        world.Clock.Advance(TimeSpan.FromHours(2));
        await section.GenerateCommand.ExecuteAsync(null);
        Assert.Equal("Ya estaba al día. No se ha cambiado nada.", section.Message);
    }

    [Fact]
    public async Task A_diagram_out_of_date_says_which_saved_version_it_describes_and_undo_puts_the_earlier_text_back()
    {
        using var world = new DiagramFakes.World();
        var (section, _) = await OpenDiagramsAsync(world);
        await section.GenerateCommand.ExecuteAsync(null);
        await section.SourceShown;
        var first = section.SourceText;

        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        await section.RefreshCommand.ExecuteAsync(null);
        Assert.Equal($"Desactualizado: describe la versión guardada {DiagramFixtures.Commit[..7]}", section.SelectedRow!.StateText);
        Assert.False(section.UndoCommand.CanExecute(null));

        await section.GenerateCommand.ExecuteAsync(null);
        await section.SourceShown;
        Assert.NotEqual(first, section.SourceText);
        Assert.True(section.UndoCommand.CanExecute(null));

        await section.UndoCommand.ExecuteAsync(null);
        await section.SourceShown;

        Assert.Equal(first, section.SourceText);
        Assert.Equal("Has vuelto al texto anterior.", section.Message);
        Assert.False(section.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copy_puts_the_text_on_the_clipboard_and_is_only_there_for_a_diagram_that_has_one()
    {
        using var world = new DiagramFakes.World();
        var clipboard = new Clipboard();
        var (section, _) = await OpenDiagramsAsync(world, clipboard);
        Assert.False(section.CopyCommand.CanExecute(null));

        await section.GenerateCommand.ExecuteAsync(null);
        await section.SourceShown;
        Assert.True(section.CopyCommand.CanExecute(null));
        section.CopyCommand.Execute(null);

        Assert.Equal(section.SourceText, Assert.Single(clipboard.Copied));
        Assert.Equal("Texto copiado.", section.Message);
    }

    [Fact]
    public async Task Exporting_writes_the_file_and_a_file_changed_outside_is_asked_about_and_only_overwritten_when_the_person_says_so()
    {
        using var world = new DiagramFakes.World();
        var (section, _) = await OpenDiagramsAsync(world);
        await section.GenerateCommand.ExecuteAsync(null);
        await section.ExportCommand.ExecuteAsync(null);
        var file = Path.Combine(world.Root, "docs", "generado", "diagramas", "solucion.md");

        Assert.True(File.Exists(file));
        Assert.Empty(section.Conflicts);
        Assert.Equal("Exportado y al día", section.SelectedRow!.ExportText);

        await File.WriteAllTextAsync(file, "mine\n", TestContext.Current.CancellationToken);
        await section.ExportCommand.ExecuteAsync(null);

        Assert.True(section.HasConflicts);
        Assert.Equal("docs/generado/diagramas/solucion.md (Exportado, pero se cambió fuera de la aplicación)", Assert.Single(section.Conflicts).Text);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));

        await section.ResolveConflictCommand.ExecuteAsync("new");
        Assert.False(section.HasConflicts);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(world.Root, "docs", "generado", "diagramas", "solucion-2.md")));

        await section.ExportCommand.ExecuteAsync(null);
        await section.ResolveConflictCommand.ExecuteAsync("skip");
        Assert.False(section.HasConflicts);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));

        await section.ExportCommand.ExecuteAsync(null);
        await section.ResolveConflictCommand.ExecuteAsync("overwrite");
        Assert.StartsWith("<!-- Generado por Agent Studio.", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_with_a_run_going_says_why_and_writes_nothing()
    {
        using var world = new DiagramFakes.World();
        var (section, _) = await OpenDiagramsAsync(world);
        await section.GenerateCommand.ExecuteAsync(null);
        world.Activity.Busy = true;

        await section.ExportCommand.ExecuteAsync(null);

        Assert.NotNull(section.Error);
        Assert.StartsWith("No se pudo completar la acción", section.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(world.Root, "docs")));
    }

    [Fact]
    public async Task Without_the_catalog_the_section_says_it_is_not_available_and_offers_no_action()
    {
        var section = new DocsDiagramsSection(DocsBackend.None, Spanish);
        await new GraphDocumentViewModel([section]).SetProjectAsync(DiagramFakes.Project(@"C:\work\none"), CancellationToken.None);

        Assert.False(section.IsAvailable);
        Assert.Equal("Los diagramas no están disponibles en esta instalación.", section.UnavailableText);
        Assert.Empty(section.Rows);
        Assert.False(section.GenerateCommand.CanExecute(null));
        Assert.False(section.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_the_project_clears_the_list_and_the_text()
    {
        using var world = new DiagramFakes.World();
        var (section, document) = await OpenDiagramsAsync(world);
        await section.GenerateCommand.ExecuteAsync(null);

        await document.SetProjectAsync(null, CancellationToken.None);

        Assert.Empty(section.Rows);
        Assert.False(section.HasSource);
        Assert.Null(section.SelectedRow);
        Assert.False(section.HasProject);
    }

    // ---- «Discrepancias» ----

    private static ConsistencyFinding Link(string path, int line, string target, string reason = "missing", string? found = null)
    {
        var facts = new List<DoctorFact> { DoctorFact.Of("target", target), DoctorFact.Of("reason", reason) };
        if (found is not null)
        {
            facts.Add(DoctorFact.Of("found", found));
        }

        return new ConsistencyFinding(ConsistencyCodes.LinkBroken, path, line, facts);
    }

    [Fact]
    public async Task Opening_the_section_reads_what_is_saved_and_never_starts_the_analysis()
    {
        using var world = new DiagramFakes.World();
        world.Analysis.Cached = false;

        var section = await OpenDiscrepanciesAsync(world);

        Assert.Equal(0, world.Analysis.Analyzed);
        Assert.False(section.HasResult);
        Assert.True(section.NeedsCalculation);
        Assert.Empty(section.Rows);
        Assert.True(section.CalculateCommand.CanExecute(null));

        await section.CalculateCommand.ExecuteAsync(null);

        Assert.Equal(1, world.Analysis.Analyzed);
        Assert.True(section.HasResult);
        Assert.True(section.IsClean);
        Assert.False(section.NeedsCalculation);
        Assert.Equal($"Describe la versión guardada {DiagramFixtures.Commit[..7]}.", section.DescribesText);
    }

    [Fact]
    public async Task The_findings_come_grouped_by_code_with_their_sentence_and_where_they_are()
    {
        using var world = new DiagramFakes.World();
        world.Analysis.Findings =
        [
            Link("docs/a.md", 3, "no.md"),
            Link("docs/a.md", 9, "../x.md", "outside"),
            Link("docs/b.md", 1, "Docs/A.MD", "case", "docs/a.md"),
            new ConsistencyFinding(ConsistencyCodes.AdrCitesMissing, "docs/adr/0001-a.md", 7, [DoctorFact.Of("cited", "src/gone/x.cs")]),
            new ConsistencyFinding(
                ConsistencyCodes.RequirementUncovered,
                null,
                null,
                [DoctorFact.Of("milestone", "Hito uno"), DoctorFact.Of("milestone-id", "mil-uno"), DoctorFact.Of("requirement", "R1"), DoctorFact.Of("title", "Sumar")]),
            new ConsistencyFinding(ConsistencyCodes.CheckFailed, null, null, [DoctorFact.Of("check", ConsistencyCodes.RequirementUncovered)]),
        ];

        var section = await OpenDiscrepanciesAsync(world);

        Assert.True(section.HasResult);
        Assert.False(section.IsClean);
        Assert.Equal(
            ["Enlaces rotos (3)", "Decisiones que citan ficheros que ya no existen (1)", "Requisitos sin ninguna tarea (1)", "Comprobaciones que no se pudieron hacer (1)"],
            section.Rows.Where(r => r.HasGroupTitle).Select(r => r.GroupTitle!).ToArray());
        Assert.Equal("El enlace no.md no lleva a ningún sitio.", section.Rows[0].Text);
        Assert.Equal("docs/a.md, línea 3", section.Rows[0].Location);
        Assert.Equal("El enlace ../x.md sale del proyecto.", section.Rows[1].Text);
        Assert.Equal("El enlace Docs/A.MD solo falla por las mayúsculas. El fichero se llama docs/a.md.", section.Rows[2].Text);
        Assert.Equal("Cita src/gone/x.cs, que ya no existe.", section.Rows[3].Text);
        Assert.Equal("El requisito R1 (Sumar) del hito Hito uno no tiene ninguna tarea.", section.Rows[4].Text);
        Assert.Null(section.Rows[4].Location);
        Assert.True(section.Rows[4].CanGoToBoard);
        Assert.False(section.Rows[4].CanOpenFile);
        Assert.Equal("No se pudo comprobar: Requisitos sin ninguna tarea. Vuelve a calcular.", section.Rows[5].Text);
    }

    [Fact]
    public async Task A_diagram_out_of_date_is_read_from_the_cache_each_time_so_regenerating_it_clears_the_row_without_a_new_analysis()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");

        var section = await OpenDiscrepanciesAsync(world);

        var row = Assert.Single(section.Rows);
        Assert.Equal("Diagramas desactualizados (1)", row.GroupTitle);
        Assert.Equal("La solución está desactualizado.", row.Text);
        Assert.True(row.CanRegenerate);

        await section.RegenerateCommand.ExecuteAsync(row);

        Assert.Empty(section.Rows);
        Assert.True(section.IsClean);
        Assert.Equal(0, world.Analysis.Analyzed);
    }

    [Fact]
    public async Task Open_file_asks_the_shell_for_the_file_when_it_is_still_in_the_folder_and_says_so_when_it_is_not()
    {
        using var world = new DiagramFakes.World();
        Directory.CreateDirectory(Path.Combine(world.Root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(world.Root, "docs", "a.md"), "x", TestContext.Current.CancellationToken);
        world.Analysis.Findings = [Link("docs/a.md", 3, "no.md"), Link("docs/gone.md", 1, "no.md")];
        var section = await OpenDiscrepanciesAsync(world);
        var asked = new List<string>();
        section.FileRequested += (_, path) => asked.Add(path);

        section.OpenFileCommand.Execute(section.Rows[0]);
        Assert.Equal(Path.Combine(world.Root, "docs", "a.md"), Assert.Single(asked));

        section.OpenFileCommand.Execute(section.Rows[1]);
        Assert.Single(asked);
        Assert.Equal("El fichero docs/gone.md ya no está en tu carpeta.", section.Message);
    }

    [Fact]
    public async Task Going_to_the_board_raises_the_request_and_a_cut_list_says_so()
    {
        using var world = new DiagramFakes.World();
        world.Analysis.Truncated = true;
        var section = await OpenDiscrepanciesAsync(world);
        var raised = 0;
        section.BoardRequested += (_, _) => raised++;

        section.GoToBoardCommand.Execute(null);

        Assert.Equal(1, raised);
        Assert.Equal("Había más discrepancias de las que se muestran.", section.Notice);
    }

    [Fact]
    public async Task Without_the_analysis_the_section_says_it_is_not_available()
    {
        var section = new DocsDiscrepanciesSection(DocsBackend.None, Spanish);
        await new GraphDocumentViewModel([section]).SetProjectAsync(DiagramFakes.Project(@"C:\work\none"), CancellationToken.None);

        Assert.False(section.IsAvailable);
        Assert.False(section.CalculateCommand.CanExecute(null));
        Assert.Equal("Las discrepancias no están disponibles en esta instalación.", section.UnavailableText);
    }

    // ---- the line of Auto Doctor (D-30) ----

    [Fact]
    public async Task The_line_of_auto_doctor_reads_what_is_saved_and_never_calculates()
    {
        using var world = new DiagramFakes.World();
        var source = new DiscrepanciesCheckSource(world.Analysis, world.History);

        world.Analysis.Cached = false;
        var unknown = Assert.Single(await source.CheckAsync(world.Project, CancellationToken.None));
        Assert.Equal(DoctorStatus.Unknown, unknown.Status);
        Assert.Equal("doctor.fix.open-graph", unknown.FixCode);

        world.Analysis.Cached = true;
        var ok = Assert.Single(await source.CheckAsync(world.Project, CancellationToken.None));
        Assert.Equal(DoctorStatus.Ok, ok.Status);
        Assert.Null(ok.FixCode);

        world.Analysis.Findings = [Link("a.md", 1, "x.md"), Link("a.md", 2, "y.md"), new ConsistencyFinding(ConsistencyCodes.AdrCitesMissing, "docs/adr/0001-a.md", 1, [DoctorFact.Of("cited", "src/x.cs")])];
        var warning = Assert.Single(await source.CheckAsync(world.Project, CancellationToken.None));
        Assert.Equal(DoctorStatus.Warning, warning.Status);
        Assert.Equal("3", warning.Facts[0].Value);
        Assert.Contains(warning.Facts, f => f.Name == ConsistencyCodes.LinkBroken && f.Value == "2");

        Assert.Equal(0, world.Analysis.Analyzed);
        Assert.Empty(await source.CheckAsync(null, CancellationToken.None));
        Assert.Empty(await new DiscrepanciesCheckSource(null, null).CheckAsync(world.Project, CancellationToken.None));
    }
}
