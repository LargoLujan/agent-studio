using System.IO;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Codex;

namespace AgentStudio.Tests.Providers.Codex;

/// <summary>Real Codex runs. They spend subscription quota, so they only run with AGENTSTUDIO_PROVIDER_TESTS=1.</summary>
[Trait("Category", "ProviderLive")]
public sealed class CodexCliLiveTests
{
    private static void RequireOptIn() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("AGENTSTUDIO_PROVIDER_TESTS") == "1", "Set AGENTSTUDIO_PROVIDER_TESTS=1 to run real provider tests.");

    private static CodexCliAgentProvider Provider() => new(new ExecutableLocator(), new StreamingProcessRunner(), TimeProvider.System);

    private static async Task<(AgentRunOutcome Outcome, List<AgentEvent> Events)> RunAsync(AgentRunRequest request)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await Provider().StartAsync(request, ct);
        var events = new List<AgentEvent>();
        await foreach (var e in run.Events.WithCancellation(ct))
        {
            events.Add(e);
        }

        return (await run.Completion, events);
    }

    [Fact]
    public async Task Real_read_only_run_answers_ok()
    {
        RequireOptIn();
        var directory = CodexTestSupport.NewTempDirectory();
        try
        {
            var (outcome, events) = await RunAsync(CodexTestSupport.Request(
                workingDirectory: directory,
                prompt: "Responde exactamente con la palabra OK y nada más.",
                model: null,
                maxDuration: TimeSpan.FromMinutes(3)));

            Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
            Assert.Contains("OK", outcome.FinalText);
            Assert.False(string.IsNullOrEmpty(outcome.SessionId));
            var usage = Assert.Single(events.OfType<UsageReported>()).Usage;
            Assert.True(usage.InputTokens.HasValue);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Real_structured_run_returns_json_matching_the_schema()
    {
        RequireOptIn();
        var directory = CodexTestSupport.NewTempDirectory();
        const string schema = """{"type":"object","properties":{"respuesta":{"type":"string"},"numero":{"type":"integer"}},"required":["respuesta","numero"],"additionalProperties":false}""";
        try
        {
            var (outcome, _) = await RunAsync(CodexTestSupport.Request(
                workingDirectory: directory,
                prompt: "Devuelve un objeto con respuesta igual a OK y numero igual a 42.",
                schema: schema,
                model: null,
                maxDuration: TimeSpan.FromMinutes(3)));

            Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
            Assert.NotNull(outcome.StructuredOutputJson);
            using var json = JsonDocument.Parse(outcome.StructuredOutputJson);
            Assert.Equal("OK", json.RootElement.GetProperty("respuesta").GetString());
            Assert.Equal(42, json.RootElement.GetProperty("numero").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
