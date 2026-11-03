using System.Threading.Channels;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Composition;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Claude;

/// <summary>
/// docs/16 D-41 and F8-1: every attempt of Claude Code ends with a class, which the parser recognises in a capture of its
/// CLI (2.1.290) and never guesses. The classes with no real capture yet have a synthetic one, marked as such
/// (<c>Fixtures/claude-code/2.1.290/limits/README.md</c>), and every signal a class is deduced from is in a capture. The class
/// refines the status and never replaces it (docs/04 §18.1); what the parser does not recognise has none, which is «otra».
/// </summary>
public sealed class ClaudeEndClassTests
{
    private static readonly DateTimeOffset FiveHourReset = DateTimeOffset.FromUnixTimeSeconds(1792084800);

    private static readonly DateTimeOffset WeeklyReset = DateTimeOffset.FromUnixTimeSeconds(1792350000);

    private static List<AgentEvent> Parse(IEnumerable<string> lines)
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        return [.. lines.SelectMany(parser.Parse)];
    }

    private static AgentRunOutcome OutcomeOf(IEnumerable<string> lines) => Assert.Single(Parse(lines).OfType<RunFinished>()).Outcome;

    [Fact]
    public void Every_signal_a_class_is_deduced_from_is_in_a_capture_of_this_version()
    {
        var captures = CaptureTexts();

        Assert.NotEmpty(ClaudeStreamJsonParser.EndClassSignals);
        Assert.All(ClaudeStreamJsonParser.EndClassSignals, signal =>
            Assert.True(captures.Any(c => c.Text.Contains(signal, StringComparison.Ordinal)), $"«{signal}» is in no capture of Claude Code {FixtureVersion}."));
    }

    [Theory]
    [InlineData("01-texto.jsonl")]
    [InlineData("02-herramienta.jsonl")]
    [InlineData("03-estructurada.json")]
    [InlineData("04-skill-plugin.jsonl")]
    public void A_turn_that_completed_has_no_class_of_its_own_and_its_success_implies_completed(string name)
    {
        var outcome = OutcomeOf(FixtureLines(name));

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Null(outcome.End);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.Completed), outcome.Classified());
    }

    [Fact]
    public void A_five_hour_window_that_ran_out_is_a_window_quota_with_its_type_and_its_reset()
    {
        var events = Parse(LimitsLines("01-ventana-cinco-horas-rechazada.jsonl"));

        var signal = Assert.Single(events.OfType<RateLimitSignaled>());
        Assert.Equal(("rejected", "five_hour", (DateTimeOffset?)FiveHourReset), (signal.Status, signal.LimitType, signal.ResetsAt));
        var outcome = Assert.Single(events.OfType<RunFinished>()).Outcome;
        Assert.Equal(AgentRunStatus.RateLimited, outcome.Status);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", FiveHourReset), outcome.End);
    }

    [Fact]
    public void The_weekly_window_is_told_apart_from_the_five_hour_one()
    {
        var weekly = OutcomeOf(LimitsLines("02-ventana-semanal-rechazada.jsonl"));
        var fiveHour = OutcomeOf(LimitsLines("01-ventana-cinco-horas-rechazada.jsonl"));

        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, "seven_day", WeeklyReset), weekly.End);
        Assert.NotEqual(fiveHour.End!.LimitType, weekly.End!.LimitType);
        Assert.NotEqual(fiveHour.End.ResetsAt, weekly.End.ResetsAt);
    }

    [Fact]
    public void A_window_that_is_allowed_names_its_type_but_signals_no_limit()
    {
        // The real captures: every run starts with the five-hour window allowed (docs/14 §3.1).
        var events = Parse(FixtureLines("04-skill-plugin.jsonl"));

        Assert.Empty(events.OfType<RateLimitSignaled>());
        Assert.Null(Assert.Single(events.OfType<RunFinished>()).Outcome.End);
    }

    [Theory]
    [InlineData("03-max-turnos.jsonl", AttemptBudgetKind.Turns)]
    [InlineData("04-max-presupuesto.jsonl", AttemptBudgetKind.Cost)]
    public void A_budget_of_the_attempt_says_which_one_ended_it(string name, AttemptBudgetKind budget)
    {
        var outcome = OutcomeOf(LimitsLines(name));

        Assert.Equal(AgentRunStatus.LimitReached, outcome.Status);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.AttemptBudget, Budget: budget), outcome.End);
    }

    [Theory]
    [InlineData("05-contexto-agotado.jsonl", AttemptEndClass.ContextExhausted)]
    [InlineData("06-sesion-caducada.jsonl", AttemptEndClass.SignedOut)]
    [InlineData("07-sobrecarga.jsonl", AttemptEndClass.RateOrOverload)]
    [InlineData("08-red-sin-conexion.jsonl", AttemptEndClass.Network)]
    [InlineData("09-red-tiempo-agotado.jsonl", AttemptEndClass.Network)]
    public void A_failure_whose_cause_the_cli_says_stays_a_failure_and_gains_its_class(string name, AttemptEndClass expected)
    {
        var outcome = OutcomeOf(LimitsLines(name));

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(new AgentRunEnd(expected), outcome.End);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    /// <summary>
    /// The class refines the status and never changes it: a window that ran out and an error without the 429 (the text of the
    /// limit does not look like one to the status rule) stays a failure, with the class of the window. The engine has to
    /// decide by the class (WP1), not by the status.
    /// </summary>
    [Fact]
    public void A_window_that_ran_out_without_a_429_stays_a_failure_with_the_class_of_the_window()
    {
        var outcome = OutcomeOf(
        [
            .. LimitsLines("01-ventana-cinco-horas-rechazada.jsonl").Take(1),
            """{"type":"result","subtype":"success","is_error":true,"result":"You've hit your session limit · resets 3:45pm","session_id":"s"}""",
        ]);

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", FiveHourReset), outcome.End);
    }

    [Fact]
    public void A_rate_limit_without_a_window_that_ran_out_is_a_limit_of_a_kind_the_parser_cannot_tell()
    {
        // A 429 and no rejected window: the plan limit or a throttling, and the CLI did not say which (docs/04 §18.1).
        var outcome = OutcomeOf(["""{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"Too many requests"}"""]);

        Assert.Equal(AgentRunStatus.RateLimited, outcome.Status);
        Assert.Null(outcome.End);
        Assert.Equal(new AgentRunEnd(AttemptEndClass.Other), outcome.Classified());
    }

    [Fact]
    public void A_warning_that_a_window_is_nearly_spent_is_not_a_window_that_ran_out()
    {
        var events = Parse(
        [
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1792084800,"rateLimitType":"five_hour"}}""",
            """{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"Too many requests"}""",
        ]);

        Assert.Equal("five_hour", Assert.Single(events.OfType<RateLimitSignaled>()).LimitType);
        Assert.Null(Assert.Single(events.OfType<RunFinished>()).Outcome.End);
    }

    [Fact]
    public void A_window_allowed_again_before_the_end_is_no_longer_its_cause()
    {
        var outcome = OutcomeOf(
        [
            .. LimitsLines("01-ventana-cinco-horas-rechazada.jsonl").Take(1),
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792102800,"rateLimitType":"five_hour"}}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"boom"}""",
        ]);

        Assert.Null(outcome.End);
    }

    [Fact]
    public void A_turn_that_completes_after_a_window_was_rejected_has_no_class()
    {
        var outcome = OutcomeOf(
        [
            .. LimitsLines("01-ventana-cinco-horas-rechazada.jsonl").Take(1),
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","session_id":"s"}""",
        ]);

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Null(outcome.End);
    }

    [Fact]
    public void The_final_text_of_a_turn_that_completed_never_classifies_it()
    {
        var outcome = OutcomeOf(["""{"type":"result","subtype":"success","is_error":false,"result":"The test said: Request timed out. Unable to connect to API.","session_id":"s"}"""]);

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Null(outcome.End);
    }

    [Fact]
    public void The_error_of_a_subagent_is_not_the_cause_of_the_end_of_the_session()
    {
        var outcome = OutcomeOf(
        [
            """{"type":"assistant","parent_tool_use_id":"toolu_1","message":{"id":"msg_sub","content":[{"type":"text","text":"x"}]},"error":"authentication_failed"}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"boom"}""",
        ]);

        Assert.Equal(AgentRunStatus.Failed, outcome.Status);
        Assert.Null(outcome.End);
    }

    [Fact]
    public void Only_the_error_of_the_last_message_of_the_main_loop_counts()
    {
        var outcome = OutcomeOf(
        [
            .. LimitsLines("07-sobrecarga.jsonl").Skip(1).Take(1),
            """{"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"sigo"}]},"parent_tool_use_id":null}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"boom"}""",
        ]);

        Assert.Null(outcome.End);
    }

    [Theory]
    [InlineData("billing_error")]
    [InlineData("rate_limit")]
    [InlineData("unknown")]
    [InlineData("Not A Token")]
    public void An_error_the_parser_has_no_capture_for_is_other(string error)
    {
        var outcome = OutcomeOf(
        [
            $$"""{"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"x"}]},"parent_tool_use_id":null,"error":"{{error}}"}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"boom"}""",
        ]);

        Assert.Null(outcome.End);
        Assert.Equal(AttemptEndClass.Other, outcome.Classified().Class);
    }

    [Fact]
    public void A_window_type_that_is_not_a_token_is_left_out_and_the_window_still_ran_out()
    {
        var outcome = OutcomeOf(
        [
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792084800,"rateLimitType":"<b>five hour</b>"}}""",
            """{"type":"result","subtype":"success","is_error":true,"api_error_status":429,"result":"limit"}""",
        ]);

        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, null, FiveHourReset), outcome.End);
    }

    [Fact]
    public async Task The_class_reaches_the_outcome_of_the_adapter_and_the_result_line_of_the_journal()
    {
        var token = TestContext.Current.CancellationToken;
        var journal = new RecordingJournal();
        var session = new ScriptedSession();
        var provider = AgentProviderPipeline.Compose(new ClaudeCodeAgentProvider(new Locator(), new Runner(session), TimeProvider.System), journal, TimeProvider.System);

        AgentRunOutcome outcome;
        await using (var run = await provider.StartAsync(Request(), token))
        {
            session.Play(LimitsLines("01-ventana-cinco-horas-rechazada.jsonl"), exit: 1);
            outcome = await run.Completion;
        }

        Assert.Equal(new AgentRunEnd(AttemptEndClass.WindowQuota, "five_hour", FiveHourReset), outcome.End);
        var line = Assert.Single(journal.Outcomes);
        Assert.Equal(AgentRunStatus.RateLimited, line.Status);
        Assert.Equal("clase WindowQuota five_hour, reinicio 2026-10-15 17:20 UTC · You've hit your session limit · resets 3:45pm", line.Note);
    }

    [Fact]
    public async Task A_turn_that_completed_adds_nothing_to_the_result_line_of_the_journal()
    {
        var token = TestContext.Current.CancellationToken;
        var journal = new RecordingJournal();
        var session = new ScriptedSession();
        var provider = AgentProviderPipeline.Compose(new ClaudeCodeAgentProvider(new Locator(), new Runner(session), TimeProvider.System), journal, TimeProvider.System);

        await using (var run = await provider.StartAsync(Request(), token))
        {
            session.Play(FixtureLines("01-texto.jsonl"), exit: 0);
            await run.Completion;
        }

        Assert.Null(Assert.Single(journal.Outcomes).Note);
    }

    private sealed class RecordingJournal : IPromptJournal
    {
        private readonly Lock _gate = new();
        private readonly List<PromptOutcome> _outcomes = [];

        public IReadOnlyList<PromptOutcome> Outcomes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _outcomes];
                }
            }
        }

        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct) =>
            Task.FromResult(new JournalEntryId(new DateOnly(2026, 10, 21), 1));

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct)
        {
            lock (_gate)
            {
                _outcomes.Add(outcome);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(string command) => "C:/bin/claude.exe";
    }

    private sealed class Runner(ScriptedSession session) : IStreamingProcessRunner
    {
        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) => Task.FromResult<IProcessSession>(session);
    }

    private sealed class ScriptedSession : IProcessSession
    {
        private readonly Channel<ProcessOutputLine> _lines = Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 1;

        public IAsyncEnumerable<ProcessOutputLine> Output => _lines.Reader.ReadAllAsync();

        public Task<int> Exited => _exited.Task;

        public void Play(IEnumerable<string> stdout, int exit)
        {
            foreach (var line in stdout)
            {
                _lines.Writer.TryWrite(new ProcessOutputLine(ProcessStream.StandardOutput, line, DateTimeOffset.UtcNow));
            }

            End(exit);
        }

        public Task KillAsync()
        {
            End(-1);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            End(-1);
            return ValueTask.CompletedTask;
        }

        private void End(int exit)
        {
            _lines.Writer.TryComplete();
            _exited.TrySetResult(exit);
        }
    }
}
