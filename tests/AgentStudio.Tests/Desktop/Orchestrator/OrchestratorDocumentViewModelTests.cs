using System.Windows;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Domain.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The «Orquestador» document: validation next to the nodes, «Ejecutar» only for a valid and approved plan, the plan the
/// orchestrator agent proposes, and the run it starts (docs/13 section 8, F3-6). The engine is a fake.
/// </summary>
public sealed class OrchestratorDocumentViewModelTests
{
    private const string Root = @"C:\proyectos\calc";
    private static readonly RunId Run = new("20260101-abc123");

    private sealed class MemoryLayoutStore : ICanvasLayoutStore
    {
        public CanvasLayout Saved { get; private set; } = CanvasLayout.Empty;

        /// <summary>How many times the canvas asked to save its layout.</summary>
        public int Saves { get; private set; }

        public string GetLayoutPath(string projectRoot) => projectRoot + @"\.agentstudio\canvas-layout.json";

        public CanvasLayout Load(string projectRoot) => Saved;

        public void Save(string projectRoot, CanvasLayout layout)
        {
            Saves++;
            Saved = layout;
        }
    }

    /// <summary>Opens the project, or refuses its storage as the real workspace does with a link left in it (ADR-0010, addendum A-05).</summary>
    private sealed class SwitchableWorkspace : IProjectWorkspace
    {
        public Exception? Refusal { get; set; }

        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) =>
            Refusal is null ? Task.FromResult(new ProjectContext("p-1", projectRoot)) : Task.FromException<ProjectContext>(Refusal);
    }

    private static ProjectStorageIntegrityException StorageRefusal() =>
        new($@"Refusing to use '{Root}\.agentstudio' of the project's storage: it is a link (junction or symbolic link). Nothing was written through it; remove or fix it and open the project again.");

    /// <summary>The fixture plan with its tool task on the profile's «lint» step: valid against <see cref="Backends.Profile"/>.</summary>
    private static PlanDefinition ValidPlan() => PlanFixtures.Plan();

    private static async Task<OrchestratorDocumentViewModel> OpenAsync(OrchestrationBackend backend)
    {
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend), new MemoryLayoutStore(), new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();
        return document;
    }

    private static NodeViewModel NodeOf(OrchestratorDocumentViewModel document, string id) => document.Canvas.Nodes.Single(n => n.Id == Tid(id));

    private static PlanProposal ProposalOf(PlanDefinition plan) =>
        new(Run, plan, [], Corrected: false, AgentRunStatus.Succeeded, Error: null, RawPlanEvidence: null);

    [Fact]
    public async Task Without_an_engine_the_screen_says_so_and_cannot_plan_or_run()
    {
        var document = await OpenAsync(OrchestrationBackend.None);
        document.RequestText = "Añadir Calc.Multiply";

        Assert.False(document.HasEngine);
        Assert.Equal("Motor no disponible", document.EngineStatus);
        Assert.StartsWith("Motor no disponible", document.Missing[0], StringComparison.Ordinal);
        Assert.False(document.RequestPlanCommand.CanExecute(null));
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.StartsWith("Motor no disponible", document.ExecuteBlockers, StringComparison.Ordinal);
        Assert.Equal(ValidationState.NoContext, document.ValidationState);

        // The canvas itself works: nothing about it needs the engine.
        Assert.True(document.Canvas.AddNodeCommand.CanExecute(WorkTaskKind.AgentWork));
    }

    [Fact]
    public async Task Validar_shows_the_validator_messages_next_to_the_nodes()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()));
        document.Canvas.LoadTasks(PlanFixtures.Replace("T1", t => t with { Objective = string.Empty }), CanvasLayout.Empty);

        // Known but not shown until «Validar».
        Assert.Equal(ValidationState.Invalid, document.ValidationState);
        Assert.Empty(NodeOf(document, "T1").Problems);
        Assert.Empty(document.Problems);

        document.ValidateCommand.Execute(null);

        Assert.Equal(["La tarea T1 no tiene objetivo."], NodeOf(document, "T1").Problems);
        Assert.Equal([new PlanProblem(Tid("T1"), "La tarea T1 no tiene objetivo.")], document.Problems);
        Assert.Equal("1 problema en el plan.", document.ValidationText);
        Assert.Equal("1 problema en el plan.", document.Canvas.NoticeText);

        // From then on the messages follow the edits.
        NodeOf(document, "T1").Objective = "Añadir Calc.Multiply(a, b)";

        Assert.Empty(NodeOf(document, "T1").Problems);
        Assert.Equal("Plan válido.", document.ValidationText);
    }

    /// <summary>
    /// A build is not a test: the canvas checks the plan with the steps of the profile that run tests, so a write task of
    /// an agent whose policy asks for system-captured tests is shown wrong when it only builds (T2 of the fixture plan).
    /// </summary>
    [Fact]
    public async Task Validar_shows_that_a_policy_of_tests_is_not_met_by_a_build_step()
    {
        IReadOnlyList<AgentDefinition> roster = [.. PlanFixtures.Roster().Select(a => a.Id == Aid("ejecutor2") ? a with { Review = new ReviewPolicy(false, true) } : a)];
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Roster = new FakeRoster(() => roster) });
        document.Canvas.LoadTasks(PlanFixtures.ValidTasks(), CanvasLayout.Empty);

        document.ValidateCommand.Execute(null);

        const string Expected = "La tarea T2 la hace «ejecutor2», cuya política exige pruebas capturadas por el sistema, pero ninguno de sus pasos de verificación ejecuta pruebas: añade un paso de pruebas del perfil (una compilación no es una prueba).";
        Assert.Equal(PlanViolationCode.TestStepRequiredByPolicy, Assert.Single(document.Violations).Code);
        Assert.Equal([Expected], NodeOf(document, "T2").Problems);
        Assert.Equal([new PlanProblem(Tid("T2"), Expected)], document.Problems);
    }

    [Fact]
    public async Task Clicking_a_problem_selects_its_node()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()));
        document.Canvas.LoadTasks(PlanFixtures.Replace("T6", t => t with { Objective = string.Empty }), CanvasLayout.Empty);
        document.ValidateCommand.Execute(null);

        document.SelectProblemCommand.Execute(document.Problems.Single());

        Assert.Equal(Tid("T6"), document.Canvas.SelectedNode?.Id);
        Assert.Equal(Tid("T6"), document.Editor?.Node.Id);
        Assert.Contains(document.Editor!.AgentOptions, o => o.Value == Aid("ejecutor"));
    }

    [Fact]
    public async Task Ejecutar_is_disabled_while_the_plan_is_invalid_or_unapproved()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()));
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        document.RequestText = "Añadir Calc.Multiply";

        Assert.Equal(ValidationState.Valid, document.ValidationState);
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.Equal("Aprueba el plan", document.ExecuteBlockers);

        document.ApproveCommand.Execute(null);
        Assert.True(document.ExecuteCommand.CanExecute(null));
        Assert.Equal(string.Empty, document.ExecuteBlockers);

        NodeOf(document, "T1").Objective = " ";
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.False(document.ApproveCommand.CanExecute(null));
        Assert.False(document.IsApproved);
        Assert.Equal("El plan tiene problemas: pulsa «Validar»", document.ExecuteBlockers);

        // Back to exactly the approved content: that is the plan the user approved.
        NodeOf(document, "T1").Objective = "Do T1";
        Assert.True(document.IsApproved);
        Assert.True(document.ExecuteCommand.CanExecute(null));

        // Any other change voids the approval.
        NodeOf(document, "T1").Title = "Otro título";
        Assert.False(document.IsApproved);
        Assert.Equal("El plan cambió después de aprobarlo: apruébalo otra vez.", document.ApprovalText);
        Assert.False(document.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Approving_and_rejecting_give_the_decision_the_engine_needs()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()));
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        var hash = document.CurrentHash[..12];

        document.ApproveCommand.Execute(null);
        Assert.Equal(new ApprovalDecision(true, "user", T0, $"Plan {hash} aprobado en el lienzo."), document.PlanDecision);
        Assert.Equal($"Plan aprobado ({hash}).", document.ApprovalText);

        document.RejectCommand.Execute(null);
        Assert.Equal(new ApprovalDecision(false, "user", T0, $"Plan {hash} rechazado en el lienzo."), document.PlanDecision);
        Assert.False(document.IsApproved);
        Assert.Equal("Plan rechazado.", document.ApprovalText);
    }

    [Fact]
    public async Task The_plan_the_orchestrator_proposes_is_shown_on_the_canvas_with_its_validation()
    {
        var engine = new FakeOrchestrator();
        var proposed = PlanFixtures.Plan(PlanFixtures.Replace("T2", t => t with { Covers = [Rid("R9")] }));
        engine.Plan = (request, progress, _) =>
        {
            progress?.Report(new ToolInvoked(T0, "Read", "src/Calc/Calc.cs"));
            return Task.FromResult(new PlanProposal(Run, proposed, [], Corrected: true, AgentRunStatus.Succeeded, null, null));
        };
        var document = await OpenAsync(Backends.Complete(engine));
        document.RequestText = "  Añadir Calc.Multiply  ";

        await document.RequestPlanCommand.ExecuteAsync(null);

        var request = Assert.Single(engine.PlanRequests);
        Assert.Equal("Añadir Calc.Multiply", request.OriginalRequest);
        Assert.Equal(Aid("orquestador"), request.Orchestrator);
        Assert.Equal(Backends.Profile, request.Verification);
        Assert.Equal(PlanFixtures.Requirements().ContentHash, request.Requirements.ContentHash);
        Assert.Equal(8, document.Canvas.Nodes.Count);
        Assert.Equal(proposed.Summary, document.PlanSummary);
        Assert.Equal(proposed.Risks, document.Risks);
        Assert.Equal(proposed.ContentHash, document.CurrentHash);
        Assert.Same(proposed, document.CurrentPlan());
        Assert.Equal(["La tarea T2 cubre el requisito R9, que no está en los requisitos congelados."], NodeOf(document, "T2").Problems);
        Assert.Equal("Plan propuesto por el orquestador con problemas: 1 problema en el plan. Se le pidió una corrección.", document.ProposalStatus);
        Assert.Equal(["Usa Read: src/Calc/Calc.cs"], document.PlanningActivity);
        Assert.False(document.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_proposal_without_a_plan_says_why()
    {
        var engine = new FakeOrchestrator
        {
            Plan = (_, _, _) => Task.FromResult(new PlanProposal(Run, null, [], false, AgentRunStatus.Failed, "The answer was not valid JSON.", null)),
        };
        var document = await OpenAsync(Backends.Complete(engine));
        document.RequestText = "Añadir Calc.Multiply";

        await document.RequestPlanCommand.ExecuteAsync(null);

        Assert.Equal("El orquestador no devolvió un plan utilizable (fallido): The answer was not valid JSON.", document.ProposalStatus);
        Assert.True(document.Canvas.IsEmpty);
    }

    [Fact]
    public async Task Ejecutar_starts_the_approved_plan_and_the_canvas_follows_the_run()
    {
        var engine = new FakeOrchestrator();
        var plan = ValidPlan();
        FakeRun? started = null;
        engine.Plan = (_, _, _) => Task.FromResult(ProposalOf(plan));
        engine.Start = request => started = new FakeRun(new RunSnapshot(Run, RunPhase.Running, [.. request.Plan.Tasks.Select(t => FakeRun.View(t.Id.Value, t.Kind))], FakeRun.Healthy, 0));
        var document = await OpenAsync(Backends.Complete(engine));
        document.RequestText = "Añadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        document.ApproveCommand.Execute(null);

        await document.ExecuteCommand.ExecuteAsync(null);

        var request = Assert.Single(engine.RunRequests);
        Assert.Equal(Run, request.Run);
        Assert.Same(plan, request.Plan);
        Assert.True(request.PlanApproval.Approved);
        Assert.Equal("Añadir Calc.Multiply", request.OriginalRequest);
        Assert.Equal(Backends.Profile, request.Verification);
        Assert.True(document.Monitor.IsActive);
        Assert.True(document.Canvas.IsReadOnly);
        Assert.False(document.ExecuteCommand.CanExecute(null));

        started!.Emit(new TaskStatusChangedEvent(T0, Tid("T1"), WorkTaskStatus.Pending, WorkTaskStatus.Running, "started"));
        started.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee", null, "Hecho"));
        await document.Monitor.Listening;

        Assert.Equal(WorkTaskStatus.Running, NodeOf(document, "T1").Status);
        Assert.True(document.EditPlanCommand.CanExecute(null));

        // A finished run stays on screen until «Editar el plan»: nothing may start, plan or approve over it (the same
        // approval would send the same run id again).
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.False(document.RequestPlanCommand.CanExecute(null));
        Assert.False(document.ApproveCommand.CanExecute(null));
        Assert.Contains("Editar el plan", document.ExecuteBlockers, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => document.Monitor.Attach(new FakeRun(FakeRun.SnapshotOf(RunPhase.Running))));

        await document.EditPlanCommand.ExecuteAsync(null);

        Assert.False(document.Canvas.IsReadOnly);
        Assert.Equal(WorkTaskStatus.Pending, NodeOf(document, "T1").Status);
        Assert.Null(document.PlanDecision);
        Assert.True(started.Disposed);
        Assert.True(document.ApproveCommand.CanExecute(null));
        Assert.False(document.ExecuteCommand.CanExecute(null));
    }

    /// <summary>
    /// «Ejecutar» sends the roster the plan was validated against and shown with when the user approved it. An agent
    /// edited in the store afterwards (given a shell, say) is not what the request carries, so the engine, which compares
    /// it with the stored roster, refuses to start.
    /// </summary>
    [Fact]
    public async Task Ejecutar_sends_the_roster_the_plan_was_validated_against_and_approved_with()
    {
        var engine = new FakeOrchestrator { Start = _ => new FakeRun(new RunSnapshot(Run, RunPhase.Running, [], FakeRun.Healthy, 0)) };
        var approved = PlanFixtures.Roster();
        var roster = approved;
        var document = await OpenAsync(Backends.Complete(engine) with { Roster = new FakeRoster(() => roster) });
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);

        // Another roster that leaves the warning as it was (a tool that makes a writer at medium integrity withdraws the
        // approval instead: An_approval_is_withdrawn_when_the_warning_changes_after_it).
        roster = [.. approved.Select(a => a.Id == Aid("ejecutor") ? a with { SystemInstructions = a.SystemInstructions + " Otra instrucción." } : a)];
        await document.ReloadCommand.ExecuteAsync(null);
        Assert.True(document.ExecuteCommand.CanExecute(null));

        await document.ExecuteCommand.ExecuteAsync(null);

        var request = Assert.Single(engine.RunRequests);
        Assert.NotNull(request.ValidatedRoster);
        Assert.Equal(RosterHash.Of(approved), RosterHash.Of(request.ValidatedRoster));
        Assert.NotEqual(RosterHash.Of(roster), RosterHash.Of(request.ValidatedRoster));
    }

    [Fact]
    public async Task A_run_that_finished_well_is_offered_for_delivery_through_the_projects_delivery_service()
    {
        var engine = new FakeOrchestrator();
        var delivery = new FakeDeliveryService();
        FakeRun? started = null;
        engine.Plan = (_, _, _) => Task.FromResult(ProposalOf(ValidPlan()));
        engine.Start = _ => started = new FakeRun(new RunSnapshot(Run, RunPhase.Running, [], FakeRun.Healthy, 0));
        var document = await OpenAsync(Backends.Complete(engine) with { Delivery = delivery });
        document.RequestText = "Añadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        started!.Finish(new RunResult(Run, RunPhase.Succeeded, FakeRun.Healthy, "c0ffee0000000000000000000000000000000000", "veredicto-1", "Hecho"));
        await document.Monitor.Listening;
        await document.Monitor.DeliveryCheck;

        Assert.Equal([(new ProjectContext("p-1", Root), Run)], delivery.Checks);
        Assert.True(document.Monitor.DeliverCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_edited_proposal_runs_as_the_users_plan()
    {
        var engine = new FakeOrchestrator();
        engine.Plan = (_, _, _) => Task.FromResult(ProposalOf(ValidPlan()));
        engine.Start = request => new FakeRun(new RunSnapshot(new RunId("20260101-def456"), RunPhase.Running, [], FakeRun.Healthy, 0));
        var document = await OpenAsync(Backends.Complete(engine));
        document.RequestText = "Añadir Calc.Multiply";
        await document.RequestPlanCommand.ExecuteAsync(null);

        NodeOf(document, "T1").Instructions = "Mantén la API pública.";
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        var request = Assert.Single(engine.RunRequests);
        Assert.Null(request.Run);
        Assert.Equal(PlanSource.User, request.Plan.Source);
        Assert.Null(request.Plan.Author);
        Assert.Equal("Mantén la API pública.", request.Plan.Tasks.Single(t => t.Id == Tid("T1")).Instructions);
    }

    [Fact]
    public async Task An_interrupted_run_is_listed_and_can_be_resumed()
    {
        var plan = ValidPlan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var engine = new FakeOrchestrator();
        engine.Runs.Add(new RunSummary(Run, T0, RunPhase.Interrupted, plan.ContentHash, "Añadir Calc.Multiply", null));
        engine.Runs.Add(new RunSummary(new RunId("20251231-fff000"), T0.AddDays(-1), RunPhase.Succeeded, plan.ContentHash, "Antes", null));
        engine.Resume = id => new FakeRun(new RunSnapshot(id, RunPhase.Interrupted, [FakeRun.View("T1", status: WorkTaskStatus.Blocked, blockReason: "Interrupted")], FakeRun.Healthy, 1));
        var document = await OpenAsync(Backends.Complete(engine, plans));

        Assert.Equal(["Interrumpida", "Completada"], document.PreviousRuns.Select(r => r.PhaseLabel));
        Assert.True(document.ResumeRunCommand.CanExecute(document.PreviousRuns[0]));
        Assert.False(document.ResumeRunCommand.CanExecute(document.PreviousRuns[1]));

        await document.ResumeRunCommand.ExecuteAsync(document.PreviousRuns[0]);

        Assert.Equal([Run], engine.Resumed);
        Assert.Equal(8, document.Canvas.Nodes.Count);
        Assert.Equal(WorkTaskStatus.Blocked, NodeOf(document, "T1").Status);
        Assert.Equal("Interrumpida", document.Monitor.PhaseLabel);
        Assert.Equal("Interrumpida", document.Monitor.Tasks[0].StatusLabel);
    }

    /// <summary>Types one valid requirement in the «Requisitos» panel of the document.</summary>
    private static void TypeRequirement(OrchestratorDocumentViewModel document)
    {
        document.Requirements.AddRequirementCommand.Execute(null);
        document.Requirements.Drafts[^1].Statement = "Añadir Calc.Multiply(a, b)";
        document.Requirements.Drafts[^1].Criteria[0].Description = "Multiply(2, 3) devuelve 6";
    }

    [Fact]
    public async Task Pedir_plan_waits_for_frozen_requirements_and_plans_against_them()
    {
        var store = new FakeRequirementStore();
        var engine = new FakeOrchestrator { Plan = (_, _, _) => Task.FromResult(ProposalOf(ValidPlan())) };
        var document = await OpenAsync(Backends.Complete(engine) with { Requirements = store });
        document.RequestText = "Añadir Calc.Multiply";

        Assert.Null(document.Context?.Requirements);
        Assert.False(document.RequestPlanCommand.CanExecute(null));

        TypeRequirement(document);

        // A draft is not a frozen set.
        Assert.False(document.RequestPlanCommand.CanExecute(null));

        await document.Requirements.FreezeCommand.ExecuteAsync(null);

        var frozen = Assert.Single(store.Saved);
        Assert.Same(frozen, document.Context?.Requirements);
        Assert.DoesNotContain(document.Missing, line => line.Contains("requisitos congelados", StringComparison.Ordinal));
        Assert.True(document.RequestPlanCommand.CanExecute(null));

        await document.RequestPlanCommand.ExecuteAsync(null);

        Assert.Equal(frozen.ContentHash, Assert.Single(engine.PlanRequests).Requirements.ContentHash);
    }

    [Fact]
    public async Task Planning_and_running_wait_while_the_requirements_have_changes_that_are_not_frozen()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()));
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        Assert.True(document.RequestPlanCommand.CanExecute(null));
        Assert.True(document.ExecuteCommand.CanExecute(null));

        document.Requirements.EditCommand.Execute(null);
        document.Requirements.Drafts[0].Statement = "Otra cosa";

        Assert.False(document.RequestPlanCommand.CanExecute(null));
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.Contains("Congela o descarta los cambios de los requisitos", document.ExecuteBlockers, StringComparison.Ordinal);

        document.Requirements.DiscardCommand.Execute(null);

        Assert.True(document.RequestPlanCommand.CanExecute(null));
        Assert.True(document.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_new_version_of_the_requirements_voids_the_approval_of_the_plan()
    {
        var store = new FakeRequirementStore(PlanFixtures.Requirements());
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Requirements = store });
        document.RequestText = "Añadir Calc.Multiply";
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        Assert.True(document.IsApproved);

        document.Requirements.EditCommand.Execute(null);
        document.Requirements.Drafts[1].Statement = "Document it in the README";
        await document.Requirements.FreezeCommand.ExecuteAsync(null);

        Assert.NotEqual(PlanFixtures.Requirements().ContentHash, document.Context?.Requirements?.ContentHash);
        Assert.False(document.IsApproved);
        Assert.Null(document.PlanDecision);
        Assert.False(document.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_proposed_profile_is_shown_and_only_becomes_the_profile_when_the_user_accepts_it()
    {
        var source = new FakeProfileSource(null, proposal: Backends.Profile);
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Verification = source });

        Assert.Null(document.Context?.Verification);
        Assert.True(document.ShowsProfileProposal);
        Assert.Equal(
            ["build · Compilar · dotnet build", "test · Probar · dotnet test", "lint · Analizar · dotnet format --verify-no-changes"],
            document.ProfileSteps);
        Assert.Empty(source.Saved);
        Assert.Equal(ValidationState.NoContext, document.ValidationState);
        Assert.True(document.UseProposedProfileCommand.CanExecute(null));

        await document.UseProposedProfileCommand.ExecuteAsync(null);

        Assert.Equal([Backends.Profile], source.Saved);
        Assert.Same(Backends.Profile, document.Context?.Verification);
        Assert.False(document.ShowsProfileProposal);
        Assert.False(document.UseProposedProfileCommand.CanExecute(null));
        Assert.Contains(Backends.Profile.ContentHash[..12], document.ProfileText, StringComparison.Ordinal);
    }

    // ---- the warning next to «Aprobar plan»: the writers at medium integrity of the plan (ADR-0018, decision 3) ----

    /// <summary>How every warning of a plan starts and ends; what is in between names each task and why.</summary>
    private const string Opening = "Atención: este plan tiene escritores con integridad Medium. ";

    private const string Meaning = " Pueden escribir en cualquier sitio donde puedas escribir tú, y lo que hagan queda fuera del modelo de amenaza (ADR-0018). Por eso la comprobación de tu repositorio será estricta: una carpeta ignorada que no se pueda verificar bloquea la ejecución.";

    /// <summary>The fixture roster with its Anthropic agents on Claude Code, and <paramref name="extra"/>.</summary>
    private static IReadOnlyList<AgentDefinition> ClaudeRoster(params AgentDefinition[] extra) =>
        [.. PlanFixtures.Roster().Select(a => a.Model.Vendor == VendorId.Anthropic ? a with { Model = a.Model with { Provider = ProviderId.ClaudeCode } } : a), .. extra];

    /// <summary>
    /// The backend of the tests with <see cref="ClaudeRoster"/> and a probe that says whether the installed Claude Code
    /// is a measured one: on a measured one the fixture plan has no writer at medium integrity, so only what a test
    /// changes is warned about.
    /// </summary>
    private static OrchestrationBackend OnClaude(bool measured = true, VerificationProfile? profile = null, params AgentDefinition[] extra)
    {
        IReadOnlyList<AgentDefinition> roster = ClaudeRoster(extra);
        return Backends.Complete(new FakeOrchestrator()) with
        {
            Roster = new FakeRoster(() => roster),
            Verification = new FakeProfileSource(profile ?? Backends.Profile),
            WriteConfinement = measured ? new FixedWriteConfinement(ProviderId.ClaudeCode) : new FixedWriteConfinement(),
        };
    }

    [Fact]
    public async Task A_step_the_plan_runs_without_isolation_is_flagged_in_its_line_and_next_to_the_approval_of_the_plan()
    {
        var steps = Backends.Profile.Steps;
        var profile = Backends.Profile with { Steps = [steps[0] with { Isolation = ProcessIsolation.None }, .. steps.Skip(1)] };
        var document = await OpenAsync(OnClaude(profile: profile));
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);

        Assert.Equal(
            [
                "build · Compilar · dotnet build · SIN AISLAMIENTO: el código que escriben los agentes se ejecuta con tus permisos en el worktree",
                "test · Probar · dotnet test",
                "lint · Analizar · dotnet format --verify-no-changes",
            ],
            document.ProfileSteps);
        Assert.True(document.HasIsolationWarning);
        Assert.Equal(
            Opening + "T1: el paso «build» se ejecuta sin aislamiento. T2: el paso «build» se ejecuta sin aislamiento. T3: el paso «build» se ejecuta sin aislamiento." + Meaning,
            document.IsolationWarning);

        // The warning stays on screen while the plan is approved: it is part of what the user approves.
        document.ApproveCommand.Execute(null);
        Assert.True(document.IsApproved);
        Assert.True(document.HasIsolationWarning);
    }

    [Fact]
    public async Task The_fixture_plan_on_a_measured_Claude_Code_with_isolated_steps_shows_no_warning()
    {
        var document = await OpenAsync(OnClaude());
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);

        Assert.Equal(ValidationState.Valid, document.ValidationState);
        Assert.False(document.HasIsolationWarning);
        Assert.Null(document.IsolationWarning);
        Assert.DoesNotContain(document.ProfileSteps, line => line.Contains("AISLAMIENTO", StringComparison.Ordinal));
    }

    /// <summary>The roster a project gets by default, a measured Claude Code and isolated steps: the plan the app expects most of the time warns about nothing.</summary>
    [Fact]
    public async Task The_default_roster_with_isolated_steps_shows_no_warning()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Roster = new FakeRoster(), WriteConfinement = new FixedWriteConfinement(ProviderId.ClaudeCode) });
        document.Canvas.LoadTasks(
            [
                PlanFixtures.Spec("T1") with { Agent = DefaultAgentRoster.Executor, Workspace = WorkspaceMode.Write, WriteScope = ["src/Calc/"], Covers = [Rid("R1")], VerificationSteps = ["build", "test"] },
                PlanFixtures.Spec("R1", WorkTaskKind.Review) with { Agent = DefaultAgentRoster.Judge, Workspace = WorkspaceMode.ReadOnly, DependsOn = [PlanFixtures.Dep("T1")], MaxRetries = 0 },
                PlanFixtures.Spec("L1", WorkTaskKind.ToolRun) with { ToolId = "lint", DependsOn = [PlanFixtures.Dep("R1")] },
            ],
            CanvasLayout.Empty);

        Assert.Equal(ValidationState.Valid, document.ValidationState);
        Assert.Null(document.IsolationWarning);
        Assert.False(document.HasIsolationWarning);
    }

    /// <summary>
    /// B10, seen live: the orchestrator proposed «write → review» without an integration; the run ended approved and could not
    /// be delivered. The preparation warns before the plan is approved, and the warning goes away once an integration
    /// reviewed by the judge takes the writing in.
    /// </summary>
    [Fact]
    public async Task A_valid_plan_that_could_not_be_delivered_is_warned_about_before_it_is_approved()
    {
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Roster = new FakeRoster(), WriteConfinement = new FixedWriteConfinement(ProviderId.ClaudeCode) });
        var write = PlanFixtures.Spec("T1") with { Agent = DefaultAgentRoster.Executor, Workspace = WorkspaceMode.Write, WriteScope = ["src/Calc/"], Covers = [Rid("R1")], VerificationSteps = ["build", "test"] };
        document.Canvas.LoadTasks(
            [
                write,
                PlanFixtures.Spec("R1", WorkTaskKind.Review) with { Agent = DefaultAgentRoster.Judge, Workspace = WorkspaceMode.ReadOnly, DependsOn = [PlanFixtures.Dep("T1")], MaxRetries = 0 },
            ],
            CanvasLayout.Empty);

        Assert.Equal(ValidationState.Valid, document.ValidationState);
        Assert.True(document.HasDeliveryWarning);
        Assert.Contains("T1", document.DeliveryWarning, StringComparison.Ordinal);

        document.Canvas.LoadTasks(
            [
                write,
                PlanFixtures.Spec("I1", WorkTaskKind.Integration) with { DependsOn = [PlanFixtures.Dep("T1")], VerificationSteps = ["build", "test"], MaxRetries = 0 },
                PlanFixtures.Spec("R1", WorkTaskKind.Review) with { Agent = DefaultAgentRoster.Judge, Workspace = WorkspaceMode.ReadOnly, DependsOn = [PlanFixtures.Dep("I1")], MaxRetries = 0 },
            ],
            CanvasLayout.Empty);

        Assert.Equal(ValidationState.Valid, document.ValidationState);
        Assert.False(document.HasDeliveryWarning);
        Assert.Null(document.DeliveryWarning);
    }

    [Fact]
    public async Task On_a_Claude_Code_never_measured_every_task_that_writes_is_named()
    {
        var document = await OpenAsync(OnClaude(measured: false));
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);

        Assert.Equal(
            Opening
            + "T1: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido. "
            + "T2: el agente «ejecutor2» escribe con una versión de Claude Code cuyo confinamiento no se ha medido. "
            + "T6: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido."
            + Meaning,
            document.IsolationWarning);
    }

    [Fact]
    public async Task The_warning_follows_the_plan_as_it_is_drawn()
    {
        var shell = PlanFixtures.Agent("ejecutorShell", AgentRole.Executor, VendorId.Anthropic) with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), AllowedTools = ["Read", "Edit", "Write", "Bash"] };
        var document = await OpenAsync(OnClaude(extra: [shell]));
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        var changes = new List<string?>();
        document.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(document.HasIsolationWarning);

        NodeOf(document, "T6").Agent = Aid("ejecutorShell");

        Assert.Equal(Opening + "T6: el agente «ejecutorShell» tiene Bash." + Meaning, document.IsolationWarning);
        Assert.True(document.HasIsolationWarning);
        Assert.Contains(nameof(OrchestratorDocumentViewModel.IsolationWarning), changes);
        Assert.Contains(nameof(OrchestratorDocumentViewModel.HasIsolationWarning), changes);

        // The same task in a read-only workspace cannot change its worktree, so its shell writes nowhere.
        NodeOf(document, "T6").Workspace = WorkspaceMode.ReadOnly;

        Assert.Null(document.IsolationWarning);
    }

    [Fact]
    public async Task The_warning_follows_the_profile_when_the_project_is_read_again()
    {
        var source = new FakeProfileSource(Backends.Profile);
        var document = await OpenAsync(OnClaude() with { Verification = source });
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        var changes = new List<string?>();
        document.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(document.HasIsolationWarning);

        await source.SaveAsync(document.Context!.Project!, Backends.Profile with { Steps = [.. Backends.Profile.Steps.Select(s => s.Id == "lint" ? s with { Isolation = ProcessIsolation.None } : s)] }, TestContext.Current.CancellationToken);
        await document.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(Opening + "T8: el paso «lint» se ejecuta sin aislamiento." + Meaning, document.IsolationWarning);
        Assert.Contains(nameof(OrchestratorDocumentViewModel.IsolationWarning), changes);
        Assert.Contains(nameof(OrchestratorDocumentViewModel.HasIsolationWarning), changes);
    }

    /// <summary>
    /// The approval is given next to one warning (ADR-0018, A-04): when the project is read again and the warning is
    /// another (a step that now runs without isolation), the approval no longer stands and the plan must be approved
    /// again, so what runs is what the user saw.
    /// </summary>
    [Fact]
    public async Task An_approval_is_withdrawn_when_the_warning_changes_after_it()
    {
        var source = new FakeProfileSource(Backends.Profile);
        var document = await OpenAsync(OnClaude() with { Verification = source });
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);
        Assert.True(document.PlanDecision?.Approved);

        await source.SaveAsync(document.Context!.Project!, Backends.Profile with { Steps = [.. Backends.Profile.Steps.Select(s => s.Id == "lint" ? s with { Isolation = ProcessIsolation.None } : s)] }, TestContext.Current.CancellationToken);
        await document.ReloadCommand.ExecuteAsync(null);

        Assert.True(document.HasIsolationWarning);
        Assert.Null(document.PlanDecision);
        Assert.False(document.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_approval_stands_when_the_project_is_read_again_with_the_same_warning()
    {
        var source = new FakeProfileSource(Backends.Profile);
        var document = await OpenAsync(OnClaude() with { Verification = source });
        document.Canvas.LoadTasks(ValidPlan().Tasks, CanvasLayout.Empty);
        document.ApproveCommand.Execute(null);

        await document.ReloadCommand.ExecuteAsync(null);

        Assert.True(document.PlanDecision?.Approved);
    }

    private sealed class FixedWorkspace(ProjectContext project) : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(project);
    }

    /// <summary>
    /// What «Aprobar plan» shows is what the engine records with the approval: the screen and the engine read the same
    /// writers (plan, roster, profile and the answer of the confinement probe), so the warning the user approved is the
    /// one the decision log keeps. The engine is the real one, with its ports faked (<see cref="EngineHarness"/>).
    /// </summary>
    [Fact]
    public async Task The_warning_shown_next_to_the_approval_is_the_text_the_engine_records_with_it()
    {
        using var harness = new EngineHarness();
        var ct = TestContext.Current.CancellationToken;
        await harness.RequirementStore.SaveAsync(harness.Project.ProjectId, OrchestrationFixtures.Requirements(), ct);
        await harness.Roster.SaveAsync(harness.Project.ProjectId, OrchestrationFixtures.Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), ct);
        var profile = OrchestrationFixtures.Profile() with { Steps = [.. OrchestrationFixtures.Profile().Steps.Select(s => s.Id == "build" ? s with { Isolation = ProcessIsolation.None } : s)] };
        var backend = new OrchestrationBackend(harness.CreateOrchestrator(), new FixedWorkspace(harness.Project), harness.RequirementStore, harness.Roster, new FakeProfileSource(profile), harness.Plans, WriteConfinement: harness.WriteConfinement);
        var ui = new QueueSynchronizationContext();
        var document = new OrchestratorDocumentViewModel(harness.RepositoryRoot, new OrchestrationSession(backend), new MemoryLayoutStore(), new FakeTimeProvider(T0), ui);
        await document.OpenAsync();
        document.RequestText = "Añade Calc.Multiply(a,b) con sus pruebas";
        document.Canvas.LoadTasks(
            [OrchestrationFixtures.Work("T1").Verified("build"), OrchestrationFixtures.Work("T2", agent: "ejecutorShell"), OrchestrationFixtures.Work("T3", agent: "ejecutorCodex")],
            CanvasLayout.Empty);
        Assert.Equal(ValidationState.Valid, document.ValidationState);
        document.ApproveCommand.Execute(null);
        var shown = document.IsolationWarning;

        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(document.ErrorMessage);
        var asked = Assert.Single(harness.Payloads<ApprovalRequestedPayload>(), p => p.Payload.Action == "plan.approve").Payload;
        Assert.Equal($"Ejecutar el plan {document.CurrentHash[..12]} (3 tareas). {shown}", asked.Description);
        Assert.Equal(
            Opening
            + "T1: el paso «build» se ejecuta sin aislamiento. "
            + "T2: el agente «ejecutorShell» tiene Bash. "
            + "T3: el agente «ejecutorCodex» escribe con Codex, cuyo sandbox siempre tiene shell."
            + Meaning,
            shown);

        // The run went on without the screen: it is let finish and released before the harness goes.
        await document.Monitor.Listening.WaitAsync(RunTestSupport.Guard, ct);
        ui.RunAll();
        await document.Monitor.DetachAsync();
    }

    // ---- a resume the engine refuses because the writers at medium integrity changed (G-02) ----

    /// <summary>What the review shows next to «Aprobar plan» for the fixture plan on a Claude Code never measured.</summary>
    private const string UnmeasuredWarning =
        Opening
        + "T1: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido. "
        + "T2: el agente «ejecutor2» escribe con una versión de Claude Code cuyo confinamiento no se ha medido. "
        + "T6: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido."
        + Meaning;

    /// <summary>
    /// The screen of a project with an interrupted run of the fixture plan, approved on a measured Claude Code and now on
    /// one never measured: the engine refuses to resume it, as it does, with the warning the plan has now.
    /// </summary>
    private static async Task<(OrchestratorDocumentViewModel Document, FakeOrchestrator Engine, PlanDefinition Plan)> RefusedResumeAsync(Action<OrchestratorDocumentViewModel>? before = null)
    {
        var plan = ValidPlan();
        var plans = new FakePlanStore();
        plans.Plans[plan.ContentHash] = plan;
        var engine = new FakeOrchestrator
        {
            Resume = id => throw new RunNeedsApprovalException($"Run {id} cannot be resumed: its writers at medium integrity changed.", id, plan.ContentHash, warningAnchored: true, approvedWarning: null, currentWarning: UnmeasuredWarning),
            Start = _ => new FakeRun(new RunSnapshot(new RunId("20260102-fff111"), RunPhase.Running, [], FakeRun.Healthy, 0)),
        };
        engine.Runs.Add(new RunSummary(Run, T0, RunPhase.Interrupted, plan.ContentHash, "Interrumpida", null));
        var document = await OpenAsync(OnClaude(measured: false) with { Orchestrator = engine, Plans = plans });
        document.RequestText = "Añadir Calc.Multiply";
        before?.Invoke(document);

        await document.ResumeRunCommand.ExecuteAsync(Assert.Single(document.PreviousRuns));

        return (document, engine, plan);
    }

    /// <summary>
    /// The same plan approved on the canvas before the resume (proposed under the id of that very run, say) is not an
    /// approval of the follow-up: it was given before the user read why the resume was refused, so the follow-up waits for
    /// a new «Aprobar plan», and it never takes the id of the refused run.
    /// </summary>
    [Fact]
    public async Task An_approval_given_before_a_refused_resume_is_not_taken_for_the_follow_up()
    {
        var (document, engine, plan) = await RefusedResumeAsync(before: document =>
        {
            document.LoadProposal(ProposalOf(ValidPlan()));
            document.ApproveCommand.Execute(null);
            Assert.True(document.IsApproved);
        });

        Assert.Equal(plan.ContentHash, document.CurrentHash);
        Assert.Null(document.PlanDecision);
        Assert.False(document.IsApproved);
        Assert.False(document.ExecuteCommand.CanExecute(null));

        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);
        var request = Assert.Single(engine.RunRequests);
        Assert.Equal(Run, request.Follows);
        Assert.Null(request.Run);
    }

    [Fact]
    public async Task A_resume_refused_because_the_writers_changed_shows_the_new_warning_and_puts_the_plan_up_for_approval_again()
    {
        var (document, engine, plan) = await RefusedResumeAsync();

        Assert.Equal([Run], engine.Resumed);
        Assert.False(document.Monitor.IsAttached);
        Assert.Contains($"No se reanuda la ejecución {Run}", document.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains($"«{UnmeasuredWarning}»", document.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("ejecución de seguimiento", document.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("«Rechazar»", document.ErrorMessage, StringComparison.Ordinal);

        // The plan of the run is on the canvas, for review, with the warning it would run with now next to «Aprobar
        // plan»; nothing is approved until the user approves it.
        Assert.Equal(plan.ContentHash, document.CurrentHash);
        Assert.False(document.Canvas.IsReadOnly);
        Assert.Equal(UnmeasuredWarning, document.IsolationWarning);
        Assert.Null(document.PlanDecision);
        Assert.Equal(Run, document.FollowUpOf);
        Assert.False(document.ExecuteCommand.CanExecute(null));
        Assert.Contains("Aprueba el plan", document.ExecuteBlockers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_the_plan_of_a_refused_resume_again_starts_a_follow_up_run_of_it()
    {
        var (document, engine, plan) = await RefusedResumeAsync();

        document.ApproveCommand.Execute(null);
        Assert.Contains($"ejecución de seguimiento de {Run}", document.ApprovalText, StringComparison.Ordinal);
        await document.ExecuteCommand.ExecuteAsync(null);

        var request = Assert.Single(engine.RunRequests);
        Assert.Equal(Run, request.Follows);
        Assert.Null(request.Run); // a new run: the refused one keeps its id, and the engine would say it already started
        Assert.Equal(plan.ContentHash, request.Plan.ContentHash);
        Assert.Equal(UnmeasuredWarning, request.ApprovedWarning);
        Assert.True(document.Monitor.IsAttached);
        Assert.Null(document.ErrorMessage);
        Assert.Null(document.FollowUpOf);
        Assert.Equal([Run], engine.Resumed);
    }

    [Fact]
    public async Task Rejecting_the_plan_of_a_refused_resume_discards_the_follow_up()
    {
        var (document, engine, _) = await RefusedResumeAsync();

        document.RejectCommand.Execute(null);

        Assert.Null(document.FollowUpOf);
        Assert.Null(document.ErrorMessage);
        Assert.Equal("Plan rechazado.", document.ApprovalText);

        // Approved and run afterwards, it is a run of its own.
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(engine.RunRequests).Follows);
    }

    [Fact]
    public async Task A_plan_edited_after_a_refused_resume_is_no_longer_a_follow_up_of_it()
    {
        var (document, engine, _) = await RefusedResumeAsync();

        NodeOf(document, "T1").Instructions = "Mantén la API pública.";

        Assert.Null(document.FollowUpOf);
        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(engine.RunRequests).Follows);
    }

    /// <summary>
    /// The whole way with the real engine (its ports faked, <see cref="EngineHarness"/>): a run approved on a measured
    /// Claude Code is interrupted; the app opens again on one never measured; the engine refuses to resume it and the
    /// screen says why; the plan approved again next to the warning it has now starts a follow-up run, which the engine
    /// accepts with that warning.
    /// </summary>
    [Fact]
    public async Task A_resume_the_engine_refuses_for_its_writers_is_followed_up_from_the_screen_with_the_warning_shown()
    {
        using var harness = new EngineHarness();
        var ct = TestContext.Current.CancellationToken;
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(OrchestrationFixtures.Plan(OrchestrationFixtures.Work("T1")));
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        Assert.Equal(RunPhase.Interrupted, (await first.CompleteAsync(harness)).Phase);

        harness.WriteConfinement = new FixedWriteConfinement();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed());
        var backend = new OrchestrationBackend(harness.CreateOrchestrator(), new FixedWorkspace(harness.Project), harness.RequirementStore, harness.Roster, new FakeProfileSource(OrchestrationFixtures.Profile()), harness.Plans, WriteConfinement: harness.WriteConfinement);
        var ui = new QueueSynchronizationContext();
        var document = new OrchestratorDocumentViewModel(harness.RepositoryRoot, new OrchestrationSession(backend), new MemoryLayoutStore(), new FakeTimeProvider(T0), ui);
        await document.OpenAsync();
        document.RequestText = "Añade Calc.Multiply(a,b) con sus pruebas";

        await document.ResumeRunCommand.ExecuteAsync(Assert.Single(document.PreviousRuns));

        const string Now = Opening + "T1: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido." + Meaning;
        Assert.Contains($"«{Now}»", document.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(Now, document.IsolationWarning);
        Assert.Equal(first.Id, document.FollowUpOf);
        Assert.Single(harness.Claude.Starts);

        document.ApproveCommand.Execute(null);
        await document.ExecuteCommand.ExecuteAsync(null);

        Assert.Null(document.ErrorMessage);
        var started = harness.Payloads<RunStartedPayload>().Select(p => p.Payload).Last();
        Assert.Equal(first.Id.Value, started.Follows);
        Assert.Equal(Now, started.ApprovedWarning);

        // The follow-up runs without the screen: it is let finish and released before the harness goes.
        await document.Monitor.Listening.WaitAsync(RunTestSupport.Guard, ct);
        ui.RunAll();
        var finished = Assert.Single(harness.Payloads<RunFinishedPayload>()).Payload;
        Assert.Equal((started.RunId, RunPhase.Succeeded), (finished.RunId, finished.Phase));
        await document.Monitor.DetachAsync();
    }

    [Fact]
    public async Task The_roster_and_the_default_it_got_are_shown()
    {
        var roster = new FakeRoster();
        var document = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Roster = roster });

        Assert.Equal(
            [
                "orquestador · Orquestador · claude-code (anthropic) · Read, Grep, Glob",
                "ejecutor · Ejecutor · claude-code (anthropic) · Read, Grep, Glob, Edit, Write",
                "juez · Juez · codex-cli (openai) · solo lectura",
            ],
            document.RosterLines.Take(3));

        // The four agents of the debates (docs/15 D-09) come after them.
        Assert.Equal(7, document.RosterLines.Count);
        Assert.Equal(["arquitecto", "critico", "juez-claude", "juez-revision"], document.RosterLines.Skip(3).Select(l => l.Split(" · ")[0]));
        Assert.Equal("El proyecto no tenía agentes: se creó la plantilla por defecto (Claude orquesta y ejecuta, Codex juzga).", document.RosterNotice);
    }

    [Fact]
    public async Task The_preparation_opens_shown_while_something_is_missing_and_hidden_when_the_project_is_ready()
    {
        var ready = await OpenAsync(Backends.Complete(new FakeOrchestrator()));
        var incomplete = await OpenAsync(Backends.Complete(new FakeOrchestrator()) with { Requirements = new FakeRequirementStore() });

        Assert.False(ready.IsPreparationExpanded);
        Assert.True(incomplete.IsPreparationExpanded);

        // From then on it is the user's choice: a reload does not override it.
        incomplete.TogglePreparationCommand.Execute(null);
        await incomplete.ReloadCommand.ExecuteAsync(null);

        Assert.False(incomplete.IsPreparationExpanded);
        Assert.Equal("Mostrar", incomplete.PreparationToggleText);
        Assert.StartsWith("Sin petición · sin requisitos congelados · 9 agentes · perfil «pruebas»", incomplete.PreparationSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_canvas_layout_is_saved_for_the_project_once_its_storage_is_open()
    {
        var layout = new MemoryLayoutStore();
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(Backends.Complete(new FakeOrchestrator())), layout, new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();

        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        document.Canvas.Nodes[0].Location = new Point(320, 160);
        document.Canvas.SaveLayoutCommand.Execute(null);

        Assert.Equal(new Point(320, 160), layout.Saved.Positions["T1"]);
        Assert.Equal(OrchestratorDocumentViewModel.CreateId(Root), document.ContentId);
        Assert.Equal("Orquestador · calc", document.Title);
    }

    // ---- the canvas only saves into a project storage that opened (H-02) ----

    [Fact]
    public void The_canvas_saves_no_layout_before_the_project_storage_is_opened()
    {
        var layout = new MemoryLayoutStore();
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(Backends.Complete(new FakeOrchestrator())), layout, new FakeTimeProvider(T0), new InlineSynchronizationContext());

        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        document.Canvas.SaveLayoutCommand.Execute(null);

        // The canvas works, keeps the positions in memory and says nothing it did not do.
        Assert.Equal(0, layout.Saves);
        Assert.Single(document.Canvas.Nodes);
        Assert.Equal(NoticeKind.Info, document.Canvas.NoticeKind);
        Assert.StartsWith("Creado T1", document.Canvas.NoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_canvas_saves_no_layout_when_the_project_storage_was_refused()
    {
        var layout = new MemoryLayoutStore();
        var backend = Backends.Complete(new FakeOrchestrator()) with { Workspace = new SwitchableWorkspace { Refusal = StorageRefusal() } };
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend), layout, new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();

        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        document.Canvas.SaveLayoutCommand.Execute(null);

        Assert.Equal(0, layout.Saves);
        Assert.Contains(document.Missing, m => m.StartsWith("No se pudo abrir el proyecto para orquestar: Refusing to use", StringComparison.Ordinal));
        Assert.Equal(NoticeKind.Info, document.Canvas.NoticeKind);
        Assert.StartsWith("Creado T1", document.Canvas.NoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_canvas_stops_saving_when_the_project_storage_is_refused_on_a_reload()
    {
        var layout = new MemoryLayoutStore();
        var workspace = new SwitchableWorkspace();
        var backend = Backends.Complete(new FakeOrchestrator()) with { Workspace = workspace };
        var document = new OrchestratorDocumentViewModel(Root, new OrchestrationSession(backend), layout, new FakeTimeProvider(T0), new InlineSynchronizationContext());
        await document.OpenAsync();
        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        var saves = layout.Saves;

        workspace.Refusal = StorageRefusal();
        await document.ReloadCommand.ExecuteAsync(null);
        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        document.Canvas.SaveLayoutCommand.Execute(null);

        Assert.True(saves > 0, "The canvas did not save while the storage was open.");
        Assert.Equal(saves, layout.Saves);
        Assert.Equal(2, document.Canvas.Nodes.Count);
    }

    // ---- a decision log no anchor recognises at the project's location (ADR-0010, addendum of 2026-10-18) ----

    [Fact]
    public async Task A_history_without_an_anchor_blocks_planning_and_running_until_the_person_accepts_it_in_the_dialog()
    {
        var history = new HistoryBackend();
        await history.WriteUnanchoredAsync(2);
        var document = await OpenAsync(history.On(Backends.Complete(new FakeOrchestrator())));
        document.RequestText = "Añadir Calc.Multiply";

        Assert.True(document.HasIntegrityProblem);
        Assert.Contains("no hay ancla para esta ubicación", document.IntegrityProblem, StringComparison.Ordinal);
        Assert.True(document.CanAcceptHistory);
        Assert.True(document.AcceptHistoryCommand.CanExecute(null));
        Assert.False(document.RequestPlanCommand.CanExecute(null));
        Assert.Contains("El registro de decisiones no es íntegro", document.ExecuteBlockers, StringComparison.Ordinal);

        await document.AcceptHistoryCommand.ExecuteAsync(null);

        Assert.Single(history.Prompt.Requests);
        Assert.Null(document.ErrorMessage);
        Assert.False(document.HasIntegrityProblem);
        Assert.False(document.CanAcceptHistory);
        Assert.False(document.AcceptHistoryCommand.CanExecute(null));
        Assert.True(document.RequestPlanCommand.CanExecute(null));
        Assert.DoesNotContain("registro de decisiones", document.ExecuteBlockers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_accepting_the_history_leaves_the_screen_blocked_and_says_so()
    {
        var history = new HistoryBackend(approve: false);
        await history.WriteUnanchoredAsync(2);
        var document = await OpenAsync(history.On(Backends.Complete(new FakeOrchestrator())));

        await document.AcceptHistoryCommand.ExecuteAsync(null);

        Assert.Single(history.Prompt.Requests);
        Assert.Equal(2, history.Entries.Count);
        Assert.True(document.HasIntegrityProblem);
        Assert.True(document.AcceptHistoryCommand.CanExecute(null));
        Assert.NotNull(document.ErrorMessage);
    }

    [Fact]
    public async Task A_tampered_log_shows_its_problem_and_offers_no_acceptance()
    {
        var history = new HistoryBackend();
        await history.WriteTamperedAsync();
        var document = await OpenAsync(history.On(Backends.Complete(new FakeOrchestrator())));

        Assert.True(document.HasIntegrityProblem);
        Assert.False(document.CanAcceptHistory);
        Assert.False(document.AcceptHistoryCommand.CanExecute(null));
        Assert.Contains("El registro de decisiones no es íntegro", document.ExecuteBlockers, StringComparison.Ordinal);
    }
}
