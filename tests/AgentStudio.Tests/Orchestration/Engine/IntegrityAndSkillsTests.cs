using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>The user-repository check around every agent run (ADR-0013) and F3-9, the skills of each agent.</summary>
public sealed class IntegrityAndSkillsTests
{
    [Fact]
    public async Task A_change_to_the_user_repository_during_an_agent_run_fails_the_task_alerts_and_stops_dispatching()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with
            {
                HeadCommit = new string('e', 40),
                Branches = new Dictionary<string, string> { ["refs/heads/main"] = new string('e', 40) },
            },
        });

        // One task at a time: T3 is ready while T1 runs, and must never be dispatched after the change is seen.
        var plan = Plan(Work("T1"), Work("T3"));
        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains("repositorio del usuario", result.Summary, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("T1").Status);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T3").Status);
        Assert.Single(harness.Claude.Starts);
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        Assert.Contains("HEAD moved", alert.Message, StringComparison.Ordinal);
        Assert.Contains("Branch 'refs/heads/main' moved", alert.Message, StringComparison.Ordinal);
        Assert.Contains("HEAD moved", harness.Payloads<TaskStatusChangedPayload>().Single(p => p.Payload.To == WorkTaskStatus.Failed).Payload.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stash")]
    [InlineData("index")]
    [InlineData("tag")]
    [InlineData("status")]
    public async Task Every_part_of_the_user_repository_is_watched(string part)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => harness.Worktrees.UserState = part switch
            {
                "stash" => harness.Worktrees.UserState with { StashCommit = new string('5', 40) },
                "index" => harness.Worktrees.UserState with { IndexHash = "index-cambiado" },
                "tag" => harness.Worktrees.UserState with { Tags = new Dictionary<string, string> { ["refs/tags/v1"] = new string('7', 40) } },
                _ => harness.Worktrees.UserState with { PorcelainStatus = [" M src/Program.cs"] },
            },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Single(harness.Alerts("user-repository.changed"));
    }

    [Fact]
    public async Task What_Agent_Studio_itself_writes_under_agentstudio_during_a_run_is_not_a_change()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with
            {
                PorcelainStatus =
                [
                    " M .agentstudio/bitacora/2026-10-15.md",
                    "?? .agentstudio/bitacora/2026-10-16.md",
                    "?? .agentstudio/agentstudio.db-wal",
                    "?? .agentstudio/agentstudio.db-shm",
                    "?? .agentstudio/agentstudio.db.lock",
                    "?? .agentstudio/evidence/0f3a9c",
                    "?? .agentstudio/tmp/verificacion/salida.txt",
                    " M .agentstudio/canvas-layout.json",
                    " M .agentstudio/verification.json",

                    // The capture lists ignored files too, and an ignored folder once, with its trailing '/'.
                    "!! .agentstudio/evidence/",
                    "!! .agentstudio/tmp/",
                    "!! .agentstudio/agentstudio.db",
                    "!! .agentstudio/agentstudio.db-wal",
                    "!! .agentstudio/agentstudio.db-shm",
                    "!! .agentstudio/agentstudio.db.lock",
                ],
            },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts("user-repository.changed"));
    }

    [Theory]
    [InlineData("?? .agentstudio/memory/nota.md")]
    [InlineData("R  .agentstudio/a.md -> .agentstudio/b.md")]
    [InlineData("?? .agentstudio/hooks/pre-commit")]
    [InlineData("?? .agentstudio/evidence-falsa/x")]
    [InlineData("?? .agentstudio/verification.json.bak")]
    [InlineData("?? .agentstudio/tmp")]
    [InlineData("?? .agentstudio/bitacora/notas.txt")]
    [InlineData("?? .agentstudio/bitacora/notas.md")]
    [InlineData("?? .agentstudio/bitacora/2026-10-15-copia.md")]
    [InlineData("?? .agentstudio/bitacora/2026-13-45.md")]
    [InlineData("?? .agentstudio/bitacora/20261015.md")]
    [InlineData(" M .agentstudio/bitacora/README.md")]
    [InlineData("?? .agentstudio/bitacora/sub/2026-10-15.md")]
    [InlineData(" D .agentstudio/bitacora/2026-10-15.md")]
    [InlineData("R  .agentstudio/bitacora/2026-10-15.md -> .agentstudio/bitacora/2026-10-14.md")]
    [InlineData("?? .agentstudio/db/agentstudio.db")]
    [InlineData("?? .agentstudio/inicio.db.ps1")]
    [InlineData("?? .agentstudio/agentstudio.db.ps1")]
    [InlineData("?? .agentstudio/agentstudio.dbx")]
    [InlineData("?? .agentstudio/copia.db")]
    [InlineData("!! .agentstudio/otra.db-wal")]
    [InlineData("!! .agentstudio/cache/")]
    [InlineData("!! .agentstudio/")]
    [InlineData("!! .agentstudio/evidence-falsa/")]
    public async Task Anything_else_that_changes_under_agentstudio_is_a_change(string line)
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = [line] },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains(line, Assert.Single(harness.Alerts("user-repository.changed")).Payload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rename_out_of_agentstudio_is_a_change()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = ["R  .agentstudio/a.md -> src/a.md"] },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
    }

    /// <summary>The path of a day of the prompt journal in the harness's repository, with its folder created.</summary>
    private static string JournalDay(EngineHarness harness, string day)
    {
        var folder = Path.Combine(harness.RepositoryRoot, ".agentstudio", "bitacora");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, day + ".md");
    }

    [Fact]
    public async Task A_journal_that_only_grows_during_the_run_is_not_a_change()
    {
        using var harness = new EngineHarness();
        var journal = JournalDay(harness, "2026-10-15");
        await File.WriteAllTextAsync(journal, "# Bitácora del 2026-10-15\n\n## P-0001 · 09:00\nPrimera petición.\n", TestContext.Current.CancellationToken);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ =>
            {
                File.AppendAllText(journal, "\n## P-0002 · 09:05\nSegunda petición.\n");
                harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = [" M .agentstudio/bitacora/2026-10-15.md"] };
            },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Empty(harness.Alerts("user-repository.changed"));
    }

    [Theory]
    [InlineData("rewritten")]
    [InlineData("truncated")]
    [InlineData("deleted")]
    public async Task A_journal_that_does_not_only_grow_during_the_run_is_a_change(string how)
    {
        using var harness = new EngineHarness();
        var journal = JournalDay(harness, "2026-10-15");
        await File.WriteAllTextAsync(journal, "## P-0001 · 09:00\nPrimera petición.\n## P-0002 · 09:05\nSegunda petición.\n", TestContext.Current.CancellationToken);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ =>
            {
                switch (how)
                {
                    case "rewritten":
                        // Longer than before, but the first entry no longer says what it said: not an append.
                        File.WriteAllText(journal, "## P-0001 · 09:00\nOtra cosa.\n## P-0002 · 09:05\nSegunda petición.\n## P-0003 · 09:10\nTercera.\n");
                        break;
                    case "truncated":
                        File.WriteAllText(journal, "## P-0001 · 09:00\nPrimera petición.\n");
                        break;
                    default:
                        File.Delete(journal);
                        break;
                }

                // The status line is one Agent Studio itself causes; only the content tells the difference.
                harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = [" M .agentstudio/bitacora/2026-10-15.md"] };
            },
        });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Contains(".agentstudio/bitacora/2026-10-15.md", Assert.Single(harness.Alerts("user-repository.changed")).Payload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_journal_that_appears_during_the_run_may_only_grow_from_then_on()
    {
        using var harness = new EngineHarness();
        var journal = JournalDay(harness, "2026-10-16");
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript
        {
            OnStart = _ =>
            {
                File.WriteAllText(journal, "## P-0001 · 00:01\nNueva.\n");
                harness.Worktrees.UserState = harness.Worktrees.UserState with { PorcelainStatus = ["?? .agentstudio/bitacora/2026-10-16.md"] };
            },
        });
        harness.Claude.Script("ejecutor", "T2", 1, new AgentScript { OnStart = _ => File.WriteAllText(journal, "## P-0001 · 00:01\nCambiada.\n") });

        await using var run = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Completed, run.Snapshot().View("T1").Status);
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Equal([new TaskId("T2")], alert.Tasks);
        Assert.Contains(".agentstudio/bitacora/2026-10-16.md", alert.Message, StringComparison.Ordinal);
    }

    /// <summary>Denies (or gives back) the current user the listing of a folder, so it still exists but cannot be listed.</summary>
    private static void DenyListing(string folder, bool deny)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var rule = new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        if (deny)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRule(rule);
        }

        info.SetAccessControl(security);
    }

    [Fact]
    public async Task A_journal_folder_that_can_no_longer_be_listed_during_the_run_is_a_change()
    {
        using var harness = new EngineHarness();
        var folder = Path.GetDirectoryName(JournalDay(harness, "2026-10-15"))!;
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { OnStart = _ => DenyListing(folder, deny: true) });
        try
        {
            await using var run = await harness.StartAsync(Plan(Work("T1")));
            var result = await run.CompleteAsync(harness);

            Assert.Equal(RunPhase.Failed, result.Phase);
            Assert.True(Directory.Exists(folder));
            var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
            Assert.Equal([new TaskId("T1")], alert.Tasks);
            Assert.Contains("'.agentstudio/bitacora/' cannot be listed any more", alert.Message, StringComparison.Ordinal);
        }
        finally
        {
            DenyListing(folder, deny: false);
        }
    }

    [Fact]
    public async Task A_run_whose_journal_folder_cannot_be_listed_has_no_baseline_and_never_starts()
    {
        using var harness = new EngineHarness();
        var folder = Path.GetDirectoryName(JournalDay(harness, "2026-10-15"))!;
        DenyListing(folder, deny: true);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.StartAsync(Plan(Work("T1"))));

            Assert.Contains("cannot be listed", error.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Claude.Starts);
            Assert.DoesNotContain(harness.Entries, e => e.Kind == DecisionKind.RunStarted);
        }
        finally
        {
            DenyListing(folder, deny: false);
        }
    }

    [Fact]
    public async Task Every_step_that_runs_agent_influenced_code_is_checked_before_and_after_against_the_run_baseline()
    {
        using var harness = new EngineHarness();
        var steps = new List<string>();
        void Note(string step)
        {
            lock (steps)
            {
                steps.Add(step);
            }
        }

        harness.Worktrees.OnCapture = _ => Note("capture");
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.cs", "a")) with { OnStart = _ => Note("agent T1") });
        harness.Verification.Script = (scope, step) =>
        {
            Note($"verify {scope.Task} {step.Id}");
            return null;
        };
        harness.Integrator.FailVerification = request =>
        {
            Note($"integrate {request.Task}");
            return false;
        };
        harness.Review.OnReview = request => Note($"review {request.ReviewTask}");
        var plan = Plan(
            Work("T1").Verified("build"),
            Node("L1", WorkTaskKind.ToolRun).After(On("T1")),
            Node("I1", WorkTaskKind.Integration).After(On("L1")),
            Node("R1", WorkTaskKind.Review).After(On("I1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        Assert.Equal(
            [
                "capture",
                "capture", "agent T1", "capture",
                "capture", "verify T1 build", "capture",
                "capture", "verify L1 lint", "capture",
                "capture", "verify I1 build", "verify I1 test", "integrate I1", "capture",
                "capture", "review R1", "capture",
            ],
            steps);
    }

    [Fact]
    public async Task A_change_made_by_the_agents_code_during_verification_fails_the_task_alerts_and_stops_dispatching()
    {
        using var harness = new EngineHarness();
        harness.Verification.Script = (scope, step) =>
        {
            // The build and the tests of T1 run the agent's code: it moves the user's HEAD and dirties the tree.
            if (scope.Task == new TaskId("T1") && step.Id == "test")
            {
                harness.Worktrees.UserState = harness.Worktrees.UserState with { HeadCommit = new string('e', 40), PorcelainStatus = [" M src/Program.cs"] };
            }

            return null;
        };
        var plan = Plan(Work("T1").Verified("build", "test"), Work("T2"));

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("T1").Status);
        Assert.Equal(0, run.Snapshot().View("T1").RetryCount);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Equal([new TaskId("T1")], alert.Tasks);
        Assert.Contains("HEAD moved", alert.Message, StringComparison.Ordinal);
        Assert.Contains("Status line added:  M src/Program.cs", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_made_during_a_tool_run_fails_it_alerts_and_stops_dispatching()
    {
        using var harness = new EngineHarness();
        harness.Verification.Script = (scope, step) =>
        {
            if (scope.Task == new TaskId("L1"))
            {
                harness.Worktrees.UserState = harness.Worktrees.UserState with { HeadCommit = new string('f', 40) };
            }

            return null;
        };
        var plan = Plan(Work("T1"), Node("L1", WorkTaskKind.ToolRun).After(On("T1")), Work("T2"));

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("L1").Status);
        Assert.Equal(0, run.Snapshot().View("L1").RetryCount);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Equal([new TaskId("L1")], alert.Tasks);
        Assert.Contains("HEAD moved", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_made_during_the_integration_fails_it_alerts_and_the_judge_is_never_launched()
    {
        using var harness = new EngineHarness();
        harness.Integrator.FailVerification = _ =>
        {
            // The build and the tests of the merged code run in _integration: the change happens there.
            harness.Worktrees.UserState = harness.Worktrees.UserState with { HeadCommit = new string('a', 40) };
            return false;
        };
        var plan = Plan(Work("T1"), Node("I1", WorkTaskKind.Integration).After(On("T1")), Node("R1", WorkTaskKind.Review).After(On("I1")), Work("T2"));

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("I1").Status);
        Assert.Equal(WorkTaskStatus.Pending, run.Snapshot().View("T2").Status);
        Assert.Empty(harness.Review.Requests);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Equal([new TaskId("I1")], alert.Tasks);
    }

    [Fact]
    public async Task A_change_between_two_steps_is_never_absorbed_by_the_first_check_of_the_next_one()
    {
        using var harness = new EngineHarness();
        harness.Evidence.FailWhen = draft =>
        {
            // After T1 was checked for the last time and before T2 is checked for the first time.
            if (draft.Title == "Prompt de T2 (intento 1)")
            {
                harness.Worktrees.UserState = harness.Worktrees.UserState with { StashCommit = new string('5', 40) };
            }

            return null;
        };
        var plan = Plan(Work("T1"), Work("T2"));

        await using var run = await harness.StartAsync(plan, new RunOptions(MaxParallelTasks: 1));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Completed, run.Snapshot().View("T1").Status);
        Assert.Equal(["T1"], harness.Claude.Starts.Select(s => s.Request.TaskId));
        Assert.DoesNotContain(harness.Payloads<AgentRunStartedPayload>(), p => p.Payload.Task == new TaskId("T2"));
        var (_, alert) = Assert.Single(harness.Alerts("user-repository.changed"));
        Assert.Equal([new TaskId("T2")], alert.Tasks);
        Assert.Contains("Stash changed", alert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_during_the_review_fails_the_review_too()
    {
        using var harness = new EngineHarness();
        harness.Review.OnReview = _ => harness.Worktrees.UserState = harness.Worktrees.UserState with { HeadCommit = new string('d', 40) };
        var plan = Plan(Work("T1"), Node("R1", WorkTaskKind.Review).After(On("T1")));

        await using var run = await harness.StartAsync(plan);
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.Equal(WorkTaskStatus.Failed, run.Snapshot().View("R1").Status);
        Assert.Equal([new TaskId("R1")], Assert.Single(harness.Alerts("user-repository.changed")).Payload.Tasks);
    }

    [Fact]
    public async Task Each_agent_gets_its_own_skills_with_agent_over_project_over_global_precedence()
    {
        static SkillActivation On(string skill, SkillScopeKind scope, string? scopeId) => new(new SkillId(skill), scope, scopeId, true);
        static SkillActivation Off(string skill, SkillScopeKind scope, string? scopeId) => new(new SkillId(skill), scope, scopeId, false);
        using var harness = new EngineHarness(
            activations:
            [
                On("global", SkillScopeKind.Global, null),
                On("proyecto", SkillScopeKind.Project, "p1"),
                Off("proyecto", SkillScopeKind.Agent, "ejecutor2"),
                On("solo-ejecutor", SkillScopeKind.Agent, "ejecutor"),
            ],
            catalog: [FakeSkillCatalog.Vendored("global"), FakeSkillCatalog.Vendored("proyecto"), FakeSkillCatalog.Vendored("solo-ejecutor")]);
        var plan = Plan(Work("T1"), Work("T2", agent: "ejecutor2"));

        await using var run = await harness.StartAsync(plan);
        await run.CompleteAsync(harness);

        var byTask = harness.Materializer.Materialized.ToDictionary(m => m.Session.RunId[(run.Id.Value.Length + 1)..^2], m => m);
        Assert.Equal(["global", "proyecto", "solo-ejecutor"], byTask["T1"].Skills);
        Assert.Equal(["global"], byTask["T2"].Skills);
        Assert.All(harness.Materializer.Materialized, m => Assert.Equal(ProviderId.ClaudeCode, m.Cli));
        var t1 = harness.Claude.Starts.Single(s => s.Request.TaskId == "T1").Request;
        Assert.Equal(byTask["T1"].Directory, t1.SkillsDirectory);
        Assert.Equal(t1.WorkingDirectory, byTask["T1"].Session.WorkingDirectory);
        var started = harness.Payloads<AgentRunStartedPayload>().Single(p => p.Payload.Task == new TaskId("T1")).Payload;
        Assert.Equal([new SkillId("global"), new SkillId("proyecto"), new SkillId("solo-ejecutor")], started.Skills);
        Assert.Equal(started.Skills, harness.Payloads<InstructionsIssuedPayload>().Single(p => p.Payload.Task == new TaskId("T1")).Payload.Skills);
    }

    [Fact]
    public async Task Skills_are_released_after_success_failure_and_cancellation()
    {
        using var harness = new EngineHarness(
            activations: [new SkillActivation(new SkillId("s"), SkillScopeKind.Global, null, true)],
            catalog: [FakeSkillCatalog.Vendored("s")]);
        harness.Claude.Script("ejecutor", "T2", 1, AgentScript.Failing());
        harness.Claude.Script("ejecutor", "T3", 1, new AgentScript { Hang = true });
        var plan = Plan(Work("T1"), Work("T2") with { MaxRetries = 0 }, Work("T3"));

        await using var run = await harness.StartAsync(plan);
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Completed);
        await harness.WaitForStatusAsync(run, "T2", WorkTaskStatus.Failed);
        await harness.WaitForStatusAsync(run, "T3", WorkTaskStatus.Running);
        await run.CancelAsync("fin");
        await run.CompleteAsync(harness);

        Assert.Equal(3, harness.Materializer.Materialized.Count);
        Assert.Equal(
            harness.Materializer.Materialized.Select(m => m.Directory).Order(StringComparer.Ordinal),
            harness.Materializer.Cleaned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Skills_are_released_when_the_launch_fails()
    {
        using var harness = new EngineHarness(
            activations: [new SkillActivation(new SkillId("s"), SkillScopeKind.Global, null, true)],
            catalog: [FakeSkillCatalog.Vendored("s")]);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { ThrowOnStart = new InvalidOperationException("sin sesión") });

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.Blocked);

        Assert.Single(harness.Materializer.Materialized);
        Assert.Equal([harness.Materializer.Materialized[0].Directory], harness.Materializer.Cleaned);
    }

    [Fact]
    public async Task Skills_are_released_when_the_app_closes_during_a_run()
    {
        using var harness = new EngineHarness(
            activations: [new SkillActivation(new SkillId("s"), SkillScopeKind.Global, null, true)],
            catalog: [FakeSkillCatalog.Vendored("s")]);
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });

        var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForStatusAsync(run, "T1", WorkTaskStatus.Running);
        await run.DisposeAsync();
        await run.CompleteAsync(harness);

        await RunTestSupport.WaitUntilAsync(() => harness.Materializer.Cleaned.Count == 1, "skills released");
    }

    private sealed class ThrowingProvisioner : ISkillProvisioner
    {
        public Task<ProvisionedSkills> ProvisionAsync(string projectId, AgentStudio.Domain.Agents.AgentDefinition agent, string runKey, string workingDirectory, CancellationToken ct) =>
            throw new IOException("catálogo ilegible");

        public void Release(ProvisionedSkills skills)
        {
        }
    }

    [Fact]
    public async Task A_task_whose_skills_cannot_be_prepared_is_blocked_before_launching_anything()
    {
        using var harness = new EngineHarness();
        harness.Skills = new ThrowingProvisioner();

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await harness.WaitForPhaseAsync(run, RunPhase.Blocked);

        Assert.Empty(harness.Claude.Starts);
        Assert.Empty(harness.Payloads<AgentRunStartedPayload>());
        Assert.Contains("catálogo ilegible", harness.Payloads<TaskStatusChangedPayload>().Single().Payload.Reason, StringComparison.Ordinal);
        Assert.Equal(0, run.Snapshot().AgentRunsUsed);
    }
}
