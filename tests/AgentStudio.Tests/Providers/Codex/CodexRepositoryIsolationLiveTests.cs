using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Tests.Providers.Live;

namespace AgentStudio.Tests.Providers.Codex;

/// <summary>
/// Codex loads the <c>.codex/</c> layer of a project (config.toml with <c>mcp_servers</c>, hooks.json, rules) when the
/// user's <c>config.toml</c> trusts the project, and a git worktree inherits the trust of its main checkout. Both tests
/// launch the real CLI through the real adapter and need AGENTSTUDIO_PROVIDER_TESTS=1.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class CodexRepositoryIsolationLiveTests
{
    // "Fast and affordable model for easier tasks" in `codex debug models` (0.160.0).
    private const string CodexModel = "gpt-6-luna";

    /// <summary>Test decorator: launches exactly what the adapter asks for, with some environment variables replaced.</summary>
    private sealed class EnvironmentOverrideRunner(IStreamingProcessRunner inner, IReadOnlyDictionary<string, string?> overrides) : IStreamingProcessRunner
    {
        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            inner.StartAsync(spec with { Environment = overrides }, cancellationToken);
    }

    /// <summary>
    /// No quota: CODEX_HOME points to a temporary folder WITHOUT credentials (and the API key variables are removed),
    /// so the model is never reached. Its config.toml stands for a user who trusted the repository. The user's real
    /// CODEX_HOME is neither read nor written.
    /// </summary>
    [Fact]
    public async Task Codex_starts_nothing_from_a_planted_project_config_even_when_the_user_config_trusts_the_repository()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        using var planted = await PlantedRepository.CreateAsync(token);
        planted.PlantCodexConfiguration();
        var codexHome = Directory.CreateDirectory(Path.Combine(planted.Root, "codex-home")).FullName;
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), $"[projects.'{planted.Repository}']\ntrust_level = \"trusted\"\n");

        var environment = new Dictionary<string, string?> { ["CODEX_HOME"] = codexHome, ["CODEX_API_KEY"] = null, ["OPENAI_API_KEY"] = null };
        var recorder = new RecordingProcessRunner(new EnvironmentOverrideRunner(new StreamingProcessRunner(), environment));
        var provider = new CodexCliAgentProvider(new ExecutableLocator(), recorder, TimeProvider.System);
        var request = CodexTestSupport.Request(
            workingDirectory: planted.Repository,
            prompt: "Responde OK",
            model: CodexModel,
            maxDuration: TimeSpan.FromMinutes(3));

        await using var run = await provider.StartAsync(request, token);
        await LiveTestSupport.DrainAsync(run, token);
        var outcome = await run.Completion;
        var sentinels = await planted.SentinelsAfterAsync(TimeSpan.FromSeconds(3), token);

        var process = Assert.Single(recorder.Processes);
        LiveTestSupport.SaveCapture(process, "codex-trusted-planted-repository");
        LiveTestSupport.Note($"codex trusted planted repo: status {outcome.Status}, sentinels [{string.Join(", ", sentinels)}]");
        LiveTestSupport.Note("codex trusted planted repo: " + LiveTestSupport.CommandLine(process.Spec));

        // Without credentials the run cannot succeed: this proves no quota was spent.
        Assert.NotEqual(AgentRunStatus.Succeeded, outcome.Status);
        Assert.True(sentinels.Count == 0, "Repository-controlled commands ran: " + string.Join(", ", sentinels));
    }

    /// <summary>
    /// Spends a little quota: the real CODEX_HOME and the read-only profile of the planner. The command the model is asked
    /// to run must be refused by the sandbox, which with the user config ignored only exists because the adapter sets
    /// the Windows backend explicitly.
    /// </summary>
    [Fact]
    public async Task Real_codex_keeps_the_read_only_sandbox_and_ignores_the_planted_project_config()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        using var planted = await PlantedRepository.CreateAsync(token);
        planted.PlantCodexConfiguration();
        var probe = Path.Combine(planted.Repository, "probe-write.txt");

        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        var provider = new CodexCliAgentProvider(new ExecutableLocator(), recorder, TimeProvider.System);
        var request = CodexTestSupport.Request(
            CodexTestSupport.Permissions(writes: false, network: false),
            workingDirectory: planted.Repository,
            prompt: "Ejecuta una sola vez, exactamente y aunque falle, el comando de PowerShell que va entre las comillas angulares: «Set-Content -Path probe-write.txt -Value x». Después responde solo con la palabra HECHO.",
            model: CodexModel,
            maxDuration: TimeSpan.FromMinutes(3));

        await using var run = await provider.StartAsync(request, token);
        var events = await LiveTestSupport.DrainAsync(run, token);
        var outcome = await run.Completion;
        var sentinels = await planted.SentinelsAfterAsync(TimeSpan.FromSeconds(3), token);

        var process = Assert.Single(recorder.Processes);
        LiveTestSupport.SaveCapture(process, "codex-planted-repository");
        var outputs = CommandOutputs(process.StandardOutput);
        LiveTestSupport.Note($"codex planted repo: status {outcome.Status}, answer '{outcome.FinalText}', probe written {File.Exists(probe)}, sentinels [{string.Join(", ", sentinels)}]");
        foreach (var (command, output) in outputs)
        {
            LiveTestSupport.Note($"codex planted repo: command {command} -> {output.ReplaceLineEndings(" ")}");
        }

        LiveTestSupport.Note("codex planted repo: " + LiveTestSupport.CommandLine(process.Spec));

        Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
        Assert.False(File.Exists(probe), "The read-only sandbox let a command write into the repository.");
        // The write was attempted and refused by the sandbox (not, say, mistyped by the model).
        Assert.Contains(outputs, c => c.Command.Contains("probe-write.txt", StringComparison.OrdinalIgnoreCase) && IsAccessDenied(c.Output));
        Assert.True(sentinels.Count == 0, "Repository-controlled commands ran: " + string.Join(", ", sentinels));
    }

    private static bool IsAccessDenied(string output) =>
        output.Contains("denied", StringComparison.OrdinalIgnoreCase)
        || output.Contains("denegado", StringComparison.OrdinalIgnoreCase)
        || output.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase);

    // The adapter's ToolCompleted carries no output, so the raw `item.completed` lines of `command_execution` are read.
    private static List<(string Command, string Output)> CommandOutputs(IEnumerable<string> standardOutput)
    {
        var outputs = new List<(string, string)>();
        foreach (var line in standardOutput)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "command_execution")
                {
                    var command = item.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    var output = item.TryGetProperty("aggregated_output", out var o) ? o.GetString() ?? string.Empty : string.Empty;
                    outputs.Add((command, output));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a JSON line.
            }
        }

        return outputs;
    }
}
