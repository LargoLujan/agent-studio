using System.IO;
using AgentStudio.Application.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Gemini;
using static AgentStudio.Tests.Providers.Gemini.GeminiTestData;

namespace AgentStudio.Tests.Providers.Gemini;

public sealed class GeminiLiveTests
{
    /// <summary>
    /// Real Gemini CLI. Skipped unless AGENTSTUDIO_PROVIDER_TESTS=1. Without a session the CLI exits with 41 and spends no
    /// quota; with a session this sends a tiny prompt (consumes quota).
    /// </summary>
    [Fact]
    [Trait("Category", "ProviderLive")]
    public async Task Real_gemini_run_succeeds_or_reports_missing_session()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("AGENTSTUDIO_PROVIDER_TESTS") == "1", "Set AGENTSTUDIO_PROVIDER_TESTS=1 to run real provider tests.");
        var locator = new ExecutableLocator();
        Assert.SkipUnless(locator.Find("gemini") is not null, "Gemini CLI is not installed.");

        var work = Path.Combine(Path.GetTempPath(), "agentstudio-gemini-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var provider = new GeminiCliAgentProvider(locator, new StreamingProcessRunner());
            await using var run = await provider.StartAsync(
                Request(prompt: "Responde exactamente con la palabra OK y nada más.", instructions: null, workingDirectory: work, maxDuration: TimeSpan.FromMinutes(2)),
                TestContext.Current.CancellationToken);

            var outcome = await run.Completion.WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);

            if (outcome.ExitCode == 41)
            {
                Assert.Equal(AgentRunStatus.Failed, outcome.Status);
                Assert.Equal(GeminiCliAgentProvider.NoSessionMessage, outcome.Error);
            }
            else
            {
                Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
                Assert.False(string.IsNullOrWhiteSpace(outcome.FinalText));
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
