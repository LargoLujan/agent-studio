using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// The warning of the plan and the check of the user's repository read the same writers at medium integrity (ADR-0018,
/// decision 3): the approval request the engine records names a task exactly when it brings such a writer, and the
/// check is strict (an ignored folder it cannot verify blocks) exactly when that warning is there. Checked over every
/// combination of the fixtures' executors, workspaces, steps, isolation and confinement, against a model written from
/// the ADR, never from the code under test.
/// </summary>
public sealed class MediumIntegrityWarningTests
{
    private const string Unverified = "user-repository.capture-indeterminate";
    private const string Changed = "user-repository.changed";

    /// <summary>What the capture says of an ignored folder past its budget (<c>bin/</c> of a .NET repository, say).</summary>
    private static readonly FileFingerprint OutOfBudget = new(null, "folder:5000+ files", "the capture budget ran out inside this folder");

    public enum Executor
    {
        /// <summary>Claude Code with Read, Edit and Write: the measured profile.</summary>
        Measured,

        /// <summary>Claude Code with Bash.</summary>
        Shell,

        /// <summary>Codex, whose sandbox always has a shell.</summary>
        Codex,

        /// <summary>Gemini, whose confinement was never measured.</summary>
        Gemini,

        /// <summary>Claude Code with a tool the measurement did not cover.</summary>
        MoreTools,
    }

    private static string AgentOf(Executor executor) => executor switch
    {
        Executor.Measured => "ejecutor",
        Executor.Shell => "ejecutorShell",
        Executor.Codex => "ejecutorCodex",
        Executor.Gemini => "ejecutorGemini",
        _ => "ejecutorMas",
    };

    /// <summary>The model of ADR-0018: who writes wherever the user can (a step the plan runs is weighed apart).</summary>
    private static bool WritesAtMediumIntegrity(Executor executor, WorkspaceMode workspace, bool claudeMeasured) =>
        workspace == WorkspaceMode.Write && executor switch
        {
            Executor.Measured => !claudeMeasured,
            _ => true,
        };

    private static VerificationProfile ProfileWith(bool buildIsolated, bool lintIsolated) =>
        Profile() with
        {
            Steps = [.. Profile().Steps.Select(s => (s.Id == "build" && !buildIsolated) || (s.Id == "lint" && !lintIsolated) ? s with { Isolation = ProcessIsolation.None } : s)],
        };

    private static async Task<EngineHarness> HarnessAsync(bool claudeMeasured)
    {
        var harness = new EngineHarness { WriteConfinement = claudeMeasured ? new FixedWriteConfinement(ProviderId.ClaudeCode) : new FixedWriteConfinement() };
        var gemini = new ScriptedAgentProvider(ProviderId.GeminiCli, VendorId.Google, harness.Time, 2);
        harness.Providers = new FakeProjectAgentProviders([.. harness.Providers.For(harness.RepositoryRoot), AgentProviderPipeline.Compose(gemini, harness.Journal, harness.Time)]);
        var ct = TestContext.Current.CancellationToken;
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), ct);
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorGemini", AgentRole.Executor, ProviderId.GeminiCli, VendorId.Google, ["Read", "Edit", "Write"]), ct);
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorMas", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Monitor"]), ct);

        // An ignored folder the capture cannot verify: it blocks only a run whose check is strict.
        harness.Worktrees.UserState = harness.Worktrees.UserState with
        {
            PorcelainStatus = ["!! bin/"],
            Contents = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal) { ["bin/"] = OutOfBudget },
        };
        return harness;
    }

    [Fact]
    public async Task The_approval_request_names_exactly_the_tasks_that_make_the_check_of_the_users_repository_strict()
    {
        var strict = 0;
        var relaxed = 0;
        foreach (var executor in Enum.GetValues<Executor>())
        {
            foreach (var workspace in (WorkspaceMode[])[WorkspaceMode.Write, WorkspaceMode.ReadOnly])
            {
                foreach (var runsBuild in (bool[])[false, true])
                {
                    foreach (var buildIsolated in (bool[])[true, false])
                    {
                        foreach (var lintIsolated in (bool[])[true, false])
                        {
                            foreach (var claudeMeasured in (bool[])[true, false])
                            {
                                var world = $"{executor} {workspace} build={(runsBuild ? "run" : "not run")} build isolated={buildIsolated} lint isolated={lintIsolated} Claude measured={claudeMeasured}";
                                var t1Named = WritesAtMediumIntegrity(executor, workspace, claudeMeasured) || (runsBuild && !buildIsolated);
                                var l1Named = !lintIsolated;

                                using var harness = await HarnessAsync(claudeMeasured);
                                var t1 = Work("T1", agent: AgentOf(executor), workspace: workspace);
                                var plan = Plan(runsBuild ? t1.Verified("build") : t1, Node("L1", WorkTaskKind.ToolRun).After(On("T1")));
                                await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan, profile: ProfileWith(buildIsolated, lintIsolated)), TestContext.Current.CancellationToken);
                                await run.CompleteAsync(harness);

                                var description = Assert.Single(harness.Payloads<ApprovalRequestedPayload>(), p => p.Payload.Action == "plan.approve").Payload.Description;
                                var warned = description != $"Ejecutar el plan {plan.ContentHash[..12]} (2 tareas)";
                                var blocked = harness.Alerts(Changed).Any(a => a.Payload.Message.Contains("'bin/' is indeterminate", StringComparison.Ordinal));
                                var letThrough = harness.Alerts(Unverified).Count > 0;

                                Assert.True(warned == (t1Named || l1Named), $"{world}: warned={warned}, the model says {t1Named || l1Named}. {description}");
                                Assert.True(blocked == warned, $"{world}: the check blocked={blocked}, the approval warned={warned}. {description}");
                                Assert.True(letThrough != blocked, $"{world}: an unverified ignored folder was both let through and blocked, or neither.");
                                Assert.True(description.Contains(" T1: ", StringComparison.Ordinal) == t1Named, $"{world}: T1 should be named={t1Named}. {description}");
                                Assert.True(description.Contains(" L1: ", StringComparison.Ordinal) == l1Named, $"{world}: L1 should be named={l1Named}. {description}");
                                if (blocked)
                                {
                                    strict++;
                                }
                                else
                                {
                                    relaxed++;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Both behaviours of the check were reached, and every combination ran.
        Assert.Equal(160, strict + relaxed);
        Assert.True(relaxed >= 10, $"Only {relaxed} runs had a relaxed check.");
        Assert.True(strict >= 100, $"Only {strict} runs had a strict check.");
    }

    /// <summary>
    /// The user approved the plan next to one warning; the engine reads the writers again when the run starts (another
    /// Claude Code installed meanwhile, say). If the warning it would record is not the one approved, nothing starts and
    /// nothing is written: the approval was given to something else.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Atención: otro aviso.")]
    public async Task A_run_whose_approved_warning_is_not_the_one_the_engine_reads_does_not_start(string approved)
    {
        using var harness = new EngineHarness();
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), TestContext.Current.CancellationToken);
        var request = harness.Request(Plan(Work("T1", agent: "ejecutorShell"))) with { ApprovedWarning = approved };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().StartAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("aviso", refused.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Entries);
        Assert.Empty(harness.Claude.Starts);
    }

    [Fact]
    public async Task A_run_whose_approved_warning_is_the_one_the_engine_reads_starts()
    {
        using var harness = new EngineHarness();
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), TestContext.Current.CancellationToken);
        var plan = Plan(Work("T1", agent: "ejecutorShell"));
        var warning = VerificationStepText.PlanWarning(plan, harness.Roster.Agents, Profile(), claudeWritesConfined: true);
        Assert.NotNull(warning);

        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan) with { ApprovedWarning = warning }, TestContext.Current.CancellationToken);

        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
    }

    /// <summary>
    /// A request that does not say which warning was shown next to «Aprobar plan» was approved without one: when the
    /// engine reads writers at medium integrity in the plan, the user was never told about them, so nothing starts and
    /// nothing is written (fail closed).
    /// </summary>
    [Fact]
    public async Task A_run_whose_request_carries_no_warning_does_not_start_when_the_plan_has_writers_at_medium_integrity()
    {
        using var harness = new EngineHarness();
        await harness.Roster.SaveAsync(harness.Project.ProjectId, Agent("ejecutorShell", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write", "Bash"]), TestContext.Current.CancellationToken);
        var request = harness.Request(Plan(Work("T1", agent: "ejecutorShell"))) with { ApprovedWarning = null };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.CreateOrchestrator().StartAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(RunRequest.ApprovedWarning), refused.Message, StringComparison.Ordinal);
        Assert.Contains("ejecutorShell", refused.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Entries);
        Assert.Empty(harness.Claude.Starts);
        Assert.Empty(harness.Worktrees.Calls);
        Assert.Empty(await harness.Runs.ListAsync(harness.Project.ProjectId, TestContext.Current.CancellationToken));
        Assert.Empty(harness.Plans.All());
        Assert.Equal(0, harness.RequirementStore.Saves);
        Assert.Empty(harness.Evidence.All());
    }

    [Fact]
    public async Task A_run_whose_request_carries_no_warning_starts_when_the_plan_has_no_writer_at_medium_integrity()
    {
        using var harness = new EngineHarness();
        var plan = Plan(Work("T1"));
        Assert.Null(VerificationStepText.PlanWarning(plan, harness.Roster.Agents, Profile(), claudeWritesConfined: true));

        await using var run = await harness.CreateOrchestrator().StartAsync(harness.Request(plan) with { ApprovedWarning = null }, TestContext.Current.CancellationToken);

        Assert.Equal(RunPhase.Succeeded, (await run.CompleteAsync(harness)).Phase);
    }
}
