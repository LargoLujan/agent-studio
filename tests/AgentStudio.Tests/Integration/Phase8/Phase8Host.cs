using System.Globalization;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase8;

/// <summary>
/// The clock of a Phase 8 app (docs/16 D-40): awake time goes by on a <see cref="FakeTimeProvider"/>, which also runs every
/// timer of the app (the heartbeat of the loop, the deadline of an attempt), and a sleep moves only the wall clock
/// (<see cref="SuspendableClock"/> of the engine tests, which behaves as the timer queue of .NET 10 does on Windows). The
/// timestamp is the awake clock (the app's <c>AwakeTimeProvider</c>, QueryUnbiasedInterruptTime) unless a test asks for one
/// that counts sleep, as QueryPerformanceCounter does. Nothing waits in real time: a test moves the clock.
/// </summary>
internal sealed class Phase8Clock
{
    /// <summary>The reset of the five-hour window of the synthetic capture <c>01-ventana-cinco-horas-rechazada</c> (2026-10-15 17:20 UTC).</summary>
    public static readonly DateTimeOffset FiveHourReset = DateTimeOffset.FromUnixTimeSeconds(1792084800);

    /// <summary>The reset of the weekly window of the synthetic capture <c>02-ventana-semanal-rechazada</c> (2026-10-18 19:00 UTC).</summary>
    public static readonly DateTimeOffset WeeklyReset = DateTimeOffset.FromUnixTimeSeconds(1792350000);

    /// <summary>Three hours before <see cref="FiveHourReset"/>: a window of Claude that ran out takes that long to reset.</summary>
    public static readonly DateTimeOffset ThreeHoursBeforeReset = FiveHourReset - TimeSpan.FromHours(3);

    public Phase8Clock(DateTimeOffset start, bool timestampCountsSleep = false)
    {
        Awake = new FakeTimeProvider(start);
        Time = new SuspendableClock(Awake, timestampCountsSleep);
    }

    /// <summary>The awake time of the machine, which runs the timers.</summary>
    public FakeTimeProvider Awake { get; }

    /// <summary>The <see cref="TimeProvider"/> of the app.</summary>
    public SuspendableClock Time { get; }

    /// <summary>The wall clock.</summary>
    public DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>Awake time goes by: the wall clock with it, and the timers due fire.</summary>
    public void Advance(TimeSpan duration) => Awake.Advance(duration);

    /// <summary>Awake time goes by until the wall clock says <paramref name="at"/>.</summary>
    public void AdvanceTo(DateTimeOffset at)
    {
        var left = at - Now;
        if (left > TimeSpan.Zero)
        {
            Awake.Advance(left);
        }
    }

    /// <summary>The machine sleeps <paramref name="duration"/> and wakes: only the wall clock moves, and no timer fires.</summary>
    public void Sleep(TimeSpan duration) => Time.Sleep(duration);
}

/// <summary>
/// The session probe of a CLI (<c>claude auth status</c>, <c>codex login status</c>) as a test answers it: nothing is run.
/// The real probes of the composition would run the installed CLI; a provider without a probe is never checked.
/// </summary>
internal sealed class FakeSessionProbe(ProviderId provider) : IProviderProbe
{
    private int _calls;

    public ProviderId Provider => provider;

    public VendorId Vendor => provider == ProviderId.CodexCli ? VendorId.OpenAI : VendorId.Anthropic;

    public string DisplayName => provider == ProviderId.CodexCli ? "Codex CLI" : "Claude Code";

    /// <summary>What the probe says of the session now.</summary>
    public AuthenticationState State { get; set; } = AuthenticationState.Authenticated;

    public int Calls => Volatile.Read(ref _calls);

    public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new ProviderProbeResult(provider, DisplayName, InstallationState.Installed, "fake-cli.exe", "0.0.0", State, null, []));
    }
}

/// <summary>
/// The read of the Codex quota through its app-server (ADR-0020) as a test answers it: by default the user did not opt in
/// (<see cref="QuotaReadStatus.NotOptedIn"/>), which is the composition's default and launches nothing.
/// </summary>
internal sealed class FakeQuotaRead(ProviderId provider) : IProviderQuotaReader
{
    private int _calls;

    public ProviderId Provider => provider;

    /// <summary>The answer to the n-th read (from 1).</summary>
    public Func<int, QuotaReadResult> Answer { get; set; } = _ => new QuotaReadResult(QuotaReadStatus.NotOptedIn, []);

    public int Calls => Volatile.Read(ref _calls);

    public Task<QuotaReadResult> ReadAsync(CancellationToken ct) => Task.FromResult(Answer(Interlocked.Increment(ref _calls)));

    /// <summary>A read with the opt-in: the primary window is full and resets at <paramref name="resetsAt"/>.</summary>
    public static QuotaReadResult StillLimited(DateTimeOffset resetsAt, DateTimeOffset observedAt) =>
        new(QuotaReadStatus.Read, [new QuotaObservation(ProviderId.CodexCli, "primary", AgentStudio.Domain.Metrics.MetricValue<double>.Exact(1, "prueba"), TimeSpan.FromHours(5), resetsAt, observedAt, "prueba") { Reached = "primary" }])
        {
            OrdinaryUsageAllowed = false,
        };
}

/// <summary>
/// Everything one test shares across the instances of the app it opens on its project: the clock, the CLIs, the session
/// probes, the read of the Codex quota, the power events and the person behind the dialogs. The CLIs are the fake ones
/// (<see cref="FakeCli"/>) except the judge's, when the run is reviewed: then Codex is the scripted CLI of Phase 5
/// (<see cref="Phase5Clis"/>), which answers a judge from the dossier in its prompt (one adapter per provider: the engine
/// takes the first of each).
/// </summary>
internal sealed class Phase8World
{
    public Phase8World(DateTimeOffset start, bool reviewed = false, bool timestampCountsSleep = false, bool powerEvents = false)
    {
        Clock = new Phase8Clock(start, timestampCountsSleep);
        Claude = FakeCli.Claude(Clock.Time);
        CodexExec = FakeCli.Codex(Clock.Time);
        Judges = new Phase5Clis();
        Reviewed = reviewed;
        Power = powerEvents ? new FakePowerEvents() : null;
    }

    public Phase8Clock Clock { get; }

    /// <summary>Claude Code: its real adapter over a fake process.</summary>
    public FakeCli Claude { get; }

    /// <summary><c>codex exec</c>: its real adapter over a fake process, for the runs without a judge.</summary>
    public FakeCli CodexExec { get; }

    /// <summary>The scripted CLIs of Phase 5: their Codex answers the judge of a reviewed run.</summary>
    public Phase5Clis Judges { get; }

    /// <summary>The runs of this test are reviewed by Codex's judge: Codex is <see cref="Judges"/>, not <see cref="CodexExec"/>.</summary>
    public bool Reviewed { get; }

    public FakeSessionProbe ClaudeProbe { get; } = new(ProviderId.ClaudeCode);

    public FakeSessionProbe CodexProbe { get; } = new(ProviderId.CodexCli);

    public FakeQuotaRead CodexQuota { get; } = new(ProviderId.CodexCli);

    public FakePowerEvents? Power { get; }

    public ScriptedApprovalPrompt Person { get; } = new();

    /// <summary>
    /// One instance of the app on <paramref name="repository"/>, composed as Phase 5's with what Phase 8 needs only a
    /// composition root to give it: the clock, the session probes, the read of the Codex quota and the power events. The
    /// first instance saves the agent template; a later one finds it in the project.
    /// </summary>
    public Task<Phase4App> OpenAsync(string repository, IReadOnlyList<AgentDefinition>? roster = null)
    {
        var codex = Reviewed ? (IAgentProvider)Judges.Codex : CodexExec;
        var services = Phase5Composition.Services(Phase4Paths.Beside(repository), Person, Phase4ScriptedProviders.Over(Claude, codex));
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock.Time));

        // The real probes would run the installed CLIs (claude auth status, codex login status): never in these tests.
        services.RemoveAll<IProviderProbe>();
        services.AddSingleton<IProviderProbe>(ClaudeProbe);
        services.AddSingleton<IProviderProbe>(CodexProbe);
        services.RemoveAll<IProviderQuotaReader>();
        services.AddSingleton<IProviderQuotaReader>(CodexQuota);
        if (Power is { } power)
        {
            services.AddSingleton<IPowerEvents>(power);
        }

        return Phase4App.OpenAsync(repository, services, roster);
    }
}

/// <summary>The runs of the Phase 8 tests: what they are asked with, and how they are waited for.</summary>
internal static class Phase8Runs
{
    /// <summary>The consent of D-39 in the header, with the engine's margin and cap.</summary>
    public static readonly RunOptions Consent = new(AutoResumeConsent: true);

    /// <summary>
    /// The longest a test waits for one thing to happen, in real time. The steps that run here (git, SQLite, pwsh with the
    /// check of Phase 5) take seconds; the machine is shared with other suites.
    /// </summary>
    public static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    /// <summary>How long the snapshot of a loop must stay the same, in real time, for the loop to be taken as resting on its timer.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>No verification: the runs that only exercise the engine (no pwsh, seconds).</summary>
    public static VerificationProfile NoSteps() => new("fase 8 sin pasos", []);

    /// <summary>
    /// The trusted step of Phase 5 (the check of <c>src/feature.txt</c>, which reports in JUnit), run in the worktree at the
    /// integrity of the app (ADR-0017, <see cref="ProcessIsolation.None"/>): what is under test is not the isolation, and the
    /// machine-wide lock of the isolated steps is shared with the other suites. The plan warning that this gives is approved
    /// with the plan, as the canvas shows it.
    /// </summary>
    public static VerificationProfile Checked() =>
        new("fase 8", [Phase3Repos.ScriptStep("check", "Comprobar la función", "check.ps1", TimeSpan.FromMinutes(3)) with { Isolation = ProcessIsolation.None }]);

    /// <summary>One task of Claude's executor that writes <c>src/T1/</c>, with one retry.</summary>
    public static PlanDefinition Single(string task = "T1", string agent = "ejecutor") => Plan(Work(task, agent));

    /// <summary>
    /// The request the canvas sends once the person approved <paramref name="plan"/> (Phase 4's), with the options of the
    /// header and the approval at the time of the app's clock.
    /// </summary>
    public static async Task<RunRequest> RequestAsync(Phase4App app, Phase8Clock clock, PlanDefinition plan, VerificationProfile profile, RunOptions options)
    {
        var request = await app.RunRequestAsync("Añade la función y que el juez la revise.", Requirements(), plan, profile);
        return request with { Options = options, PlanApproval = request.PlanApproval with { At = clock.Now } };
    }

    public static async Task<IOrchestrationRun> StartAsync(Phase4App app, Phase8Clock clock, PlanDefinition plan, VerificationProfile profile, RunOptions options) =>
        await app.Orchestrator.StartAsync(await RequestAsync(app, clock, plan, profile, options), Ct);

    /// <summary>The scheduler of the resumes by consent, as the composition builds it over the engine of <paramref name="app"/>.</summary>
    public static ResumeScheduler Scheduler(Phase4App app, IRunRegistry? runs = null) =>
        new(app.Orchestrator, app.Get<IDecisionRecorder>(), runs ?? app.Get<IRunRegistry>());

    /// <summary>
    /// The app dies with <paramref name="run"/> going (F8-4): the run is disposed, which kills the processes it started and
    /// records nothing, not even a <c>RunExitChosen</c>. Returns the sequence of the last entry of the log, the cut.
    /// </summary>
    public static async Task<long> DieAsync(Phase4App app, IOrchestrationRun run)
    {
        await run.DisposeAsync();
        var interrupted = await run.Completion.WaitAsync(Guard, Ct);
        Assert.Equal(RunPhase.Interrupted, interrupted.Phase);
        Assert.Empty(await Phase8Log.OfAsync<RunFinishedPayload>(app, run.Id));
        return (await app.LoadLogAsync()).Entries[^1].Sequence;
    }

    /// <summary>Polls, in real time, until <paramref name="condition"/> holds of the run; fails with its snapshot and its log.</summary>
    public static async Task UntilAsync(Phase4App app, IOrchestrationRun run, Func<RunSnapshot, bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition(run.Snapshot()))
        {
            if (watch.Elapsed > Guard)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.\n{Describe(run.Snapshot())}\n{Phase4Checks.Describe(await app.LoadLogAsync())}");
            }

            if (run.Completion.IsCompleted && !condition(run.Snapshot()))
            {
                var result = await run.Completion;
                throw new InvalidOperationException($"The run ended before: {what}.\n{Phase4Checks.Describe(result)}\n{Phase4Checks.Describe(await app.LoadLogAsync())}");
            }

            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Polls, in real time, until <paramref name="condition"/> holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Guard)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20, Ct);
        }
    }

    public static Task UntilStatusAsync(Phase4App app, IOrchestrationRun run, string task, WorkTaskStatus status) =>
        UntilAsync(app, run, s => s.View(task).Status == status, $"{task} {status}");

    /// <summary>
    /// Waits until the loop rests: the snapshot it publishes at the end of every turn stays the same for a while, so it waits
    /// on its timer, which only the clock of the test moves.
    /// </summary>
    public static async Task RestingAsync(IOrchestrationRun run)
    {
        var last = run.Snapshot();
        var since = System.Diagnostics.Stopwatch.StartNew();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (since.Elapsed < Settle)
        {
            if (watch.Elapsed > Guard)
            {
                throw new TimeoutException("The loop of the run never rested.\n" + Describe(run.Snapshot()));
            }

            await Task.Delay(10, Ct);
            var now = run.Snapshot();
            if (!ReferenceEquals(now, last))
            {
                last = now;
                since.Restart();
            }
        }
    }

    /// <summary>
    /// Whether the loop rests within <paramref name="limit"/> of real time: false when it keeps taking turns without the clock
    /// of the test moving (a loop that spins).
    /// </summary>
    public static async Task<bool> RestsWithinAsync(IOrchestrationRun run, TimeSpan limit)
    {
        var last = run.Snapshot();
        var since = System.Diagnostics.Stopwatch.StartNew();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (since.Elapsed < Settle)
        {
            if (watch.Elapsed > limit)
            {
                return false;
            }

            await Task.Delay(10, Ct);
            var now = run.Snapshot();
            if (!ReferenceEquals(now, last))
            {
                last = now;
                since.Restart();
            }
        }

        return true;
    }

    /// <summary>
    /// The clock moves (<paramref name="move"/>) and the loop takes at least one turn on it (a new snapshot) and rests again:
    /// whatever the loop does on that clock, it has done.
    /// </summary>
    public static async Task TurnAsync(IOrchestrationRun run, Action move)
    {
        await RestingAsync(run);
        var before = run.Snapshot();
        move();
        await UntilAsync(() => !ReferenceEquals(run.Snapshot(), before) || run.Completion.IsCompleted, "a turn of the loop");
        await RestingAsync(run);
    }

    /// <summary>The end of a run; on timeout the message has its snapshot and its log.</summary>
    public static async Task<RunResult> EndAsync(Phase4App app, IOrchestrationRun run)
    {
        try
        {
            return await run.Completion.WaitAsync(Guard, Ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The run did not end in time.\n{Describe(run.Snapshot())}\n{Phase4Checks.Describe(await app.LoadLogAsync())}");
        }
    }

    /// <summary>A run that must have succeeded: the assertion message has its log.</summary>
    public static async Task<RunResult> SucceededAsync(Phase4App app, IOrchestrationRun run)
    {
        var result = await EndAsync(app, run);
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));
        return result;
    }

    public static string Describe(RunSnapshot snapshot) =>
        $"{snapshot.Phase} (agent runs {snapshot.AgentRunsUsed}): " + string.Join("; ", snapshot.Tasks.Select(t => $"{t.Id} {t.Status} #{t.Attempt} {t.BlockReason}"));
}

/// <summary>
/// The registry of runs of the app, for one pass of a <see cref="ResumeScheduler"/>, held: the pass reads the log and lists
/// what is left unfinished, and then waits here, before it decides, until the test lets it go on. So a test can put what a
/// person does in between two passes that overlap.
/// </summary>
internal sealed class HeldRunRegistry(IRunRegistry inner, Task release) : IRunRegistry
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the pass has read the log and is waiting to decide.</summary>
    public Task Reached => _reached.Task;

    public Task CreateAsync(RunRecord run, CancellationToken cancellationToken) => inner.CreateAsync(run, cancellationToken);

    public async Task<RunRecord?> FindAsync(string projectId, RunId id, CancellationToken cancellationToken)
    {
        _reached.TrySetResult();
        await release.WaitAsync(cancellationToken);
        return await inner.FindAsync(projectId, id, cancellationToken);
    }

    public Task<IReadOnlyList<RunRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => inner.ListAsync(projectId, cancellationToken);
}

/// <summary>What the Phase 8 tests read of the decision log, which is what decides every behaviour under test (ADR-0010).</summary>
internal static class Phase8Log
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The entries of <paramref name="run"/>, with their payloads, in log order.</summary>
    public static async Task<IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)>> OfAsync(Phase4App app, RunId run) =>
        [.. (await app.LoadLogAsync()).Entries
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is not null && x.Payload.RunId == run.Value)
            .Select(x => (x.Entry, x.Payload!))];

    /// <summary>
    /// The story of a run: the entries that decide what Phase 8 does, one line each, in log order. The evidence, the
    /// instructions and the worktrees are left out: they interleave with the rest as the workers go.
    /// </summary>
    public static IReadOnlyList<string> Story(IEnumerable<(DecisionEntry Entry, DecisionPayload Payload)> entries) =>
        [.. entries.Select(e => Line(e.Payload)).OfType<string>()];

    public static async Task<IReadOnlyList<string>> StoryAsync(Phase4App app, RunId run) => Story(await OfAsync(app, run));

    /// <summary>One line of the story, or null for an entry that is not part of it.</summary>
    public static string? Line(DecisionPayload payload) => payload switch
    {
        RunStartedPayload => "started",
        AgentRunStartedPayload p => $"launch {p.Task?.Value ?? p.Agent.Value}#{p.Attempt}",
        AgentRunFinishedPayload p => $"end {p.Task?.Value ?? "-"}#{p.Attempt} {p.Status} {End(p)}",
        TaskStatusChangedPayload p => $"{p.Task} {p.From}>{p.To}" + (p.BlockReason is { } block ? " " + block : string.Empty),
        AccessCheckedPayload p => $"check {p.Task?.Value ?? "-"} {p.Provider} {p.Source} {p.Result}" + (p.ResetsAt is { } at ? " " + Time(at) : string.Empty),
        RunResumedPayload p => $"resumed {p.Cause}",
        RunDispatchChangedPayload p => $"dispatch {p.Change}",
        RunExitChosenPayload p => $"exit {p.Choice}",
        IntegrationPerformedPayload p => $"integration {p.Task}",
        VerdictIssuedPayload p => $"verdict {p.Task} {p.Decision}",
        AuditAlertPayload p => $"alert {p.Code}",
        RunFinishedPayload p => $"finished {p.Phase}",
        _ => null,
    };

    /// <summary>The class of an end as its story line gives it: the class, its window and its reset when it has them.</summary>
    public static string End(AgentRunFinishedPayload end)
    {
        var resolved = end.ResolvedEnd();
        return string.Join(' ', new[] { resolved.Class.ToString(), resolved.LimitType, resolved.ResetsAt is { } at ? Time(at) : null }.OfType<string>());
    }

    /// <summary>A time as the block reasons of the log write it.</summary>
    public static string Time(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The block reason of a wait for quota until <paramref name="until"/>, as the log writes it.</summary>
    public static string Until(DateTimeOffset until, bool estimated = false) => $"RateLimitedUntil{(estimated ? "Estimated" : string.Empty)}({Time(until)})";

    /// <summary>When a wait for quota ends, read from its block reason (<c>RateLimitedUntil(…)</c> or <c>RateLimitedUntilEstimated(…)</c>); null for any other.</summary>
    public static DateTimeOffset? UntilOf(string? block)
    {
        if (block is null || !block.StartsWith("RateLimitedUntil", StringComparison.Ordinal) || block.IndexOf('(', StringComparison.Ordinal) is var open && open < 0)
        {
            return null;
        }

        return DateTimeOffset.ParseExact(block[(open + 1)..^1], "O", CultureInfo.InvariantCulture);
    }

    /// <summary>The entries of one kind of payload of <paramref name="run"/>, in log order.</summary>
    public static async Task<IReadOnlyList<(DecisionEntry Entry, T Payload)>> OfAsync<T>(Phase4App app, RunId run)
        where T : DecisionPayload =>
        [.. (await OfAsync(app, run)).Where(x => x.Payload is T).Select(x => (x.Entry, (T)x.Payload))];

    /// <summary>
    /// The log of the project verifies: the hash chain, and its head against the anchor kept outside the project (ADR-0010,
    /// addendum), which is exactly the head of the log.
    /// </summary>
    public static async Task AssertVerifiedAsync(Phase4App app)
    {
        var log = await app.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);

        // The same, independently of the recorder: the domain replays the chain of the stored entries against the anchor file.
        var anchor = await app.Get<IDecisionLogAnchorStore>().ReadAsync(app.Project.ProjectId, Ct);
        Assert.NotNull(anchor);
        Assert.Equal(((long)log.Entries.Count, log.HeadHash), (anchor.EntryCount, anchor.HeadHash));
        var replayed = DecisionLog.Load(log.Entries).Verify(anchor);
        Assert.True(replayed.IsValid, replayed.Problem);
    }

    /// <summary>
    /// F8-4: every launch the log announced has its end, and no attempt was announced twice without one in between (the
    /// launches of a task, and those of the judge, which carry no task, are paired in log order as the recovery pairs them).
    /// </summary>
    public static void AssertEveryLaunchEndedOnce(IEnumerable<(DecisionEntry Entry, DecisionPayload Payload)> entries)
    {
        var open = new List<(TaskId? Task, int Attempt, long Sequence)>();
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case AgentRunStartedPayload started:
                    Assert.False(
                        open.Any(o => o.Task == started.Task && o.Attempt == started.Attempt),
                        $"Entry {entry.Sequence} launches {started.Task?.Value ?? started.Agent.Value}#{started.Attempt} again before its previous launch ended.");
                    open.Add((started.Task, started.Attempt, entry.Sequence));
                    break;
                case AgentRunFinishedPayload finished:
                    var index = open.FindIndex(o => o.Task == finished.Task && o.Attempt == finished.Attempt);
                    Assert.True(index >= 0, $"Entry {entry.Sequence} ends {finished.Task?.Value ?? "-"}#{finished.Attempt}, which no launch announced.");
                    open.RemoveAt(index);
                    break;
            }
        }

        Assert.True(open.Count == 0, "Launches without their end: " + string.Join(", ", open.Select(o => $"{o.Task?.Value ?? "-"}#{o.Attempt} (entry {o.Sequence})")));
    }

    /// <summary>The project's prompt journal (ADR-0008): its result lines carry the class of an end when it says more than the status.</summary>
    public static IReadOnlyList<string> Journal(string repository) => Phase5Checks.JournalLines(repository);

    /// <summary>Every file under <paramref name="folders"/> whose text holds any of <paramref name="texts"/>, by its path in its folder.</summary>
    public static IReadOnlyList<string> FilesHolding(IEnumerable<string> folders, IReadOnlyList<string> texts) =>
        [.. folders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(file => texts.Any(text => File.ReadAllText(file).Contains(text, StringComparison.Ordinal)))
                .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')))];
}
