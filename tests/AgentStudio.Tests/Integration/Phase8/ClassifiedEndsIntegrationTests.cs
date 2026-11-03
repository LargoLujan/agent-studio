using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>
/// F8-1 across the packages (docs/16 D-41, ADR-0035): one run per class and per CLI, in which the first attempt ends as a
/// synthetic capture of WP2 says, played by the real adapter of the CLI. Its end reaches the log as
/// <c>AgentRunFinished</c> version 3 with that class (the window's type and reset when the CLI gives them), the journal of
/// the project says the class in its result line, and the engine of WP1 follows the class, not the status: a window or an
/// overload waits and goes on in its session, a context that ran out goes on in a new session with the handoff, a session
/// that expired waits for a person with the command that fixes it, and the network is retried. The log verifies after each.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class ClassifiedEndsIntegrationTests
{
    [Theory]
    [InlineData("claude", "01-ventana-cinco-horas-rechazada.jsonl", AttemptEndClass.WindowQuota, AttemptOutcome.RateLimited)]
    [InlineData("claude", "05-contexto-agotado.jsonl", AttemptEndClass.ContextExhausted, AttemptOutcome.Failed)]
    [InlineData("claude", "06-sesion-caducada.jsonl", AttemptEndClass.SignedOut, AttemptOutcome.Failed)]
    [InlineData("claude", "07-sobrecarga.jsonl", AttemptEndClass.RateOrOverload, AttemptOutcome.Failed)]
    [InlineData("claude", "08-red-sin-conexion.jsonl", AttemptEndClass.Network, AttemptOutcome.Failed)]
    [InlineData("codex", "01-limite-de-uso.jsonl", AttemptEndClass.WindowQuota, AttemptOutcome.RateLimited)]
    [InlineData("codex", "02-contexto-agotado.jsonl", AttemptEndClass.ContextExhausted, AttemptOutcome.Failed)]
    [InlineData("codex", "11-sesion-caducada.jsonl", AttemptEndClass.SignedOut, AttemptOutcome.Failed)]
    [InlineData("codex", "04-limite-de-velocidad.jsonl", AttemptEndClass.RateOrOverload, AttemptOutcome.RateLimited)]
    [InlineData("codex", "06-conexion-fallida.jsonl", AttemptEndClass.Network, AttemptOutcome.Failed)]
    public async Task A_classified_end_lands_in_the_log_as_AgentRunFinished_v3_with_its_class_and_the_engine_follows_it(string cli, string capture, AttemptEndClass expected, AttemptOutcome status)
    {
        var claude = cli == "claude";
        var provider = claude ? ProviderId.ClaudeCode : ProviderId.CodexCli;
        using var repo = TempGitRepo.Create();
        var world = new Phase8World(Phase8Clock.ThreeHoursBeforeReset);
        var clock = world.Clock;
        var fake = claude ? world.Claude : world.CodexExec;
        fake.Turn = launch => launch.Attempt == 1 ? CliTurns.Capture(provider, capture) : CliTurns.Works(launch, ("src/T1/a.txt", "a\n"));
        await using var app = await world.OpenAsync(repo.Root, Roster());

        await using var run = await Phase8Runs.StartAsync(app, clock, Phase8Runs.Single("T1", claude ? "ejecutor" : "ejecutorCodex"), Phase8Runs.NoSteps(), Phase8Runs.Consent);
        var result = await DriveAsync(app, run, clock);

        var entries = await Phase8Log.OfAsync(app, run.Id);
        var ends = entries.Select(e => e.Payload).OfType<AgentRunFinishedPayload>().ToList();
        var end = ends[0];
        Assert.True(end.RecordsEndClass(), $"AgentRunFinished version {end.V}");
        Assert.Equal((status, expected), (end.Status, end.End!.Class));
        Assert.All(ends, e => Assert.True(e.RecordsEndClass(), $"AgentRunFinished version {e.V}"));
        var window = claude && expected == AttemptEndClass.WindowQuota
            ? ("five_hour", (DateTimeOffset?)Phase8Clock.FiveHourReset)
            : (null, null);
        Assert.Equal(window, (end.End.LimitType, end.End.ResetsAt));

        // The journal of the project (ADR-0008) says the class in the result line of the launch, since it says more than the status.
        Assert.Contains(Phase8Log.Journal(repo.Root), line => line.Contains("clase " + expected, StringComparison.Ordinal));

        // What the engine did with it, by the class.
        var story = Phase8Log.Story(entries);
        var launches = fake.Launches;
        var second = launches.Count > 1 ? launches[1] : null;
        switch (expected)
        {
            case AttemptEndClass.WindowQuota or AttemptEndClass.RateOrOverload:
                Assert.Equal(RunPhase.Succeeded, result.Phase);
                Assert.Contains(story, l => l.StartsWith("T1 Running>Blocked RateLimitedUntil", StringComparison.Ordinal));
                Assert.Contains($"check T1 {provider} SessionProbe Ready", story);
                Assert.Equal(0, run.Snapshot().View("T1").RetryCount); // a limit spends no retry
                Assert.Equal(SessionOf(capture, claude), SessionResumedBy(second!, claude)); // the same session goes on
                break;
            case AttemptEndClass.ContextExhausted:
                Assert.Equal(RunPhase.Succeeded, result.Phase);
                Assert.Contains("T1 Running>Failed", story);
                Assert.Null(SessionResumedBy(second!, claude)); // a new session
                Assert.Contains("<handoff task=\"T1\" attempt=\"2\" reason=\"ContextExhausted\">", second!.StandardInput, StringComparison.Ordinal);
                break;
            case AttemptEndClass.SignedOut:
                Assert.Equal(RunPhase.Cancelled, result.Phase);
                Assert.Single(launches); // no retry: only signing in fixes it
                var blocked = entries.Select(e => e.Payload).OfType<TaskStatusChangedPayload>().Single(p => p.To == WorkTaskStatus.Blocked);
                Assert.Equal("AwaitingHuman", blocked.BlockReason);
                Assert.Contains(claude ? "claude auth login" : "codex login", blocked.Reason, StringComparison.Ordinal);
                break;
            case AttemptEndClass.Network:
                Assert.Equal(RunPhase.Succeeded, result.Phase);
                Assert.Contains("T1 Running>Failed", story);
                Assert.Equal(SessionOf(capture, claude), SessionResumedBy(second!, claude)); // retried in its session
                break;
        }

        Phase8Log.AssertEveryLaunchEndedOnce(entries);
        await Phase8Log.AssertVerifiedAsync(app);
    }

    /// <summary>
    /// Takes the run to its end with the clock of the test: a wait for quota is waited for until it ends, and a run that waits
    /// for a person (a session that expired) is cancelled, as the person would do after reading why.
    /// </summary>
    private static async Task<RunResult> DriveAsync(Phase4.Phase4App app, IOrchestrationRun run, Phase8Clock clock)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!run.Completion.IsCompleted)
        {
            if (watch.Elapsed > Phase8Runs.Guard)
            {
                return await Phase8Runs.EndAsync(app, run); // times out with the snapshot and the log
            }

            // The run waits for the clock or for a person, or it ended: only then is there something to do.
            await Phase8Runs.UntilAsync(
                () => run.Completion.IsCompleted || run.Snapshot().Phase is RunPhase.WaitingForQuota or RunPhase.AwaitingHuman,
                "the run to end or to wait for the clock or a person");
            if (run.Completion.IsCompleted)
            {
                break;
            }

            await Phase8Runs.RestingAsync(run);
            var snapshot = run.Snapshot();
            if (snapshot.Phase == RunPhase.WaitingForQuota && Phase8Log.UntilOf(snapshot.View("T1").BlockReason) is { } until)
            {
                clock.AdvanceTo(until);
            }
            else if (snapshot.Phase == RunPhase.AwaitingHuman)
            {
                await run.CancelAsync("Inicio la sesión del CLI y la lanzo otra vez.");
            }

            await Phase8Runs.UntilAsync(() => run.Completion.IsCompleted || !ReferenceEquals(run.Snapshot(), snapshot), "the run to take what it was given");
        }

        return await Phase8Runs.EndAsync(app, run);
    }

    /// <summary>The session a launch resumes: Claude's <c>--resume</c>, or the thread of <c>codex exec resume</c>.</summary>
    private static string? SessionResumedBy(CliLaunch launch, bool claude) => launch.ArgumentAfter(claude ? "--resume" : "resume");

    /// <summary>The session the synthetic capture announces, the one its CLI reported.</summary>
    private static string? SessionOf(string capture, bool claude)
    {
        var lines = claude ? AgentStudio.Tests.Providers.Claude.ClaudeTestSupport.LimitsLines(capture) : AgentStudio.Tests.Providers.Codex.CodexTestSupport.LimitsLines(capture);
        foreach (var line in lines)
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty(claude ? "session_id" : "thread_id", out var session) && session.GetString() is { } id)
            {
                return id;
            }
        }

        return null;
    }
}
