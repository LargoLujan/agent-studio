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

        public List<string> Hashes { get; } = [];

        public Task Save(ProjectContext project, AgentDefinition agent)
        {
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
            Roster.RemoveAll(a => a.Id == id);
            return Task.CompletedTask;
        }

        public Task AfterSave(TeamCanvasViewModel canvas, TeamEditorViewModel editor)
        {
            Saved++;
            Hashes.Add(RosterHash.Of(Roster));
            canvas.Refresh(Roster);
            editor.Load(Project(), Roster);
            return Task.CompletedTask;
        }

        private static IReadOnlyList<AgentDefinition> Roster_() => TeamTestSupport.Roster();
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
        await editor.RemoveSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain(store.Roster, a => a.Id == DefaultAgentRoster.ClaudeJudge);
        Assert.NotEqual(hash, store.Hashes.Single());
        Assert.StartsWith("Se quitó juez-claude del equipo.", editor.Notice, StringComparison.Ordinal);

        var (_, withoutStore, _) = Create(canRemove: false);
        Assert.False(withoutStore.CanRemoveAgents);
        Assert.False(withoutStore.RemoveSelectedCommand.CanExecute(null));
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
