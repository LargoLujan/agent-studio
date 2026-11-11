using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// B3: the request and the plan being prepared are kept for the project on this machine (in <c>canvas-layout.json</c>, next
/// to the positions of the canvas: excluded from git, and left out of the checks of the user's repository) and come back
/// when the tab opens again, never approved; a proposal comes back linked to its run while that run did not start. A draft
/// that is damaged, too big or not this installation's is ignored with a notice; a plan kept is never replaced by an empty
/// canvas the person did not empty; and what waits to be kept is kept when the tab closes.
/// </summary>
public sealed class PlanDraftTests : IDisposable
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly RunId Planning = new("20260101-abc123");

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>Positions and draft in memory, as the file keeps them, shared by the documents of one test.</summary>
    private sealed class MemoryCanvasStore : ICanvasLayoutStore, IPlanDraftStore
    {
        public CanvasLayout Layout { get; private set; } = CanvasLayout.Empty;

        public PlanDraft? Draft { get; set; }

        /// <summary>Why the draft found is ignored, as the store would say it.</summary>
        public string? Ignored { get; set; }

        /// <summary>When set, reading the draft throws it.</summary>
        public Exception? ReadFailure { get; set; }

        public int DraftSaves { get; private set; }

        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => Layout;

        public void Save(string projectRoot, CanvasLayout layout) => Layout = layout;

        public DraftReading ReadDraft(string projectRoot) =>
            ReadFailure is { } failure ? throw failure : Ignored is { } why ? new DraftReading(null, why) : new DraftReading(Draft);

        public void SaveDraft(string projectRoot, PlanDraft? draft)
        {
            DraftSaves++;
            Draft = draft;
        }
    }

    private static PlanProposal ProposalOf(PlanDefinition plan) => new(Planning, plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null);

    private static OrchestratorDocumentViewModel NewDocument(MemoryCanvasStore store, FakeTimeProvider time, FakeOrchestrator? engine = null, FakePlanStore? plans = null) =>
        new(Root, new OrchestrationSession(Backends.Complete(engine ?? new FakeOrchestrator(), plans)), store, time, new InlineSynchronizationContext(), drafts: store);

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(MemoryCanvasStore store, FakeTimeProvider time, FakeOrchestrator? engine = null, FakePlanStore? plans = null)
    {
        var document = NewDocument(store, time, engine, plans);
        await document.OpenAsync();
        return document;
    }

    /// <summary>A draft of two tasks this installation kept: what a document brings back on opening.</summary>
    private static PlanDraft TwoTasks() => new("Añadir Calc.Multiply", "Resumen", [], [PlanFixtures.Spec("T1"), PlanFixtures.Spec("T2")]);

    // ---- the file ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_draft_is_kept_whole_in_the_layout_file_and_each_save_keeps_what_the_other_wrote()
    {
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        var tasks = PlanFixtures.ValidTasks();
        var draft = new PlanDraft("Añadir Calc.Multiply", "Resumen", ["Riesgo"], tasks, new PlanDraftProposal(Planning, new string('b', 64), Corrected: true));

        store.Save(_folder.Root, new CanvasLayout(new Dictionary<string, Point>(StringComparer.Ordinal) { ["T1"] = new Point(320, 160) }));
        store.SaveDraft(_folder.Root, draft);
        store.Save(_folder.Root, new CanvasLayout(new Dictionary<string, Point>(StringComparer.Ordinal) { ["T1"] = new Point(10, 20) }));

        var reading = store.ReadDraft(_folder.Root);
        Assert.Null(reading.Ignored);
        var read = Assert.IsType<PlanDraft>(reading.Draft);
        Assert.Equal((draft.Request, draft.Summary, draft.Proposal), (read.Request, read.Summary, read.Proposal));
        Assert.Equal(draft.Risks, read.Risks);
        Assert.Equal(PlanDefinition.Create("x", tasks, [], PlanSource.User, null).ContentHash, PlanDefinition.Create("x", read.Tasks, [], PlanSource.User, null).ContentHash);
        Assert.Equal(new Point(10, 20), store.Load(_folder.Root).Positions["T1"]);
        Assert.Equal([FileCanvasLayoutStore.FileName], Directory.EnumerateFiles(_folder.DataDirectory).Select(Path.GetFileName));

        store.SaveDraft(_folder.Root, null);
        Assert.Equal(DraftReading.None, store.ReadDraft(_folder.Root));
        Assert.Equal(new Point(10, 20), store.Load(_folder.Root).Positions["T1"]);
    }

    /// <summary>Saves a valid draft, changes it in the file as <paramref name="damage"/> says, and reads it back.</summary>
    private DraftReading Tampered(FileCanvasLayoutStore store, Action<JsonObject> damage)
    {
        store.Save(_folder.Root, new CanvasLayout(new Dictionary<string, Point>(StringComparer.Ordinal) { ["T1"] = new Point(1, 2) }));
        var task = PlanFixtures.Spec("T1", WorkTaskKind.Condition) with { DependsOn = [PlanFixtures.Dep("T0")], Condition = new TestsPassed(Tid("T0")) };
        store.SaveDraft(_folder.Root, new PlanDraft("x", "s", ["r"], [task], new PlanDraftProposal(Planning, new string('c', 64), Corrected: false)));
        var file = store.GetLayoutPath(_folder.Root);
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        damage(root["draft"]!.AsObject());
        File.WriteAllText(file, root.ToJsonString());
        return store.ReadDraft(_folder.Root);
    }

    [Theory]
    [InlineData("a task that is null")]
    [InlineData("a dependency that is null")]
    [InlineData("a kind out of range")]
    [InlineData("a blank id")]
    [InlineData("a run that is not an id")]
    [InlineData("a condition of no known type")]
    [InlineData("tasks that are not a list")]
    [InlineData("a risk that is null")]
    public void A_damaged_draft_is_ignored_with_why_and_the_positions_still_open(string damage)
    {
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());

        var reading = Tampered(store, draft =>
        {
            switch (damage)
            {
                case "a task that is null":
                    draft["tasks"] = new JsonArray((JsonNode?)null);
                    break;
                case "a dependency that is null":
                    draft["tasks"]![0]!["dependsOn"] = new JsonArray((JsonNode?)null);
                    break;
                case "a kind out of range":
                    draft["tasks"]![0]!["kind"] = 99;
                    break;
                case "a blank id":
                    draft["tasks"]![0]!["id"] = " ";
                    break;
                case "a run that is not an id":
                    draft["proposal"]!["run"] = "no es un id";
                    break;
                case "a condition of no known type":
                    draft["tasks"]![0]!["condition"]!["type"] = "Nada";
                    break;
                case "tasks that are not a list":
                    draft["tasks"] = "no es una lista";
                    break;
                case "a risk that is null":
                    draft["risks"] = new JsonArray((JsonNode?)null);
                    break;
            }
        });

        Assert.Null(reading.Draft);
        Assert.Contains("dañado", reading.Ignored, StringComparison.Ordinal);
        Assert.Equal(new Point(1, 2), store.Load(_folder.Root).Positions["T1"]);

        // And a save of the positions keeps the file usable.
        store.Save(_folder.Root, new CanvasLayout(new Dictionary<string, Point>(StringComparer.Ordinal) { ["T1"] = new Point(3, 4) }));
        Assert.Equal(new Point(3, 4), store.Load(_folder.Root).Positions["T1"]);
    }

    [Fact]
    public void A_draft_past_its_limits_or_a_file_too_big_is_ignored_and_never_read_in_part()
    {
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());

        var tooManyTasks = Tampered(store, draft => draft["tasks"] = new JsonArray([.. Enumerable.Range(0, PlanDraftLimits.MaxTasks + 1).Select(i => (JsonNode?)new JsonObject { ["id"] = $"T{i}", ["kind"] = "AgentWork", ["join"] = "AllCompleted", ["workspace"] = "Write" })]));
        var tooLongRequest = Tampered(store, draft => draft["request"] = new string('x', PlanDraftLimits.MaxText + 1));
        Assert.Null(tooManyTasks.Draft);
        Assert.Contains("demasiado grande", tooManyTasks.Ignored, StringComparison.Ordinal);
        Assert.Null(tooLongRequest.Draft);
        Assert.Contains("demasiado grande", tooLongRequest.Ignored, StringComparison.Ordinal);

        File.WriteAllText(store.GetLayoutPath(_folder.Root), "{\"version\":1,\"padding\":\"" + new string(' ', (int)PlanDraftLimits.MaxFileBytes) + "\"}");
        Assert.Contains("demasiado grande", store.ReadDraft(_folder.Root).Ignored, StringComparison.Ordinal);
        Assert.Empty(store.Load(_folder.Root).Positions);
    }

    [Fact]
    public void A_draft_another_installation_saved_is_not_taken_for_this_ones()
    {
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());

        var cloned = Tampered(store, draft => draft["owner"] = "0123456789abcdef0123456789abcdef");
        var unowned = Tampered(store, draft => draft.Remove("owner"));

        Assert.Null(cloned.Draft);
        Assert.Contains("no lo guardó Agent Studio en este equipo", cloned.Ignored, StringComparison.Ordinal);
        Assert.Null(unowned.Draft);
        Assert.NotNull(unowned.Ignored);
    }

    // ---- the tab -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_is_being_prepared_is_saved_a_moment_after_it_changes_and_a_proposal_at_once()
    {
        var store = new MemoryCanvasStore();
        var time = new FakeTimeProvider(T0);
        var document = await OpenAsync(store, time);

        document.RequestText = "Añadir Calc.Multiply";
        Assert.Equal(0, store.DraftSaves);
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        Assert.Equal("Añadir Calc.Multiply", store.Draft?.Request);

        document.LoadProposal(ProposalOf(PlanFixtures.Plan()));
        Assert.Equal(PlanFixtures.Plan().Tasks.Count, store.Draft?.Tasks.Count);

        // V43: with what the orchestrator planned with, so a draft brought back sends its answers only while they are the same.
        var requirements = document.Context!.Requirements!.ContentHash;
        Assert.Equal(
            new PlanDraftProposal(Planning, PlanFixtures.Plan().ContentHash, Corrected: false, PlannedRequest: "Añadir Calc.Multiply", RequirementsHash: requirements),
            store.Draft?.Proposal);
    }

    [Fact]
    public async Task Why_the_orchestrator_planned_without_the_history_search_is_said_with_the_proposal_and_comes_back_with_its_draft()
    {
        const string Codex = "El orquestador no tuvo la búsqueda en el historial: Codex no admite herramientas externas.";
        var store = new MemoryCanvasStore();
        var time = new FakeTimeProvider(T0);
        var plan = PlanFixtures.Plan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var first = await OpenAsync(store, time, plans: plans);

        // With the search, nothing is added; without it, the status of the proposal says why, after what it said before.
        first.LoadProposal(ProposalOf(plan));
        Assert.DoesNotContain("historial", first.ProposalStatus, StringComparison.Ordinal);
        first.LoadProposal(ProposalOf(plan) with { HistoryToolNotice = Codex });
        Assert.StartsWith("Plan propuesto por el orquestador", first.ProposalStatus, StringComparison.Ordinal);
        Assert.EndsWith(" " + Codex, first.ProposalStatus, StringComparison.Ordinal);
        Assert.Equal(Codex, store.Draft?.Proposal?.HistoryToolNotice);

        // A proposal without a plan says it too.
        first.LoadProposal(new PlanProposal(Planning, null, [], false, AgentRunStatus.Failed, "boom", null) { HistoryToolNotice = Codex });
        Assert.EndsWith(" " + Codex, first.ProposalStatus, StringComparison.Ordinal);

        // The draft keeps it: the tab opened again still says it.
        first.LoadProposal(ProposalOf(plan) with { HistoryToolNotice = Codex });
        first.FlushDraft();
        var again = await OpenAsync(store, time, plans: plans);
        Assert.Equal(Codex, again.Proposal?.HistoryToolNotice);
        Assert.StartsWith("Borrador recuperado", again.ProposalStatus, StringComparison.Ordinal);
        Assert.EndsWith(" " + Codex, again.ProposalStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_keeps_why_the_orchestrator_planned_without_the_search_and_a_draft_of_before_reads_without_it()
    {
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        var notice = "El orquestador no tuvo la búsqueda en el historial: está apagada en Ajustes («Búsqueda en el historial para el orquestador»).";
        store.SaveDraft(_folder.Root, new PlanDraft("x", "s", [], [PlanFixtures.Spec("T1")], new PlanDraftProposal(Planning, new string('d', 64), Corrected: false, notice)));

        Assert.Equal(notice, store.ReadDraft(_folder.Root).Draft?.Proposal?.HistoryToolNotice);

        // A draft written before the search in the history has no notice: it reads as it did.
        var file = store.GetLayoutPath(_folder.Root);
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        root["draft"]!["proposal"]!.AsObject().Remove("historyToolNotice");
        File.WriteAllText(file, root.ToJsonString());
        var before = store.ReadDraft(_folder.Root);
        Assert.Null(before.Ignored);
        Assert.Equal(new PlanDraftProposal(Planning, new string('d', 64), Corrected: false), before.Draft?.Proposal);
    }

    [Fact]
    public async Task The_draft_comes_back_when_the_tab_opens_again_unapproved_and_linked_to_its_proposal()
    {
        var store = new MemoryCanvasStore();
        var time = new FakeTimeProvider(T0);
        var plan = PlanFixtures.Plan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var first = await OpenAsync(store, time, plans: plans);
        first.RequestText = "Añadir Calc.Multiply";
        first.LoadProposal(ProposalOf(plan));

        // Approved before the app closed: the approval is not kept with the draft.
        first.ApproveCommand.Execute(null);
        Assert.True(first.IsApproved);
        first.FlushDraft();

        var engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(Planning, RunPhase.Running, [], FakeRun.Healthy, 0)) };
        var again = await OpenAsync(store, time, engine, plans);

        Assert.Equal("Añadir Calc.Multiply", again.RequestText);
        Assert.Equal(plan.Tasks.Select(t => t.Id), again.Canvas.Nodes.Select(n => n.Id));
        Assert.Equal(plan.ContentHash, again.CurrentHash);
        Assert.Equal(Planning, again.Proposal?.Run);
        Assert.Null(again.PlanDecision);
        Assert.False(again.IsApproved);
        Assert.False(again.ExecuteCommand.CanExecute(null));
        Assert.Equal("Aprueba el plan", again.ExecuteBlockers);
        Assert.Contains("borrador", again.Canvas.NoticeText, StringComparison.OrdinalIgnoreCase);

        // Approved again, it runs as the proposal it came from, and the answers the orchestrator got still reach it (V43).
        again.ApproveCommand.Execute(null);
        await again.ExecuteCommand.ExecuteAsync(null);
        var request = Assert.Single(engine.RunRequests);
        Assert.Equal(Planning, request.Run);
        Assert.Equal(Planning, request.PlannedIn);
    }

    [Fact]
    public async Task A_draft_brought_back_sends_the_answers_only_while_the_request_is_the_one_planned_and_one_of_before_sends_none()
    {
        var plan = PlanFixtures.Plan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var time = new FakeTimeProvider(T0);

        // Planned for one request, the draft is saved with another: brought back, the run is not told where it was planned.
        var store = new MemoryCanvasStore();
        var first = await OpenAsync(store, time, plans: plans);
        first.RequestText = "Añadir Calc.Multiply";
        first.LoadProposal(ProposalOf(plan));
        first.RequestText = "Otra petición";
        first.FlushDraft();
        var engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(Planning, RunPhase.Running, [], FakeRun.Healthy, 0)) };
        var changed = await OpenAsync(store, time, engine, plans);
        Assert.Equal("Otra petición", changed.RequestText);
        changed.ApproveCommand.Execute(null);
        await changed.ExecuteCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(engine.RunRequests).PlannedIn);

        // A draft of before V43 does not say what it was planned with: it sends none rather than guess.
        var old = new MemoryCanvasStore { Draft = new PlanDraft("Añadir Calc.Multiply", plan.Summary, plan.Risks, plan.Tasks, new PlanDraftProposal(Planning, plan.ContentHash, Corrected: false)) };
        var oldEngine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(Planning, RunPhase.Running, [], FakeRun.Healthy, 0)) };
        var before = await OpenAsync(old, time, oldEngine, plans);
        before.ApproveCommand.Execute(null);
        await before.ExecuteCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(oldEngine.RunRequests).PlannedIn);
    }

    [Fact]
    public void The_file_keeps_what_the_orchestrator_planned_with_only_whole()
    {
        var store = new FileCanvasLayoutStore(new ProjectStorageFiles());
        var requirements = new string('e', 64);
        store.SaveDraft(_folder.Root, new PlanDraft("x", "s", [], [PlanFixtures.Spec("T1")], new PlanDraftProposal(Planning, new string('d', 64), Corrected: false, PlannedRequest: "x", RequirementsHash: requirements)));
        var kept = store.ReadDraft(_folder.Root).Draft?.Proposal;
        Assert.Equal("x", kept?.PlannedRequest);
        Assert.Equal(requirements, kept?.RequirementsHash);

        // One without the other reads as neither; a hash that is not a SHA-256 makes the draft damaged.
        var file = store.GetLayoutPath(_folder.Root);
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        root["draft"]!["proposal"]!.AsObject().Remove("requirementsHash");
        File.WriteAllText(file, root.ToJsonString());
        var half = store.ReadDraft(_folder.Root).Draft?.Proposal;
        Assert.NotNull(half);
        Assert.Null(half.PlannedRequest);
        Assert.Null(half.RequirementsHash);

        root["draft"]!["proposal"]!["requirementsHash"] = "no-es-un-hash";
        File.WriteAllText(file, root.ToJsonString());
        Assert.Null(store.ReadDraft(_folder.Root).Draft);
    }

    [Fact]
    public async Task A_proposal_whose_run_already_started_comes_back_as_the_users_plan()
    {
        var store = new MemoryCanvasStore();
        var time = new FakeTimeProvider(T0);
        var plan = PlanFixtures.Plan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        store.Draft = new PlanDraft("Añadir Calc.Multiply", plan.Summary, plan.Risks, plan.Tasks, new PlanDraftProposal(Planning, plan.ContentHash, Corrected: false));
        var engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(new RunId("20260109-fff999"), RunPhase.Running, [], FakeRun.Healthy, 0)) };
        engine.Runs.Add(new RunSummary(Planning, T0, RunPhase.Succeeded, plan.ContentHash, "Hecho.", null));

        var document = await OpenAsync(store, time, engine, plans);
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(document.Proposal);
        Assert.Null(Assert.Single(engine.RunRequests).Run);
    }

    [Fact]
    public async Task A_draft_that_cannot_be_read_or_is_ignored_leaves_the_tab_usable_and_says_so_discreetly()
    {
        var time = new FakeTimeProvider(T0);
        var ignored = new MemoryCanvasStore { Ignored = "no se puede leer: está dañado o incompleto" };
        var failing = new MemoryCanvasStore { ReadFailure = new NullReferenceException("roto") };

        var first = await OpenAsync(ignored, time);
        var second = await OpenAsync(failing, time);

        Assert.True(first.Canvas.IsEmpty);
        Assert.Null(first.ErrorMessage);
        Assert.Contains("Se ignoró el borrador", first.Canvas.NoticeText, StringComparison.Ordinal);
        Assert.Equal(NoticeKind.Info, first.Canvas.NoticeKind);
        Assert.True(second.Canvas.IsEmpty);
        Assert.Null(second.ErrorMessage);
        Assert.Contains("no se pudo leer", second.Canvas.NoticeText, StringComparison.Ordinal);

        // What is prepared from then on is kept over it.
        first.RequestText = "Algo nuevo";
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        Assert.Equal("Algo nuevo", ignored.Draft?.Request);
    }

    [Fact]
    public async Task Nothing_is_saved_while_the_canvas_shows_a_run_and_its_plan_never_becomes_the_draft()
    {
        var store = new MemoryCanvasStore();
        var time = new FakeTimeProvider(T0);
        var engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(Planning, RunPhase.Running, [], FakeRun.Healthy, 0)) };
        var document = await OpenAsync(store, time, engine);
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(PlanFixtures.Plan().Tasks, CanvasLayout.Empty);
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        var saved = store.DraftSaves;
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        document.RequestText = "Otra cosa";
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);

        Assert.True(document.Monitor.IsAttached);
        Assert.Equal(saved, store.DraftSaves);
        Assert.Equal("Añadir Calc.Multiply", store.Draft?.Request);
    }

    [Fact]
    public async Task A_plan_kept_is_not_replaced_by_an_empty_canvas_unless_the_person_empties_it()
    {
        var store = new MemoryCanvasStore { Draft = TwoTasks() };
        var time = new FakeTimeProvider(T0);
        var document = await OpenAsync(store, time);
        Assert.Equal(2, document.Canvas.Nodes.Count);

        // A follow-up prepared (the canvas emptied for it, the request replaced) keeps the plan that was kept.
        await document.PrepareFollowUpAsync(new RunId("20260103-aaa111"), "Ejecución de seguimiento de 20260103-aaa111");
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        Assert.Equal(2, store.Draft?.Tasks.Count);
        Assert.Equal("Añadir Calc.Multiply", store.Draft?.Request);

        // The person deleting every node is an empty plan they want kept.
        document.Canvas.LoadTasks(TwoTasks().Tasks, CanvasLayout.Empty);
        foreach (var node in document.Canvas.Nodes)
        {
            document.Canvas.SelectedNodes.Add(node);
        }

        document.Canvas.DeleteSelectionCommand.Execute(null);
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        Assert.Equal(0, store.Draft?.Tasks.Count ?? 0);
    }

    [Fact]
    public async Task A_run_resumed_before_the_tab_opened_leaves_the_draft_kept_untouched_by_Editar_el_plan()
    {
        var store = new MemoryCanvasStore { Draft = TwoTasks() };
        var time = new FakeTimeProvider(T0);
        var document = NewDocument(store, time);
        var resumed = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running));
        Assert.True(document.FollowResumed(resumed, new AgentStudio.Application.Projects.ProjectContext("p-1", Root)));
        await document.OpenAsync();
        await document.ResumedCheck;

        resumed.Finish(new RunResult(resumed.Id, RunPhase.Succeeded, FakeRun.Healthy, null, null, "Hecho."));
        await document.Monitor.Listening;
        await document.EditPlanCommand.ExecuteAsync(null);
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        document.FlushDraft();

        Assert.Equal(0, store.DraftSaves);
        Assert.Equal(2, store.Draft?.Tasks.Count);
        Assert.Equal("Añadir Calc.Multiply", document.RequestText);
    }

    [Fact]
    public async Task What_waits_to_be_kept_is_kept_when_the_tab_closes()
    {
        var store = new MemoryCanvasStore();
        var document = await OpenAsync(store, new FakeTimeProvider(T0));
        document.RequestText = "Escrito justo antes de cerrar";
        Assert.Equal(0, store.DraftSaves);

        // As the shell does once it removed the tab.
        typeof(DocumentViewModel).GetMethod("NotifyClosed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, null);

        Assert.Equal("Escrito justo antes de cerrar", store.Draft?.Request);
    }

    [Fact]
    public async Task Without_a_draft_store_or_before_the_storage_opened_nothing_is_kept()
    {
        var time = new FakeTimeProvider(T0);
        var withoutStore = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(Backends.Complete(new FakeOrchestrator())), new MemoryCanvasStore(), time, new InlineSynchronizationContext());
        await withoutStore.OpenAsync();
        withoutStore.RequestText = "Algo";
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);

        var store = new MemoryCanvasStore();
        var notOpened = NewDocument(store, time);
        notOpened.RequestText = "Algo";
        time.Advance(OrchestratorDocumentViewModel.DraftSaveDelay);
        notOpened.FlushDraft();

        Assert.Equal(0, store.DraftSaves);
    }

    [Fact]
    public async Task The_preparation_folds_once_complete_when_a_plan_comes_or_a_node_is_created()
    {
        var store = new MemoryCanvasStore();
        var document = await OpenAsync(store, new FakeTimeProvider(T0));
        document.IsPreparationExpanded = true;

        // Nothing is folded while something is missing there: the request.
        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        Assert.True(document.IsPreparationExpanded);

        document.RequestText = "Añadir Calc.Multiply";
        Assert.True(document.IsPreparationComplete);
        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        Assert.False(document.IsPreparationExpanded);

        document.IsPreparationExpanded = true;
        document.LoadProposal(ProposalOf(PlanFixtures.Plan()));
        Assert.False(document.IsPreparationExpanded);
    }
}
