using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Tests.Providers.Live;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Claude;

/// <summary>
/// The repository is untrusted: hooks in <c>.claude/settings.json</c> / <c>settings.local.json</c> and servers in
/// <c>.mcp.json</c> run with the user's permissions as soon as <c>claude -p</c> starts, with no trust dialog. This real
/// run plants them (each one only creates a sentinel file outside the repository) and launches the real adapter with the
/// read-only profile of the planner. It spends a little subscription quota (haiku, two turns), so it needs
/// AGENTSTUDIO_PROVIDER_TESTS=1.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class ClaudeRepositoryIsolationLiveTests
{
    [Fact]
    public async Task Real_claude_runs_none_of_the_hooks_or_mcp_servers_planted_in_the_repository()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        using var planted = await PlantedRepository.CreateAsync(token);
        planted.PlantClaudeConfiguration();
        File.WriteAllText(Path.Combine(planted.Repository, "nota.txt"), "OK");

        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        var provider = new ClaudeCodeAgentProvider(new ExecutableLocator(), recorder, TimeProvider.System);
        // The planner's profile: read-only, no shell, no network. Reading a file makes PreToolUse fire if hooks are loaded.
        var request = Request(
            Permissions(allowed: ["Read"]),
            prompt: "Usa la herramienta Read para leer el fichero nota.txt y responde solo con su contenido.",
            model: "haiku",
            budget: new ExecutionBudget(4, TimeSpan.FromMinutes(3)),
            workingDirectory: planted.Repository);

        await using var run = await provider.StartAsync(request, token);
        var events = await LiveTestSupport.DrainAsync(run, token);
        var outcome = await run.Completion;
        var sentinels = await planted.SentinelsAfterAsync(TimeSpan.FromSeconds(3), token);

        var process = Assert.Single(recorder.Processes);
        LiveTestSupport.SaveCapture(process, "claude-planted-repository");
        var init = ClaudeInitEvent.Find(process.StandardOutput);
        var hookEvents = process.StandardOutput.Count(ClaudeInitEvent.IsHookEvent);
        LiveTestSupport.Note($"claude planted repo: status {outcome.Status}, answer '{outcome.FinalText}', sentinels [{string.Join(", ", sentinels)}], hook events {hookEvents}");
        LiveTestSupport.Note($"claude planted repo: init.mcp_servers [{string.Join(", ", init?.McpServers.Select(s => $"{s.Name} ({s.Source}, {s.Status})") ?? [])}]");
        LiveTestSupport.Note($"claude planted repo: init.plugins [{string.Join(", ", init?.Plugins.Select(p => $"{p.Name} ({p.Path})") ?? [])}]");
        LiveTestSupport.Note("claude planted repo: " + LiveTestSupport.CommandLine(process.Spec));

        // The run still works: subscription auth, the model and the Read tool (so a PreToolUse hook would have fired).
        Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
        Assert.Contains("OK", outcome.FinalText ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(events.OfType<ToolInvoked>(), invoked => invoked.Tool == "Read");

        // No repository command ran: no sentinel, and no hook event at all (not even the user's global hooks).
        Assert.True(sentinels.Count == 0, "Repository-controlled commands ran: " + string.Join(", ", sentinels));
        Assert.Equal(0, hookEvents);

        // Raw init event: no MCP server (neither .mcp.json nor the claude.ai connectors) and only built-in plugins.
        Assert.NotNull(init);
        Assert.Empty(init.McpServers);
        Assert.All(init.Plugins, plugin => Assert.Equal("builtin", plugin.Path));

        var arguments = process.Spec.Arguments.ToList();
        Assert.Equal(string.Empty, arguments[arguments.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Contains("--settings", arguments);
    }
}
