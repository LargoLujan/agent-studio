using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Graph;
using AgentStudio.Memory.Consistency;
using AgentStudio.Memory.Diagrams;
using AgentStudio.Tests.Memory.Consistency;

namespace AgentStudio.Tests.Memory.Diagrams;

/// <summary>
/// The points of the independent review of WP-D that are about the cache and the catalog: every read of the index waits for the same gate as the writes and an index that cannot
/// be read is never an empty one; an export that fails halfway leaves the index true; a run that starts while an export waits stops it; names that differ only in case do not
/// meet in one file; a diagram that cannot be built is a row and not the end of the list; a diagram of a run that cannot be checked is not skipped; and nothing heavy runs on the
/// thread that asked.
/// </summary>
public sealed class DiagramReviewTests
{
    private static readonly DiagramRef Solution = new(DiagramKind.Solution, string.Empty);

    private static readonly DiagramRef Plan = new(DiagramKind.RunPlan, DiagramFixtures.Run);

    private static readonly DiagramRef Sequence = new(DiagramKind.RunSequence, DiagramFixtures.Run);

    private static DiagramExportRequest Request(DiagramExportConflictChoice choice, params DiagramRef[] diagrams) =>
        new(diagrams, DiagramExportRequest.DefaultFolder, choice);

    private static string IndexFile(DiagramFakes.World world) =>
        Assert.Single(Directory.EnumerateFiles(world.Cache, "index.json", SearchOption.AllDirectories));

    // ---- point 2: reads wait for the gate; «no se pudo leer» is not «no hay nada» ----

    [Fact]
    public async Task A_read_of_the_index_by_a_check_waits_for_the_gate_of_the_writes()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, TestContext.Current.CancellationToken);
        var check = new DiagramStaleCheck(world.Staleness);
        var scope = new ConsistencyScope(world.Project, new FakeSnapshot(("README.md", "x")), DiagramFixtures.Graph(appName: "Renamed"));

        await world.Catalog.Store.Gate.WaitAsync(TestContext.Current.CancellationToken);
        Task<IReadOnlyList<ConsistencyFinding>?> read;
        try
        {
            read = check.RunAsync(scope, TestContext.Current.CancellationToken);

            // The gate is held: whatever the time given, the read does not finish. (A bound only for the case it does: with the gate it never is reached.)
            var winner = await Task.WhenAny(read, Task.Delay(300, TestContext.Current.CancellationToken));
            Assert.NotSame(read, winner);
        }
        finally
        {
            world.Catalog.Store.Gate.Release();
        }

        var findings = await read;
        Assert.Single(findings!);
    }

    [Fact]
    public async Task An_index_that_is_there_and_cannot_be_read_is_an_error_and_the_checks_report_it_instead_of_saying_all_is_well()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, TestContext.Current.CancellationToken);
        var staleCheck = new DiagramStaleCheck(world.Staleness);

        // Another process holds the file: the store retries a few times and then says it could not read, never «there are no diagrams».
        await using var held = new FileStream(IndexFile(world), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<DiagramIndexUnreadableException>(() => world.Catalog.Store.LoadAsync(world.Project.ProjectId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DiagramIndexUnreadableException>(() => world.Catalog.ListAsync(world.Project, TestContext.Current.CancellationToken));

        var report = await new ConsistencyService([staleCheck]).RunAsync(world.Project, new FakeSnapshot(("README.md", "x")), DiagramFixtures.Graph(),TestContext.Current.CancellationToken);

        Assert.Equal(ConsistencyCodes.CheckFailed, Assert.Single(report.Findings).Code);
        Assert.Empty(report.NotApplicable);
    }

    [Fact]
    public async Task No_index_at_all_is_an_empty_one_and_not_an_error()
    {
        using var world = new DiagramFakes.World();

        var index = await world.Catalog.Store.LoadAsync(world.Project.ProjectId, TestContext.Current.CancellationToken);

        Assert.Empty(index.Entries);
    }

    // ---- point 3: an export that fails halfway leaves the index true ----

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task When_the_second_file_of_an_export_fails_the_first_is_in_the_index_and_no_temporary_file_is_left()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, TestContext.Current.CancellationToken);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunPlan, DiagramFixtures.Run, TestContext.Current.CancellationToken);
        var folder = Path.Combine(world.Root, "docs", "generado", "diagramas");
        Directory.CreateDirectory(folder);
        var second = Path.Combine(folder, "plan-run-golden.md");
        await File.WriteAllTextAsync(second, "someone else's\n", TestContext.Current.CancellationToken);

        // The second destination exists and another program holds it: writing over it fails after the first file was written.
        Exception? failed;
        await using (new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failed = await Record.ExceptionAsync(() => world.Catalog.ExportAsync(
                world.Project,
                Request(DiagramExportConflictChoice.Overwrite, Solution, Plan),
                TestContext.Current.CancellationToken));
        }

        Assert.True(failed is IOException or UnauthorizedAccessException, failed?.ToString());
        Assert.True(File.Exists(Path.Combine(folder, "solucion.md")));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp"));
        var list = await world.Catalog.ListAsync(world.Project, TestContext.Current.CancellationToken);
        Assert.Equal(DiagramExportState.SameAsCache, Assert.Single(list, s => s.Diagram == Solution).Export);

        // The next export treats the first file as its own: it writes over it without asking.
        var again = await world.Catalog.ExportAsync(world.Project, Request(DiagramExportConflictChoice.Ask, Solution), TestContext.Current.CancellationToken);
        Assert.Single(again.Written);
        Assert.Empty(again.Conflicts);
    }

    // ---- point 8: a run that starts while the export waits stops it ----

    [Fact]
    public async Task A_run_that_starts_between_the_first_question_and_the_write_stops_the_export_and_nothing_is_written()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, TestContext.Current.CancellationToken);

        // The first question (before the gate) is answered «free»; the one inside the gate, «a run has the project».
        world.Activity.FreeFor = 1;
        world.Activity.Busy = true;

        await Assert.ThrowsAsync<DiagramExportBusyException>(() => world.Catalog.ExportAsync(world.Project, Request(DiagramExportConflictChoice.Ask, Solution), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(world.Root, "docs")));
    }

    // ---- point 5: names that differ only in case ----

    [Fact]
    public void Names_that_differ_only_in_case_never_meet_in_one_file()
    {
        var upper = DiagramExportFiles.FileName(new DiagramRef(DiagramKind.RunPlan, "Abc"));
        var lower = DiagramExportFiles.FileName(new DiagramRef(DiagramKind.RunPlan, "abc"));

        Assert.NotEqual(upper, lower, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("plan-abc.md", lower);
        Assert.Equal(upper, DiagramExportFiles.FileName(new DiagramRef(DiagramKind.RunPlan, "Abc")));
        Assert.NotEqual(SafeName.Of("P-One"), SafeName.Of("p-one"), StringComparer.OrdinalIgnoreCase);
    }

    // ---- point 6: a diagram that cannot be built is a row ----

    [Fact]
    public async Task A_diagram_that_cannot_be_built_is_one_row_that_says_so_and_the_list_goes_on()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, TestContext.Current.CancellationToken);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunPlan, DiagramFixtures.Run, TestContext.Current.CancellationToken);
        world.Plans.Broken = true;

        var list = await world.Catalog.ListAsync(world.Project, TestContext.Current.CancellationToken);

        var plan = Assert.Single(list, s => s.Diagram == Plan);
        Assert.Equal(DiagramState.Unavailable, plan.State);
        Assert.NotNull(plan.ModelHash);
        Assert.Equal(DiagramState.Current, Assert.Single(list, s => s.Diagram == Solution).State);
        Assert.Equal(6, list.Count);
    }

    // ---- point 9 ----

    [Fact]
    public async Task A_diagram_of_a_run_that_cannot_be_checked_is_reported_as_out_of_date_and_not_skipped()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunSequence, DiagramFixtures.Run, TestContext.Current.CancellationToken);
        var blind = new DiagramStaleness(world.Catalog.Store, new RunModels(new DiagramFakes.Recorder(DiagramFixtures.Log(), valid: false), world.Plans), world.Clock);

        var stale = await blind.FindAsync(world.Project, DiagramFixtures.Graph(), TestContext.Current.CancellationToken);

        Assert.Equal(Sequence, Assert.Single(stale).Diagram);
    }

    [Fact]
    public async Task Listing_does_not_do_its_work_on_the_thread_that_asked()
    {
        using var world = new DiagramFakes.World();
        var caller = 0;
        var list = await Task.Factory.StartNew(
            () =>
            {
                caller = Environment.CurrentManagedThreadId;
                return world.Catalog.ListAsync(world.Project, CancellationToken.None).GetAwaiter().GetResult();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.NotEmpty(list);
        Assert.NotEqual(0, world.History.ThreadId);
        Assert.NotEqual(caller, world.History.ThreadId);
    }

    [Fact]
    public async Task Undoing_and_the_live_discrepancies_do_not_do_their_work_on_the_thread_that_asked()
    {
        using var world = new DiagramFakes.World();
        var ct = TestContext.Current.CancellationToken;
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, ct);
        world.Analysis.Graph = DiagramFixtures.Graph(appName: "Renamed");
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, ct);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.RunSequence, DiagramFixtures.Run, ct);
        var live = new LiveConsistency(world.Staleness, null);
        var saved = new CommitAnalysis(world.Analysis.Graph, new ConsistencyReport(DiagramFixtures.Commit, [], [], false));
        int callerUndo = 0, callerLive = 0, undoThread = 0, generateThread = 0;

        await Task.Factory.StartNew(
            () =>
            {
                callerUndo = Environment.CurrentManagedThreadId;
                world.Catalog.RestorePreviousAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None).GetAwaiter().GetResult();
                undoThread = world.History.ThreadId;
                Assert.NotNull(world.Catalog.ReadAsync(world.Project, Solution, CancellationToken.None).GetAwaiter().GetResult());
                world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None).GetAwaiter().GetResult();
                generateThread = world.History.ThreadId;
                callerLive = Environment.CurrentManagedThreadId;
                live.RefreshAsync(world.Project, saved, CancellationToken.None).GetAwaiter().GetResult();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.Equal(callerUndo, callerLive);
        Assert.NotEqual(0, undoThread);
        Assert.NotEqual(callerUndo, undoThread);
        Assert.NotEqual(0, generateThread);
        Assert.NotEqual(callerUndo, generateThread);
        Assert.NotEqual(0, world.Recorder.ThreadId);
        Assert.NotEqual(callerLive, world.Recorder.ThreadId);
    }

    [Fact]
    public async Task What_is_out_of_date_is_the_same_the_second_time_and_follows_the_model_when_it_changes()
    {
        using var world = new DiagramFakes.World();
        var ct = TestContext.Current.CancellationToken;
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, ct);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.ProjectNamespaces, DiagramFixtures.CoreProject, ct);
        var current = DiagramFixtures.Graph();
        var renamed = DiagramFixtures.Graph(appName: "Renamed");

        Assert.Empty(await world.Staleness.FindAsync(world.Project, current, ct));
        Assert.Equal(2, world.Staleness.GraphRenders);
        Assert.Empty(await world.Staleness.FindAsync(world.Project, current, ct));

        // The second time the two diagrams of the model come from the memory: nothing is drawn again.
        Assert.Equal(2, world.Staleness.GraphRenders);
        var first = await world.Staleness.FindAsync(world.Project, renamed, ct);
        var second = await world.Staleness.FindAsync(world.Project, renamed, ct);
        Assert.Equal(4, world.Staleness.GraphRenders);

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(s => s.Diagram), second.Select(s => s.Diagram));
        Assert.Contains(first, s => s.Diagram.Kind == DiagramKind.Solution);
        Assert.Empty(await world.Staleness.FindAsync(world.Project, current, ct));
    }
}
