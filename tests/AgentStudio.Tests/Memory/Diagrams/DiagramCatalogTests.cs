using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Graph;
using AgentStudio.Memory.Diagrams;

namespace AgentStudio.Tests.Memory.Diagrams;

/// <summary>
/// F6-10 (docs/20 D-25, ADR-0045): the cache of the diagrams with its index, «desactualizado» decided with the index only, regenerating and undoing byte by byte, and
/// the export to the repository, which is an order of the person and does not overwrite a file that was changed outside. Every test works in temporary folders: none
/// writes to the real <c>%LOCALAPPDATA%</c>.
/// </summary>
public sealed class DiagramCatalogTests
{
    private static readonly DiagramRef Solution = new(DiagramKind.Solution, string.Empty);

    private static readonly DiagramRef Core = new(DiagramKind.ProjectNamespaces, DiagramFixtures.CoreProject);

    private static readonly DiagramRef Plan = new(DiagramKind.RunPlan, DiagramFixtures.Run);

    private static readonly DiagramRef Sequence = new(DiagramKind.RunSequence, DiagramFixtures.Run);

    private static DiagramStatus Of(IReadOnlyList<DiagramStatus> list, DiagramRef diagram) => Assert.Single(list, s => s.Diagram == diagram);

    [Fact]
    public async Task Nothing_is_generated_until_the_person_asks_and_every_diagram_is_listed()
    {
        using var world = new DiagramFakes.World();

        var list = await world.Catalog.ListAsync(world.Project, CancellationToken.None);

        Assert.All(list, s => Assert.Equal(DiagramState.NotGenerated, s.State));
        Assert.All(list, s => Assert.Equal(DiagramExportState.NotExported, s.Export));
        Assert.Contains(list, s => s.Diagram == Solution);
        Assert.Contains(list, s => s.Diagram == Core);
        Assert.Contains(list, s => s.Diagram == Plan);
        Assert.Contains(list, s => s.Diagram == Sequence);
        Assert.Equal(1 + 3 + 2, list.Count);
        Assert.Empty(Directory.EnumerateFileSystemEntries(world.Cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(world.Root));
    }

    [Fact]
    public async Task Generating_keeps_the_text_in_the_cache_with_its_header_and_the_status_says_which_commit_it_describes()
    {
        using var world = new DiagramFakes.World();

        var status = await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var text = await world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None);

        Assert.Equal(DiagramState.Current, status.State);
        Assert.Equal(DiagramFixtures.Commit, status.Describes);
        Assert.Equal(DiagramFixtures.Graph().ModelHash, status.ModelHash);
        Assert.Equal(DiagramFixtures.At, status.GeneratedAt);
        Assert.False(status.HasPrevious);
        Assert.Equal(GraphDiagrams.Solution(DiagramFixtures.Graph(), DiagramFixtures.At).Mermaid, text);

        // The repository of the person is untouched: the cache lives apart.
        Assert.Empty(Directory.EnumerateFileSystemEntries(world.Root));
        Assert.NotEmpty(Directory.EnumerateFiles(world.Cache, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Regenerating_a_diagram_that_is_up_to_date_changes_nothing_not_even_its_date()
    {
        using var world = new DiagramFakes.World();
        var first = await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var files = SnapshotOf(world.Cache);

        world.Clock.Advance(TimeSpan.FromDays(3));
        var again = await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        Assert.Equal(first, again);
        Assert.Equal(files, SnapshotOf(world.Cache));
    }

    [Fact]
    public async Task A_diagram_is_out_of_date_when_the_fingerprint_of_the_model_changes_and_not_when_only_the_commit_does()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.ProjectNamespaces, DiagramFixtures.CoreProject, CancellationToken.None);

        // A new commit with the very same model: the fingerprint is the same, so nothing is out of date.
        var graph = DiagramFixtures.Graph();
        var other = new string('7', 40);
        world.Analysis.Graph = new CodeGraph(other, graph.GeneratorVersion, graph.Nodes, graph.Edges, graph.Gaps);
        world.History.Commit = other;
        var same = await world.Catalog.ListAsync(world.Project, CancellationToken.None);

        Assert.Equal(DiagramState.Current, Of(same, Solution).State);
        Assert.Equal(DiagramState.Current, Of(same, Core).State);

        // Now the model changes: a project is renamed. The solution is out of date; the namespaces of Core are not, because their own diagram did not change.
        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        world.History.Commit = DiagramFixtures.Commit;
        var changed = await world.Catalog.ListAsync(world.Project, CancellationToken.None);

        var stale = Of(changed, Solution);
        Assert.Equal(DiagramState.Stale, stale.State);
        Assert.Equal(DiagramFixtures.Commit, stale.Describes);
        Assert.Equal(DiagramState.Current, Of(changed, Core).State);
    }

    [Fact]
    public async Task Without_an_analysis_of_the_commit_a_diagram_is_current_only_if_it_describes_that_very_commit()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        world.Analysis.Cached = false;
        Assert.Equal(DiagramState.Current, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).State);

        world.History.Commit = new string('7', 40);
        Assert.Equal(DiagramState.Stale, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).State);

        // Listing never starts the analysis.
        Assert.Equal(0, world.Analysis.Analyzed);
    }

    [Fact]
    public async Task Regenerating_keeps_the_previous_text_and_undoing_puts_it_back_byte_by_byte()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var before = await world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None);
        var statusBefore = Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution);

        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        world.Clock.Advance(TimeSpan.FromHours(1));
        var regenerated = await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var after = await world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None);

        Assert.NotEqual(before, after);
        Assert.True(regenerated.HasPrevious);
        Assert.Equal(DiagramState.Current, regenerated.State);

        var undone = await world.Catalog.RestorePreviousAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var restored = await world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None);

        Assert.Equal(before, restored);
        Assert.Equal(Encoding.UTF8.GetBytes(before!), Encoding.UTF8.GetBytes(restored!));
        Assert.False(undone.HasPrevious);

        // Its line of the index is back too: the model of the old text is not the model of now, so it says so.
        Assert.Equal(statusBefore.ModelHash, undone.ModelHash);
        Assert.Equal(statusBefore.GeneratedAt, undone.GeneratedAt);
        Assert.Equal(DiagramState.Stale, undone.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Catalog.RestorePreviousAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_diagram_generated_for_the_first_time_has_nothing_to_undo()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Catalog.RestorePreviousAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task The_state_is_decided_by_the_index_alone_a_lost_or_damaged_text_changes_nothing_but_the_text()
    {
        using var world = new DiagramFakes.World();
        var status = await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var texts = Directory.EnumerateFiles(world.Cache, "*.mmd", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(texts);

        await File.WriteAllTextAsync(file, "flowchart LR\n  evil --> evil\n", CancellationToken.None);
        var damaged = Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution);
        Assert.Equal(status.State, damaged.State);
        Assert.Equal(status.ModelHash, damaged.ModelHash);
        Assert.Null(await world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None));

        File.Delete(file);
        Assert.Equal(DiagramState.Current, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).State);

        // Generating again repairs the text, because what is in the cache is not what the index fingerprinted.
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        Assert.Equal(GraphDiagrams.Solution(DiagramFixtures.Graph(), DiagramFixtures.At).Mermaid, await world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None));
    }

    [Fact]
    public async Task An_index_that_cannot_be_read_is_an_empty_index_and_never_an_error()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        foreach (var index in Directory.EnumerateFiles(world.Cache, "index.json", SearchOption.AllDirectories))
        {
            await File.WriteAllTextAsync(index, "{ not json", CancellationToken.None);
        }

        var list = await world.Catalog.ListAsync(world.Project, CancellationToken.None);

        Assert.All(list, s => Assert.Equal(DiagramState.NotGenerated, s.State));
    }

    [Fact]
    public async Task The_name_of_a_cache_file_is_never_the_subject_that_came_from_data()
    {
        var graph = DiagramFixtures.Graph();
        using var world = new DiagramFakes.World(graph);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.ProjectNamespaces, DiagramFixtures.CoreProject, CancellationToken.None);

        var names = Directory.EnumerateFiles(world.Cache, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();

        Assert.DoesNotContain(names, n => n!.Contains("Core", StringComparison.Ordinal));
        Assert.Contains(names, n => n!.StartsWith("proyecto-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_diagrams_of_a_run_come_from_its_plan_and_its_log_and_an_entry_of_the_run_makes_the_sequence_out_of_date()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunPlan, DiagramFixtures.Run, CancellationToken.None);
        var sequence = await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunSequence, DiagramFixtures.Run, CancellationToken.None);

        Assert.Null(sequence.Describes);
        Assert.Contains("sequenceDiagram", await world.Catalog.ReadAsync(world.Project, Sequence, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("flowchart TD", await world.Catalog.ReadAsync(world.Project, Plan, CancellationToken.None), StringComparison.Ordinal);

        // An entry of another run does not move it.
        var other = DiagramFixtures.Log("other-run");
        world.Recorder.Entries.AddRange(other);
        var untouched = await world.Catalog.ListAsync(world.Project, CancellationToken.None);
        Assert.Equal(DiagramState.Current, Of(untouched, Sequence).State);
        Assert.Equal(DiagramState.Current, Of(untouched, Plan).State);

        // An entry the diagram draws, in that run, does.
        var last = world.Recorder.Entries.Where(e => e.Kind == DecisionKind.RunFinished).First();
        var extra = new DecisionEntry(
            100,
            DiagramFixtures.At.AddHours(1),
            new Actor(ActorKind.Orchestrator, "Orchestrator"),
            DecisionKind.ReviewRequested,
            "resumen",
            DecisionPayloadSerializer.Serialize(new ReviewRequestedPayload(new TaskId("T2"), new AgentId("juez")) { RunId = DiagramFixtures.Run }),
            null,
            last.Hash,
            ContentHash.Sha256Hex("extra"));
        world.Recorder.Entries.Add(extra);
        var moved = await world.Catalog.ListAsync(world.Project, CancellationToken.None);

        Assert.Equal(DiagramState.Stale, Of(moved, Sequence).State);
        Assert.Equal(DiagramState.Current, Of(moved, Plan).State);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_fails_closed_for_a_diagram_of_a_run_and_is_never_called_current()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunSequence, DiagramFixtures.Run, CancellationToken.None);
        var broken = new DiagramCatalog(world.Cache, world.Graphs, new RunModels(new DiagramFakes.Recorder(DiagramFixtures.Log(), valid: false), world.Plans), world.Activity, world.Clock);

        var list = await broken.ListAsync(world.Project, CancellationToken.None);

        Assert.Equal(DiagramState.Stale, Of(list, Sequence).State);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => broken.GenerateAsync(world.Project, DiagramKind.RunSequence, DiagramFixtures.Run, CancellationToken.None));
    }

    [Fact]
    public async Task A_model_that_is_not_there_cannot_be_drawn()
    {
        using var world = new DiagramFakes.World();

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Catalog.GenerateAsync(world.Project, DiagramKind.ProjectNamespaces, "src/Gone/Gone.csproj", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Catalog.GenerateAsync(world.Project, DiagramKind.RunSequence, "no-such-run", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(world.Cache));
    }

    // ---- exporting ----

    private static string Exported(DiagramFakes.World world, string name = "solucion.md") =>
        Path.Combine(world.Root, "docs", "generado", "diagramas", name);

    [Fact]
    public async Task Exporting_writes_markdown_with_a_mermaid_block_in_the_default_folder_and_remembers_what_it_wrote()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        var result = await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);

        var written = Assert.Single(result.Written);
        Assert.Empty(result.Conflicts);
        Assert.Equal("docs/generado/diagramas/solucion.md", written.Path);
        var markdown = await File.ReadAllTextAsync(Exported(world), CancellationToken.None);
        Assert.StartsWith("<!-- Generado por Agent Studio.", markdown, StringComparison.Ordinal);
        Assert.Contains("```mermaid\n%% Generado por Agent Studio.", markdown.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(written.Sha256, ContentHash.Sha256Hex(await File.ReadAllBytesAsync(Exported(world), CancellationToken.None)));
        Assert.Equal(DiagramExportState.SameAsCache, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).Export);
    }

    [Fact]
    public async Task What_was_exported_is_never_read_back_editing_it_only_makes_it_changed_outside()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);

        // The person edits it, with a header that claims another model and another version.
        await File.WriteAllTextAsync(Exported(world), "%% modelo: sha256:" + new string('0', 64) + "\n# mine\n", CancellationToken.None);
        var list = await world.Catalog.ListAsync(world.Project, CancellationToken.None);

        Assert.Equal(DiagramExportState.ChangedOutside, Of(list, Solution).Export);
        Assert.Equal(DiagramState.Current, Of(list, Solution).State);

        File.Delete(Exported(world));
        Assert.Equal(DiagramExportState.ChangedOutside, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).Export);
    }

    [Fact]
    public async Task Exporting_over_a_file_that_was_changed_outside_asks_and_writes_nothing()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);
        await File.WriteAllTextAsync(Exported(world), "mine\n", CancellationToken.None);

        var asked = await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);

        Assert.Empty(asked.Written);
        var conflict = Assert.Single(asked.Conflicts);
        Assert.Equal(DiagramExportState.ChangedOutside, conflict.State);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Exported(world), CancellationToken.None));

        var skipped = await world.Catalog.ExportAsync(world.Project, Request(Solution, DiagramExportConflictChoice.Skip), CancellationToken.None);
        Assert.Empty(skipped.Written);
        Assert.Empty(skipped.Conflicts);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Exported(world), CancellationToken.None));

        var copy = await world.Catalog.ExportAsync(world.Project, Request(Solution, DiagramExportConflictChoice.SaveAsNew), CancellationToken.None);
        Assert.Equal("docs/generado/diagramas/solucion-2.md", Assert.Single(copy.Written).Path);
        Assert.Equal("mine\n", await File.ReadAllTextAsync(Exported(world), CancellationToken.None));
        Assert.True(File.Exists(Exported(world, "solucion-2.md")));

        var overwritten = await world.Catalog.ExportAsync(world.Project, Request(Solution, DiagramExportConflictChoice.Overwrite), CancellationToken.None);
        Assert.Equal("docs/generado/diagramas/solucion.md", Assert.Single(overwritten.Written).Path);
        Assert.StartsWith("<!-- Generado por Agent Studio.", await File.ReadAllTextAsync(Exported(world), CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_this_computer_did_not_write_is_unknown_and_not_overwritten_without_asking()
    {
        using var world = new DiagramFakes.World();
        Directory.CreateDirectory(Path.GetDirectoryName(Exported(world))!);
        await File.WriteAllTextAsync(Exported(world), "from a clone\n", CancellationToken.None);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        Assert.Equal(DiagramExportState.Unknown, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).Export);
        var asked = await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);

        Assert.Equal(DiagramExportState.Unknown, Assert.Single(asked.Conflicts).State);
        Assert.Equal("from a clone\n", await File.ReadAllTextAsync(Exported(world), CancellationToken.None));
    }

    [Fact]
    public async Task An_older_version_is_said_to_be_older_and_exporting_it_again_replaces_what_this_computer_wrote()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);
        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        Assert.Equal(DiagramExportState.Older, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).Export);
        var again = await world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None);

        Assert.Single(again.Written);
        Assert.Empty(again.Conflicts);
        Assert.Contains("Renamed", await File.ReadAllTextAsync(Exported(world), CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(DiagramExportState.SameAsCache, Of(await world.Catalog.ListAsync(world.Project, CancellationToken.None), Solution).Export);
    }

    [Fact]
    public async Task Nothing_is_exported_with_a_run_going_or_for_a_diagram_that_is_not_generated()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        world.Activity.Busy = true;
        await Assert.ThrowsAsync<DiagramExportBusyException>(() => world.Catalog.ExportAsync(world.Project, Request(Solution), CancellationToken.None));
        world.Activity.Busy = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Catalog.ExportAsync(world.Project, Request(Solution, others: [Plan]), CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(world.Root, "docs")));
    }

    [Fact]
    public async Task Exporting_to_another_folder_names_the_files_from_a_closed_list_and_cleans_names_that_came_from_data()
    {
        using var world = new DiagramFakes.World();
        foreach (var diagram in new[] { Solution, Core, Plan, Sequence })
        {
            await world.Catalog.GenerateAsync(world.Project, diagram.Kind, diagram.Subject, CancellationToken.None);
        }

        var result = await world.Catalog.ExportAsync(
            world.Project,
            new DiagramExportRequest([Solution, Core, Plan, Sequence], "docs/otros", DiagramExportConflictChoice.Ask),
            CancellationToken.None);

        var names = result.Written.Select(w => w.Path[(w.Path.LastIndexOf('/') + 1)..]).ToList();
        Assert.Equal(4, names.Count);
        Assert.Contains("solucion.md", names);
        Assert.Contains("plan-run-golden.md", names);
        Assert.Contains("secuencia-run-golden.md", names);
        var project = Assert.Single(names, n => n.StartsWith("proyecto-", StringComparison.Ordinal));
        Assert.Matches("^[A-Za-z0-9_-]+\\.md$", project);
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(world.Root, "docs", "otros")).Count());
    }

    [Fact]
    public void A_request_cannot_name_a_folder_outside_the_project_or_the_one_of_git()
    {
        foreach (var folder in new[] { "../fuera", "/abs", "C:/x", ".git", ".git/hooks", "a/../../b", "a\\b" })
        {
            Assert.Throws<ArgumentException>(() => new DiagramExportRequest([Solution], folder, DiagramExportConflictChoice.Ask));
        }
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task A_link_in_the_folder_of_the_export_is_never_followed()
    {
        using var world = new DiagramFakes.World();
        using var outside = new TemporaryFolder();
        Directory.CreateDirectory(Path.Combine(world.Root, "docs", "generado"));
        var link = Path.Combine(world.Root, "docs", "generado", "diagramas");
        if (!TryJunction(link, outside.Path))
        {
            Assert.Skip("A junction cannot be created on this computer.");
        }

        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        // Through a link nothing is written, whatever the person chose: it is a conflict, and writing under another name finds no free place either.
        foreach (var choice in new[] { DiagramExportConflictChoice.Ask, DiagramExportConflictChoice.Overwrite })
        {
            var result = await world.Catalog.ExportAsync(world.Project, Request(Solution, choice), CancellationToken.None);
            Assert.Empty(result.Written);
            Assert.Single(result.Conflicts);
        }

        await Assert.ThrowsAsync<IOException>(() => world.Catalog.ExportAsync(world.Project, Request(Solution, DiagramExportConflictChoice.SaveAsNew), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        Assert.NotEmpty(await world.Catalog.ListAsync(world.Project, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task A_file_of_the_export_that_is_a_link_is_never_overwritten_even_when_asked_to()
    {
        using var world = new DiagramFakes.World();
        using var outside = new TemporaryFolder();
        var target = Path.Combine(outside.Path, "target.md");
        await File.WriteAllTextAsync(target, "outside\n", CancellationToken.None);
        var folder = Path.Combine(world.Root, "docs", "generado", "diagramas");
        Directory.CreateDirectory(folder);
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "solucion.md"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("A symbolic link cannot be created on this computer.");
        }

        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var result = await world.Catalog.ExportAsync(world.Project, Request(Solution, DiagramExportConflictChoice.Overwrite), CancellationToken.None);

        Assert.Empty(result.Written);
        Assert.Single(result.Conflicts);
        Assert.Equal("outside\n", await File.ReadAllTextAsync(target, CancellationToken.None));
    }

    private static DiagramExportRequest Request(DiagramRef diagram, DiagramExportConflictChoice choice = DiagramExportConflictChoice.Ask, DiagramRef[]? others = null) =>
        new([diagram, .. others ?? []], DiagramExportRequest.DefaultFolder, choice);

    private static Dictionary<string, string> SnapshotOf(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => ContentHash.Sha256Hex(File.ReadAllBytes(f)) + File.GetLastWriteTimeUtc(f).Ticks, StringComparer.Ordinal);

    private static bool TryJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("as-diagrams-outside-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left to the system.
            }
        }
    }
}
