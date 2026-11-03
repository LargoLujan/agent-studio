using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// G-02 (seventh review): a run is resumed only with the writers at medium integrity it was approved with (ADR-0018,
/// decision 3). <c>RunStarted</c> (version 3) anchors them, with the warning the user approved; resuming reads them again
/// from the checked plan, roster and profile and from what the confinement probe answers now, before anything is
/// recovered or launched, and refuses any other. A run that started before they were anchored is resumed only when its
/// plan has none now.
/// </summary>
public sealed class ResumeWithTheApprovedWritersTests
{
    private const string NotResumable = "run.not-resumable";

    /// <summary>What the warning says of the fixtures' executor on a Claude Code whose confinement was never measured.</summary>
    private const string Unmeasured = "T1: el agente «ejecutor» escribe con una versión de Claude Code cuyo confinamiento no se ha medido.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IWriteConfinementProbe Probe(bool claudeMeasured) =>
        claudeMeasured ? new FixedWriteConfinement(ProviderId.ClaudeCode) : new FixedWriteConfinement();

    private static Task SaveShellExecutorAsync(EngineHarness harness) =>
        harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), Ct);

    /// <summary>Starts <paramref name="plan"/> as approved now, lets T1's agent hang, and closes the app under it.</summary>
    private static async Task<IOrchestrationRun> InterruptedRunAsync(EngineHarness harness, PlanDefinition plan, string executor = "ejecutor")
    {
        harness.Claude.Script(executor, "T1", 1, new AgentScript { Hang = true });
        var run = await harness.StartAsync(plan);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.DisposeAsync();
        Assert.Equal(RunPhase.Interrupted, (await run.CompleteAsync(harness)).Phase);
        return run;
    }

    // ---- what the start anchors ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_start_of_a_run_anchors_the_writers_at_medium_integrity_and_the_warning_it_was_approved_with()
    {
        using var harness = new EngineHarness();
        await SaveShellExecutorAsync(harness);
        var plan = Plan(Work("T1"), Work("T2", agent: "ejecutorShell"));
        var approved = harness.Request(plan).ApprovedWarning;

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var (_, started) = Assert.Single(harness.Payloads<RunStartedPayload>());
        Assert.Equal(3, started.V);
        Assert.True(started.AnchorsApprovedWriters());
        Assert.Contains("T2: el agente «ejecutorShell» tiene Bash.", approved, StringComparison.Ordinal);
        Assert.Equal(approved, started.ApprovedWarning);
        var writer = Assert.Single(started.ApprovedWriters!);
        Assert.Equal((new TaskId("T2"), "Shell", (AgentId?)new AgentId("ejecutorShell"), (ProviderId?)null, (string?)null), (writer.Task, writer.Reason, writer.Agent, writer.Provider, writer.Step));
        Assert.Empty(writer.Tools ?? []);

        // It still anchors the whole stored header, as version 2 did.
        var header = await harness.Runs.FindAsync(harness.Project.ProjectId, run.Id, Ct);
        Assert.Equal(RunHeaderHash.Of(header!), started.HeaderHash);
    }

    [Fact]
    public async Task The_start_of_a_run_without_writers_at_medium_integrity_anchors_that_it_has_none()
    {
        using var harness = new EngineHarness();

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        var (_, started) = Assert.Single(harness.Payloads<RunStartedPayload>());
        Assert.True(started.AnchorsApprovedWriters());
        Assert.Null(started.ApprovedWarning);
        Assert.NotNull(started.ApprovedWriters);
        Assert.Empty(started.ApprovedWriters);
    }

    // ---- resuming ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The writers are what is compared, not the probe's answer: an executor with Bash writes at medium integrity on any
    /// Claude Code, so its run goes on whatever the probe says now; the fixtures' executor writes only where it may on a
    /// measured one, and goes on while that does not change.
    /// </summary>
    [Theory]
    [InlineData("ejecutor", true, true)]
    [InlineData("ejecutor", false, false)]
    [InlineData("ejecutorShell", true, false)]
    [InlineData("ejecutorShell", false, true)]
    public async Task A_run_whose_writers_are_still_the_approved_ones_is_resumed(string executor, bool measuredAtStart, bool measuredAtResume)
    {
        using var harness = new EngineHarness { WriteConfinement = Probe(measuredAtStart) };
        await SaveShellExecutorAsync(harness);
        var first = await InterruptedRunAsync(harness, Plan(Work("T1", agent: executor)), executor);
        harness.WriteConfinement = Probe(measuredAtResume);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct);

        Assert.Equal(RunPhase.Succeeded, (await resumed.CompleteAsync(harness)).Phase);
        Assert.Empty(harness.Alerts(NotResumable));
        Assert.Equal(2, harness.Claude.Starts.Count);
    }

    /// <summary>
    /// The fixtures' executor is a writer at medium integrity exactly when the installed Claude Code is not a measured
    /// one: a Claude Code updated to a version nobody measured, or whose probe fails, gives the run a writer the user never
    /// approved; one measured since takes away a writer the user approved, which is not what was approved either.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_run_whose_writers_changed_since_it_was_approved_is_not_resumed(bool measuredAtStart, bool measuredAtResume)
    {
        using var harness = new EngineHarness { WriteConfinement = Probe(measuredAtStart) };
        var first = await InterruptedRunAsync(harness, Plan(Work("T1")));
        harness.WriteConfinement = Probe(measuredAtResume);

        var refused = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct));

        Assert.True(refused.WarningAnchored);
        Assert.Equal(measuredAtStart ? null : Unmeasured, Named(refused.ApprovedWarning));
        Assert.Equal(measuredAtResume ? null : Unmeasured, Named(refused.CurrentWarning));
        Assert.Single(harness.Alerts(NotResumable));
        Assert.Single(harness.Claude.Starts);
    }

    /// <summary>
    /// What is compared is each writer, not how many there are: as many writers for other reasons are other writers (a
    /// version of Agent Studio that reads them otherwise than the one that started the run, say). Here the start anchored
    /// T1 for a shell, and T1 writes now because Claude Code was never measured.
    /// </summary>
    [Fact]
    public async Task A_run_approved_with_as_many_writers_but_for_other_reasons_is_not_resumed()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [new MediumIntegrityWriterEntry(new TaskId("T1"), "Shell", new AgentId("ejecutor"))]);

        var refused = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct));

        Assert.True(refused.WarningAnchored);
        Assert.Equal(Unmeasured, Named(refused.CurrentWarning));
        Assert.Single(harness.Alerts(NotResumable));
        Assert.Empty(harness.Claude.Starts);
    }

    [Fact]
    public async Task A_run_whose_start_anchored_exactly_the_writers_its_plan_has_now_is_resumed()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var run = new RunId("20261018-abcdef");
        await harness.StoreStartedRunAsync(run, Plan(Work("T1")), approvedWriters: [new MediumIntegrityWriterEntry(new TaskId("T1"), "UnmeasuredClaudeVersion", new AgentId("ejecutor"))]);

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, run, Ct);

        Assert.Equal(RunPhase.Succeeded, (await resumed.CompleteAsync(harness)).Phase);
        Assert.Empty(harness.Alerts(NotResumable));
    }

    /// <summary>The scenario of G-02 in full: refused with an alert, and nothing else happens.</summary>
    [Fact]
    public async Task A_run_whose_Claude_Code_is_no_longer_a_measured_one_is_refused_before_anything_is_recovered_or_launched()
    {
        using var harness = new EngineHarness();
        var first = await InterruptedRunAsync(harness, Plan(Work("T1"), Work("T2").After(On("T1"))));
        var entries = harness.Entries.Count;
        var calls = harness.Worktrees.Calls.Count;
        var captures = harness.Worktrees.Captures;

        // While the app was closed, Claude Code was updated to a version nobody measured.
        harness.WriteConfinement = new FixedWriteConfinement();
        var refused = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct));

        Assert.Equal(first.Id, refused.Run);
        Assert.Equal(harness.Payloads<RunStartedPayload>().Single().Payload.PlanHash, refused.PlanHash);
        Assert.Null(refused.ApprovedWarning);
        Assert.Contains(Unmeasured, refused.CurrentWarning, StringComparison.Ordinal);
        Assert.Contains(Unmeasured, refused.Message, StringComparison.Ordinal);

        // The alert is the only entry: no attempt was closed, no task moved, no worktree touched, no baseline of the
        // user's repository taken, no agent launched. The run is still there, interrupted, and the log verifies.
        var (_, alert) = Assert.Single(harness.Alerts(NotResumable));
        Assert.Contains(Unmeasured, alert.Message, StringComparison.Ordinal);
        Assert.Equal(entries + 1, harness.Entries.Count);
        Assert.Equal(calls, harness.Worktrees.Calls.Count);
        Assert.Equal(captures, harness.Worktrees.Captures);
        Assert.Single(harness.Claude.Starts);
        Assert.DoesNotContain(harness.Payloads<AgentRunFinishedPayload>(), p => p.Payload.Status == AttemptOutcome.Interrupted);
        Assert.Equal(RunPhase.Interrupted, Assert.Single(await harness.CreateOrchestrator().ListRunsAsync(harness.Project, Ct)).Phase);
        Assert.True((await harness.Decisions.LoadAsync(harness.Project.ProjectId, Ct)).Verification.IsValid);
    }

    /// <summary>The way forward: the same plan, approved again next to the warning it has now, as a follow-up of the refused run.</summary>
    [Fact]
    public async Task A_refused_run_is_followed_up_by_its_plan_approved_again_with_the_warning_it_has_now()
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"));
        var first = await InterruptedRunAsync(harness, plan);
        harness.WriteConfinement = new FixedWriteConfinement();
        var refused = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct));
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed());

        // What the canvas approves now is the warning the refusal named.
        var request = harness.Request(plan, follows: first.Id);
        Assert.Equal(refused.CurrentWarning, request.ApprovedWarning);
        await using var followUp = await harness.CreateOrchestrator().StartAsync(request, Ct);

        Assert.Equal(RunPhase.Succeeded, (await followUp.CompleteAsync(harness)).Phase);
        var started = harness.Payloads<RunStartedPayload>().Single(p => p.Payload.RunId == followUp.Id.Value).Payload;
        Assert.Equal(first.Id.Value, started.Follows);
        Assert.Equal(refused.CurrentWarning, started.ApprovedWarning);
        Assert.Equal("UnmeasuredClaudeVersion", Assert.Single(started.ApprovedWriters!).Reason);
    }

    // ---- a run that started before the writers were anchored -----------------------------------------------------------

    [Fact]
    public async Task A_run_started_before_its_writers_were_anchored_is_resumed_when_its_plan_has_none()
    {
        using var harness = new EngineHarness();
        var legacy = new RunId("20261014-abcdef");
        await harness.StoreStartedRunAsync(legacy, Plan(Work("T1")));

        await using var run = await harness.CreateOrchestrator().ResumeAsync(harness.Project, legacy, Ct);

        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
        Assert.Empty(harness.Alerts(NotResumable));
    }

    [Fact]
    public async Task A_run_started_before_its_writers_were_anchored_is_not_resumed_when_its_plan_has_some_now()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        var legacy = new RunId("20261014-abcdef");
        await harness.StoreStartedRunAsync(legacy, Plan(Work("T1")));

        var refused = await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, legacy, Ct));

        Assert.False(refused.WarningAnchored);
        Assert.Null(refused.ApprovedWarning);
        Assert.Contains(Unmeasured, refused.CurrentWarning, StringComparison.Ordinal);
        Assert.Contains("version 2", refused.Message, StringComparison.Ordinal);
        Assert.Single(harness.Alerts(NotResumable));
        Assert.Empty(harness.Claude.Starts);
        Assert.Equal(0, harness.Worktrees.Captures);
    }

    // ---- how the writers are compared ----------------------------------------------------------------------------------

    /// <summary>One writer of each shape: with an agent, with tools, with a CLI, with a step.</summary>
    private static readonly MediumIntegrityWriter[] Writers =
    [
        new(new TaskId("T1"), MediumIntegrityReason.Shell) { Agent = new AgentId("ejecutorShell") },
        new(new TaskId("T2"), MediumIntegrityReason.ToolsBeyondMeasuredProfile) { Agent = new AgentId("ejecutorMas"), Tools = ["Monitor"] },
        new(new TaskId("T3"), MediumIntegrityReason.UnmeasuredCli) { Agent = new AgentId("ejecutorGemini"), Provider = ProviderId.GeminiCli },
        new(new TaskId("L1"), MediumIntegrityReason.StepWithoutIsolation) { Step = "lint" },
    ];

    [Fact]
    public void The_approved_writers_are_the_ones_read_now_in_any_order()
    {
        var approved = MediumIntegrityWriters.Anchor(Writers);

        Assert.True(MediumIntegrityWriters.AreTheApproved([.. approved.Reverse()], Writers));
        Assert.True(MediumIntegrityWriters.AreTheApproved([], []));
    }

    [Theory]
    [InlineData("task")]
    [InlineData("reason")]
    [InlineData("agent")]
    [InlineData("no agent")]
    [InlineData("provider")]
    [InlineData("tools")]
    [InlineData("step")]
    [InlineData("one more")]
    [InlineData("one fewer")]
    [InlineData("a repeated one")]
    public void Writers_that_differ_in_anything_are_not_the_approved_ones(string change)
    {
        var approved = MediumIntegrityWriters.Anchor(Writers);
        MediumIntegrityWriter[] now = change switch
        {
            "task" => [Writers[0] with { Task = new TaskId("T9") }, .. Writers[1..]],
            "reason" => [Writers[0] with { Reason = MediumIntegrityReason.CodexWrites }, .. Writers[1..]],
            "agent" => [Writers[0] with { Agent = new AgentId("otro") }, .. Writers[1..]],
            "no agent" => [Writers[0] with { Agent = null }, .. Writers[1..]],
            "provider" => [.. Writers[..2], Writers[2] with { Provider = ProviderId.CodexCli }, Writers[3]],
            "tools" => [Writers[0], Writers[1] with { Tools = ["Monitor", "Workflow"] }, .. Writers[2..]],
            "step" => [.. Writers[..3], Writers[3] with { Step = "build" }],
            "one more" => [.. Writers, Writers[0] with { Task = new TaskId("T4") }],
            "one fewer" => [.. Writers[1..]],
            "a repeated one" => [.. Writers, Writers[0]],
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
        };

        Assert.False(MediumIntegrityWriters.AreTheApproved(approved, now), change);
    }

    /// <summary>The part of a warning that names the tasks, between its fixed opening and its fixed meaning; null for none.</summary>
    private static string? Named(string? warning)
    {
        if (warning is null)
        {
            return null;
        }

        const string Opening = "Atención: este plan tiene escritores con integridad Medium. ";
        const string Meaning = " Pueden escribir en cualquier sitio";
        Assert.StartsWith(Opening, warning, StringComparison.Ordinal);
        var end = warning.IndexOf(Meaning, StringComparison.Ordinal);
        Assert.True(end > Opening.Length, warning);
        return warning[Opening.Length..end];
    }
}
