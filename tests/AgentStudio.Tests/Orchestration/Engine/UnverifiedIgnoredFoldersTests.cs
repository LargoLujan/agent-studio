using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The capture of the user's repository hashes content within a budget (ADR-0012, N-07): whatever it cannot verify is
/// indeterminate. The engine blocks on every difference and on every indeterminate entry, except an ignored folder in a
/// run that cannot write to the user's repository at medium integrity (no executor with a shell or on Codex in a
/// writable worktree, no step of the profile run without isolation): that one is recorded once, as an alert.
/// </summary>
public sealed class UnverifiedIgnoredFoldersTests
{
    private const string Unverified = "user-repository.capture-indeterminate";
    private const string Changed = "user-repository.changed";

    /// <summary>What the capture says of an ignored folder past its budget (<c>bin/</c> of a .NET repository, say).</summary>
    private static readonly FileFingerprint OutOfBudget = new(null, "folder:5000+ files", "the capture budget ran out inside this folder");

    /// <summary>What the capture says of a file larger than the per-file cap.</summary>
    private static readonly FileFingerprint TooLarge = new(null, "size:9437185;mtime:638900000000000000", "larger than the 8388608-byte per-file cap");

    private static FileFingerprint Hashed(char digit) => new(new string(digit, 64), "folder:2 files");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The user's repository with these status lines and the fingerprint of each path. The fake returns the same state in
    /// every capture, so two captures of it are identical: nothing changed.
    /// </summary>
    private static void Repository(EngineHarness harness, params (string Line, FileFingerprint Fingerprint)[] entries) =>
        harness.Worktrees.UserState = WithEntries(harness.Worktrees.UserState, entries);

    private static UserRepositoryState WithEntries(UserRepositoryState state, params (string Line, FileFingerprint Fingerprint)[] entries) =>
        state with
        {
            PorcelainStatus = [.. entries.Select(e => e.Line)],
            Contents = entries.ToDictionary(e => e.Line[3..], e => e.Fingerprint, StringComparer.Ordinal),
        };

    private static Task SaveAgentAsync(EngineHarness harness, string id, ProviderId provider, VendorId vendor, params string[] tools) =>
        harness.Roster.SaveAsync(harness.Project.ProjectId, Agent(id, AgentRole.Executor, provider, vendor, tools), Ct);

    /// <summary>The profile of the fixtures with <paramref name="step"/> run without isolation (<c>"isolation": "none"</c>).</summary>
    private static VerificationProfile WithoutIsolation(string step) =>
        Profile() with { Steps = [.. Profile().Steps.Select(s => s.Id == step ? s with { Isolation = ProcessIsolation.None } : s)] };

    private static async Task<RunResult> RunAsync(EngineHarness harness, PlanDefinition plan, VerificationProfile? profile = null)
    {
        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan, profile: profile), Ct);
        return await run.CompleteAsync(harness);
    }

    // ---- a run that cannot write to the user's repository at medium integrity ---------------------------------------

    [Fact]
    public async Task Ignored_folders_the_capture_cannot_verify_do_not_block_a_run_that_cannot_write_there_and_are_alerted_once()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget), ("!! obj/", OutOfBudget));

        // The checks before both agents run at the same time: they must still record the alert once.
        using var together = new Barrier(2);
        harness.Worktrees.OnCapture = number =>
        {
            if (number is 2 or 3)
            {
                together.SignalAndWait(TimeSpan.FromSeconds(10));
            }
        };

        await using var run = await harness.StartAsync(Plan(Work("T1"), Work("T2")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(2, harness.Claude.Starts.Count);
        Assert.Empty(harness.Alerts(Changed));
        var (_, alert) = Assert.Single(harness.Alerts(Unverified));
        Assert.Equal(run.Id.Value, alert.RunId);
        Assert.Empty(alert.Tasks);
        Assert.Contains("'bin/' (the capture budget ran out inside this folder)", alert.Message, StringComparison.Ordinal);
        Assert.Contains("'obj/' (the capture budget ran out inside this folder)", alert.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same content is verified the same way, so a folder that was verified and no longer can be changed (R3-06): a
    /// file added past the budget is exactly how a writer would hide a swap there. It blocks even in a run that cannot
    /// write there, like the user's own build growing it would.
    /// </summary>
    [Fact]
    public async Task An_ignored_folder_that_was_verified_and_no_longer_can_be_blocks_also_in_a_run_that_cannot_write_there()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget), ("!! obj/", Hashed('a')));
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => Repository(harness, ("!! bin/", OutOfBudget), ("!! obj/", OutOfBudget)),
        });

        var result = await RunAsync(harness, Plan(Work("T1"), Work("T2").After(On("T1"))));

        Assert.Equal(RunPhase.Failed, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Contains("Content of 'obj/' changed: one capture verified its content and the other could not", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'bin/'", alert.Message, StringComparison.Ordinal);
        Assert.Contains("'bin/'", Assert.Single(harness.Alerts(Unverified)).Payload.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ignored folder neither capture verified is still compared by the metadata of every entry (R3-06): a file added,
    /// removed, resized or touched there blocks even a run that cannot write there; only a change that keeps all of them,
    /// a same-length swap with the timestamps restored, is what such a run lets through.
    /// </summary>
    [Fact]
    public async Task An_unverified_ignored_folder_whose_metadata_changed_blocks_also_in_a_run_that_cannot_write_there()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => Repository(harness, ("!! bin/", OutOfBudget with { Approximation = "folder:5001+ files" })),
        });

        var result = await RunAsync(harness, Plan(Work("T1")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Contains("Content of 'bin/' changed (approximate", alert.Message, StringComparison.Ordinal);
        Assert.Single(harness.Alerts(Unverified));
    }

    [Fact]
    public async Task A_step_without_isolation_that_no_task_runs_does_not_make_the_run_able_to_write_there()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));

        // "lint" would run at the user's rights, but no task of this plan runs it.
        var result = await RunAsync(harness, Plan(Work("T1").Verified("build")), WithoutIsolation("lint"));

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Alerts(Unverified));
        Assert.Empty(harness.Alerts(Changed));
    }

    [Theory]
    [InlineData("shell, read-only worktree")]
    [InlineData("codex, read-only worktree")]
    public async Task An_executor_that_cannot_write_its_worktree_cannot_write_there_either(string executor)
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        await SaveAgentAsync(harness, "ejecutorShell", ProviderId.ClaudeCode, VendorId.Anthropic, "Read", "Grep", "Bash");
        var task = executor.StartsWith("shell", StringComparison.Ordinal)
            ? Work("T1", agent: "ejecutorShell", workspace: WorkspaceMode.ReadOnly)
            : Work("T1", agent: "ejecutorCodex", workspace: WorkspaceMode.ReadOnly);

        var result = await RunAsync(harness, Plan(task));

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Alerts(Unverified));
    }

    // ---- a run that can write there: an unverified ignored folder blocks ---------------------------------------------

    [Theory]
    [InlineData("shell")]
    [InlineData("codex")]
    public async Task An_executor_that_can_write_at_medium_integrity_makes_an_unverified_ignored_folder_block(string executor)
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        await SaveAgentAsync(harness, "ejecutorShell", ProviderId.ClaudeCode, VendorId.Anthropic, "Read", "Edit", "Write", "Bash");
        var task = executor == "shell" ? Work("T1", agent: "ejecutorShell") : Work("T1", agent: "ejecutorCodex");

        var result = await RunAsync(harness, Plan(task));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Empty(harness.Claude.Starts);
        Assert.Empty(harness.Codex.Starts);
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        Assert.Contains("'bin/' is indeterminate", alert.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts(Unverified));
    }

    [Theory]
    [InlineData("verification")]
    [InlineData("tool")]
    public async Task A_profile_step_the_plan_runs_without_isolation_makes_an_unverified_ignored_folder_block(string how)
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        var (plan, step) = how == "verification"
            ? (Plan(Work("T1").Verified("build")), "build")
            : (Plan(Work("T1"), Node("L1", WorkTaskKind.ToolRun).After(On("T1"))), "lint");

        var result = await RunAsync(harness, plan, WithoutIsolation(step));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("'bin/' is indeterminate", Assert.Single(harness.Alerts(Changed)).Payload.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts(Unverified));
    }

    // ---- the confinement of the executor's CLI (ADR-0018, «Medición») -------------------------------------------------

    /// <summary>The harness with a scripted Gemini CLI too, through the same pipeline as the others.</summary>
    private static ScriptedAgentProvider WithGemini(EngineHarness harness)
    {
        var gemini = new ScriptedAgentProvider(ProviderId.GeminiCli, VendorId.Google, harness.Time, 2);
        harness.Providers = new FakeProjectAgentProviders([.. harness.Providers.For(harness.RepositoryRoot), AgentProviderPipeline.Compose(gemini, harness.Journal, harness.Time)]);
        return gemini;
    }

    [Fact]
    public async Task A_Claude_executor_whose_installed_version_was_never_measured_makes_an_unverified_ignored_folder_block()
    {
        using var harness = new EngineHarness { WriteConfinement = new FixedWriteConfinement() };
        Repository(harness, ("!! bin/", OutOfBudget));

        var result = await RunAsync(harness, Plan(Work("T1")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Empty(harness.Claude.Starts);
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        Assert.Contains("'bin/' is indeterminate", alert.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts(Unverified));
    }

    [Theory]
    [InlineData("Monitor")]
    [InlineData("Workflow")]
    [InlineData("Bash(git log:*)")]
    [InlineData("Skill")]
    public async Task A_Claude_executor_with_a_tool_beyond_the_measured_profile_makes_an_unverified_ignored_folder_block(string tool)
    {
        // The confinement was measured for Read, Grep, Glob, Edit and Write only: whatever else is pre-approved was not.
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        await SaveAgentAsync(harness, "ejecutorMas", ProviderId.ClaudeCode, VendorId.Anthropic, "Read", "Edit", "Write", tool);

        var result = await RunAsync(harness, Plan(Work("T1", agent: "ejecutorMas")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Empty(harness.Claude.Starts);
        Assert.Contains("'bin/' is indeterminate", Assert.Single(harness.Alerts(Changed)).Payload.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts(Unverified));
    }

    [Fact]
    public async Task A_Claude_executor_with_part_of_the_measured_tools_on_a_measured_version_does_not_block()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        await SaveAgentAsync(harness, "ejecutorMenos", ProviderId.ClaudeCode, VendorId.Anthropic, "Edit", "Grep");

        var result = await RunAsync(harness, Plan(Work("T1", agent: "ejecutorMenos")));

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(harness.Alerts(Unverified));
    }

    [Fact]
    public async Task A_Claude_executor_without_a_probe_of_its_version_makes_an_unverified_ignored_folder_block()
    {
        using var harness = new EngineHarness { WriteConfinement = null };
        Repository(harness, ("!! bin/", OutOfBudget));

        var result = await RunAsync(harness, Plan(Work("T1")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("'bin/' is indeterminate", Assert.Single(harness.Alerts(Changed)).Payload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_executor_that_writes_files_on_a_CLI_whose_confinement_was_never_measured_makes_an_unverified_ignored_folder_block()
    {
        using var harness = new EngineHarness();
        var gemini = WithGemini(harness);
        Repository(harness, ("!! bin/", OutOfBudget));
        await SaveAgentAsync(harness, "ejecutorGemini", ProviderId.GeminiCli, VendorId.Google, "Read", "Edit", "Write");

        var result = await RunAsync(harness, Plan(Work("T1", agent: "ejecutorGemini")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Empty(gemini.Starts);
        Assert.Contains("'bin/' is indeterminate", Assert.Single(harness.Alerts(Changed)).Payload.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts(Unverified));
    }

    [Fact]
    public async Task An_executor_on_such_a_CLI_that_cannot_write_its_worktree_cannot_write_there_either()
    {
        using var harness = new EngineHarness();
        var gemini = WithGemini(harness);
        Repository(harness, ("!! bin/", OutOfBudget));
        await SaveAgentAsync(harness, "ejecutorGemini", ProviderId.GeminiCli, VendorId.Google, "Read", "Grep");

        var result = await RunAsync(harness, Plan(Work("T1", agent: "ejecutorGemini", workspace: WorkspaceMode.ReadOnly)));

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Single(gemini.Starts);
        Assert.Single(harness.Alerts(Unverified));
    }

    /// <summary>
    /// Before G-02 this run was resumed, with a check made strict by a writer at medium integrity the user had never
    /// approved, and blocked there. Now the writers of a resumed run must be the approved ones (ADR-0018, decision 3), so
    /// it is refused before its baseline is taken and before anything is launched: its plan has to be approved again.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_whose_Claude_version_is_no_longer_a_measured_one_is_refused_before_its_check_is_made()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        harness.Claude.Script("ejecutor", "T2", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        await harness.WaitForStatusAsync(first, "T2", WorkTaskStatus.Running);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);
        Assert.Single(harness.Alerts(Unverified));
        var captures = harness.Worktrees.Captures;

        // While the app was closed, Claude Code was updated to a version nobody measured.
        harness.WriteConfinement = new FixedWriteConfinement();
        await Assert.ThrowsAsync<RunNeedsApprovalException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct));

        Assert.Equal(["T1", "T2"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.Equal(captures, harness.Worktrees.Captures);
        Assert.Empty(harness.Alerts(Changed));
        Assert.Single(harness.Alerts(Unverified));
        Assert.Single(harness.Alerts("run.not-resumable"));
    }

    // ---- what always blocks ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(" M datos/grande.bin")]
    [InlineData("?? nuevo/grande.bin")]
    [InlineData("!! registro.log")]
    [InlineData("?? anidado/")]
    public async Task Content_the_capture_cannot_verify_that_is_not_an_ignored_folder_always_blocks(string line)
    {
        // A tracked, untracked or ignored file larger than the per-file cap is the user's own work, and an untracked
        // folder (a nested repository) is not an ignored one: none of them is relaxed, whoever runs.
        using var harness = new EngineHarness();
        Repository(harness, (line, TooLarge), ("!! bin/", OutOfBudget));

        var result = await RunAsync(harness, Plan(Work("T1")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Contains($"'{line[3..]}' is indeterminate", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'bin/'", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_file_the_capture_cannot_verify_always_blocks()
    {
        using var harness = new EngineHarness();
        harness.Worktrees.UserState = harness.Worktrees.UserState with
        {
            GitFiles = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal) { [".git/hooks/pre-commit"] = TooLarge },
        };

        var result = await RunAsync(harness, Plan(Work("T1")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("Git file '.git/hooks/pre-commit' is indeterminate", Assert.Single(harness.Alerts(Changed)).Payload.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("content of a verified ignored folder")]
    [InlineData("HEAD")]
    public async Task A_real_difference_always_blocks_also_next_to_an_unverified_ignored_folder(string what)
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget), ("!! cache/", Hashed('a')));
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => harness.Worktrees.UserState = what == "HEAD"
                ? harness.Worktrees.UserState with { HeadCommit = new string('e', 40) }
                : WithEntries(harness.Worktrees.UserState, ("!! bin/", OutOfBudget), ("!! cache/", Hashed('b'))),
        });

        var result = await RunAsync(harness, Plan(Work("T1")));

        Assert.Equal(RunPhase.Failed, result.Phase);
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Contains(what == "HEAD" ? "HEAD moved" : "Content of 'cache/' changed.", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'bin/'", alert.Message, StringComparison.Ordinal);

        // The check before the agent had already relaxed bin/ and recorded it.
        Assert.Single(harness.Alerts(Unverified));
    }

    // ---- resuming ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resumed_run_that_cannot_write_there_is_not_blocked_by_an_unverified_ignored_folder_and_alerts_from_its_new_baseline()
    {
        using var harness = new EngineHarness();
        Repository(harness, ("!! bin/", OutOfBudget));
        harness.Claude.Script("ejecutor", "T2", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        await harness.WaitForStatusAsync(first, "T2", WorkTaskStatus.Running);
        await first.DisposeAsync();
        Assert.Equal(RunPhase.Interrupted, (await first.CompleteAsync(harness)).Phase);
        Assert.Single(harness.Alerts(Unverified));

        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct);
        var result = await resumed.CompleteAsync(harness);

        // The alert of the first part is not an integrity breach: the resumed run goes on, with an alert of its own.
        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts(Changed));
        Assert.Equal(2, harness.Alerts(Unverified).Count);
        Assert.All(harness.Alerts(Unverified), a => Assert.Equal(first.Id.Value, a.Payload.RunId));
    }

    [Fact]
    public async Task A_resumed_run_with_an_executor_that_can_write_there_still_blocks_on_an_unverified_ignored_folder()
    {
        using var harness = new EngineHarness();
        await SaveAgentAsync(harness, "ejecutorShell", ProviderId.ClaudeCode, VendorId.Anthropic, "Read", "Edit", "Write", "Bash");
        harness.Claude.Script("ejecutorShell", "T2", 1, new AgentScript { Hang = true });
        var first = await harness.StartAsync(Plan(Work("T1"), Work("T2", agent: "ejecutorShell").After(On("T1"))));
        await harness.WaitForStatusAsync(first, "T2", WorkTaskStatus.Running);
        await first.DisposeAsync();
        await first.CompleteAsync(harness);

        // While the app was closed, the user's build grew bin/ past the budget: the resumed run's baseline has it unverified.
        Repository(harness, ("!! bin/", OutOfBudget));
        await using var resumed = await harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, Ct);
        var result = await resumed.CompleteAsync(harness);

        // The second attempt of T2 is never launched: the check before it already blocks.
        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(["T1", "T2"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        var (_, alert) = Assert.Single(harness.Alerts(Changed));
        Assert.Equal([new TaskId("T2")], alert.Tasks);
        Assert.Contains("'bin/' is indeterminate", alert.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Alerts(Unverified));
    }
}
