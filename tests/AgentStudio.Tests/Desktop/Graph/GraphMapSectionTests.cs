using System.IO;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Help;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Tests.GraphWorker;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Graph.GraphDesktopFakes;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Graph;

/// <summary>
/// docs/20 F6-3 to F6-5 (WP-G) on the section «Grafo»: what the canvas draws from the model, that a relation an AI proposed is told apart from one that was read
/// (style, label and the name for the screen reader) and can be hidden without the hash of the model moving, and where the person goes from a node (the file of a
/// project, the files of a namespace, the tasks of a project), with every path checked again before it is opened.
/// </summary>
public sealed class GraphMapSectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(GraphMapSection Section, GraphDocumentViewModel Document, Tasks Tasks, Inference Inference)> LoadAsync(
        GraphEdge[]? inferred = null,
        IGraphRelationProposer? proposer = null,
        ProjectContextOverride? project = null,
        Inference? store = null)
    {
        var tasks = new Tasks();
        var inference = store ?? new Inference(inferred ?? []);
        var backend = new GraphBackend(new Analysis(), new History(), inference, tasks, proposer);
        var section = new GraphMapSection(backend, new FakeTimeProvider(T0));
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(project?.Context ?? GraphDesktopFakes.Project, Ct);
        return (section, document, tasks, inference);
    }

    /// <summary>A project other than the default one (its folder holds the files the person opens).</summary>
    private sealed record ProjectContextOverride(AgentStudio.Application.Projects.ProjectContext Context);

    // ---- F6-3: what is drawn ----

    [Fact]
    public async Task The_projects_are_drawn_with_the_references_that_were_read_and_the_namespaces_and_packages_stay_hidden_until_asked()
    {
        var (section, _, _, _) = await LoadAsync();

        Assert.Equal(["App", "Core", "Web"], section.Nodes.Select(n => n.Title));
        Assert.Equal([("src/App/App.csproj", "src/Core/Core.csproj"), ("src/Web/Web.csproj", "src/App/App.csproj")], section.Wires.Select(w => (w.User.Id, w.Used.Id)).Order());
        Assert.All(section.Wires, w => Assert.False(w.IsInferred));
        Assert.True(section.HasGraph);

        section.ShowPackages = true;

        Assert.Contains(section.Nodes, n => n.Kind == GraphNodeKind.Package);
        Assert.Contains(section.Wires, w => w.Edge.Kind == GraphEdgeKind.PackageReference);
    }

    [Fact]
    public async Task A_partial_project_says_so_in_words_and_the_coverage_and_the_gaps_say_what_could_not_be_read()
    {
        var (section, _, _, _) = await LoadAsync();

        var app = section.Nodes.Single(n => n.Title == "App");

        Assert.True(app.IsPartial);
        Assert.Equal(Texts.Get("Graph_Node_Partial"), app.PartialText);
        Assert.Contains(Texts.Get("Graph_Kind_Project"), app.AutomationName, StringComparison.Ordinal);
        Assert.Equal(Texts.Format("Graph_Coverage", 3, 2, 1, 2), section.CoverageText);
        Assert.Equal(2, section.Gaps.Count);
        Assert.Contains(section.Gaps, g => g.Where == Texts.Format("Graph_Gap_Where", "src/App/App.csproj", 7));
        Assert.Contains(section.Gaps, g => g.Where == "src/App/Big.cs");
        Assert.DoesNotContain(section.Gaps, g => g.Text.StartsWith('['));
    }

    [Fact]
    public async Task The_version_it_describes_is_said_and_a_folder_with_changes_is_said_too()
    {
        var withChanges = new GraphMapSection(new GraphBackend(new Analysis(), new History(changes: true)));
        var clean = new GraphMapSection(new GraphBackend(new Analysis(), new History(changes: false)));
        var documentA = new GraphDocumentViewModel([withChanges]);
        var documentB = new GraphDocumentViewModel([clean]);

        await documentA.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await documentB.SetProjectAsync(GraphDesktopFakes.Project, Ct);

        Assert.Equal(Texts.Format("Graph_Version_WithChanges", "aaaaaaa"), withChanges.VersionText);
        Assert.Equal(Texts.Format("Graph_Version", "aaaaaaa"), clean.VersionText);
    }

    [Fact]
    public async Task Nothing_cached_for_the_version_is_calculated_when_the_screen_opens_and_cached_is_not_calculated_again()
    {
        var analysis = new Analysis { HasCache = false };
        var section = new GraphMapSection(new GraphBackend(analysis, new History()));
        var document = new GraphDocumentViewModel([section]);

        await document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await document.ReloadAsync(Ct);

        Assert.Equal(1, analysis.Analyzed);
        Assert.True(section.HasGraph);
    }

    [Fact]
    public async Task Actualizar_calculates_the_version_again_even_when_something_is_kept_for_it()
    {
        var analysis = new Analysis();
        var section = new GraphMapSection(new GraphBackend(analysis, new History()));
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        Assert.Equal(0, analysis.Analyzed);

        await section.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, analysis.Analyzed);
        Assert.True(section.HasGraph);
        Assert.False(section.IsBusy);
    }

    [Fact]
    public async Task Proponer_relaciones_is_enabled_once_the_graph_is_there_and_the_button_is_asked_again_when_it_arrives_while_it_runs_and_when_the_card_opens()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysis = new GatedAnalysis(gate.Task);
        var section = new GraphMapSection(new GraphBackend(analysis, new History(), null, null, new Proposer()));
        var document = new GraphDocumentViewModel([section]);
        var asked = 0;
        section.ProposeCommand.CanExecuteChanged += (_, _) => asked++;

        var load = document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await WaitForAsync(() => section.IsBusy);

        Assert.False(section.ProposeCommand.CanExecute(null), "no graph yet");
        gate.SetResult();
        await load;

        Assert.True(section.ProposeCommand.CanExecute(null), "the graph arrived");
        Assert.True(asked > 0, "the button was asked again: nothing re-enables it otherwise");

        var before = asked;
        await section.ProposeCommand.ExecuteAsync(null);

        Assert.True(asked > before, "opening the card asks again");
        Assert.False(section.ProposeCommand.CanExecute(null), "the card is open");
        section.CancelProposalCommand.Execute(null);
        Assert.True(section.ProposeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_the_project_while_the_graph_of_the_other_is_being_calculated_never_paints_the_old_one_under_the_new_one()
    {
        var other = new AgentStudio.Application.Projects.ProjectContext("p-other", @"C:\work\other");
        var otherCommit = new string('b', 40);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysis = new PerProjectAnalysis(Commit, gate.Task, otherCommit);
        var section = new GraphMapSection(new GraphBackend(analysis, new PerProjectHistory(GraphDesktopFakes.Project.RootPath, Commit, otherCommit)));
        var document = new GraphDocumentViewModel([section]);

        var slow = document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await WaitForAsync(() => analysis.Waiting);
        await document.SetProjectAsync(other, Ct);
        Assert.Equal(Texts.Format("Graph_Version", "bbbbbbb"), section.VersionText);

        gate.SetResult();
        await slow;

        Assert.Equal(other, section.Project);
        Assert.Equal(otherCommit, section.Graph!.Commit);
        Assert.Contains("bbbbbbb", section.VersionText, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaa", section.VersionText, StringComparison.Ordinal);
        Assert.False(section.IsBusy);
        Assert.Null(section.Status);
    }

    [Fact]
    public async Task Changing_the_project_or_closing_the_tab_cancels_the_analysis_that_is_running()
    {
        var observing = new ObservingAnalysis();
        var section = new GraphMapSection(new GraphBackend(observing, new History()));
        var document = new GraphDocumentViewModel([section]);

        var first = document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await WaitForAsync(() => observing.Started == 1);
        await document.SetProjectAsync(null, Ct);
        await first;

        Assert.True(observing.Cancelled[0], "the project changed and the analysis kept running");
        Assert.False(section.IsBusy);
        Assert.Null(section.Error);

        var second = document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await WaitForAsync(() => observing.Started == 2);
        document.CancelPending();
        await second;

        Assert.True(observing.Cancelled[1], "the tab closed and the analysis kept running");
        Assert.False(section.IsBusy);
        Assert.Null(section.Error);
    }

    private sealed class ObservingAnalysis : AgentStudio.Application.Graph.ICommitAnalysisService
    {
        public int Started { get; private set; }

        public List<bool> Cancelled { get; } = [];

        public Task<CommitAnalysis?> FindAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => Task.FromResult<CommitAnalysis?>(null);

        public async Task<CommitAnalysis> AnalyzeAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct)
        {
            var index = Started++;
            Cancelled.Add(false);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled[index] = true;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class GatedAnalysis(Task gate) : AgentStudio.Application.Graph.ICommitAnalysisService
    {
        public Task<CommitAnalysis?> FindAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => Task.FromResult<CommitAnalysis?>(null);

        public async Task<CommitAnalysis> AnalyzeAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct)
        {
            await gate;
            return (await new Analysis().AnalyzeAsync(project, commit, ct));
        }
    }

    private sealed class PerProjectAnalysis(string slowCommit, Task gate, string fastCommit) : AgentStudio.Application.Graph.ICommitAnalysisService
    {
        public bool Waiting { get; private set; }

        public Task<CommitAnalysis?> FindAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => Task.FromResult<CommitAnalysis?>(null);

        public async Task<CommitAnalysis> AnalyzeAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct)
        {
            if (commit == slowCommit)
            {
                Waiting = true;
                await gate;
            }

            return await new Analysis(commit == fastCommit ? fastCommit : slowCommit).AnalyzeAsync(project, commit, ct);
        }
    }

    private sealed class PerProjectHistory(string rootA, string commitA, string commitB) : AgentStudio.Application.Git.IRepositoryHistoryReader
    {
        public Task<AgentStudio.Application.Git.RepositorySummary> SummaryAsync(AgentStudio.Application.Projects.ProjectContext project, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentStudio.Application.Git.RepositorySummary(project.RootPath, true, "main", project.RootPath == rootA ? commitA : commitB, false, []));

        public Task<IReadOnlyList<AgentStudio.Application.Git.BranchSummary>> BranchesAsync(AgentStudio.Application.Projects.ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentStudio.Application.Git.CommitNode>> CommitsAsync(AgentStudio.Application.Projects.ProjectContext project, AgentStudio.Application.Git.CommitGraphQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_project_with_no_saved_version_says_so_and_a_section_without_its_services_says_it_is_not_available()
    {
        var none = new GraphMapSection(new GraphBackend(new Analysis(), new History(accepted: false)));
        var documentA = new GraphDocumentViewModel([none]);
        var bare = new GraphMapSection(GraphBackend.None);
        var documentB = new GraphDocumentViewModel([bare]);

        await documentA.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        await documentB.SetProjectAsync(GraphDesktopFakes.Project, Ct);

        Assert.Equal(Texts.Get("Graph_Error_NoVersion"), none.Error);
        Assert.False(none.HasGraph);
        Assert.False(bare.IsAvailable);
        Assert.False(bare.HasGraph);
    }

    [Fact]
    public async Task A_failed_analysis_is_said_and_never_thrown_at_the_screen()
    {
        var failing = new FailingAnalysis();
        var section = new GraphMapSection(new GraphBackend(failing, new History()));
        var document = new GraphDocumentViewModel([section]);

        await document.SetProjectAsync(GraphDesktopFakes.Project, Ct);

        Assert.Equal(Texts.Format("Graph_Error_Analysis", "boom"), section.Error);
        Assert.False(section.IsBusy);
    }

    private sealed class FailingAnalysis : AgentStudio.Application.Graph.ICommitAnalysisService
    {
        public Task<CommitAnalysis?> FindAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => Task.FromResult<CommitAnalysis?>(null);

        public Task<CommitAnalysis> AnalyzeAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => throw new GraphAnalysisException("boom");
    }

    [Fact]
    public async Task Changing_the_project_drops_what_the_other_one_held()
    {
        var (section, document, _, _) = await LoadAsync();

        await document.SetProjectAsync(null, Ct);

        Assert.Empty(section.Nodes);
        Assert.Empty(section.Wires);
        Assert.False(section.HasGraph);
        Assert.Equal(string.Empty, section.VersionText);
    }

    // ---- F6-4: what an AI proposed is told apart ----

    [Fact]
    public async Task A_relation_an_ai_proposed_has_its_own_style_its_label_and_a_name_that_says_who_proposed_it()
    {
        var (section, _, _, _) = await LoadAsync([Proposed]);

        var wire = Assert.Single(section.Wires, w => w.IsInferred);
        var read = section.Wires.First(w => !w.IsInferred);

        Assert.Equal((Proposed.From, Proposed.To), (wire.User.Id, wire.Used.Id));
        Assert.Equal(Texts.Get("Graph_Wire_Label_Inferred"), wire.Label);
        Assert.Null(read.Label);
        Assert.Contains("relaciones-grafo", wire.AutomationName, StringComparison.Ordinal);
        Assert.Equal(Texts.Format("Graph_Wire_NameInferred", "Web", Texts.Get("Graph_Edge_ProjectReference"), "Core", "relaciones-grafo"), wire.AutomationName);
        Assert.Equal(Texts.Format("Graph_Wire_Name", "App", Texts.Get("Graph_Edge_ProjectReference"), "Core"), read.AutomationName);
        Assert.DoesNotContain("relaciones-grafo", read.AutomationName, StringComparison.Ordinal);
        Assert.Equal(Texts.Format("Graph_Wire_ProposedBy", "relaciones-grafo", "haiku"), wire.ProposedBy);
        Assert.Null(read.ProposedBy);
    }

    [Fact]
    public async Task The_legend_names_the_relations_of_an_ai_only_when_there_is_one_and_the_switch_hides_them()
    {
        var (without, _, _, _) = await LoadAsync();
        var (with, _, _, _) = await LoadAsync([Proposed]);

        Assert.False(without.HasInferred);
        Assert.DoesNotContain(without.Legend, l => l.IsDashed);
        Assert.True(with.HasInferred);
        Assert.Contains(with.Legend, l => l.IsDashed && l.Text == Texts.Get("Graph_Legend_Inferred"));

        with.ShowInferred = false;

        Assert.DoesNotContain(with.Wires, w => w.IsInferred);
        Assert.Equal(2, with.Wires.Count);
        Assert.True(with.HasInferred, "the switch and the legend stay: the relations are only hidden");
        with.ShowInferred = true;
        Assert.Single(with.Wires, w => w.IsInferred);
    }

    [Fact]
    public async Task A_relation_that_is_proposed_does_not_move_the_hash_of_the_model_and_one_whose_end_is_not_in_the_model_is_not_drawn()
    {
        var stranger = new GraphEdge("src/Nope/Nope.csproj", "src/Core/Core.csproj", GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, new GraphInference("agente", "modelo", T0, "x"));
        var (section, _, _, _) = await LoadAsync([Proposed, stranger]);

        Assert.Equal(GraphDesktopFakes.Graph.ModelHash, section.Graph!.ModelHash);
        Assert.Equal(GraphDesktopFakes.Graph.ModelHash, section.Displayed!.ModelHash);
        Assert.Single(section.Wires, w => w.IsInferred);
        Assert.DoesNotContain(section.Displayed.Edges, e => e.From == stranger.From);
    }

    [Fact]
    public async Task The_list_for_the_keyboard_says_the_same_as_the_canvas_and_marks_what_an_ai_proposed()
    {
        var (section, _, _, _) = await LoadAsync([Proposed]);

        Assert.Equal(section.Nodes.Count, section.Rows.Count);
        var web = section.Rows.Single(r => r.Id == "src/Web/Web.csproj");
        Assert.True(web.IsInferred);
        Assert.Contains("App", web.Text, StringComparison.Ordinal);
        Assert.Contains("Core", web.Text, StringComparison.Ordinal);

        section.SelectedRow = section.Rows.Single(r => r.Id == "src/Core/Core.csproj");

        Assert.Equal("src/Core/Core.csproj", section.SelectedNode!.Id);
        Assert.True(section.SelectedNode.IsSelected);
    }

    // ---- layout ----

    [Fact]
    public async Task The_layout_puts_what_is_used_to_the_left_of_who_uses_it_and_is_the_same_every_time()
    {
        var (first, _, _, _) = await LoadAsync([Proposed]);
        var (second, _, _, _) = await LoadAsync([Proposed]);

        double X(GraphMapSection s, string title) => s.Nodes.Single(n => n.Title == title).Location.X;

        Assert.True(X(first, "Core") < X(first, "App"));
        Assert.True(X(first, "App") < X(first, "Web"));
        Assert.Equal(first.Nodes.Select(n => n.Location), second.Nodes.Select(n => n.Location));
    }

    [Fact]
    public void Layers_hold_a_chain_a_diamond_and_a_cycle_without_failing_and_ignore_what_is_not_a_node()
    {
        var layers = GraphLayout.Layers(
            ["a", "b", "c", "d", "x", "y"],
            [("a", "b"), ("a", "c"), ("b", "d"), ("c", "d"), ("x", "y"), ("y", "x"), ("a", "ghost"), ("ghost", "a"), ("d", "d")]);

        Assert.Equal(0, layers["d"]);
        Assert.Equal(1, layers["b"]);
        Assert.Equal(1, layers["c"]);
        Assert.Equal(2, layers["a"]);
        Assert.Equal(new HashSet<int> { 0, 1 }, new HashSet<int> { layers["x"], layers["y"] });
    }

    [Fact]
    public void Locations_order_each_layer_by_name_and_a_long_chain_does_not_use_the_stack()
    {
        var chain = Enumerable.Range(0, 20_000).Select(i => "n" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).ToList();

        var layers = GraphLayout.Layers(chain, chain.Zip(chain.Skip(1), (user, used) => (user, used)));
        var places = GraphLayout.Locations(["b", "a", "c"], [], id => id);

        Assert.Equal(chain.Count - 1, layers[chain[0]]);
        Assert.Equal(0, layers[chain[^1]]);
        Assert.Equal([0d, GraphLayout.RowHeight, 2 * GraphLayout.RowHeight], [places["a"].Y, places["b"].Y, places["c"].Y]);
        Assert.All(places.Values, p => Assert.Equal(0d, p.X));
    }

    // ---- unfolding ----

    [Fact]
    public async Task A_project_unfolds_into_its_namespaces_and_folds_back()
    {
        var (section, _, _, _) = await LoadAsync();
        section.SelectedNode = section.Nodes.Single(n => n.Title == "Core");
        Assert.True(section.SelectedNode.CanExpand);

        section.ToggleExpandCommand.Execute(null);

        Assert.Contains(section.Nodes, n => n.Kind == GraphNodeKind.Namespace && n.Title == "Calc.Core");
        Assert.True(section.Nodes.Single(n => n.Title == "Core").IsExpanded);
        Assert.Equal("src/Core/Core.csproj", section.SelectedNode!.Id);

        section.ToggleExpandCommand.Execute(null);

        Assert.DoesNotContain(section.Nodes, n => n.Kind == GraphNodeKind.Namespace);
    }

    [Fact]
    public async Task Unfolding_past_two_hundred_visible_nodes_is_refused_and_says_why()
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        nodes.Add(new GraphNode("S.slnx", GraphNodeKind.Solution, "S", "S.slnx", false));
        nodes.Add(new GraphNode("big/Big.csproj", GraphNodeKind.Project, "Big", "big/Big.csproj", false));
        edges.Add(new GraphEdge("S.slnx", "big/Big.csproj", GraphEdgeKind.Contains, GraphEdgeOrigin.Static));
        for (var i = 0; i < 250; i++)
        {
            var ns = new GraphNode($"big/Big.csproj|N{i:D3}", GraphNodeKind.Namespace, $"N{i:D3}", null, false);
            nodes.Add(ns);
            edges.Add(new GraphEdge("big/Big.csproj", ns.Id, GraphEdgeKind.Contains, GraphEdgeOrigin.Static));
        }

        var big = new CodeGraph(Commit, 1, nodes, edges, []);
        var section = new GraphMapSection(new GraphBackend(new BigAnalysis(big), new History()));
        var document = new GraphDocumentViewModel([section]);
        await document.SetProjectAsync(GraphDesktopFakes.Project, Ct);
        section.SelectedNode = section.Nodes.Single();

        section.ToggleExpandCommand.Execute(null);

        Assert.Single(section.Nodes);
        Assert.Equal(Texts.Format("Graph_Message_TooMany", GraphMapSection.MaxVisibleNodes), section.Message);
    }

    private sealed class BigAnalysis(CodeGraph graph) : AgentStudio.Application.Graph.ICommitAnalysisService
    {
        private readonly CommitAnalysis _analysis = new(graph, new AgentStudio.Application.Consistency.ConsistencyReport(graph.Commit, [], AgentStudio.Application.Consistency.ConsistencyCodes.All, false));

        public Task<CommitAnalysis?> FindAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => Task.FromResult<CommitAnalysis?>(_analysis);

        public Task<CommitAnalysis> AnalyzeAsync(AgentStudio.Application.Projects.ProjectContext project, string commit, CancellationToken ct) => Task.FromResult(_analysis);
    }

    // ---- F6-5: from a node to its file and to its work ----

    [Fact]
    public async Task From_a_project_the_person_opens_its_project_file_which_is_a_file_of_the_folder()
    {
        using var folder = GraphTestRepository.FromSample();
        folder.Add("src/Core/Core.csproj", "<Project/>");
        var (section, _, _, _) = await LoadAsync(project: new ProjectContextOverride(new AgentStudio.Application.Projects.ProjectContext("p", folder.Root)));
        var requested = new List<string>();
        section.FileRequested += (_, path) => requested.Add(path);
        section.SelectedNode = section.Nodes.Single(n => n.Title == "Core");

        Assert.True(section.OpenProjectFileCommand.CanExecute(null));
        section.OpenProjectFileCommand.Execute(null);

        Assert.Equal([Path.Combine(folder.Root, "src", "Core", "Core.csproj")], requested);
    }

    [Fact]
    public async Task From_a_namespace_the_person_sees_the_files_that_declare_it_and_opens_the_one_they_choose()
    {
        using var folder = new GraphTestRepository();
        folder.Add("src/Core/Calc2.cs", "namespace Calc.Core { }");
        var (section, _, _, _) = await LoadAsync(project: new ProjectContextOverride(new AgentStudio.Application.Projects.ProjectContext("p", folder.Root)));
        var requested = new List<string>();
        section.FileRequested += (_, path) => requested.Add(path);
        section.SelectedNode = section.Nodes.Single(n => n.Title == "Core");
        section.ToggleExpandCommand.Execute(null);

        section.SelectedNode = section.Nodes.Single(n => n.Title == "Calc.Core");

        Assert.Equal(["src/Core/Calc.cs", "src/Core/Calc2.cs"], section.Files.Select(f => f.Path));
        Assert.True(section.SelectedIsNamespace);
        section.OpenFileCommand.Execute("src/Core/Calc2.cs");
        Assert.Equal([Path.Combine(folder.Root, "src", "Core", "Calc2.cs")], requested);

        // The file that is no longer in the folder is said, and nothing is opened.
        requested.Clear();
        section.OpenFileCommand.Execute("src/Core/Calc.cs");
        Assert.Empty(requested);
        Assert.Equal(Texts.Format("Graph_Message_FileGone", "src/Core/Calc.cs"), section.Message);
    }

    [Fact]
    public async Task From_a_project_the_person_sees_the_tasks_that_changed_files_under_its_folder_and_goes_to_the_one_they_choose()
    {
        var (section, _, tasks, _) = await LoadAsync();
        var requested = new List<TraceRef>();
        section.TaskRequested += (_, task) => requested.Add(task);

        section.SelectedNode = section.Nodes.Single(n => n.Title == "Core");
        await WaitForAsync(() => section.TaskRows.Count > 0);

        Assert.Equal(["src/Core"], tasks.Asked);
        var row = Assert.Single(section.TaskRows);
        Assert.Equal(Texts.Format("Graph_Task_Line", "Tarea T1", 1), row.Text);
        Assert.False(section.HasNoTasks);

        section.OpenTaskCommand.Execute(row);

        Assert.Equal([TraceRef.Task("20261111-a1b2c3", "T1")], requested);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public void The_file_the_person_opens_is_checked_again_against_the_folder_so_no_path_of_the_model_opens_anything_else()
    {
        using var folder = new GraphTestRepository();
        folder.Add("src/ok.cs", "x");
        folder.AddOutside("secret.cs", "secret");
        folder.Add("real/linked.cs", "y");
        folder.Junction("link", Path.Combine(folder.Outside));
        var root = folder.Root;

        Assert.Equal(Path.Combine(root, "src", "ok.cs"), GraphFileTarget.Resolve(root, "src/ok.cs"));
        Assert.Null(GraphFileTarget.Resolve(root, "src/missing.cs"));
        Assert.Null(GraphFileTarget.Resolve(root, "../outside/secret.cs"));
        Assert.Null(GraphFileTarget.Resolve(root, @"..\outside\secret.cs"));
        Assert.Null(GraphFileTarget.Resolve(root, Path.Combine(folder.Outside, "secret.cs")));
        Assert.Null(GraphFileTarget.Resolve(root, "/etc/passwd"));
        Assert.Null(GraphFileTarget.Resolve(root, "link/secret.cs"));
        Assert.Null(GraphFileTarget.Resolve(root, "src"));
        Assert.Null(GraphFileTarget.Resolve(root, ""));
        Assert.Null(GraphFileTarget.Resolve(string.Empty, "src/ok.cs"));
    }

    // ---- «Proponer relaciones»: the OK of the person, each time ----

    [Fact]
    public async Task Without_the_proposer_there_is_no_button_and_with_it_the_card_opens_before_anything_is_launched()
    {
        var (without, _, _, _) = await LoadAsync();
        var proposer = new Proposer();
        var (with, _, _, _) = await LoadAsync(proposer: proposer);

        Assert.False(without.HasProposer);
        Assert.False(without.ProposeCommand.CanExecute(null));
        Assert.True(with.HasProposer);

        await with.ProposeCommand.ExecuteAsync(null);

        Assert.True(with.Consent.IsOpen);
        Assert.True(with.Consent.IsReady);
        Assert.Equal(4, with.CardLines.Count);
        Assert.Equal(Texts.Format("Graph_Card_Nodes", 5), with.CardLines[0]);
        Assert.Equal(Texts.Format("Graph_Card_Known", 3), with.CardLines[1]);
        Assert.Contains(GraphInferenceService.ConsentTag(with.Graph!, HelpAgentChoice.Default), with.Consent.Description, StringComparison.Ordinal);
        Assert.Empty(proposer.Requests);
    }

    [Fact]
    public async Task Cancelling_the_card_launches_nothing_and_giving_the_ok_launches_one_proposal_with_the_ok_of_this_graph()
    {
        var store = new Inference();

        // What the real service does: it keeps what it accepted in the store, and the section reads the store again.
        var proposer = new Proposer(request =>
        {
            store.Edges.Add(Proposed);
            return new GraphProposalReply(new RunId("aux-20261112-abcdef"), AgentRunStatus.Succeeded, null, [Proposed], 1, null);
        });
        var (section, _, _, _) = await LoadAsync(proposer: proposer, store: store);

        await section.ProposeCommand.ExecuteAsync(null);
        section.CancelProposalCommand.Execute(null);
        Assert.False(section.Consent.IsOpen);
        Assert.Empty(proposer.Requests);

        await section.ProposeCommand.ExecuteAsync(null);
        await section.ConfirmProposalCommand.ExecuteAsync(null);

        var request = Assert.Single(proposer.Requests);
        Assert.Equal(GraphDesktopFakes.Project, request.Project);
        Assert.Equal(HelpAgentChoice.Default, request.Agent);
        Assert.Equal(section.Graph, request.Graph);
        Assert.Contains(GraphInferenceService.ConsentTag(section.Graph!, request.Agent), request.Consent.Description, StringComparison.Ordinal);
        Assert.Equal(Texts.Format("Graph_Message_Proposed", 1, 1), section.Message);
        Assert.False(section.Consent.IsOpen);
        Assert.False(section.IsBusy);
        Assert.Equal(["Web"], section.Wires.Where(w => w.IsInferred).Select(w => w.User.Title));
    }

    [Fact]
    public async Task What_the_agent_proposed_is_drawn_apart_after_the_store_has_it_and_a_failed_proposal_is_said()
    {
        var failing = new Proposer(_ => throw new InvalidOperationException("sin proveedor"));
        var (section, _, _, _) = await LoadAsync(proposer: failing);

        await section.ProposeCommand.ExecuteAsync(null);
        await section.ConfirmProposalCommand.ExecuteAsync(null);

        Assert.Equal(Texts.Format("Graph_Error_Proposal", "sin proveedor"), section.Error);
        Assert.False(section.IsBusy);
        Assert.DoesNotContain(section.Wires, w => w.IsInferred);
    }

    [Fact]
    public async Task A_reply_with_an_error_is_said_and_draws_nothing()
    {
        var proposer = new Proposer(_ => new GraphProposalReply(new RunId("aux-20261112-abcdef"), AgentRunStatus.Failed, null, [], 0, "no hubo respuesta"));
        var (section, _, _, _) = await LoadAsync(proposer: proposer);

        await section.ProposeCommand.ExecuteAsync(null);
        await section.ConfirmProposalCommand.ExecuteAsync(null);

        Assert.Equal(Texts.Format("Graph_Error_Proposal", "no hubo respuesta"), section.Error);
        Assert.DoesNotContain(section.Wires, w => w.IsInferred);
    }
}
