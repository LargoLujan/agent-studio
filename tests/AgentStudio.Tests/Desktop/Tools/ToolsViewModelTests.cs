using System.Globalization;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Tools;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Tests.Desktop.Tools;

/// <summary>
/// F13-18 (docs/17 D-46): «Herramientas» is calculated as the real launch. What each agent may use is read from the arguments
/// <see cref="ClaudeCodeArguments.Build"/> and <see cref="CodexExecArguments.Build"/> produce, so it cannot drift from what the
/// launch does; each tool says its risk and whether giving it asks Windows Hello.
/// </summary>
public sealed class ToolsViewModelTests
{
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    private static AgentDefinition Agent(string id, string name, ProviderId provider, params string[] tools) => new(
        new AgentId(id),
        name,
        AgentRole.Executor,
        new ModelBinding(provider, provider == ProviderId.ClaudeCode ? VendorId.Anthropic : new VendorId("openai"), "model-x"),
        string.Empty,
        [],
        tools,
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(10)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    /// <summary>The launch the engine makes for an executor, written here from its own description: the folder is the test's.</summary>
    private static IReadOnlyList<string> RealClaudeLaunch(AgentDefinition agent, bool writes, string folder)
    {
        var permissions = new PermissionProfile(
            writes,
            writes && agent.AllowedTools.Contains("Bash"),
            false,
            [.. agent.AllowedTools.Where(t => t is not ("Bash" or "WebFetch" or "WebSearch")).Where(t => writes || t is not ("Write" or "Edit"))],
            [],
            []);
        return ClaudeCodeArguments.Build(new AgentRunRequest(agent, string.Empty, folder, permissions, OutputJsonSchema: "{}"), "preview-session", null);
    }

    private static string? After(IReadOnlyList<string> args, string flag) => args.Zip(args.Skip(1)).FirstOrDefault(p => p.First == flag).Second;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_a_claude_agent_may_use_is_what_the_arguments_of_its_launch_say(bool writes)
    {
        var agent = Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read", "Grep", "Glob", "Edit", "Write", "Bash");
        var folder = Path.Combine(Path.GetTempPath(), "tools-compare");

        var real = RealClaudeLaunch(agent, writes, folder);
        var set = EffectiveTools.For(agent, writes);

        Assert.Equal(EffectiveTools.Names(After(real, "--tools")), set.Available);
        Assert.Equal(EffectiveTools.Names(After(real, "--allowedTools")), set.Allowed);
        Assert.Equal(EffectiveTools.Names(After(real, "--disallowedTools")), set.Denied);

        // The launch of the preview is the launch of the engine for another folder: the same arguments but for the folder.
        var preview = ClaudeCodeArguments.Build(EffectiveTools.RequestFor(agent, writes), "preview-session", null);
        Assert.Equal(preview, set.Arguments);
        Assert.Equal(real.Count, set.Arguments.Count);
        Assert.Equal(After(real, "--permission-mode"), After(set.Arguments, "--permission-mode"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_permissions_of_the_view_are_the_rule_of_the_engine_itself_not_a_copy(bool writes)
    {
        var agent = Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read", "Edit", "Write", "Bash", "WebFetch");

        var rule = AgentStudio.Orchestration.Engine.AgentPermissions.For(writes, agent);

        var viewed = EffectiveTools.ProfileOf(agent, writes);
        Assert.Equal((rule.AllowFileWrites, rule.AllowShell, rule.AllowNetwork), (viewed.AllowFileWrites, viewed.AllowShell, viewed.AllowNetwork));
        Assert.Equal(rule.AllowedTools, viewed.AllowedTools);
        Assert.Equal(writes, rule.AllowFileWrites);
        Assert.Equal(writes, rule.AllowShell);
        Assert.False(rule.AllowNetwork);
        Assert.DoesNotContain("WebFetch", rule.AllowedTools);
        Assert.Equal(writes, rule.AllowedTools.Contains("Edit"));
    }

    [Fact]
    public void In_a_task_that_writes_the_agent_has_edit_write_and_a_shell_and_in_one_that_reads_it_has_neither()
    {
        var agent = Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read", "Grep", "Glob", "Edit", "Write", "Bash");

        var writing = EffectiveTools.For(agent, writes: true);
        var reading = EffectiveTools.For(agent, writes: false);

        Assert.Contains("Bash", writing.Available);
        Assert.Contains("Edit", writing.Available);
        Assert.Contains("Write", writing.Available);
        Assert.DoesNotContain("Bash", reading.Available);
        Assert.DoesNotContain("Edit", reading.Available);
        Assert.Contains("Bash", reading.Denied);
        Assert.Contains("Write", reading.Denied);

        // Nobody gets the network, and the launch never allows the tools that leave the worktree.
        Assert.DoesNotContain("WebFetch", writing.Available);
        Assert.Contains("WebFetch", writing.Denied);
        Assert.Contains("WebSearch", writing.Denied);
        Assert.All(ClaudeCodeArguments.OutsideTools, tool => Assert.Contains(tool, writing.Denied));
    }

    [Fact]
    public void The_view_follows_the_template_and_so_the_launch_not_a_copy_of_its_rules()
    {
        var withShell = Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read", "Bash");
        var withoutShell = Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read");
        var model = new ToolsViewModel();

        model.Load([withShell], null);
        var before = model.Groups[0].Rows.First(r => r.Name == "Bash").AgentsText;
        model.Load([withoutShell], null);
        var after = model.Groups[0].Rows.First(r => r.Name == "Bash").AgentsText;

        Assert.Equal("La tienen: Ejecutor 1 (solo en tareas que escriben)", before);
        Assert.Equal("Ningún agente la tiene.", after);

        // A task that writes edits and writes files whatever the template lists: the launch says so, and so does the view.
        Assert.Equal("La tienen: Ejecutor 1 (solo en tareas que escriben)", model.Groups[0].Rows.First(r => r.Name == "Edit").AgentsText);
        Assert.Equal("La tienen: Ejecutor 1", model.Groups[0].Rows.First(r => r.Name == "Read").AgentsText);
    }

    [Fact]
    public void Each_tool_says_what_it_allows_its_risk_and_whether_giving_it_asks_windows_hello()
    {
        var model = new ToolsViewModel();
        model.Load([Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read", "Grep", "Glob", "Edit", "Write", "Bash")], null);
        var rows = model.Groups[0].Rows.ToDictionary(r => r.Name);

        Assert.Equal(["Read", "Grep", "Glob", "Edit", "Write", "Bash", "WebFetch", "WebSearch"], rows.Keys);
        Assert.Equal(ToolRisk.Low, rows["Read"].Risk);
        Assert.False(rows["Read"].AsksHello);
        Assert.Equal("Darla no pide Windows Hello", rows["Read"].HelloText);
        Assert.Equal(ToolRisk.Medium, rows["Edit"].Risk);
        Assert.False(rows["Edit"].AsksHello);
        foreach (var tool in new[] { "Bash", "WebFetch", "WebSearch" })
        {
            Assert.Equal(ToolRisk.High, rows[tool].Risk);
            Assert.True(rows[tool].AsksHello);
            Assert.Equal("Darla pide Windows Hello", rows[tool].HelloText);
        }

        Assert.Equal("Ejecutar órdenes de la consola.", rows["Bash"].Description);
        Assert.Equal("Ningún agente la tiene.", rows["WebFetch"].AgentsText);
        Assert.Contains("Ejecuta órdenes o llega a internet", rows["Bash"].AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Darla pide Windows Hello", rows["Bash"].AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tools_that_ask_windows_hello_are_the_ones_the_analyzer_calls_high_risk()
    {
        var model = new ToolsViewModel();
        model.Load([Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read")], null);

        var asks = model.Groups[0].Rows.Where(r => r.AsksHello).Select(r => r.Name);

        Assert.Equal(AgentStudio.Domain.Orchestration.Analysis.Rules.HighRiskCapabilityRule.HighRiskTools.Order(), asks.Order());
    }

    [Fact]
    public void A_codex_agent_is_told_by_its_sandbox_in_each_mode_and_gets_no_network()
    {
        var agent = Agent("exec2", "Ejecutor 2", ProviderId.CodexCli, "Read", "Edit", "Write");
        var folder = Path.Combine(Path.GetTempPath(), "tools-compare");
        var real = CodexExecArguments.Build(new AgentRunRequest(agent, string.Empty, folder, new PermissionProfile(true, false, false, ["Read", "Edit", "Write"], [], []), OutputJsonSchema: "{}"), "schema.json", isRepository: true);

        var writing = EffectiveTools.For(agent, writes: true);
        var reading = EffectiveTools.For(agent, writes: false);

        Assert.Equal(After(real, "-s"), writing.Sandbox);
        Assert.Equal("workspace-write", writing.Sandbox);
        Assert.Equal("read-only", reading.Sandbox);
        Assert.False(writing.Network);
        Assert.Null(reading.Network);

        var model = new ToolsViewModel();
        model.Load([agent], null);
        var rows = model.Groups[1].Rows.ToDictionary(r => r.Name);
        Assert.Equal("La tienen: Ejecutor 2", rows["read-only"].AgentsText);
        Assert.Equal("La tienen: Ejecutor 2", rows["workspace-write"].AgentsText);
        Assert.Equal("Ningún agente la tiene.", rows["network"].AgentsText);
        Assert.False(rows["read-only"].CanBeGiven);
    }

    [Fact]
    public void A_program_without_a_list_of_tools_is_not_given_one_the_launch_does_not_say()
    {
        var agent = Agent("exec3", "Ejecutor 3", ProviderId.GeminiCli, "Read");

        var set = EffectiveTools.For(agent, writes: true);

        Assert.Empty(set.Available);
        Assert.Empty(set.Allowed);
        Assert.Null(set.Sandbox);
        Assert.Empty(set.Arguments);
    }

    [Fact]
    public void The_steps_of_the_verification_profile_are_listed_as_tools_for_a_task_of_tool()
    {
        var profile = new VerificationProfile("dotnet", [
            new VerificationStep("build", "Compilar", "dotnet", ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(5), ProcessIsolation.None),
            new VerificationStep("test", "Pruebas", "dotnet", ["test"], EvidenceKind.TestRun, TestReportFormat.Trx, "r.trx", TimeSpan.FromMinutes(5)),
        ]);
        var model = new ToolsViewModel();

        model.Load([Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read")], profile);

        var group = model.Groups[2];
        Assert.Equal("Pasos del perfil de verificación (dotnet)", group.Title);
        Assert.Equal(["build", "test"], group.Rows.Select(r => r.Name));
        Assert.Equal("Compilar: dotnet build (sin aislamiento)", group.Rows[0].Description);
        Assert.Equal("Pruebas: dotnet test (en una zona aislada de integridad baja)", group.Rows[1].Description);
        Assert.Equal("Cambiar el perfil pide Windows Hello", group.Rows[0].HelloText);
        Assert.Equal("Úsalo como tarea de herramienta en el plan.", group.Rows[0].AgentsText);
    }

    [Fact]
    public void The_tools_of_the_local_server_say_where_they_are_allowed()
    {
        var model = new ToolsViewModel();
        model.Load([], null);

        var group = model.Groups[^1];

        Assert.Equal([SkillsMcpNames.SearchTool, SkillsMcpNames.RecommendTool, ProjectHistoryNames.Tool], group.Rows.Select(r => r.Name));
        Assert.Contains("Solo en la recomendación con un agente", group.Rows[0].AgentsText, StringComparison.Ordinal);
        Assert.Contains("orquestador que planifica", group.Rows[2].AgentsText, StringComparison.Ordinal);
        Assert.All(group.Rows, r => Assert.Equal(ToolRisk.Low, r.Risk));
        Assert.False(model.HasAgents);
    }

    [Fact]
    public void Giving_a_tool_leads_to_the_inspector_and_never_changes_anything_itself()
    {
        var model = new ToolsViewModel();
        model.Load([Agent("exec1", "Ejecutor 1", ProviderId.ClaudeCode, "Read")], null);
        var asked = new List<string>();
        model.GiveToAgentRequested += (_, tool) => asked.Add(tool);

        model.GiveToAgentCommand.Execute(model.Groups[0].Rows.First(r => r.Name == "Bash"));
        model.GiveToAgentCommand.Execute(model.Groups[1].Rows[0]);
        model.GiveToAgentCommand.Execute(null);

        Assert.Equal(["Bash"], asked);
        Assert.Equal("Dar Bash a un agente", model.Groups[0].Rows.First(r => r.Name == "Bash").GiveName);
    }

    [Fact]
    public void The_texts_are_said_in_english_too()
    {
        var model = new ToolsViewModel(English);

        model.Load([Agent("exec1", "Exec 1", ProviderId.ClaudeCode, "Read", "Bash")], null);

        var bash = model.Groups[0].Rows.First(r => r.Name == "Bash");
        Assert.Equal("Run commands of the shell.", bash.Description);
        Assert.Equal("Giving it asks Windows Hello", bash.HelloText);
        Assert.Equal("Who has it: Exec 1 (only in tasks that write)", bash.AgentsText);
        Assert.Equal("Claude Code: built-in tools", model.Groups[0].Title);
    }
}
