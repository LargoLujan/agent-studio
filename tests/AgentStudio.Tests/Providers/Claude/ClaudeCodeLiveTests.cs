using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Claude;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Claude;

/// <summary>Real runs against the installed CLI. They consume subscription quota, so they need AGENTSTUDIO_PROVIDER_TESTS=1.</summary>
[Trait("Category", "ProviderLive")]
public sealed class ClaudeCodeLiveTests
{
    private static void RequireOptIn()
    {
        if (Environment.GetEnvironmentVariable("AGENTSTUDIO_PROVIDER_TESTS") != "1")
        {
            Assert.Skip("Define AGENTSTUDIO_PROVIDER_TESTS=1 para ejecutar pruebas reales (consumen cuota).");
        }
    }

    private static async Task<(AgentRunOutcome Outcome, List<AgentEvent> Events)> RunAsync(AgentRunRequest request, CancellationToken token)
    {
        var provider = new ClaudeCodeAgentProvider(new ExecutableLocator(), new StreamingProcessRunner(), TimeProvider.System);
        await using var run = await provider.StartAsync(request, token);
        var events = new List<AgentEvent>();
        await foreach (var evt in run.Events.WithCancellation(token))
        {
            events.Add(evt);
        }

        return (await run.Completion, events);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentstudio-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Real_claude_answers_OK()
    {
        RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        var directory = NewTempDirectory();
        try
        {
            var request = Request(
                prompt: "Responde exactamente con la palabra OK y nada más.",
                model: "haiku",
                budget: new AgentStudio.Domain.Agents.ExecutionBudget(2, TimeSpan.FromMinutes(3)),
                workingDirectory: directory);

            var (outcome, events) = await RunAsync(request, token);

            Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
            Assert.Contains("OK", outcome.FinalText, StringComparison.Ordinal);
            Assert.NotEmpty(events.OfType<SessionStarted>());
            Assert.NotEmpty(events.OfType<UsageReported>());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Real_claude_returns_structured_output()
    {
        RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        var directory = NewTempDirectory();
        try
        {
            var schema = """{"type":"object","properties":{"respuesta":{"type":"string"},"numero":{"type":"integer"}},"required":["respuesta","numero"]}""";
            var request = Request(
                prompt: "Devuelve un objeto con respuesta igual a OK y numero igual a 42.",
                model: "haiku",
                schema: schema,
                budget: new AgentStudio.Domain.Agents.ExecutionBudget(4, TimeSpan.FromMinutes(3)),
                workingDirectory: directory);

            var (outcome, _) = await RunAsync(request, token);

            Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
            Assert.NotNull(outcome.StructuredOutputJson);
            using var json = System.Text.Json.JsonDocument.Parse(outcome.StructuredOutputJson);
            Assert.Equal("OK", json.RootElement.GetProperty("respuesta").GetString());
            Assert.Equal(42, json.RootElement.GetProperty("numero").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
