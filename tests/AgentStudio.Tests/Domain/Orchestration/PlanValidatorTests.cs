using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration;

public sealed class PlanValidatorTests
{
    private sealed record Case(PlanDefinition Plan, PlanContext Context, params PlanViolationCode[] AlsoExpected);

    private static IReadOnlyList<PlanViolation> Check(IReadOnlyList<TaskSpec> tasks, PlanContext? context = null, string? author = "orquestador") =>
        PlanValidator.Validate(Plan(tasks, author: author), context ?? Context());

    private static IReadOnlyList<PlanViolation> Check(Case c) => PlanValidator.Validate(c.Plan, c.Context);

    private static HashSet<PlanViolationCode> Codes(IEnumerable<PlanViolation> violations) => violations.Select(v => v.Code).ToHashSet();

    /// <summary>
    /// One way to break the valid plan for each rule. Adding a code without adding its case here fails the test below,
    /// which is what makes "one test per code" structural.
    /// </summary>
    private static readonly Dictionary<PlanViolationCode, Func<Case>> Cases = new()
    {
        [PlanViolationCode.EmptyPlan] = () => new(Plan([]), Context()),
        [PlanViolationCode.TooManyTasks] = () => new(Plan(), Context(new PlanLimits(MaxTasks: 7))),
        [PlanViolationCode.InvalidTaskId] = () => new(Plan(Replace("T8", t => t with { Id = Tid("8bad") })), Context()),
        [PlanViolationCode.DuplicateTaskId] = () => new(Plan([.. ValidTasks(), ValidTasks()[7]]), Context()),
        [PlanViolationCode.TitleRequired] = () => new(Plan(Replace("T8", t => t with { Title = "  " })), Context()),
        [PlanViolationCode.ObjectiveRequired] = () => new(Plan(Replace("T1", t => t with { Objective = string.Empty })), Context()),
        [PlanViolationCode.UnknownDependency] = () => new(Plan(Replace("T8", t => t with { DependsOn = [Dep("T7"), Dep("ghost")] })), Context()),
        [PlanViolationCode.DuplicateDependency] = () => new(Plan(Replace("T3", t => t with { DependsOn = [Dep("T1"), Dep("T2"), Dep("T2")] })), Context()),
        [PlanViolationCode.DependencyCycle] = () => new(Plan(Replace("T1", t => t with { DependsOn = [Dep("T8")] })), Context()),
        [PlanViolationCode.UnknownAgent] = () => new(Plan(Replace("T1", t => t with { Agent = Aid("ghost") })), Context()),
        [PlanViolationCode.AgentRequired] = () => new(Plan(Replace("T2", t => t with { Agent = null })), Context()),
        [PlanViolationCode.OrchestratorHasTask] = () => new(Plan(Replace("T2", t => t with { Agent = Aid("orquestador") })), Context()),
        [PlanViolationCode.JudgeOutsideReview] = () => new(Plan(Replace("T2", t => t with { Agent = Aid("juez2") })), Context()),
        [PlanViolationCode.ReviewerNotJudge] = () => new(Plan(Replace("T7", t => t with { Agent = Aid("ejecutorG") })), Context()),
        [PlanViolationCode.JudgeVendorNotIndependent] = () => new(Plan(Replace("T7", t => t with { Agent = Aid("juezA") })), Context()),
        [PlanViolationCode.ConditionWithoutPredicate] = () => new(Plan(Replace("T4", t => t with { Condition = null })), Context()),
        [PlanViolationCode.PredicateOnNonCondition] = () => new(Plan(Replace("T8", t => t with { Condition = new TaskSucceeded(Tid("T7")) })), Context()),
        [PlanViolationCode.PredicateNotAncestor] = () => new(Plan(Replace("T4", t => t with { Condition = new TestsPassed(Tid("T8")) })), Context()),
        [PlanViolationCode.BranchRequired] = () => new(Plan(Replace("T5", t => t with { DependsOn = [Dep("T4")] })), Context()),
        [PlanViolationCode.BranchOnNonCondition] = () => new(Plan(Replace("T7", t => t with { DependsOn = [Dep("T5", "yes"), Dep("T6")] })), Context()),
        [PlanViolationCode.UnknownBranch] = () => new(Plan(Replace("T5", t => t with { DependsOn = [Dep("T4", "maybe")] })), Context()),
        [PlanViolationCode.FanInWithoutIntegration] = () => new(Plan(Replace("T3", t => t with { Kind = WorkTaskKind.ToolRun, ToolId = "lint" })), Context()),
        [PlanViolationCode.UnsafeWriteScope] = () => new(Plan(Replace("T1", t => t with { WriteScope = ["../escape/"] })), Context()),
        [PlanViolationCode.WriteScopeRequired] = () => new(Plan(Replace("T2", t => t with { WriteScope = [] })), Context()),
        [PlanViolationCode.OverlappingWriteScopes] = () => new(Plan(Replace("T2", t => t with { WriteScope = ["src/Calc/sub/"] })), Context()),
        [PlanViolationCode.UnknownRequirement] = () => new(Plan(Replace("T1", t => t with { Covers = [Rid("R1"), Rid("R9")] })), Context()),
        [PlanViolationCode.MandatoryRequirementUncovered] = () => new(Plan(Replace("T1", t => t with { Covers = [] })), Context()),
        [PlanViolationCode.UnknownVerificationStep] = () => new(Plan(Replace("T1", t => t with { VerificationSteps = ["build", "deploy"] })), Context()),
        [PlanViolationCode.ToolRequired] = () => new(Plan(Replace("T8", t => t with { ToolId = null })), Context()),
        [PlanViolationCode.UnknownTool] = () => new(Plan(Replace("T8", t => t with { ToolId = "rm-rf" })), Context()),
        [PlanViolationCode.RetriesNotAllowed] = () => new(Plan(Replace("T7", t => t with { MaxRetries = 1 })), Context()),
        [PlanViolationCode.RetriesOutOfRange] = () => new(Plan(Replace("T1", t => t with { MaxRetries = 9 })), Context()),
        [PlanViolationCode.InvalidTimeout] = () => new(Plan(Replace("T1", t => t with { Timeout = TimeSpan.Zero })), Context()),
        [PlanViolationCode.DebateNotAvailable] = () => new(Plan(Replace("T8", t => t with { Kind = WorkTaskKind.Debate })), Context()),
        [PlanViolationCode.IntegrationWithoutVerification] = () => new(Plan(Replace("T3", t => t with { VerificationSteps = [] })), Context()),
        [PlanViolationCode.IntegrationsNotOrdered] = () => new(Plan([.. ValidTasks(), SecondIntegration("T9", Dep("T1"))]), Context()),

        // T6 (ejecutor, writes docs/) has no verification steps; T1 (ejecutor too) has them.
        [PlanViolationCode.VerificationRequiredByPolicy] = () => new(Plan(), Context(roster: WithPolicy("ejecutor", new ReviewPolicy(false, true)))),

        // T9 writes after the review T7: no review comes after every writer of ejecutor2 (T2 and T9).
        [PlanViolationCode.ReviewRequiredByPolicy] = () => new(Plan([.. ValidTasks(), LateWriter("T9")]), Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(true, false)))),

        // T2 (ejecutor2) is verified, but only with the build: no step of it runs tests.
        [PlanViolationCode.TestStepRequiredByPolicy] = () => new(Plan(), Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(false, true)))),

        // Phase 13 (F13-5): ejecutor2 may only write lib/, and T2 writes src/Other/.
        [PlanViolationCode.WriteScopeOutsideAgentArea] = () => new(Plan(), Context(roster: [.. Roster().Select(a => a.Id == Aid("ejecutor2") ? a with { PathGrants = [new PathGrant("lib/", FileAccessLevel.ReadWrite)] } : a)])),
    };

    /// <summary>The roster with the review policy of <paramref name="agent"/> replaced.</summary>
    private static IReadOnlyList<AgentDefinition> WithPolicy(string agent, ReviewPolicy policy) =>
        [.. Roster().Select(a => a.Id == Aid(agent) ? a with { Review = policy } : a)];

    /// <summary>A task of ejecutor2 that writes lib/ after the review of the valid plan.</summary>
    private static TaskSpec LateWriter(string id) =>
        Spec(id) with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = ["lib/"], DependsOn = [Dep("T7")] };

    /// <summary>An integration of the code of <paramref name="dependencies"/>, verified with the build.</summary>
    private static TaskSpec SecondIntegration(string id, params DependencySpec[] dependencies) =>
        Spec(id, WorkTaskKind.Integration) with { DependsOn = dependencies, VerificationSteps = ["build"] };

    public static TheoryData<PlanViolationCode> AllCodes() => [.. Enum.GetValues<PlanViolationCode>()];

    [Fact]
    public void Every_violation_code_has_a_case_that_breaks_the_valid_plan_in_exactly_that_way()
    {
        Assert.Equal(
            Enum.GetValues<PlanViolationCode>().Order(),
            Cases.Keys.Order());
    }

    [Fact]
    public void The_valid_plan_has_no_violations()
    {
        Assert.Empty(Check(ValidTasks()));
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Each_rule_reports_its_code_and_nothing_else(PlanViolationCode code)
    {
        var broken = Cases[code]();

        var violations = Check(broken);

        Assert.Equal(new[] { code }.Concat(broken.AlsoExpected).Order(), Codes(violations).Order());
        Assert.All(violations, v => Assert.False(string.IsNullOrWhiteSpace(v.Message)));
    }

    // ---- the codes name the task they are about ----

    [Theory]
    [InlineData(PlanViolationCode.InvalidTaskId, "8bad")]
    [InlineData(PlanViolationCode.DuplicateTaskId, "T8")]
    [InlineData(PlanViolationCode.TitleRequired, "T8")]
    [InlineData(PlanViolationCode.ObjectiveRequired, "T1")]
    [InlineData(PlanViolationCode.UnknownDependency, "T8")]
    [InlineData(PlanViolationCode.DuplicateDependency, "T3")]
    [InlineData(PlanViolationCode.UnknownAgent, "T1")]
    [InlineData(PlanViolationCode.AgentRequired, "T2")]
    [InlineData(PlanViolationCode.JudgeVendorNotIndependent, "T7")]
    [InlineData(PlanViolationCode.ConditionWithoutPredicate, "T4")]
    [InlineData(PlanViolationCode.PredicateNotAncestor, "T4")]
    [InlineData(PlanViolationCode.BranchRequired, "T5")]
    [InlineData(PlanViolationCode.UnsafeWriteScope, "T1")]
    [InlineData(PlanViolationCode.WriteScopeRequired, "T2")]
    [InlineData(PlanViolationCode.OverlappingWriteScopes, "T1")]
    [InlineData(PlanViolationCode.UnknownRequirement, "T1")]
    [InlineData(PlanViolationCode.ToolRequired, "T8")]
    [InlineData(PlanViolationCode.RetriesNotAllowed, "T7")]
    [InlineData(PlanViolationCode.InvalidTimeout, "T1")]
    [InlineData(PlanViolationCode.DebateNotAvailable, "T8")]
    public void A_violation_names_the_task_it_is_about(PlanViolationCode code, string task)
    {
        var violations = Check(Cases[code]());

        Assert.Contains(violations, v => v.Code == code && v.Task == Tid(task));
    }

    [Theory]
    [InlineData(PlanViolationCode.EmptyPlan)]
    [InlineData(PlanViolationCode.TooManyTasks)]
    [InlineData(PlanViolationCode.MandatoryRequirementUncovered)]
    public void Plan_level_violations_name_no_task(PlanViolationCode code)
    {
        Assert.All(Check(Cases[code]()).Where(v => v.Code == code), v => Assert.Null(v.Task));
    }

    // ---- identifiers ----

    [Theory]
    [InlineData("A", true)]
    [InlineData("a", true)]
    [InlineData("T1", true)]
    [InlineData("build_and-test2", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)] // 32
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)] // 33
    [InlineData("1abc", false)]
    [InlineData("_abc", false)]
    [InlineData("-abc", false)]
    [InlineData("has space", false)]
    [InlineData("a/b", false)]
    [InlineData("a.b", false)]
    [InlineData("año", false)]
    [InlineData("a:b", false)]
    public void Task_ids_are_safe_for_refs_and_paths(string id, bool valid)
    {
        var violations = Check(Replace("T8", t => t with { Id = Tid(id) }));

        Assert.Equal(!valid, violations.Any(v => v.Code == PlanViolationCode.InvalidTaskId));
    }

    [Fact]
    public void A_plan_with_exactly_the_maximum_number_of_tasks_is_accepted()
    {
        Assert.DoesNotContain(Check(ValidTasks(), Context(new PlanLimits(MaxTasks: 8))), v => v.Code == PlanViolationCode.TooManyTasks);
    }

    [Fact]
    public void An_empty_plan_is_only_reported_as_empty()
    {
        Assert.Equal([PlanViolationCode.EmptyPlan], Check([]).Select(v => v.Code));
    }

    // ---- graph ----

    [Fact]
    public void A_task_that_depends_on_itself_is_a_cycle()
    {
        var violations = Check(Replace("T8", t => t with { DependsOn = [Dep("T7"), Dep("T8")] }));

        var cycle = Assert.Single(violations, v => v.Code == PlanViolationCode.DependencyCycle);
        Assert.Equal(Tid("T8"), cycle.Task);
        Assert.Contains("T8 -> T8", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_is_reported_with_its_path()
    {
        var violations = Check(Cases[PlanViolationCode.DependencyCycle]().Plan.Tasks);

        var cycle = Assert.Single(violations, v => v.Code == PlanViolationCode.DependencyCycle);
        Assert.Contains("T1 -> T8 -> T7", cycle.Message, StringComparison.Ordinal);
        Assert.EndsWith("-> T1.", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_separate_cycles_are_both_reported()
    {
        var tasks = new[] { Spec("A") with { DependsOn = [Dep("B")] }, Spec("B") with { DependsOn = [Dep("A")] }, Spec("C") with { DependsOn = [Dep("D")] }, Spec("D") with { DependsOn = [Dep("C")] } };

        var cycles = Check(tasks, Context()).Where(v => v.Code == PlanViolationCode.DependencyCycle).ToList();

        Assert.Equal(2, cycles.Count);
    }

    [Fact]
    public void Structural_mistakes_do_not_trigger_the_rules_that_need_a_sound_graph()
    {
        // A cycle also breaks the fan-in and overlap analyses; reporting them as well would only bury the cause.
        var violations = Check(Replace("T1", t => t with { DependsOn = [Dep("T8")], WriteScope = ["docs/"] }));

        Assert.Equal([PlanViolationCode.DependencyCycle], Codes(violations));
    }

    [Fact]
    public void A_duplicated_writer_is_reported_as_a_duplicate_not_as_a_scope_overlap_with_itself()
    {
        var violations = Check([.. ValidTasks(), ValidTasks()[0]]);

        Assert.Equal([PlanViolationCode.DuplicateTaskId], Codes(violations));
    }

    [Fact]
    public void A_branch_edge_may_leave_only_a_condition_and_must_be_yes_or_no()
    {
        Assert.Contains(Check(Replace("T5", t => t with { DependsOn = [Dep("T4", "YES")] })), v => v.Code == PlanViolationCode.UnknownBranch);
        Assert.Contains(Check(Replace("T5", t => t with { DependsOn = [Dep("T4", " ")] })), v => v.Code == PlanViolationCode.BranchRequired);
        Assert.Empty(Check(Replace("T5", t => t with { DependsOn = [Dep("T4", "yes")] })));
        Assert.Empty(Check(Replace("T5", t => t with { DependsOn = [Dep("T4", "no")] })));
    }

    // ---- conditions ----

    [Fact]
    public void A_predicate_may_read_any_ancestor_but_not_itself_a_descendant_or_an_unknown_task()
    {
        Assert.Empty(Check(Replace("T4", t => t with { Condition = new TaskSucceeded(Tid("T1")) }))); // grandparent
        Assert.Empty(Check(Replace("T4", t => t with { Condition = new OutputEquals(Tid("T2"), "k", "v") })));

        foreach (var cited in new[] { "T4", "T5", "T8", "ghost" })
        {
            var violations = Check(Replace("T4", t => t with { Condition = new PathsChanged(Tid(cited), "src/") }));
            Assert.Contains(violations, v => v.Code == PlanViolationCode.PredicateNotAncestor && v.Task == Tid("T4"));
        }
    }

    // ---- agents ----

    [Fact]
    public void Only_a_judge_or_an_auditor_can_review()
    {
        Assert.Empty(Check(Replace("T7", t => t with { Agent = Aid("auditor") })));
        Assert.Contains(Check(Replace("T7", t => t with { Agent = Aid("orquestador") })), v => v.Code == PlanViolationCode.OrchestratorHasTask);
    }

    [Fact]
    public void An_auditor_cannot_take_work_either()
    {
        Assert.Contains(Check(Replace("T2", t => t with { Agent = Aid("auditor") })), v => v.Code == PlanViolationCode.JudgeOutsideReview);
    }

    [Fact]
    public void Tasks_the_system_runs_need_no_agent()
    {
        Assert.Null(ValidTasks().Single(t => t.Id == Tid("T3")).Agent);
        Assert.Empty(Check(ValidTasks()));
    }

    // ---- judge independence (Phase 3, decision 7) ----

    [Fact]
    public void A_judge_may_not_share_a_vendor_with_the_orchestrator()
    {
        var violation = Assert.Single(Check(Cases[PlanViolationCode.JudgeVendorNotIndependent]().Plan.Tasks));

        Assert.Equal(PlanViolationCode.JudgeVendorNotIndependent, violation.Code);
        Assert.Contains("orchestrator", violation.Message, StringComparison.Ordinal);
        Assert.Contains("anthropic", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_judge_may_not_share_a_vendor_with_an_executor_even_when_the_orchestrator_differs()
    {
        // Orchestrator on Anthropic, one executor on OpenAI, judge on OpenAI.
        var tasks = Replace("T2", t => t with { Agent = Aid("ejecutorO") });

        var violation = Assert.Single(Check(tasks));

        Assert.Equal(PlanViolationCode.JudgeVendorNotIndependent, violation.Code);
        Assert.Equal(Tid("T7"), violation.Task);
        Assert.Contains("executor 'ejecutorO'", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("orchestrator", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_agents_that_do_no_work_in_the_plan_do_not_count_as_executors()
    {
        // ejecutorO (OpenAI) is in the roster but assigned nothing, and the judge is on OpenAI: still independent.
        Assert.Contains(Roster(), a => a.Id == Aid("ejecutorO"));
        Assert.Empty(Check(ValidTasks()));
    }

    [Fact]
    public void The_author_of_the_plan_counts_as_a_producer_even_without_the_orchestrator_role()
    {
        // The plan was written by "ejecutorO" (OpenAI); the judge is on OpenAI.
        var violations = Check(ValidTasks(), Context(), author: "ejecutorO");

        var violation = Assert.Single(violations);
        Assert.Equal(PlanViolationCode.JudgeVendorNotIndependent, violation.Code);
    }

    [Fact]
    public void A_plan_written_by_the_user_is_still_checked_against_the_orchestrator_of_the_roster()
    {
        var plan = Plan(Replace("T7", t => t with { Agent = Aid("juezA") }), PlanSource.User, author: null);

        Assert.Contains(PlanValidator.Validate(plan, Context()), v => v.Code == PlanViolationCode.JudgeVendorNotIndependent);
    }

    [Fact]
    public void Every_review_task_is_checked()
    {
        var second = Spec("T9", WorkTaskKind.Review) with { Agent = Aid("juezA"), DependsOn = [Dep("T7")], MaxRetries = 0, Workspace = WorkspaceMode.ReadOnly };

        var violations = Check([.. ValidTasks(), second]);

        var violation = Assert.Single(violations);
        Assert.Equal(Tid("T9"), violation.Task);
    }

    [Fact]
    public void A_judge_that_also_executes_is_not_independent_of_itself()
    {
        var tasks = Replace("T2", t => t with { Agent = Aid("juez") });

        var codes = Codes(Check(tasks));

        Assert.Contains(PlanViolationCode.JudgeOutsideReview, codes);
        Assert.Contains(PlanViolationCode.JudgeVendorNotIndependent, codes);
    }

    [Fact]
    public void A_roster_without_a_second_vendor_cannot_host_a_review()
    {
        var onlyAnthropic = Roster().Where(a => a.Model.Vendor == AgentStudio.Domain.Providers.VendorId.Anthropic).ToList();
        onlyAnthropic.Add(Agent("juezOnly", AgentRole.Judge, AgentStudio.Domain.Providers.VendorId.Anthropic));
        var tasks = Replace("T7", t => t with { Agent = Aid("juezOnly") });

        Assert.Contains(Check(tasks, Context(roster: onlyAnthropic)), v => v.Code == PlanViolationCode.JudgeVendorNotIndependent);
    }

    // ---- scopes ----

    [Theory]
    [InlineData(".git/hooks/")]
    [InlineData(".agentstudio/")]
    [InlineData("/etc/")]
    [InlineData("C:/Windows/")]
    [InlineData("src/../../x/")]
    [InlineData("src/*.cs")]
    [InlineData(".")]
    [InlineData("")]
    public void Unsafe_write_scopes_are_rejected(string entry)
    {
        var violations = Check(Replace("T1", t => t with { WriteScope = [entry, "src/Calc/"] }));

        Assert.Contains(violations, v => v.Code == PlanViolationCode.UnsafeWriteScope && v.Message.Contains(entry, StringComparison.Ordinal));
    }

    [Fact]
    public void Tasks_that_cannot_run_at_the_same_time_may_share_a_scope()
    {
        // T6 runs after T1 (through the integration and the condition), so it may write where T1 wrote.
        Assert.Empty(Check(Replace("T6", t => t with { WriteScope = ["src/Calc/", "docs/"] })));
    }

    [Fact]
    public void Concurrent_tasks_with_overlapping_exact_files_or_folders_are_rejected()
    {
        Assert.Contains(Check(Replace("T2", t => t with { WriteScope = ["src/Calc/Multiply.cs"] })), v => v.Code == PlanViolationCode.OverlappingWriteScopes);
        Assert.Contains(Check(Replace("T2", t => t with { WriteScope = ["src/"] })), v => v.Code == PlanViolationCode.OverlappingWriteScopes);
        Assert.Contains(Check(Replace("T2", t => t with { WriteScope = ["SRC/CALC/"] })), v => v.Code == PlanViolationCode.OverlappingWriteScopes);
        Assert.DoesNotContain(Check(Replace("T2", t => t with { WriteScope = ["src/CalcExtra/"] })), v => v.Code == PlanViolationCode.OverlappingWriteScopes);
    }

    [Fact]
    public void A_task_that_only_reads_does_not_compete_for_a_scope()
    {
        var tasks = Replace("T2", t => t with { Workspace = WorkspaceMode.ReadOnly, WriteScope = ["src/Calc/"] });

        Assert.DoesNotContain(Check(tasks), v => v.Code == PlanViolationCode.OverlappingWriteScopes);
    }

    // ---- fan-in ----

    [Fact]
    public void Two_integrations_that_could_run_at_the_same_time_are_rejected_naming_both()
    {
        var violation = Assert.Single(Check([.. ValidTasks(), SecondIntegration("T9", Dep("T1"))]), v => v.Code == PlanViolationCode.IntegrationsNotOrdered);

        Assert.Equal(Tid("T9"), violation.Task);
        Assert.Contains("'T3'", violation.Message, StringComparison.Ordinal);
        Assert.Contains("'T9'", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrations_in_a_chain_are_accepted_directly_or_through_other_tasks()
    {
        Assert.Empty(Check([.. ValidTasks(), SecondIntegration("T9", Dep("T3"))]));
        Assert.Empty(Check([.. ValidTasks(), SecondIntegration("T9", Dep("T8"))]));
    }

    [Fact]
    public void Each_pair_of_integrations_that_is_not_ordered_is_reported_once()
    {
        // T3 integrates T1 and T2; T9 and Ta both integrate T1 and depend on nothing else: three unordered pairs.
        var violations = Check([.. ValidTasks(), SecondIntegration("T9", Dep("T1")), SecondIntegration("Ta", Dep("T1"))])
            .Where(v => v.Code == PlanViolationCode.IntegrationsNotOrdered)
            .Select(v => v.Task!.Value.Value)
            .ToList();

        Assert.Equal(["T9", "Ta", "Ta"], violations);
    }

    // ---- the review policy of the executors ----

    [Fact]
    public void A_writer_whose_policy_asks_for_tests_needs_verification_steps()
    {
        var violation = Assert.Single(Check(ValidTasks(), Context(roster: WithPolicy("ejecutor", new ReviewPolicy(false, true)))));

        Assert.Equal((PlanViolationCode.VerificationRequiredByPolicy, Tid("T6")), (violation.Code, violation.Task));
        Assert.Contains("'ejecutor'", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_that_only_reads_needs_no_verification_even_when_its_agent_asks_for_tests()
    {
        var tasks = Replace("T6", t => t with { Workspace = WorkspaceMode.ReadOnly, WriteScope = [] });

        Assert.Empty(Check(tasks, Context(roster: WithPolicy("ejecutor", new ReviewPolicy(false, true)))));
    }

    [Fact]
    public void A_writer_whose_policy_asks_for_review_needs_a_review_after_it()
    {
        var violation = Assert.Single(Check([.. ValidTasks(), LateWriter("T9")], Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(true, false)))));

        Assert.Equal((PlanViolationCode.ReviewRequiredByPolicy, Tid("T9")), (violation.Code, violation.Task));
        Assert.Contains("'ejecutor2'", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_any_review_every_writer_whose_policy_asks_for_one_is_reported()
    {
        TaskSpec Writer(string id, string area) =>
            Spec(id) with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = [area], Covers = id == "A1" ? [Rid("R1")] : [] };

        var violations = Check([Writer("A1", "src/A/"), Writer("A2", "src/B/")], Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(true, false))));

        Assert.Equal([(PlanViolationCode.ReviewRequiredByPolicy, "A1"), (PlanViolationCode.ReviewRequiredByPolicy, "A2")], violations.Select(v => (v.Code, v.Task!.Value.Value)));
    }

    [Fact]
    public void One_review_must_come_after_every_such_writer_two_reviews_of_one_writer_each_are_not_enough()
    {
        TaskSpec Writer(string id, string area) =>
            Spec(id) with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = [area], Covers = id == "A1" ? [Rid("R1")] : [] };
        TaskSpec ReviewOf(string id, string writer) =>
            Spec(id, WorkTaskKind.Review) with { Agent = Aid("juez"), Workspace = WorkspaceMode.ReadOnly, DependsOn = [Dep(writer)], MaxRetries = 0 };
        var context = Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(true, false)));

        var split = Check([Writer("A1", "src/A/"), Writer("A2", "src/B/"), ReviewOf("R1", "A1"), ReviewOf("R2", "A2")], context);
        var joined = Check(
            [
                Writer("A1", "src/A/"),
                Writer("A2", "src/B/"),
                SecondIntegration("I1", Dep("A1"), Dep("A2")),
                ReviewOf("R1", "I1"),
            ],
            context);

        // The review that covers most of them (R1, the first of a tie) leaves A2 uncovered.
        var violation = Assert.Single(split);
        Assert.Equal((PlanViolationCode.ReviewRequiredByPolicy, Tid("A2")), (violation.Code, violation.Task));
        Assert.Empty(joined);
    }

    [Fact]
    public void A_writer_whose_policy_asks_for_tests_and_runs_the_test_step_is_valid()
    {
        var tasks = Replace("T2", t => t with { VerificationSteps = ["build", "test"] });

        Assert.Empty(Check(tasks, Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(false, true)))));
    }

    [Fact]
    public void Without_the_kinds_of_the_steps_a_build_cannot_be_told_from_a_test_and_only_the_missing_steps_are_reported()
    {
        var context = Context(roster: WithPolicy("ejecutor2", new ReviewPolicy(false, true))) with { TestRunSteps = null };

        Assert.Empty(Check(ValidTasks(), context));
        Assert.Equal(
            [PlanViolationCode.VerificationRequiredByPolicy],
            Check(Replace("T2", t => t with { VerificationSteps = [] }), context).Select(v => v.Code));
    }

    [Fact]
    public void The_policy_of_an_agent_without_writing_tasks_asks_for_nothing()
    {
        var roster = WithPolicy("ejecutorG", new ReviewPolicy(true, true));

        Assert.Empty(Check(ValidTasks(), Context(roster: roster)));
    }

    [Fact]
    public void Parallel_code_may_be_merged_by_an_integration_task()
    {
        Assert.Empty(Check(ValidTasks()));
    }

    [Fact]
    public void Looking_through_conditions_approvals_and_reviews_the_fan_in_is_still_found()
    {
        var violations = Check(Cases[PlanViolationCode.FanInWithoutIntegration]().Plan.Tasks);

        var tasks = violations.Where(v => v.Code == PlanViolationCode.FanInWithoutIntegration).Select(v => v.Task!.Value.Value).Order().ToList();
        Assert.Equal(["T3", "T6"], tasks); // T7 and T8 build on T6 alone: T1 and T2 are already inside it
    }

    [Fact]
    public void A_review_after_two_parallel_writers_needs_an_integration_first()
    {
        var tasks = new[]
        {
            Spec("W1") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.Write, WriteScope = ["a/"], Covers = [Rid("R1")] },
            Spec("W2") with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = ["b/"] },
            Spec("RV", WorkTaskKind.Review) with { Agent = Aid("juez"), DependsOn = [Dep("W1"), Dep("W2")], MaxRetries = 0 },
        };

        var violation = Assert.Single(Check(tasks), v => v.Code == PlanViolationCode.FanInWithoutIntegration);

        Assert.Equal(Tid("RV"), violation.Task);
        Assert.Contains("'W1', 'W2'", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_only_agent_task_also_needs_a_single_code_base()
    {
        var tasks = new[]
        {
            Spec("W1") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.Write, WriteScope = ["a/"], Covers = [Rid("R1")] },
            Spec("W2") with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = ["b/"] },
            Spec("R") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.ReadOnly, DependsOn = [Dep("W1"), Dep("W2")] },
        };

        Assert.Contains(Check(tasks), v => v.Code == PlanViolationCode.FanInWithoutIntegration && v.Task == Tid("R"));
    }

    [Fact]
    public void Tasks_with_no_workspace_may_join_any_number_of_branches()
    {
        var tasks = new[]
        {
            Spec("W1") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.Write, WriteScope = ["a/"], Covers = [Rid("R1")] },
            Spec("W2") with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = ["b/"] },
            Spec("Notes") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.None, DependsOn = [Dep("W1"), Dep("W2")] },
            Spec("Ask", WorkTaskKind.HumanApproval) with { DependsOn = [Dep("W1"), Dep("W2")], MaxRetries = 0 },
        };

        Assert.Empty(Check(tasks));
    }

    [Fact]
    public void A_task_whose_second_code_ancestor_is_already_inside_the_first_has_no_fan_in()
    {
        var tasks = new[]
        {
            Spec("W1") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.Write, WriteScope = ["a/"], Covers = [Rid("R1")] },
            Spec("W2") with { Agent = Aid("ejecutor2"), Workspace = WorkspaceMode.Write, WriteScope = ["b/"], DependsOn = [Dep("W1")] },
            Spec("Last") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.ReadOnly, DependsOn = [Dep("W1"), Dep("W2")] },
        };

        Assert.Empty(Check(tasks));
    }

    // ---- requirements, references, limits ----

    [Fact]
    public void Only_the_uncovered_mandatory_requirements_are_listed()
    {
        var context = Context() with
        {
            Requirements = AgentStudio.Domain.Requirements.FrozenRequirementSet.Freeze(
                [Req("R1"), Req("R2"), Req("R3"), Req("R4", AgentStudio.Domain.Requirements.RequirementPriority.Desirable)],
                T0),
        };

        var uncovered = Check(ValidTasks(), context).Where(v => v.Code == PlanViolationCode.MandatoryRequirementUncovered).ToList();

        var only = Assert.Single(uncovered);
        Assert.Contains("'R3'", only.Message, StringComparison.Ordinal); // R1 and R2 are covered by T1 and T2; R4 is only desirable
    }

    [Fact]
    public void A_requirement_covered_by_any_kind_of_task_is_covered()
    {
        var tasks = Replace("T1", t => t with { Covers = [] });
        tasks = [.. tasks.Select(t => t.Id == Tid("T7") ? t with { Covers = [Rid("R1")] } : t)];

        Assert.DoesNotContain(Check(tasks), v => v.Code == PlanViolationCode.MandatoryRequirementUncovered);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Retries_must_be_between_zero_and_the_limit(int retries, bool violation)
    {
        var violations = Check(Replace("T1", t => t with { MaxRetries = retries }));

        Assert.Equal(violation, violations.Any(v => v.Code == PlanViolationCode.RetriesOutOfRange));
    }

    [Fact]
    public void Debate_nodes_are_accepted_once_they_are_available()
    {
        var violations = Check(Replace("T8", t => t with { Kind = WorkTaskKind.Debate }), Context(new PlanLimits(DebateNodesAvailable: true)));

        Assert.Empty(violations);
    }

    [Fact]
    public void Human_approval_tasks_cannot_be_retried_either()
    {
        Assert.Contains(Check(Replace("T5", t => t with { MaxRetries = 2 })), v => v.Code == PlanViolationCode.RetriesNotAllowed && v.Task == Tid("T5"));
    }

    [Fact]
    public void Several_mistakes_are_all_reported_in_one_pass()
    {
        var tasks = Replace("T1", t => t with { Objective = string.Empty, Agent = Aid("ghost"), MaxRetries = 9 });

        var codes = Codes(Check(tasks));

        Assert.True(codes.IsSupersetOf([PlanViolationCode.ObjectiveRequired, PlanViolationCode.UnknownAgent, PlanViolationCode.RetriesOutOfRange]));
    }

    // ---- determinism ----

    [Fact]
    public void The_violations_do_not_depend_on_the_order_the_tasks_arrive_in()
    {
        var random = new Random(26101503);
        foreach (var code in Enum.GetValues<PlanViolationCode>())
        {
            var original = Cases[code]();
            var expected = Check(original).Select(v => (v.Code, v.Task, v.Message)).ToList();
            var shuffled = PlanDefinition.Create(
                original.Plan.Summary,
                [.. original.Plan.Tasks.OrderBy(_ => random.Next())],
                original.Plan.Risks,
                original.Plan.Source,
                original.Plan.Author);

            Assert.Equal(expected, PlanValidator.Validate(shuffled, original.Context).Select(v => (v.Code, v.Task, v.Message)).ToList());
        }
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => PlanValidator.Validate(null!, Context()));
        Assert.Throws<ArgumentNullException>(() => PlanValidator.Validate(Plan(), null!));
    }

    // ---- properties over random plans ----

    private static IReadOnlyList<TaskSpec> RandomTasks(Random random)
    {
        var count = random.Next(1, 9);
        var tasks = new List<TaskSpec>();
        var coveredR1 = false;
        bool Noise(int percent) => random.Next(100) < percent;

        for (var i = 0; i < count; i++)
        {
            var kind = random.Next(100) switch
            {
                < 40 => WorkTaskKind.AgentWork,
                < 55 => WorkTaskKind.Condition,
                < 65 => WorkTaskKind.ToolRun,
                < 75 => WorkTaskKind.HumanApproval,
                < 85 => WorkTaskKind.Review,
                < 95 => WorkTaskKind.Integration,
                _ => WorkTaskKind.Debate,
            };
            var id = Noise(3) ? $"{i}x" : $"T{i}";
            var spec = Spec(id, kind) with
            {
                Join = Noise(20) ? JoinPolicy.AnyCompleted : JoinPolicy.AllCompleted,
                MaxRetries = kind is WorkTaskKind.Review or WorkTaskKind.HumanApproval ? (Noise(8) ? 1 : 0) : (Noise(3) ? 9 : random.Next(0, 3)),
                Timeout = Noise(3) ? TimeSpan.Zero : null,
            };

            spec = kind switch
            {
                WorkTaskKind.AgentWork => spec with
                {
                    Agent = Noise(5) ? Aid("ghost") : Aid(random.Next(2) == 0 ? "ejecutor" : "ejecutor2"),
                    Workspace = Noise(25) ? WorkspaceMode.Write : WorkspaceMode.ReadOnly,
                    WriteScope = [$"area{i}/"],
                    Covers = !coveredR1 && !Noise(8) ? [Rid("R1")] : [],
                    VerificationSteps = Noise(5) ? ["weird"] : Noise(50) ? ["build"] : [],
                    Objective = Noise(3) ? string.Empty : spec.Objective,
                },
                WorkTaskKind.Review => spec with { Agent = Noise(10) ? Aid("ejecutor") : Aid("juez"), Workspace = WorkspaceMode.ReadOnly },
                WorkTaskKind.ToolRun => spec with { ToolId = Noise(90) ? "lint" : Noise(50) ? null : "other" },
                _ => spec,
            };

            var dependencies = new List<DependencySpec>();
            for (var j = 0; j < i; j++)
            {
                if (random.Next(100) >= 30)
                {
                    continue;
                }

                var prerequisite = tasks[j];
                string? branch = prerequisite.Kind == WorkTaskKind.Condition
                    ? (Noise(5) ? null : (random.Next(2) == 0 ? "yes" : "no"))
                    : (Noise(3) ? "yes" : null);
                dependencies.Add(new DependencySpec(prerequisite.Id, branch));
            }

            if (Noise(3))
            {
                dependencies.Add(new DependencySpec(Tid("ghost"), null));
            }

            spec = spec with { DependsOn = dependencies };
            if (kind == WorkTaskKind.Condition && !Noise(5))
            {
                var ancestors = dependencies.Select(d => d.Prerequisite).ToList();
                spec = spec with { Condition = new TaskSucceeded(ancestors.Count > 0 && !Noise(5) ? ancestors[random.Next(ancestors.Count)] : Tid("T0")) };
            }

            coveredR1 |= spec.Covers.Count > 0;
            tasks.Add(spec);
        }

        return tasks;
    }

    [Fact]
    public void A_plan_without_violations_can_always_be_built_into_a_graph_and_run_to_the_end()
    {
        var random = new Random(26101504);
        var valid = 0;
        var invalid = 0;
        var withConditions = 0;
        var codesSeen = new HashSet<PlanViolationCode>();
        for (var i = 0; i < 2500; i++)
        {
            var tasks = RandomTasks(random);
            var plan = Plan(tasks, author: "orquestador");
            var violations = PlanValidator.Validate(plan, Context());
            codesSeen.UnionWith(violations.Select(v => v.Code));

            if (violations.Count > 0)
            {
                invalid++;
                continue;
            }

            valid++;
            var graph = plan.BuildGraph(T0);
            Assert.Equal(plan.Tasks.Count, graph.Tasks.Count);
            Assert.Equal(plan.Tasks.Count, graph.TopologicalOrder().Count);
            withConditions += plan.Tasks.Any(t => t.Kind == WorkTaskKind.Condition) ? 1 : 0;
        }

        Assert.True(valid > 100 && invalid > 100, $"The generator must produce valid and invalid plans (valid {valid}, invalid {invalid}).");
        Assert.True(withConditions > 20, "Valid plans with conditions were too rare.");
        Assert.True(codesSeen.Count >= 15, $"Random plans reached only {codesSeen.Count} different rules.");
    }

    [Fact]
    public void Validate_never_throws_on_a_garbled_plan_and_is_deterministic()
    {
        var random = new Random(26101505);
        for (var i = 0; i < 1000; i++)
        {
            var tasks = RandomTasks(random)
                .Select(t => random.Next(10) == 0 ? t with { DependsOn = [.. t.DependsOn, new DependencySpec(t.Id, null)] } : t)
                .ToList();
            var plan = Plan(tasks);

            var first = PlanValidator.Validate(plan, Context()).Select(v => (v.Code, v.Task, v.Message)).ToList();
            var second = PlanValidator.Validate(plan, Context()).Select(v => (v.Code, v.Task, v.Message)).ToList();

            Assert.Equal(first, second);
        }
    }

    // ---- warnings: a plan that may run but whose work could never be delivered (B10) ----

    private static TaskSpec Writer(string id, string scope, params string[] after) =>
        Spec(id) with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.Write, WriteScope = [scope], Covers = [Rid("R1")], DependsOn = [.. after.Select(a => Dep(a))] };

    private static TaskSpec Integration(string id, params string[] after) =>
        Spec(id, WorkTaskKind.Integration) with { DependsOn = [.. after.Select(a => Dep(a))], VerificationSteps = ["build", "test"] };

    private static TaskSpec Review(string id, params string[] after) =>
        Spec(id, WorkTaskKind.Review) with { Agent = Aid("juez"), Workspace = WorkspaceMode.ReadOnly, DependsOn = [.. after.Select(a => Dep(a))], MaxRetries = 0 };

    private static IReadOnlyList<(PlanWarningCode Code, string? Task)> Warnings(params TaskSpec[] tasks) =>
        [.. PlanValidator.Warnings(Plan(tasks)).Select(w => (w.Code, w.Task?.Value))];

    /// <summary>The code, the task and the branches (<c>C:yes</c>) of every warning of <paramref name="tasks"/>.</summary>
    private static IReadOnlyList<(PlanWarningCode Code, string? Task, string When)> WarningsWithBranches(params TaskSpec[] tasks) =>
        [.. PlanValidator.Warnings(Plan(tasks)).Select(w => (w.Code, w.Task?.Value, string.Join(",", w.When.Select(b => $"{b.Condition}:{b.Branch}"))))];

    private static TaskSpec Condition(string id, string reads, params DependencySpec[] after) =>
        Spec(id, WorkTaskKind.Condition) with { DependsOn = after, Condition = new TaskSucceeded(Tid(reads)) };

    [Fact]
    public void A_writer_reviewed_by_the_judge_without_an_integration_is_valid_but_could_never_be_delivered()
    {
        // B10, run 20261029-3f1cbc: A writes, B (the judge) reviews A; the run ended Approved and «Entregar» stayed off.
        var tasks = new[] { Writer("A", "src/"), Review("B", "A") };

        Assert.Empty(Check(tasks));
        var warning = Assert.Single(PlanValidator.Warnings(Plan(tasks)));
        Assert.Equal((PlanWarningCode.NotDeliverable, Tid("A")), (warning.Code, warning.Task));
        Assert.Empty(warning.When);
    }

    [Fact]
    public void An_integration_on_a_branch_after_the_review_makes_delivery_depend_on_that_branch()
    {
        // When C takes yes, J integrates after the verdict, and the judge did not review what would be delivered.
        var tasks = new[] { Writer("A", "src/"), Integration("I", "A"), Review("R", "I"), Condition("C", "R", Dep("R")), Integration("J") with { DependsOn = [Dep("C", "yes")] } };

        Assert.Empty(Check(tasks));
        Assert.Equal([(PlanWarningCode.MayNotBeDeliverable, "A", "C:yes")], WarningsWithBranches(tasks));
    }

    [Fact]
    public void A_review_on_one_branch_only_leaves_the_other_without_a_verdict()
    {
        var tasks = new[] { Writer("A", "src/"), Integration("I", "A"), Condition("C", "I", Dep("I")), Review("R") with { DependsOn = [Dep("C", "yes")] } };

        Assert.Empty(Check(tasks));
        Assert.Equal([(PlanWarningCode.MayNotBeDeliverable, "A", "C:no")], WarningsWithBranches(tasks));
    }

    [Fact]
    public void A_review_with_AnyCompleted_can_run_on_a_branch_where_no_integration_does()
    {
        // When C takes no, X runs instead of I, and the review (AnyCompleted) goes on without any integration.
        var tasks = new[]
        {
            Writer("A", "src/"),
            Condition("C", "A", Dep("A")),
            Integration("I") with { DependsOn = [Dep("C", "yes")] },
            Spec("X", WorkTaskKind.HumanApproval) with { DependsOn = [Dep("C", "no")], MaxRetries = 0 },
            Review("R", "I", "X") with { Join = JoinPolicy.AnyCompleted },
        };

        Assert.Empty(Check(tasks));
        Assert.Equal([(PlanWarningCode.MayNotBeDeliverable, "A", "C:no")], WarningsWithBranches(tasks));

        // A review with AnyCompleted still waits for every prerequisite that runs: with the integration on every branch,
        // the verdict always comes after it.
        Assert.Empty(Warnings(
            Writer("A", "src/"),
            Integration("I", "A"),
            Condition("C", "I", Dep("I")),
            Spec("X", WorkTaskKind.HumanApproval) with { DependsOn = [Dep("C", "yes")], MaxRetries = 0 },
            Spec("Y", WorkTaskKind.HumanApproval) with { DependsOn = [Dep("C", "no")], MaxRetries = 0 },
            Review("R", "X", "Y") with { Join = JoinPolicy.AnyCompleted }));
    }

    [Fact]
    public void A_writer_that_only_runs_on_a_branch_is_judged_on_the_paths_where_it_runs()
    {
        // B writes only when C takes yes, and then I and R come after it: on the other branch there is nothing of B's to deliver.
        Assert.Empty(Warnings(
            Writer("A", "src/"),
            Condition("C", "A", Dep("A")),
            Writer("B", "lib/") with { DependsOn = [Dep("C", "yes")] },
            Integration("I", "A", "B") with { Join = JoinPolicy.AnyCompleted },
            Review("R", "I")));
    }

    [Fact]
    public void A_plan_too_large_to_check_gets_one_conservative_warning_at_once()
    {
        // The shape the review measured at 22.7 s: 12 conditions on a writer, each starting another writer, and a chain of
        // 400 approvals before the integration. An orchestrator's answer is not trusted, so the work is bounded first.
        var tasks = new List<TaskSpec> { Writer("W0", "src/") };
        var writers = new List<string> { "W0" };
        for (var i = 0; i < 12; i++)
        {
            tasks.Add(Condition($"C{i}", "W0", Dep("W0")));
            tasks.Add(Writer($"V{i}", $"lib{i}/") with { DependsOn = [Dep($"C{i}", "yes")] });
            writers.Add($"V{i}");
        }

        var previous = "W0";
        for (var f = 0; f < 400; f++)
        {
            tasks.Add(Spec($"H{f}", WorkTaskKind.HumanApproval) with { DependsOn = [Dep(previous)], MaxRetries = 0 });
            previous = $"H{f}";
        }

        tasks.Add(Integration("I", [.. writers, previous]) with { Join = JoinPolicy.AnyCompleted });
        tasks.Add(Review("R", "I"));
        var plan = Plan(tasks);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var warning = Assert.Single(PlanValidator.Warnings(plan));
        watch.Stop();

        Assert.Equal((PlanWarningCode.DeliveryNotChecked, (TaskId?)null), (warning.Code, warning.Task));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The check took {watch.Elapsed}.");

        // The same shape, small enough, is checked path by path, and every writer is delivered on every branch.
        var small = tasks.Where(t => !t.Id.Value.StartsWith('H')).Select(t => t.Id == Tid("I") ? t with { DependsOn = [.. writers.Select(w => Dep(w))] } : t).ToList();
        Assert.Empty(PlanValidator.Warnings(Plan(small)));
    }

    [Fact]
    public void A_writer_integrated_and_then_reviewed_by_the_judge_can_be_delivered()
    {
        Assert.Empty(Warnings(Writer("A", "src/"), Integration("I", "A"), Review("R", "I")));
        Assert.Empty(Warnings(Writer("A", "src/"), Writer("B", "lib/"), Integration("I", "A", "B"), Review("R", "I")));
        Assert.Empty(Warnings(Writer("A", "src/"), Writer("B", "lib/", "A"), Integration("I", "B"), Review("R", "I")));
    }

    [Fact]
    public void Without_a_review_after_the_last_integration_no_writer_can_be_delivered()
    {
        Assert.Equal([(PlanWarningCode.NotDeliverable, "A")], Warnings(Writer("A", "src/"), Integration("I", "A")));

        // An integration after the verdict, on every path: the judge did not review what would be delivered.
        Assert.Equal([(PlanWarningCode.NotDeliverable, "A")], Warnings(Writer("A", "src/"), Integration("I", "A"), Review("R", "I"), Integration("J", "R")));
    }

    /// <summary>The code, the task and whether it depends on which tasks finish first, of every warning of <paramref name="tasks"/>.</summary>
    private static IReadOnlyList<(PlanWarningCode Code, string? Task, bool Timing)> WarningsWithTiming(params TaskSpec[] tasks) =>
        [.. PlanValidator.Warnings(Plan(tasks)).Select(w => (w.Code, w.Task?.Value, w.DependsOnTiming))];

    [Fact]
    public void A_review_that_does_not_start_from_the_last_integration_never_gets_it_delivered()
    {
        // The review's worktree starts on its code frontier, whatever finishes first, and the delivery needs it to be the
        // integration delivered. R reviews A's commit while I integrates it: its judge works on A, never on I.
        var concurrent = PlanValidator.Warnings(Plan([Writer("A", "src/"), Integration("I", "A"), Review("R", "A")]));
        Assert.Equal([(PlanWarningCode.NotDeliverable, "A", false)], [.. concurrent.Select(w => (w.Code, w.Task?.Value, w.DependsOnTiming))]);
        Assert.Empty(Assert.Single(concurrent).When);

        // The race of the delivery review: R waits for I1 only, and I2 (after I1 and B) is the last integration.
        Assert.Equal(
            [(PlanWarningCode.NotDeliverable, "A", false), (PlanWarningCode.NotDeliverable, "B", false)],
            WarningsWithTiming(Writer("A", "src/"), Writer("B", "lib/"), Integration("I1", "A"), Review("R", "I1"), Integration("I2", "I1", "B")));

        // A task that writes between the last integration and the review: the judge works on code nobody integrated.
        Assert.Equal(
            [(PlanWarningCode.NotDeliverable, "A", false), (PlanWarningCode.NotDeliverable, "W", false)],
            WarningsWithTiming(Writer("A", "src/"), Integration("I", "A"), Writer("W", "lib/", "I"), Review("R", "W")));
    }

    [Fact]
    public void Of_the_reviews_that_can_finish_last_only_some_starting_from_the_last_integration_leave_delivery_to_which_finishes_first()
    {
        // The run ends with the verdict of the review that finishes last. A → I1 → R1, and B → I2 after I1 too, with R2
        // after I2: R2 starts from I2, but R1 does not wait for it, and its verdict counts if it finishes last.
        Assert.Equal(
            [(PlanWarningCode.MayNotBeDeliverable, "A", true), (PlanWarningCode.MayNotBeDeliverable, "B", true)],
            WarningsWithTiming(Writer("A", "src/"), Writer("B", "lib/"), Integration("I1", "A"), Review("R1", "I1"), Integration("I2", "I1", "B"), Review("R2", "I2")));

        // A review before a later integration finishes before every review after it: it never ends the run.
        Assert.Empty(Warnings(Writer("A", "src/"), Writer("B", "lib/"), Integration("I1", "A"), Review("R1", "I1"), Integration("I2", "R1", "B"), Review("R2", "I2")));

        // Two reviews that both start from the last integration: whichever finishes last, its judge saw what is delivered.
        Assert.Empty(Warnings(Writer("A", "src/"), Integration("I", "A"), Review("R1", "I"), Review("R2", "I")));
    }

    [Fact]
    public void Two_integrations_a_skipped_task_leaves_unordered_leave_delivery_to_which_finishes_last()
    {
        // I2 comes after I1 through X, which runs only when C takes yes; when C takes no, I2 (AnyCompleted) goes on after B
        // alone, and either integration can finish last: R starts from I2, so only that order is delivered.
        var tasks = new[]
        {
            Writer("A", "src/"),
            Writer("B", "lib/"),
            Integration("I1", "A"),
            Condition("C", "A", Dep("A")),
            Spec("X", WorkTaskKind.HumanApproval) with { DependsOn = [Dep("I1"), Dep("C", "yes")], MaxRetries = 0 },
            Integration("I2", "X", "B") with { Join = JoinPolicy.AnyCompleted },
            Review("R", "I2"),
        };

        Assert.Empty(Check(tasks));
        var warnings = PlanValidator.Warnings(Plan(tasks));
        Assert.Equal(
            [(PlanWarningCode.MayNotBeDeliverable, "A", true, "C:no"), (PlanWarningCode.MayNotBeDeliverable, "B", true, "C:no")],
            [.. warnings.Select(w => (w.Code, w.Task?.Value, w.DependsOnTiming, string.Join(",", w.When.Select(b => $"{b.Condition}:{b.Branch}"))))]);
    }

    [Fact]
    public void A_writer_the_reviewed_integration_does_not_merge_is_named_alone()
    {
        Assert.Equal(
            [(PlanWarningCode.NotDeliverable, "W2")],
            Warnings(Writer("W1", "src/"), Integration("I", "W1"), Review("R", "I"), Writer("W2", "lib/", "R")));

        // In the valid plan T1 and T2 are merged by T3, which the review T7 comes after; T6 writes docs/ after T3 when T4
        // takes no, and then T7 starts from T6's code, not from T3: on that branch nothing is delivered.
        Assert.Equal(
            [(PlanWarningCode.MayNotBeDeliverable, "T1", "T4:no"), (PlanWarningCode.MayNotBeDeliverable, "T2", "T4:no"), (PlanWarningCode.NotDeliverable, "T6", string.Empty)],
            [.. PlanValidator.Warnings(Plan()).Select(w => (w.Code, w.Task?.Value, string.Join(",", w.When.Select(b => $"{b.Condition}:{b.Branch}"))))]);
    }

    [Fact]
    public void A_plan_that_writes_nothing_or_whose_graph_is_not_sound_has_no_warnings()
    {
        Assert.Empty(Warnings(Spec("L", WorkTaskKind.ToolRun) with { ToolId = "lint" }));
        Assert.Empty(Warnings(Spec("A") with { Agent = Aid("ejecutor"), Workspace = WorkspaceMode.ReadOnly }, Review("R", "A")));
        Assert.Empty(Warnings(Writer("A", "src/", "R"), Review("R", "A")));
        Assert.Empty(Warnings(Writer("A", "src/", "ghost")));
        Assert.Empty(Warnings(Writer("A", "src/"), Writer("A", "lib/")));
    }

    [Fact]
    public void Warnings_never_throw_on_a_garbled_plan_and_are_deterministic()
    {
        var random = new Random(26103001);
        for (var i = 0; i < 1000; i++)
        {
            var plan = Plan(RandomTasks(random));

            var first = PlanValidator.Warnings(plan).Select(w => (w.Code, w.Task, w.Message)).ToList();
            var second = PlanValidator.Warnings(plan).Select(w => (w.Code, w.Task, w.Message)).ToList();

            Assert.Equal(first, second);
            Assert.All(
                first.Where(w => w.Code != PlanWarningCode.DeliveryNotChecked),
                w => Assert.Contains(plan.Tasks, t => t.Id == w.Task && t.Kind == WorkTaskKind.AgentWork && t.Workspace == WorkspaceMode.Write));
        }
    }
}
