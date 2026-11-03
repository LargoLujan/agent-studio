using System.Runtime.CompilerServices;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Providers.Cli.Codex;
using static AgentStudio.Tests.Providers.Codex.CodexTestSupport;

namespace AgentStudio.Tests.Providers.Codex;

/// <summary>
/// docs/16 D-41 and F8-1: every attempt of <c>codex exec</c> ends with a class, which the parser recognises in a capture of
/// its CLI (0.160.0) and never guesses. <c>exec --json</c> says why a turn failed only in the text of its error, so the class
/// comes from fragments of the texts of codex-rs 0.160.0, each in a synthetic capture marked as such
/// (<c>Fixtures/codex-cli/0.160.0/limits/README.md</c>). The class refines the status and never replaces it; a window of
/// <c>exec</c> has neither a type nor a reset (docs/04 §18.1), and what the parser does not recognise is «otra».
/// </summary>
public sealed class CodexEndClassTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static List<AgentEvent> Parse(IEnumerable<string> lines)
    {
        var parser = new CodexExecJsonParser("gpt-x");
        return [.. lines.SelectMany(line => parser.Parse(line, Now))];
    }

    private static AgentRunOutcome OutcomeOf(IEnumerable<string> lines) => Assert.Single(Parse(lines).OfType<RunFinished>()).Outcome;

    [Fact]
    public void Every_fragment_a_class_is_deduced_from_is_in_a_capture_of_this_version()
    {
        var captures = CaptureTexts();

        Assert.NotEmpty(CodexExecJsonParser.EndClassSignals);
        Assert.All(CodexExecJsonParser.EndClassSignals, signal =>
            Assert.True(captures.Any(c => c.Text.Contains(signal, StringComparison.Ordinal)), $"«{signal}» is in no capture of Codex CLI {FixtureVersion}."));
    }

    [Theory]
    [InlineData("01-texto.jsonl")]
    [InlineData("02-herramienta.jsonl")]
    [InlineData("03-estructurada.jsonl")]
    [InlineData("04-skill-agents.jsonl")]
    public void A_turn_that_completed_has_no_class_of_its_own_and_its_success_implies_completed(string name)
    {
        var outcome = OutcomeOf(FixtureLines(name));

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Null(outcome.End);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.Completed), outcome.Classified());
    }

    [Fact]
    public void A_usage_limit_is_a_window_quota_with_neither_a_type_nor_a_reset()
    {
        var events = Parse(LimitsLines("01-limite-de-uso.jsonl"));

        var signal = Assert.Single(events.OfType<RateLimitSignaled>());
        Assert.Null(signal.LimitType);
        Assert.Null(signal.ResetsAt);
        var outcome = Assert.Single(events.OfType<RunFinished>()).Outcome;
        Assert.Equal(AgentRunStatus.RateLimited, outcome.Status);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota), outcome.End);
    }

    [Theory]
    [InlineData("02-contexto-agotado.jsonl", AgentRunStatus.Failed, AttemptEndClass.ContextExhausted)]
    [InlineData("03-sin-capacidad.jsonl", AgentRunStatus.Failed, AttemptEndClass.RateOrOverload)]
    [InlineData("04-limite-de-velocidad.jsonl", AgentRunStatus.RateLimited, AttemptEndClass.RateOrOverload)]
    [InlineData("05-reintentos-agotados.jsonl", AgentRunStatus.RateLimited, AttemptEndClass.RateOrOverload)]
    [InlineData("06-conexion-fallida.jsonl", AgentRunStatus.Failed, AttemptEndClass.Network)]
    [InlineData("07-tiempo-agotado.jsonl", AgentRunStatus.Failed, AttemptEndClass.Network)]
    [InlineData("08-respuesta-interrumpida.jsonl", AgentRunStatus.Failed, AttemptEndClass.Network)]
    [InlineData("09-stream-desconectado.jsonl", AgentRunStatus.Failed, AttemptEndClass.Network)]
    [InlineData("11-sesion-caducada.jsonl", AgentRunStatus.Failed, AttemptEndClass.SignedOut)]
    public void A_failed_turn_keeps_its_status_and_gains_the_class_its_error_says(string name, AgentRunStatus status, AttemptEndClass expected)
    {
        var outcome = OutcomeOf(LimitsLines(name));

        Assert.Equal(status, outcome.Status);
        Assert.Equal(new AgentRunEnd(expected), outcome.End);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    [Fact]
    public void A_stream_cut_by_the_content_filter_is_not_the_network()
    {
        var outcome = OutcomeOf(LimitsLines("10-filtro-de-contenido.jsonl"));

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(AttemptEndClass.Other, outcome.Classified().Class);
    }

    /// <summary>
    /// The usage errors of codex-rs 0.160.0 that are not a window (<c>codex-rs/protocol/src/error.rs</c>): waiting for a reset does
    /// not fix them, so they are not a window quota. They are «otra», which is what a text without a fragment always is.
    /// </summary>
    [Theory]
    [InlineData("Your workspace is out of credits. Add credits to continue.")]
    [InlineData("You hit your spend cap set in your workspace. Increase your spend cap to continue.")]
    [InlineData("Quota exceeded. Check your plan and billing details.")]
    [InlineData("To use Codex with your ChatGPT plan, upgrade to Plus: https://chatgpt.com/explore/plus.")]
    [InlineData("We’re currently experiencing high demand, which may cause temporary errors.")]
    [InlineData("turn failed")]
    public void An_error_that_is_not_a_window_nor_any_other_class_is_other(string message)
    {
        var outcome = OutcomeOf(["{\"type\":\"turn.failed\",\"error\":{\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}}"]);

        Assert.Null(outcome.End);
        Assert.Equal(AttemptEndClass.Other, outcome.Classified().Class);
    }

    [Fact]
    public void A_usage_limit_with_the_plain_apostrophe_is_the_same_window()
    {
        // The texts of error.rs use the typographic apostrophe («You’ve»); the fragment does not depend on it.
        var outcome = OutcomeOf(["""{"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again later."}}"""]);

        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota), outcome.End);
    }

    [Fact]
    public void A_run_that_ends_without_completing_its_turn_is_classified_by_the_same_fragments()
    {
        // The adapter's path when the process ends without turn.completed: the last error and the tail of stderr.
        var parser = new CodexExecJsonParser();

        var finished = Assert.Single(parser.FailureEvents(Now, "Codex exited with code 1 without completing the turn. Last error: Connection failed: SINTETICO stderr: x").OfType<RunFinished>());

        Assert.Equal(new AgentRunEnd(AttemptEndClass.Network), finished.Outcome.End);
    }

    [Fact]
    public async Task The_adapter_keeps_the_class_when_the_process_ends_without_completing_its_turn()
    {
        var session = new ScriptedSession(
        [
            new ProcessOutputLine(ProcessStream.StandardOutput, LimitsLines("02-contexto-agotado.jsonl")[0], Now),
            new ProcessOutputLine(ProcessStream.StandardOutput, """{"type":"error","message":"Codex ran out of room in the model's context window. Start a new thread or clear earlier history before retrying."}""", Now),
        ],
            exit: 1);
        var provider = new CodexCliAgentProvider(new Locator(), new Runner(session), TimeProvider.System);

        await using var run = await provider.StartAsync(Request(), TestContext.Current.CancellationToken);
        var outcome = await run.Completion;

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.ContextExhausted), outcome.End);
    }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(string command) => "C:/bin/codex.exe";
    }

    private sealed class Runner(ScriptedSession session) : IStreamingProcessRunner
    {
        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) => Task.FromResult<IProcessSession>(session);
    }

    private sealed class ScriptedSession(IReadOnlyList<ProcessOutputLine> lines, int exit) : IProcessSession
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 1;

        public IAsyncEnumerable<ProcessOutputLine> Output => Play();

        public Task<int> Exited => _exited.Task;

        public Task KillAsync()
        {
            _exited.TrySetResult(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult(-1);
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<ProcessOutputLine> Play([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in lines)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
            }

            _exited.TrySetResult(exit);
        }
    }
}
