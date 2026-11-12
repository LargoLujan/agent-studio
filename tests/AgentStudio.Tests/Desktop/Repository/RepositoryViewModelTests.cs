using System.Globalization;
using AgentStudio.Application.Git;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.RepositoryScreen;

/// <summary>
/// «Repositorio y cambios» (docs/16 F9-9, D-44) over doubles of the readers: the summary, the branches and the graph annotated from the
/// log (a commit of the system, a suggestion, a link, none), what the screen says when the hardened git layer refuses the repository, the
/// filters of the activity, the link to the chain, and a read that a later one overtakes.
/// </summary>
public sealed class RepositoryViewModelTests
{
    private const string Root = @"C:\proyectos\calc";

    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(2));

    private static readonly string Sha1 = FakeHistory.Sha('1');
    private static readonly string Sha2 = FakeHistory.Sha('2');
    private static readonly string Sha3 = FakeHistory.Sha('3');

    private sealed class World
    {
        public FakeWorkspace Workspace { get; } = new();

        public FakeHistory History { get; } = new();

        public FakeAnnotator Annotator { get; } = new();

        public FakeActivity Activity { get; } = new();

        public FakeRemotes Remotes { get; } = new();

        public RepositoryViewModel Model() => new(Workspace, History, Annotator, Activity, Remotes, new FakeClock(Now));
    }

    private static CommitNode Node(string id, string subject, string? message = null) =>
        new(id, [], new DateTimeOffset(2026, 11, 12, 8, 30, 0, TimeSpan.Zero), subject, "Manuel", ["main"], message);

    private static RepositoryActivity Event(RepositoryActivityKind kind, string run, string? task, bool succeeded, string? code, long sequence, int minutes = 0) =>
        new(Now.AddMinutes(-minutes), kind, run, task, null, succeeded, code, sequence);

    [Fact]
    public async Task The_summary_the_branches_and_the_remotes_are_read_for_the_project()
    {
        var world = new World();
        world.History.Summary = new RepositorySummary(Root, true, "feature/x", Sha1, true, []);
        world.History.Branches.AddRange([new BranchSummary("feature/x", Sha1, true, BranchKind.Local), new BranchSummary("agentstudio/run-20261112-a1b2c3", Sha2, false, BranchKind.Run)]);
        world.Remotes.Listing = new RemoteListing([FakeRemotes.Origin], null);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal([Root], world.Workspace.Opened);
        Assert.Equal([Root], world.Remotes.Roots);
        Assert.Equal("feature/x", model.BranchText);
        Assert.Equal("11111111", model.HeadText);
        Assert.Equal("Sí, hay cambios sin guardar.", model.ChangesText);
        Assert.Equal("origin", Assert.Single(model.Remotes).Name);
        Assert.Null(model.RemotesNote);
        Assert.Equal(["feature/x", "agentstudio/run-20261112-a1b2c3"], model.Branches.Select(b => b.Name));
        Assert.Equal("actual", model.Branches[0].Marks);
        Assert.Equal("de una ejecución de Agent Studio", model.Branches[1].Marks);
        Assert.True(model.Branches[1].IsRun);
        Assert.Null(model.RefusalText);
        Assert.Null(model.StatusMessage);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task A_repository_without_a_branch_nor_remotes_nor_changes_says_so_in_words()
    {
        var world = new World();
        world.History.Summary = new RepositorySummary(Root, true, null, null, false, []);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal("ninguna: estás en una versión suelta", model.BranchText);
        Assert.Equal("todavía no hay ninguna", model.HeadText);
        Assert.Equal("No, todo está guardado.", model.ChangesText);
        Assert.Equal("Sin remotos: el repositorio solo está en este equipo.", model.RemotesNote);
        Assert.Equal("Sin ramas.", model.BranchesNote);
        Assert.Equal("Todavía no hay versiones guardadas.", model.CommitsNote);
    }

    [Fact]
    public async Task A_commit_the_log_backs_says_its_run_task_attempt_agent_and_model()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Añade Calc.Multiply"));
        world.Annotator.Annotations[Sha1] = new CommitAnnotation("20261112-a1b2c3", "T2", 3, new AgentId("ejecutor"), "claude-sonnet-5-5", [], []);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        var row = Assert.Single(model.Commits);
        Assert.True(row.IsSystem);
        Assert.Equal("11111111", row.ShortId);
        Assert.Equal("Añade Calc.Multiply", row.Subject);
        Assert.Equal("Manuel", row.Author);
        Assert.Equal("12/11/2026 10:30", row.When);
        Assert.Equal("main", row.Refs);
        var line = Assert.Single(row.Origin);
        Assert.False(line.IsSuggestion);
        Assert.Equal("Hecha por Agent Studio · ejecución 20261112-a1b2c3 · tarea T2 · intento 3 · agente ejecutor · modelo claude-sonnet-5-5", line.Text);
        Assert.Equal("Se muestran las últimas 1 versiones guardadas, de la más nueva a la más antigua.", model.CommitsNote);
        Assert.Equal(RepositoryViewModel.MaxCommits, Assert.Single(world.History.Queries).MaxCommits);
    }

    [Fact]
    public async Task A_commit_the_log_does_not_back_is_at_most_a_suggestion_marked_as_one_and_never_a_commit_of_the_system()
    {
        var world = new World();
        const string Trailers = "Copiado a mano\n\nAgent-Studio-Run: 20261112-a1b2c3\nAgent-Studio-Task: T2\nAgent-Studio-Attempt: 3\nAgent-Studio-Agent: ejecutor";
        world.History.Commits.Add(Node(Sha2, "Copiado a mano", Trailers));
        world.Annotator.Annotations[Sha2] = new CommitAnnotation(
            null, null, null, null, null, [],
            [new TraceSuggestion(TraceRef.Commit(Sha2), TraceRef.Task("20261112-a1b2c3", "T2"), TraceSuggestionSource.CommitTrailer, "Agent-Studio-Task: T2")]);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        var row = Assert.Single(model.Commits);
        Assert.False(row.IsSystem);
        var line = Assert.Single(row.Origin);
        Assert.True(line.IsSuggestion);
        Assert.Equal("Sugerencia, sin confirmar: parece relacionada con tarea 20261112-a1b2c3/T2, según las marcas de su mensaje.", line.Text);
        Assert.DoesNotContain(row.Origin, l => l.Text.Contains("Hecha por Agent Studio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_commit_with_the_trailers_in_its_message_and_no_annotation_is_not_attributed_to_the_system()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha3, "Con marcas", "Con marcas\n\nAgent-Studio-Run: 20261112-a1b2c3\nAgent-Studio-Task: T2\nAgent-Studio-Attempt: 1\nAgent-Studio-Agent: ejecutor"));
        var model = world.Model();

        await model.SetProjectAsync(Root);

        var row = Assert.Single(model.Commits);
        Assert.False(row.IsSystem);
        Assert.Equal("El registro no sabe nada de esta versión.", Assert.Single(row.Origin).Text);
    }

    [Fact]
    public async Task A_link_a_person_confirmed_is_said_as_such_and_a_suggestion_comes_after_it()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Documenta"));
        world.Annotator.Annotations[Sha1] = new CommitAnnotation(
            null, null, null, null, null,
            [new TraceLink(TraceRef.Commit(Sha1), TraceRef.Requirement("R-4"), TraceAssertedBy.User, "lo cumple", Now)],
            [new TraceSuggestion(TraceRef.Commit(Sha1), TraceRef.Run("20261112-a1b2c3"), TraceSuggestionSource.RunBranchName, "agentstudio/run-20261112-a1b2c3")]);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        var origin = Assert.Single(model.Commits).Origin;
        Assert.Equal(["Vinculada por ti a requisito R-4.", "Sugerencia, sin confirmar: parece relacionada con ejecución 20261112-a1b2c3, según el nombre de su rama."], origin.Select(l => l.Text));
        Assert.Equal([false, true], origin.Select(l => l.IsSuggestion));
    }

    [Fact]
    public async Task A_log_that_does_not_verify_leaves_the_history_of_git_without_annotations_and_says_so()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Primero"));
        world.Annotator.BrokenLog = true;
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal("Primero", Assert.Single(model.Commits).Subject);
        Assert.Equal("Se muestran las últimas 1 versiones guardadas, de la más nueva a la más antigua. El registro de decisiones no se pudo comprobar, así que las versiones se muestran sin anotar.", model.CommitsNote);
        Assert.False(model.Commits[0].IsSystem);
    }

    [Theory]
    [InlineData("not-a-repository", "la carpeta no es un repositorio de git.")]
    [InlineData("inside-worktree-root", "la carpeta está dentro de las copias de trabajo aisladas de Agent Studio.")]
    [InlineData("unsafe-configuration", "puede lanzar un programa del repositorio. Por seguridad, no se lee.")]
    [InlineData("unsafe-repository", "la carpeta no es tuya, o git no la considera segura.")]
    [InlineData("git-unavailable", "no se encontró git o no se pudo ejecutar.")]
    [InlineData("git-failed", "git falló al leer el repositorio.")]
    [InlineData("unreadable", "no se pudo leer la carpeta.")]
    [InlineData("something-new", "motivo desconocido (something-new).")]
    public async Task When_the_reader_refuses_the_repository_the_screen_says_why_and_does_not_say_there_is_nothing(string code, string why)
    {
        var world = new World();
        world.History.Summary = new RepositorySummary(Root, false, null, null, false, [code]);
        world.History.Commits.Add(Node(Sha1, "No debería verse"));
        world.History.Branches.Add(new BranchSummary("main", Sha1, true, BranchKind.Local));
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal("Agent Studio no lee este repositorio: " + why, model.RefusalText);
        Assert.Empty(model.Commits);
        Assert.Empty(model.Branches);
        Assert.Null(model.BranchesNote);
        Assert.Null(model.CommitsNote);
        Assert.Null(model.BranchText);
        Assert.Empty(world.History.Queries);
        Assert.Empty(world.Remotes.Roots);
    }

    [Fact]
    public async Task When_the_reader_refuses_the_repository_the_activity_of_the_log_is_still_shown()
    {
        var world = new World();
        world.History.Summary = new RepositorySummary(Root, false, null, null, false, ["unsafe-configuration"]);
        world.Activity.Events.Add(Event(RepositoryActivityKind.Delivery, "20261112-a1b2c3", null, true, null, 7));
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.NotNull(model.RefusalText);
        Assert.Equal("Entrega", Assert.Single(model.Activities).Kind);
    }

    [Fact]
    public async Task The_refusal_is_said_in_english_when_the_language_is_english()
    {
        using var scope = Texts.Scope(new ResourceTextProvider(CultureInfo.GetCultureInfo("en")));
        var world = new World();
        world.History.Summary = new RepositorySummary(Root, false, null, null, false, ["unsafe-configuration"]);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal("Agent Studio does not read this repository: it can launch a program of the repository. For safety, it is not read.", model.RefusalText);
        Assert.Equal("Repository and changes", model.Title);
        Assert.Equal(["Summary", "Branches", "Saved versions", "Activity"], model.Sections.Select(s => s.Label));
    }

    [Fact]
    public async Task The_remotes_that_could_not_be_read_say_why()
    {
        var world = new World();
        world.Remotes.Listing = RemoteListing.Refused(RemoteListing.OutputTooBig);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Empty(model.Remotes);
        Assert.Equal("No se pudieron leer los remotos: la respuesta de git era demasiado grande.", model.RemotesNote);
    }

    [Fact]
    public async Task The_activity_is_shown_newest_first_with_its_real_result()
    {
        var world = new World();
        world.Activity.Events.AddRange(
        [
            Event(RepositoryActivityKind.Integration, "r1", "T1", true, null, 1, minutes: 30),
            Event(RepositoryActivityKind.Integration, "r1", "T2", false, RepositoryActivityReader.ConflictCode, 2, minutes: 20),
            Event(RepositoryActivityKind.Integration, "r1", "T3", false, RepositoryActivityReader.FailedCode, 3, minutes: 10),
            Event(RepositoryActivityKind.WorktreeRemoved, "r1", "T3", true, null, 4, minutes: 5),
        ]);
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal(["Copia de trabajo retirada", "Integración", "Integración", "Integración"], model.Activities.Select(a => a.Kind));
        Assert.Equal(["Salió bien", "Falló: no dejó ninguna versión guardada", "Falló: hubo un conflicto al integrar", "Salió bien"], model.Activities.Select(a => a.Result));
        Assert.Equal("ejecución r1 · tarea T3", model.Activities[0].Where);
        Assert.Equal("12/11/2026 09:55", model.Activities[0].When);
        Assert.Equal([true, false, false, true], model.Activities.Select(a => a.Succeeded));
    }

    [Fact]
    public async Task Without_filters_the_reader_is_asked_for_everything()
    {
        var world = new World();
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Equal(new RepositoryActivityFilter(), Assert.Single(world.Activity.Filters));
        Assert.Equal("No hay actividad con estos filtros.", model.ActivityNote);
    }

    [Fact]
    public async Task Every_filter_of_the_activity_reaches_the_reader()
    {
        var world = new World();
        var model = world.Model();
        await model.SetProjectAsync(Root);
        model.RunFilter = " 20261112-a1b2c3 ";
        model.TaskFilter = "T2";
        model.MilestoneFilter = "mil-20261110-aaaaaa";
        model.AgentFilter = "ejecutor";
        model.ModelFilter = "claude-sonnet-5-5";
        model.SelectedProvider = model.Providers.Single(p => p.Value == ProviderId.CodexCli);
        model.SelectedPeriod = model.Periods.Single(p => p.Value == RepositoryPeriod.Last7Days);

        await model.ApplyFiltersCommand.ExecuteAsync(null);

        var filter = world.Activity.Filters[^1]!;
        Assert.Equal("20261112-a1b2c3", filter.Run);
        Assert.Equal("T2", filter.Task);
        Assert.Equal(new MilestoneId("mil-20261110-aaaaaa"), filter.Milestone);
        Assert.Equal(new AgentId("ejecutor"), filter.Agent);
        Assert.Equal(ProviderId.CodexCli, filter.Provider);
        Assert.Equal("claude-sonnet-5-5", filter.Model);

        // Seven days are today and the six before it, from local midnight (+02:00).
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.FromHours(2)), filter.From);
        Assert.Null(filter.To);
        Assert.Null(model.FilterError);
    }

    [Fact]
    public async Task An_id_that_is_not_valid_is_said_and_nothing_is_read()
    {
        var world = new World();
        var model = world.Model();
        await model.SetProjectAsync(Root);
        var reads = world.Activity.Filters.Count;
        model.MilestoneFilter = "hito-uno";

        await model.ApplyFiltersCommand.ExecuteAsync(null);

        Assert.Equal("«hito-uno» no es un hito válido: empieza por «mil-».", model.FilterError);
        Assert.Equal(reads, world.Activity.Filters.Count);
    }

    [Fact]
    public async Task Clearing_the_filters_empties_them_and_reads_everything_again()
    {
        var world = new World();
        var model = world.Model();
        await model.SetProjectAsync(Root);
        model.RunFilter = "r1";
        model.MilestoneFilter = "mal";
        await model.ApplyFiltersCommand.ExecuteAsync(null);
        Assert.NotNull(model.FilterError);

        await model.ClearFiltersCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, model.RunFilter);
        Assert.Equal(string.Empty, model.MilestoneFilter);
        Assert.Null(model.FilterError);
        Assert.Equal(new RepositoryActivityFilter(), world.Activity.Filters[^1]);
        Assert.Null(model.SelectedProvider.Value);
        Assert.Equal(RepositoryPeriod.All, model.SelectedPeriod.Value);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_says_so_in_the_activity_and_keeps_the_history()
    {
        var world = new World();
        world.Activity.BrokenLog = true;
        world.History.Commits.Add(Node(Sha1, "Primero"));
        var model = world.Model();

        await model.SetProjectAsync(Root);

        Assert.Empty(model.Activities);
        Assert.Equal("No se pudo leer la actividad: el registro de decisiones no se pudo comprobar.", model.ActivityNote);
        Assert.Single(model.Commits);
    }

    [Fact]
    public async Task Without_a_way_to_open_the_chain_no_version_offers_it_and_connecting_one_offers_it_on_every_version()
    {
        var world = new World();
        world.History.Commits.AddRange([Node(Sha1, "Primero"), Node("abc123", "Con un id corto")]);
        var model = world.Model();
        await model.SetProjectAsync(Root);

        Assert.False(model.CanOpenChain);
        Assert.All(model.Commits, c => Assert.False(c.CanOpenChain));

        RepositoryChainRequest? asked = null;
        model.ChainRequested += (_, request) => asked = request;

        Assert.True(model.CanOpenChain);
        Assert.True(model.Commits[0].CanOpenChain);
        Assert.False(model.Commits[1].CanOpenChain);

        model.Commits[0].OpenChainCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Equal(TraceRef.Commit(Sha1), asked.Node);
        Assert.Equal(new AgentStudio.Application.Projects.ProjectContext("p-1", Root), asked.Project);
    }

    [Fact]
    public async Task A_version_read_after_the_chain_was_connected_offers_it_at_once()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Primero"));
        var model = world.Model();
        model.ChainRequested += (_, _) => { };

        await model.SetProjectAsync(Root);

        Assert.True(Assert.Single(model.Commits).CanOpenChain);
    }

    [Fact]
    public async Task A_read_that_a_later_one_overtakes_does_not_overwrite_it()
    {
        var world = new World();
        var hold = new TaskCompletionSource();
        world.History.HoldSummary = hold;
        world.History.Summary = new RepositorySummary(Root, true, "vieja", Sha1, false, []);
        var model = world.Model();

        var slow = model.SetProjectAsync(Root);
        Assert.False(slow.IsCompleted);
        world.History.Summary = new RepositorySummary(Root, true, "nueva", Sha2, false, []);
        await model.SetProjectAsync(@"C:\proyectos\otro");
        Assert.Equal("nueva", model.BranchText);

        // The first read ends last: what it read is dropped, and the screen keeps the last one asked.
        hold.SetResult();
        await slow;

        Assert.Equal("nueva", model.BranchText);
        Assert.Equal(@"C:\proyectos\otro", model.Project!.RootPath);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Without_a_project_or_without_the_readers_the_screen_says_what_is_missing()
    {
        var empty = new RepositoryViewModel(null, null, null, null, null, new FakeClock(Now));
        await empty.SetProjectAsync(Root);
        Assert.Equal("El repositorio no está disponible en esta composición: falta el lector de la historia.", empty.StatusMessage);

        var world = new World();
        var model = world.Model();
        await model.SetProjectAsync(null);
        Assert.Equal("Abre una carpeta para ver su repositorio.", model.StatusMessage);
        Assert.Null(model.Project);
        Assert.Empty(world.Workspace.Opened);
    }

    [Fact]
    public async Task Refresh_reads_everything_again_and_changing_the_project_clears_what_was_shown()
    {
        var world = new World();
        world.History.Commits.Add(Node(Sha1, "Primero"));
        var model = world.Model();
        await model.SetProjectAsync(Root);
        world.History.Commits.Add(Node(Sha2, "Segundo"));

        await model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["Primero", "Segundo"], model.Commits.Select(c => c.Subject));

        await model.SetProjectAsync(null);

        Assert.Empty(model.Commits);
        Assert.Null(model.BranchText);
    }
}
