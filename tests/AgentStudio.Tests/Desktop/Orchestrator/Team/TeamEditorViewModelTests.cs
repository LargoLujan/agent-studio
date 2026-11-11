using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Team;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// F13-2 (docs/17 D-08, D-09 and D-42): adding, removing and configuring agents in the Team layer is editing the template. Each
/// change goes through the same save as the preparation, changes the <c>RosterHash</c>, withdraws the approval of the plan and
/// says so; the limits are seen while assembling; and what grants a capability of high risk waits for Windows Hello.
/// </summary>
public sealed class TeamEditorViewModelTests
{
    /// <summary>A store of the template in memory, with the saves and the removals counted and the callback of «saved» recorded.</summary>
    private sealed class Store
    {
        public List<AgentDefinition> Roster { get; } = [.. Roster_()];

        public int Saved { get; private set; }

        /// <summary>When set, saving this agent fails (the store goes down halfway).</summary>
        public AgentId? FailOn { get; set; }

        /// <summary>When set, removing this agent fails (the store goes down while taking agents out).</summary>
        public AgentId? FailRemoveOn { get; set; }

        /// <summary>When set, the second save of this agent fails: the one that puts it back after another save failed (the revert fails too).</summary>
        public AgentId? FailSecondSaveOf { get; set; }

        /// <summary>When set, the callback of «saved» (the document reading the template again) throws it.</summary>
        public Exception? AfterSaveFailure { get; set; }

        public List<string> Hashes { get; } = [];

        /// <summary>What reached the store, in order: <c>save:id</c> and <c>remove:id</c> (a failed call is not in it).</summary>
        public List<string> Operations { get; } = [];

        public Task Save(ProjectContext project, AgentDefinition agent)
        {
            if (FailOn is { } failing && failing == agent.Id)
            {
                return Task.FromException(new InvalidOperationException("the store is down"));
            }

            if (FailSecondSaveOf is { } second && second == agent.Id && Operations.Contains("save:" + agent.Id.Value))
            {
                return Task.FromException(new InvalidOperationException("the store is down again"));
            }

            Operations.Add("save:" + agent.Id.Value);
            var index = Roster.FindIndex(a => a.Id == agent.Id);
            if (index >= 0)
            {
                Roster[index] = agent;
            }
            else
            {
                Roster.Add(agent);
            }

            return Task.CompletedTask;
        }

        public Task Remove(ProjectContext project, AgentId id)
        {
            if (FailRemoveOn is { } failing && failing == id)
            {
                return Task.FromException(new InvalidOperationException("the store is down"));
            }

            Operations.Add("remove:" + id.Value);
            Roster.RemoveAll(a => a.Id == id);
            return Task.CompletedTask;
        }

        public Task AfterSave(TeamCanvasViewModel canvas, TeamEditorViewModel editor)
        {
            Saved++;
            Hashes.Add(RosterHash.Of(Roster));
            if (AfterSaveFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            canvas.Refresh(Roster);
            editor.Load(Project(), Roster);
            return Task.CompletedTask;
        }

        private static IReadOnlyList<AgentDefinition> Roster_() => TeamTestSupport.Roster();
    }

    /// <summary>«Quitar del equipo»: the question, and then the yes. Nothing reaches the store before the second.</summary>
    private static async Task RemoveSelectedAsync(TeamEditorViewModel editor)
    {
        editor.RemoveSelectedCommand.Execute(null);
        await editor.ConfirmRemoveCommand.ExecuteAsync(null);
    }

    private static (TeamCanvasViewModel Canvas, TeamEditorViewModel Editor, Store Store) Create(bool canRemove = true, PresenceConfirmer? confirm = null)
    {
        var store = new Store();
        var canvas = LoadedTeam(store.Roster);
        TeamEditorViewModel? created = null;
        var editor = new TeamEditorViewModel(canvas, store.Save, canRemove ? store.Remove : null, () => store.AfterSave(canvas, created!), Text());
        created = editor;
        editor.UseConfirmation(confirm);
        editor.Load(Project(), store.Roster);
        return (canvas, editor, store);
    }

    [Fact]
    public async Task Adding_an_executor_copies_the_template_of_the_role_with_a_new_id_and_asks_the_model()
    {
        var (canvas, editor, store) = Create();
        var hash = RosterHash.Of(store.Roster);

        editor.StartAddExecutorCommand.Execute(null);
        var pending = editor.PendingAdd!;
        Assert.Equal("ejecutor-2", pending.Id.Value);
        Assert.Equal("Ejecutor 2", pending.Name);
        Assert.Equal(ProviderId.ClaudeCode, pending.Provider);
        pending.Model = pending.Models.Single(m => m.Model == "haiku");
        await editor.ConfirmAddCommand.ExecuteAsync(null);

        var added = store.Roster.Single(a => a.Id.Value == "ejecutor-2");
        Assert.Equal("haiku", added.Model.Model);
        Assert.Equal(store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).AllowedTools, added.AllowedTools);
        Assert.Equal(MemoryPolicy.ReadSelected, added.Memory);
        Assert.True(added.Review.RequiresIndependentReview);
        Assert.Empty(added.PathGrants);
        Assert.NotEqual(hash, store.Hashes.Single());
        Assert.Null(editor.PendingAdd);
        Assert.Contains("Se añadió ejecutor-2 (Ejecutor 2).", editor.Notice, StringComparison.Ordinal);
        Assert.Contains("retira la aprobación del plan", editor.Notice, StringComparison.Ordinal);
        Assert.Contains(canvas.Agents, a => a.Id.Value == "ejecutor-2");
        Assert.Equal(2, canvas.Connections.Count(c => c.Kind == TeamEdgeKind.Distributes));
    }

    [Fact]
    public void The_layer_says_before_saving_that_the_approval_goes_and_says_more_when_the_plan_is_approved()
    {
        var (_, editor, _) = Create();

        Assert.Equal("Editar el equipo es editar la plantilla: cambia su huella y retira la aprobación del plan.", editor.ConsequenceText);
        editor.IsPlanApproved = true;
        Assert.Equal("El plan está aprobado: si cambias el equipo, tendrás que aprobarlo otra vez.", editor.ConsequenceText);
    }

    [Fact]
    public void The_limits_close_the_palette_one_orchestrator_and_up_to_eight_executors()
    {
        var (_, editor, store) = Create();

        Assert.False(editor.CanAddOrchestrator);
        Assert.True(editor.CanAddExecutor);
        Assert.True(editor.CanAddJudge);

        var template = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        foreach (var n in Enumerable.Range(2, 7))
        {
            store.Roster.Add(template with { Id = new AgentId($"ejecutor-{n}"), Name = $"Ejecutor {n}" });
        }

        editor.Load(Project(), store.Roster);

        Assert.Equal(8, store.Roster.Count(a => a.Role == AgentRole.Executor));
        Assert.False(editor.CanAddExecutor);
        Assert.StartsWith("Hay más ejecutores", editor.AddExecutorHint, StringComparison.Ordinal);
        Assert.False(editor.StartAddExecutorCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_an_orchestrator_the_palette_offers_one_and_a_judge_is_added_of_the_other_company()
    {
        var (_, editor, store) = Create();
        store.Roster.RemoveAll(a => a.Role == AgentRole.Orchestrator);
        store.Roster.RemoveAll(a => a.Id != DefaultAgentRoster.Executor);
        editor.Load(Project(), store.Roster);

        Assert.True(editor.CanAddOrchestrator);
        editor.StartAddJudgeCommand.Execute(null);
        Assert.Equal(ProviderId.CodexCli, editor.PendingAdd!.Provider);
        Assert.Equal(VendorId.OpenAI, editor.PendingAdd.Vendor);
        await editor.ConfirmAddCommand.ExecuteAsync(null);

        Assert.Contains(store.Roster, a => a.Id.Value == "juez" && a.Model.Vendor == VendorId.OpenAI);
    }

    [Fact]
    public async Task Removing_an_agent_edits_the_template_and_is_offered_only_when_the_store_can()
    {
        var (canvas, editor, store) = Create();
        var hash = RosterHash.Of(store.Roster);
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.ClaudeJudge));

        Assert.True(editor.CanRemoveAgents);
        await RemoveSelectedAsync(editor);

        Assert.DoesNotContain(store.Roster, a => a.Id == DefaultAgentRoster.ClaudeJudge);
        Assert.NotEqual(hash, store.Hashes.Single());
        Assert.StartsWith("Se quitó juez-claude del equipo.", editor.Notice, StringComparison.Ordinal);

        var (_, withoutStore, _) = Create(canRemove: false);
        Assert.False(withoutStore.CanRemoveAgents);
        Assert.False(withoutStore.RemoveSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Quitar_del_equipo_follows_the_selection_and_tells_the_button_so()
    {
        var (canvas, editor, _) = Create();
        var raised = 0;
        editor.RemoveSelectedCommand.CanExecuteChanged += (_, _) => raised++;

        // Nothing selected: the button is off. A WPF button reads CanExecute again only when told with CanExecuteChanged.
        Assert.False(editor.RemoveSelectedCommand.CanExecute(null));

        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));

        Assert.True(raised > 0);
        Assert.True(editor.RemoveSelectedCommand.CanExecute(null));

        var afterSelecting = raised;
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.ClaudeJudge));
        Assert.True(raised > afterSelecting);
        Assert.True(editor.RemoveSelectedCommand.CanExecute(null));

        // Losing the selection (the agent is gone from the canvas) turns it off again, and says so.
        var beforeLosing = raised;
        canvas.Refresh([.. Roster().Where(a => a.Id != DefaultAgentRoster.ClaudeJudge)]);
        Assert.True(raised > beforeLosing);
        Assert.False(editor.RemoveSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Quitar_del_equipo_asks_first_and_writes_nothing_until_the_yes()
    {
        var (canvas, editor, store) = Create();
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));

        editor.RemoveSelectedCommand.Execute(null);

        // The question says what is lost, what is turned off and what is kept.
        var question = editor.PendingRemoval!.Question;
        Assert.Contains("ejecutor", question, StringComparison.Ordinal);
        Assert.Contains("se pierde de la plantilla", question, StringComparison.Ordinal);
        Assert.Contains("skills", question, StringComparison.Ordinal);
        Assert.Contains("ejecuciones y los debates que ya hubo", question, StringComparison.Ordinal);
        Assert.Empty(store.Operations);
        Assert.True(editor.ConfirmRemoveCommand.CanExecute(null));

        // «Conservar»: the agent stays and the question goes.
        editor.CancelRemoveCommand.Execute(null);
        Assert.Null(editor.PendingRemoval);
        Assert.Empty(store.Operations);
        Assert.False(editor.ConfirmRemoveCommand.CanExecute(null));

        // The question is about the agent that was selected: another selection drops it, and the yes then does nothing.
        editor.RemoveSelectedCommand.Execute(null);
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.ClaudeJudge));
        Assert.Null(editor.PendingRemoval);
        await editor.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.Empty(store.Operations);

        // The yes.
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));
        await RemoveSelectedAsync(editor);
        Assert.Equal(["remove:ejecutor"], store.Operations);
        Assert.Null(editor.PendingRemoval);
    }

    [Fact]
    public async Task A_second_set_of_changes_does_not_start_while_one_is_being_written_and_the_buttons_wait()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (canvas, editor, store) = Create(confirm: (_, _, _) => release.Task);
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.ClaudeJudge));
        Assert.True(editor.RemoveSelectedCommand.CanExecute(null));

        // The first one grants power, so it waits for Windows Hello with nothing written yet.
        var first = editor.ApplyManyAsync([executor with { AllowedTools = [.. executor.AllowedTools, "Bash"] }], [], "x");
        Assert.False(first.IsCompleted);
        Assert.False(editor.RemoveSelectedCommand.CanExecute(null));

        var second = await editor.ApplyManyAsync([], [DefaultAgentRoster.Critic], "y");

        Assert.False(second);
        Assert.Equal("Se está guardando otro cambio de la plantilla: espera a que termine.", editor.ErrorMessage);
        Assert.Empty(store.Operations);

        release.SetResult(true);
        Assert.True(await first);

        // The one that ran leaves the layer as a success: the message of the one that did not start is gone, and the button is back.
        Assert.Null(editor.ErrorMessage);
        Assert.Equal(["save:ejecutor"], store.Operations);
        Assert.True(editor.RemoveSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task When_the_revert_fails_too_the_layer_says_the_template_may_be_halfway_and_to_read_it_again()
    {
        var (_, editor, store) = Create();
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        var judge = store.Roster.Single(a => a.Id == DefaultAgentRoster.Judge);
        store.FailOn = judge.Id;
        store.FailSecondSaveOf = executor.Id;

        var done = await editor.ApplyManyAsync([executor with { Name = "Otro" }, judge with { Name = "Otro juez" }], [], "x");

        Assert.False(done);
        Assert.Equal("Otro", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);
        Assert.Contains("the store is down", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("puede haber quedado a medias", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Volver a cargar", editor.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_that_cannot_read_the_template_again_does_not_turn_a_saved_change_into_an_exception()
    {
        var (_, editor, store) = Create();
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        store.AfterSaveFailure = new InvalidOperationException("the project is closed");

        var done = await editor.ApplyAsync(executor with { Name = "Otro" }, "Cambiado.");

        // The store has it, so it was written; the layer says what it could not do.
        Assert.True(done);
        Assert.Equal("Otro", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);
        Assert.Contains("La plantilla se guardó, pero no se pudo leer otra vez (the project is closed)", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.StartsWith("Cambiado.", editor.Notice, StringComparison.Ordinal);

        // A failure of the store with a document that cannot read either: the exception does not reach the UI, and the layer says halfway.
        store.FailOn = executor.Id;
        Assert.False(await editor.ApplyAsync(executor with { Name = "Tercero" }, "x"));
        Assert.Contains("the store is down", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Volver a cargar", editor.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuring_an_agent_changes_the_template_and_its_hash_and_is_refused_while_the_layer_is_read_only()
    {
        var (canvas, editor, store) = Create();
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));
        var inspector = editor.Inspector!;
        var before = RosterHash.Of(store.Roster);

        inspector.Instructions = "Usa tabulaciones.";
        inspector.MaxMinutes = "30";
        await inspector.ApplyFieldsCommand.ExecuteAsync(null);

        var changed = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        Assert.Equal("Usa tabulaciones.", changed.SystemInstructions);
        Assert.Equal(TimeSpan.FromMinutes(30), changed.Budget.MaxDuration);
        Assert.NotEqual(before, store.Hashes.Last());

        canvas.IsReadOnly = true;
        var saved = store.Saved;
        var refused = await editor.ApplyAsync(changed with { Name = "Otro" }, "x");
        Assert.False(refused);
        Assert.Equal(saved, store.Saved);
        Assert.Equal("El equipo no se puede cambiar mientras se sigue una ejecución o la pantalla trabaja.", editor.ErrorMessage);
    }

    [Fact]
    public async Task A_field_that_is_not_valid_says_why_and_saves_nothing()
    {
        var (canvas, editor, store) = Create();
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));
        var inspector = editor.Inspector!;

        inspector.MaxMinutes = "cero";
        await inspector.ApplyFieldsCommand.ExecuteAsync(null);

        Assert.Equal("Los minutos son un número entero entre 1 y 1440.", inspector.FieldProblem);
        Assert.Equal(0, store.Saved);
    }

    [Fact]
    public async Task A_cable_that_narrows_the_folders_is_saved_without_asking_and_one_that_widens_waits_for_windows_hello()
    {
        var asked = new List<PresenceAction>();
        var (canvas, editor, store) = Create(confirm: (action, _, _) =>
        {
            asked.Add(action);
            return Task.FromResult(true);
        });
        var executor = canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);

        // From the whole project to one folder: it narrows.
        Assert.Null(canvas.RequestConnect(executor, canvas.Repository!.ConnectorOf("src/")!));
        await editor.Saving;
        Assert.Equal([new PathGrant("src/", FileAccessLevel.ReadWrite)], store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).PathGrants);
        Assert.Empty(asked);

        // A second folder widens what it writes: Windows Hello is asked, and with it the cable is saved.
        executor = canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
        Assert.Null(canvas.RequestConnect(executor, canvas.Repository!.ConnectorOf("tests/")!));
        await editor.Saving;
        Assert.Equal([PresenceAction.GrantHighRiskCapability], asked);
        Assert.Equal(2, store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).PathGrants.Count);
    }

    [Fact]
    public async Task What_grants_more_power_is_not_done_without_a_confirmation()
    {
        var (canvas, editor, store) = Create(confirm: null);
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);

        var done = await editor.ApplyAsync(executor with { AllowedTools = [.. executor.AllowedTools, "Bash"] }, "x");

        Assert.False(done);
        Assert.DoesNotContain("Bash", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).AllowedTools);
        Assert.Equal(0, store.Saved);
        Assert.Contains("No se confirmó que eres tú", editor.ErrorMessage, StringComparison.Ordinal);

        var (canvas2, editor2, store2) = Create(confirm: (_, _, _) => Task.FromResult(false));
        canvas2.Select(canvas2.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));
        var bash = editor2.Inspector!.Tools.Single(t => t.Name == "Bash");
        Assert.True(bash.IsHighRisk);
        Assert.Equal("Bash · riesgo alto: pide Windows Hello", bash.AutomationName);
        bash.IsOn = true;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(bash.IsOn);
        Assert.DoesNotContain("Bash", store2.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).AllowedTools);

        // Taking a tool away never asks.
        var edit = editor2.Inspector!.Tools.Single(t => t.Name == "Edit");
        edit.IsOn = false;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Edit", store2.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).AllowedTools);
    }

    [Fact]
    public async Task A_set_of_changes_asks_windows_hello_once_and_saves_none_of_it_when_it_is_denied()
    {
        var asked = 0;
        var (_, editor, store) = Create(confirm: (_, _, _) =>
        {
            asked++;
            return Task.FromResult(false);
        });
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        var orchestrator = store.Roster.Single(a => a.Id == DefaultAgentRoster.Orchestrator);

        // The first change asks for nothing; the second gives the orchestrator the shell.
        var done = await editor.ApplyManyAsync([executor with { Name = "Otro" }, orchestrator with { AllowedTools = [.. orchestrator.AllowedTools, "Bash"] }], [], "x");

        Assert.False(done);
        Assert.Equal(1, asked);
        Assert.Equal(0, store.Saved);
        Assert.Equal("Ejecutor", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);
        Assert.DoesNotContain("Bash", store.Roster.Single(a => a.Id == DefaultAgentRoster.Orchestrator).AllowedTools);
    }

    [Fact]
    public async Task A_store_that_fails_halfway_puts_back_what_was_already_saved()
    {
        var (_, editor, store) = Create();
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        var judge = store.Roster.Single(a => a.Id == DefaultAgentRoster.Judge);
        store.FailOn = judge.Id;

        var done = await editor.ApplyManyAsync([executor with { Name = "Otro" }, judge with { Name = "Otro juez" }], [], "x");

        Assert.False(done);
        Assert.Equal("Ejecutor", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);
        Assert.Equal(judge.Name, store.Roster.Single(a => a.Id == DefaultAgentRoster.Judge).Name);
        Assert.Contains("the store is down", editor.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_that_needs_to_remove_an_agent_saves_nothing_when_the_store_cannot_remove()
    {
        var (_, editor, store) = Create(canRemove: false);
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);

        var done = await editor.ApplyManyAsync([executor with { Name = "Otro" }], [DefaultAgentRoster.ClaudeJudge], "x");

        Assert.False(done);
        Assert.Equal(0, store.Saved);
        Assert.Equal("Ejecutor", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);
        Assert.Contains("no admite bajas", editor.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Taking_out_the_orchestrator_or_the_last_judge_is_refused_with_the_reason_and_nothing_reaches_the_store()
    {
        var (canvas, editor, store) = Create();

        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Orchestrator));
        await RemoveSelectedAsync(editor);

        Assert.Equal("La plantilla no admite ese cambio: Hace falta el orquestador, que es quien prepara el plan.", editor.ErrorMessage);
        Assert.Null(editor.Notice);
        Assert.Contains(store.Roster, a => a.Id == DefaultAgentRoster.Orchestrator);
        Assert.Empty(store.Operations);
        Assert.Equal(0, store.Saved);

        // A set that changes another agent and drops the orchestrator is refused whole: the executor is not saved either.
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        Assert.False(await editor.ApplyManyAsync([executor with { Name = "Otro" }], [DefaultAgentRoster.Orchestrator], "x"));
        Assert.Empty(store.Operations);
        Assert.Equal("Ejecutor", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);

        // Of the three judges two can go; the last one cannot.
        foreach (var judge in new[] { DefaultAgentRoster.ClaudeJudge, DefaultAgentRoster.RevisionJudge })
        {
            canvas.Select(canvas.Agents.Single(a => a.Id == judge));
            await RemoveSelectedAsync(editor);
            Assert.Null(editor.ErrorMessage);
        }

        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Judge));
        await RemoveSelectedAsync(editor);

        Assert.StartsWith("La plantilla no admite ese cambio: Hace falta al menos un juez", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(store.Roster, a => a.Id == DefaultAgentRoster.Judge);
        Assert.Equal(["remove:juez-claude", "remove:juez-revision"], store.Operations);
    }

    [Fact]
    public async Task Taking_an_agent_out_grants_nothing_so_on_its_own_it_asks_for_no_windows_hello()
    {
        var asked = 0;
        var (canvas, editor, store) = Create(confirm: (_, _, _) =>
        {
            asked++;
            return Task.FromResult(false);
        });
        var hash = RosterHash.Of(store.Roster);
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));

        await RemoveSelectedAsync(editor);

        Assert.Equal(0, asked);
        Assert.Equal(["remove:ejecutor"], store.Operations);
        Assert.NotEqual(hash, store.Hashes.Single());
        Assert.StartsWith("Se quitó ejecutor del equipo.", editor.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain(canvas.Agents, a => a.Id == DefaultAgentRoster.Executor);
    }

    [Fact]
    public async Task A_set_that_grants_power_and_takes_agents_out_asks_windows_hello_once_and_removes_nothing_before_it_is_given()
    {
        var asked = new List<PresenceAction>();
        var answer = false;
        var (_, editor, store) = Create(confirm: (action, _, _) =>
        {
            asked.Add(action);
            return Task.FromResult(answer);
        });
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        var withShell = executor with { AllowedTools = [.. executor.AllowedTools, "Bash"] };
        AgentId[] gone = [DefaultAgentRoster.Architect, DefaultAgentRoster.Critic];

        // Denied: neither the save nor the removals reach the store, and the template is as it was.
        Assert.False(await editor.ApplyManyAsync([withShell], gone, "x"));
        Assert.Equal([PresenceAction.GrantHighRiskCapability], asked);
        Assert.Empty(store.Operations);
        Assert.Contains(store.Roster, a => a.Id == DefaultAgentRoster.Architect);
        Assert.Contains(store.Roster, a => a.Id == DefaultAgentRoster.Critic);
        Assert.DoesNotContain("Bash", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).AllowedTools);
        Assert.Contains("No se confirmó que eres tú", editor.ErrorMessage, StringComparison.Ordinal);

        // Given: one more question for the whole set, the saves first and the removals after them.
        answer = true;
        Assert.True(await editor.ApplyManyAsync([withShell], gone, "x"));
        Assert.Equal(2, asked.Count);
        Assert.Equal(["save:ejecutor", "remove:arquitecto", "remove:critico"], store.Operations);
        Assert.DoesNotContain(store.Roster, a => gone.Contains(a.Id));
        Assert.Contains("Bash", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).AllowedTools);
    }

    [Fact]
    public async Task A_store_that_fails_while_taking_agents_out_puts_back_what_was_saved_and_what_was_already_removed()
    {
        var (_, editor, store) = Create();
        var executor = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);
        store.FailRemoveOn = DefaultAgentRoster.Critic;

        var done = await editor.ApplyManyAsync([executor with { Name = "Otro" }], [DefaultAgentRoster.Architect, DefaultAgentRoster.Critic], "x");

        Assert.False(done);
        Assert.Contains("the store is down", editor.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Ejecutor", store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor).Name);
        Assert.Contains(store.Roster, a => a.Id == DefaultAgentRoster.Architect);
        Assert.Contains(store.Roster, a => a.Id == DefaultAgentRoster.Critic);
        Assert.Equal(["save:ejecutor", "remove:arquitecto", "save:ejecutor", "save:arquitecto"], store.Operations);
    }

    [Fact]
    public async Task Taking_out_an_id_that_is_not_in_the_template_changes_nothing_and_is_not_sent_to_the_store()
    {
        var (_, editor, store) = Create();

        Assert.True(await editor.ApplyManyAsync([], [new AgentId("nadie")], "x"));

        Assert.Empty(store.Operations);
        Assert.Equal(Roster().Count, store.Roster.Count);
    }

    [Fact]
    public async Task A_new_agent_that_grants_risky_tools_or_folders_of_writing_asks_windows_hello_and_one_that_grants_nothing_does_not()
    {
        var asked = new List<PresenceAction>();
        var (_, editor, store) = Create(confirm: (action, _, _) =>
        {
            asked.Add(action);
            return Task.FromResult(false);
        });
        var template = store.Roster.Single(a => a.Id == DefaultAgentRoster.Executor);

        var plain = await editor.ApplyAsync(template with { Id = new AgentId("ejecutor-2"), Name = "Ejecutor 2" }, "x");
        var shell = await editor.ApplyAsync(template with { Id = new AgentId("ejecutor-3"), Name = "Ejecutor 3", AllowedTools = [.. template.AllowedTools, "Bash"] }, "x");
        var folders = await editor.ApplyAsync(template with { Id = new AgentId("ejecutor-4"), Name = "Ejecutor 4", PathGrants = [new PathGrant("src/", FileAccessLevel.ReadWrite)] }, "x");

        Assert.True(plain);
        Assert.False(shell);
        Assert.False(folders);
        Assert.Equal([PresenceAction.GrantHighRiskCapability, PresenceAction.GrantHighRiskCapability], asked);
        Assert.Contains(store.Roster, a => a.Id.Value == "ejecutor-2");
        Assert.DoesNotContain(store.Roster, a => a.Id.Value is "ejecutor-3" or "ejecutor-4");
    }

    [Fact]
    public void The_inspector_shows_only_what_governs_and_marks_what_only_claude_takes()
    {
        var (canvas, editor, _) = Create();
        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Executor));
        var executor = editor.Inspector!;

        Assert.True(executor.IsClaude);
        Assert.True(executor.IsWriter);
        Assert.True(executor.CanReceiveMemory);
        Assert.Equal("Solo Claude Code recibe estos dos límites.", executor.OnlyClaudeText);
        Assert.Equal(["docs/", "src/", "tests/", "todo el proyecto"], executor.Folders.Select(f => f.Label).Order(StringComparer.Ordinal));

        canvas.Select(canvas.Agents.Single(a => a.Id == DefaultAgentRoster.Judge));
        var judge = editor.Inspector!;
        Assert.False(judge.IsClaude);
        Assert.False(judge.CanReceiveMemory);
        Assert.Empty(judge.Folders);
        Assert.All(judge.Tools, t => Assert.False(t.CanEdit));

        var members = typeof(AgentInspectorViewModel).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(members, m => m.Contains("MaxRounds", StringComparison.Ordinal) || m.Contains("MaxTotalTokens", StringComparison.Ordinal));
    }
}
