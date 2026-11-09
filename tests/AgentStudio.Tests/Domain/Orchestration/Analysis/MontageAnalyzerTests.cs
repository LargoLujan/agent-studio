using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Localization;
using static AgentStudio.Tests.Domain.Orchestration.Analysis.MontageFixtures;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration.Analysis;

/// <summary>
/// F13-9 (docs/17 D-18): the analyzer of the assembly. Each code of D-18 has its case here, which breaks the clean draft in
/// exactly that way, with the finding, its nodes, its arguments and its fix; adding a code without its case fails the first
/// test. The analyzer is pure and lives in the domain, which references nothing: it never spends quota.
/// </summary>
public sealed partial class MontageAnalyzerTests
{
    /// <summary>One way to break the clean draft for each code, and what the finding must say.</summary>
    private sealed record Case(MontageDraft Draft, IReadOnlyList<string> Nodes, IReadOnlyList<object> Arguments, Fix? Fix, FindingSeverity Severity);

    private static readonly Dictionary<string, Func<Case>> Cases = new()
    {
        [FindingCodes.NoIntegration] = () => new(
            Draft(tasks: [Writing("T1", "ejecutor", "src/Calc/") with { Covers = [Rid("R1")] }, Task("R1", WorkTaskKind.Review, "juez").After("T1")]),
            ["T1"], [], new AddIntegrationFix(), FindingSeverity.Error),

        [FindingCodes.TestWithoutBuild] = () => new(
            Draft() with { Steps = Steps(testRequiresBuild: false), Plan = Plan([Tasks()[0], Tasks()[1] with { VerificationSteps = ["build", "test"] }, Tasks()[2]]) },
            ["T1"], ["T1", "test"], new AddBuildStepFix(Tid("T1"), "build"), FindingSeverity.Error),

        [FindingCodes.JudgeSameVendor] = () => new(
            Draft(Roster().With("juez", a => a with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic) })),
            ["R1", "juez"], ["juez", "anthropic"], new AddJudgeFix(ProviderId.CodexCli, VendorId.OpenAI), FindingSeverity.Error),

        [FindingCodes.ParallelWriteOverlap] = () => new(
            Draft(tasks: [.. Overlapping()]),
            ["T1", "T2"], ["T1", "T2"], new AddDependencyFix(Tid("T2"), Tid("T1")), FindingSeverity.Error),

        [FindingCodes.WriteOutsideArea] = () => new(
            Draft(Roster().With("ejecutor", a => a with { PathGrants = [new PathGrant("docs/", FileAccessLevel.ReadWrite)] })),
            ["T1", "ejecutor"], ["T1", "ejecutor", "src/Calc/"], new ReassignTaskFix(Tid("T1"), Aid("ejecutor2")), FindingSeverity.Error),

        [FindingCodes.ParallelCapacity] = () => new(
            Draft([.. Roster(), Agent("ejecutor3", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet")]),
            ["ejecutor", "ejecutor2", "ejecutor3"], [3, "claude-code", 2], null, FindingSeverity.Warning),

        [FindingCodes.PackTooSmall] = () => new(
            Draft() with { TaskContexts = [Facts("T1", mandatory: 30_000)] },
            ["T1", "ejecutor"], ["T1", 30_000, 24_000], null, FindingSeverity.Error),

        [FindingCodes.WindowTight] = () => new(
            Draft() with { TaskContexts = [Facts("T1", preamble: 150_000)] },
            ["T1", "ejecutor"], ["T1", 150_000L, 200_000L, 75], new SetMemoryFix(Aid("ejecutor"), MemoryPolicy.None), FindingSeverity.Warning),

        [FindingCodes.ContextThin] = () => new(
            Draft(Roster().With("ejecutor", a => a with { Memory = MemoryPolicy.None })) with { TaskContexts = [Facts("T1", memory: false)] },
            ["T1", "ejecutor"], ["T1"], new SetMemoryFix(Aid("ejecutor"), MemoryPolicy.ReadSelected), FindingSeverity.Suggestion),

        [FindingCodes.ModelOversized] = () => new(
            Draft(Roster().With("ejecutor", a => a with { Model = a.Model with { Model = "opus" } }).With("ejecutor2", a => a with { Model = a.Model with { Model = "sonnet" } }), [Writing("T1", "ejecutor", "docs/", "tests/Calc.Tests/") with { Covers = [Rid("R1")] }, Tasks()[1], Tasks()[2]]),
            ["T1", "ejecutor"], ["T1", "ejecutor", "opus"], new ReassignTaskFix(Tid("T1"), Aid("ejecutor2")), FindingSeverity.Suggestion),

        [FindingCodes.ModelUndersized] = () => new(
            Draft(Roster().With("ejecutor", a => a with { Model = a.Model with { Model = "haiku" } })) with { Learned = new MontageLearnedFacts([new ModelPerformance(ProviderId.ClaudeCode, "claude-haiku", 5, 2, 3, 1)], null) },
            ["ejecutor"], ["ejecutor", "haiku", 3, 1], new ChangeModelFix(Aid("ejecutor"), "claude-sonnet"), FindingSeverity.Suggestion),

        [FindingCodes.CostOverBudget] = () => new(
            Draft() with { Learned = new MontageLearnedFacts([], 20) },
            [], [20L, 12], new RaiseRunBudgetFix(20), FindingSeverity.Warning),

        [FindingCodes.QuotaWindowNear] = () => new(
            Draft() with { Quota = [new QuotaWindowFacts(ProviderId.ClaudeCode, "5h", MetricValue<double>.Exact(0.82, "test"), T0.AddHours(2))] },
            ["orquestador", "ejecutor", "ejecutor2"], ["claude-code", "5h", 82, T0.AddHours(2)], null, FindingSeverity.Warning),

        [FindingCodes.OrderNoteUnapplied] = () => new(
            Draft(tasks: [.. Parallel()]) with { OrderNotes = new OrderNotes([new OrderDependencyNote(Tid("T1"), Tid("T2"), "T2 usa la API de T1")], [], []) },
            ["T2", "T1"], ["T2", "T1"], new AddDependencyFix(Tid("T2"), Tid("T1")), FindingSeverity.Warning),

        [FindingCodes.OrderHarmful] = () => new(
            Draft(tasks: [.. Parallel()]) with { OrderNotes = new OrderNotes([], [], [new HarmfulOrderNote(Tid("T2"), Tid("T1"), "T2 antes que T1 rompería la compilación")]) },
            ["T2", "T1"], ["T2", "T1"], new AddDependencyFix(Tid("T2"), Tid("T1")), FindingSeverity.Warning),

        [FindingCodes.HighRiskCapability] = () => new(
            Draft(Roster().With("ejecutor", a => a with { AllowedTools = ["Read", "Bash(git:*)"] })),
            ["ejecutor"], ["ejecutor", "Bash"], new RemoveCapabilityFix(Aid("ejecutor"), "Bash(git:*)"), FindingSeverity.Warning),
    };

    /// <summary>T1 and T2 write in parallel, with scopes that overlap.</summary>
    private static IEnumerable<TaskSpec> Overlapping() =>
    [
        Tasks()[0],
        Writing("T2", "ejecutor2", "src/Calc/Sub/"),
        Task("I1", WorkTaskKind.Integration).After("T1", "T2") with { VerificationSteps = ["test"] },
        Tasks()[2],
    ];

    /// <summary>T1 and T2 write in parallel, in folders apart.</summary>
    private static IEnumerable<TaskSpec> Parallel() =>
    [
        Tasks()[0],
        Writing("T2", "ejecutor2", "src/Other/"),
        Task("I1", WorkTaskKind.Integration).After("T1", "T2") with { VerificationSteps = ["test"] },
        Tasks()[2],
    ];

    public static TheoryData<string> AllCodes() => [.. FindingCodes.All];

    [Fact]
    public void Every_code_of_the_design_has_a_case_and_a_rule_that_returns_it()
    {
        Assert.Equal(FindingCodes.All.Order(StringComparer.Ordinal), Cases.Keys.Order(StringComparer.Ordinal));
        var ruleCodes = MontageAnalyzer.Rules.SelectMany(r => r.Codes).ToList();
        Assert.All(FindingCodes.All, code => Assert.Contains(code, ruleCodes));
        Assert.Equal(ruleCodes.Count, ruleCodes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_clean_draft_has_no_findings_and_keeps_its_generation()
    {
        var analysis = MontageAnalyzer.Analyze(Draft());

        Assert.Empty(analysis.Findings);
        Assert.Equal(7, analysis.Generation);
        Assert.False(analysis.BlocksApproval);
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Each_case_gives_its_finding_with_its_nodes_arguments_and_fix_and_nothing_else(string code)
    {
        var expected = Cases[code]();

        var findings = MontageAnalyzer.Analyze(expected.Draft).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(code, finding.Code);
        Assert.Equal(expected.Severity, finding.Severity);
        Assert.Equal(expected.Nodes, finding.Nodes);
        Assert.Equal(expected.Arguments, finding.Arguments);
        Assert.Equal(expected.Fix, finding.Fix);
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Each_fix_applied_clears_its_finding_and_undoing_brings_it_back(string code)
    {
        var expected = Cases[code]();
        if (expected.Fix is not { } fix || code is FindingCodes.WindowTight or FindingCodes.ContextThin or FindingCodes.JudgeSameVendor)
        {
            // It only informs; or the application computes the facts again after the fix (the memory of the agent); or it takes
            // two clicks (adding a judge, then giving it the review), which its own test follows.
            return;
        }

        var draft = expected.Draft;
        var history = new EditHistory<MontageEditState>(new MontageEditState(draft.Plan, draft.Context.Roster, draft.Run.MaxAgentRuns));
        var outcome = FixApplier.Apply(history.Current, fix, new FixEnvironment("Integración", draft.Steps));
        Assert.True(outcome.Applied, outcome.Refusal);
        history.Do(outcome.State);

        AssertClearedWithoutNewErrors(draft, Rebuilt(draft, history.Current), code);
        Assert.True(history.Undo());
        Assert.Contains(MontageAnalyzer.Analyze(Rebuilt(draft, history.Current)).Findings, f => f.Code == code);
        Assert.True(history.Redo());
        Assert.DoesNotContain(MontageAnalyzer.Analyze(Rebuilt(draft, history.Current)).Findings, f => f.Code == code);
    }

    /// <summary>The fix took its finding away and brought no error the draft did not have.</summary>
    private static void AssertClearedWithoutNewErrors(MontageDraft before, MontageDraft after, string code)
    {
        var findings = MontageAnalyzer.Analyze(after).Findings;
        Assert.DoesNotContain(findings, f => f.Code == code);
        var errorsBefore = MontageAnalyzer.Analyze(before).Findings.Where(f => f.BlocksApproval).Select(f => f.Code).ToHashSet(StringComparer.Ordinal);
        Assert.All(findings.Where(f => f.BlocksApproval), f => Assert.Contains(f.Code, errorsBefore));
    }

    [Theory]
    [InlineData("docs/", true)]
    [InlineData("Tests/", true)]
    [InlineData("src/Calc.Tests/", true)]
    [InlineData("src/calc.tests/Multiply.cs", true)]
    [InlineData("README.md", true)]
    [InlineData("Contests/", false)]
    [InlineData("src/Contests/", false)]
    [InlineData("src/LatestTests/", false)]
    [InlineData("src/Calc/", false)]
    public void Documentation_and_tests_are_told_by_whole_folders(string scope, bool docsOrTests)
    {
        var roster = Roster().With("ejecutor", a => a with { Model = a.Model with { Model = "opus" } });

        var findings = MontageAnalyzer.Analyze(Draft(roster, [Writing("T1", "ejecutor", scope) with { Covers = [Rid("R1")] }, Tasks()[1], Tasks()[2]])).Findings;

        Assert.Equal(docsOrTests, findings.Any(f => f.Code == FindingCodes.ModelOversized));
    }

    [Fact]
    public void Adding_the_integration_behind_a_writer_that_follows_a_review_makes_no_cycle()
    {
        var draft = Draft(tasks: WriterBehindReview());
        var finding = Assert.Single(MontageAnalyzer.Analyze(draft).Findings);
        Assert.Equal(FindingCodes.NoIntegration, finding.Code);

        var outcome = FixApplier.Apply(new MontageEditState(draft.Plan, draft.Context.Roster, draft.Run.MaxAgentRuns), finding.Fix!, new FixEnvironment("Integración", draft.Steps));

        Assert.True(outcome.Applied, outcome.Refusal);
        var plan = outcome.State.Plan!;
        Assert.Equal(["T2"], plan.Tasks.Single(t => t.Id == Tid("I1")).DependsOn.Select(d => d.Prerequisite.Value));
        Assert.Equal(["T1"], plan.Tasks.Single(t => t.Id == Tid("R1")).DependsOn.Select(d => d.Prerequisite.Value)); // R1 comes before what I1 merges
        AssertClearedWithoutNewErrors(draft, Rebuilt(draft, outcome.State), FindingCodes.NoIntegration);
    }

    [Fact]
    public void A_fix_that_would_leave_the_plan_unsound_is_neither_applied_nor_offered()
    {
        // T2 already waits for T1: making T1 wait for T2 would be a cycle, by the criterion of the validator.
        var draft = Draft(tasks: [Tasks()[0], Writing("T2", "ejecutor2", "src/Other/").After("T1"), Task("I1", WorkTaskKind.Integration).After("T2") with { VerificationSteps = ["test"] }, Tasks()[2]]);
        var state = new MontageEditState(draft.Plan, draft.Context.Roster, draft.Run.MaxAgentRuns);

        var outcome = FixApplier.Apply(state, new AddDependencyFix(Tid("T1"), Tid("T2")), new FixEnvironment("Integración", draft.Steps));

        Assert.False(outcome.Applied);
        Assert.Same(state, outcome.State);
        var noted = draft with { OrderNotes = new OrderNotes([new OrderDependencyNote(Tid("T2"), Tid("T1"), "x")], [], []) };
        Assert.Null(Assert.Single(MontageAnalyzer.Analyze(noted).Findings).Fix);
    }

    /// <summary>The draft with the state a fix left: its plan, its template (also in the team) and its budget.</summary>
    private static MontageDraft Rebuilt(MontageDraft draft, MontageEditState state) =>
        draft with
        {
            Plan = state.Plan,
            Team = draft.Team with { Agents = [.. state.Roster.Select(a => new AgentNode(a))] },
            Context = draft.Context with { Roster = state.Roster },
            Run = draft.Run with { MaxAgentRuns = state.MaxAgentRuns },
        };

    [Fact]
    public void A_rule_of_the_validator_is_wrapped_with_its_code_on_its_task_and_first()
    {
        var draft = Draft(tasks: [Tasks()[0] with { Objective = " " }, Tasks()[1], Tasks()[2]]) with { Quota = [new QuotaWindowFacts(ProviderId.ClaudeCode, "5h", MetricValue<double>.Exact(0.9, "test"), null)] };

        var findings = MontageAnalyzer.Analyze(draft).Findings;

        Assert.Equal([FindingCodes.ForViolation(PlanViolationCode.ObjectiveRequired), FindingCodes.QuotaWindowNear], findings.Select(f => f.Code));
        Assert.Equal(["T1"], findings[0].Nodes);
        Assert.Equal(["T1"], findings[0].Arguments);
        Assert.True(findings[0].BlocksApproval);
        Assert.Equal(MontageTexts.QuotaWindowNearWithoutReset, MontageTexts.SentenceOf(findings[1]).Code);
    }

    [Fact]
    public void The_violations_with_a_code_of_the_design_are_not_duplicated()
    {
        foreach (var code in new[] { FindingCodes.JudgeSameVendor, FindingCodes.ParallelWriteOverlap, FindingCodes.WriteOutsideArea })
        {
            var findings = MontageAnalyzer.Analyze(Cases[code]().Draft).Findings;

            Assert.DoesNotContain(findings, f => f.Code.StartsWith(FindingCodes.PlanViolationPrefix, StringComparison.Ordinal));
        }

        Assert.Equal(
            [PlanViolationCode.JudgeVendorNotIndependent, PlanViolationCode.OverlappingWriteScopes, PlanViolationCode.WriteScopeOutsideAgentArea],
            ValidatorRule.Mapped.Keys.Order());
    }

    [Fact]
    public void A_judge_of_the_other_vendor_in_the_team_is_offered_before_adding_one()
    {
        var roster = Roster()
            .With("juez", a => a with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic) })
            .Append(Agent("juez-codex", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI))
            .ToList();

        var finding = Assert.Single(MontageAnalyzer.Analyze(Draft(roster)).Findings);

        Assert.Equal(new SwitchJudgeFix(Tid("R1"), Aid("juez-codex")), finding.Fix);
    }

    [Fact]
    public void Adding_a_judge_of_the_other_vendor_then_giving_it_the_review_clears_the_finding_and_both_undo()
    {
        var draft = Cases[FindingCodes.JudgeSameVendor]().Draft;
        var environment = new FixEnvironment("Integración", draft.Steps);
        var history = new EditHistory<MontageEditState>(new MontageEditState(draft.Plan, draft.Context.Roster, draft.Run.MaxAgentRuns));

        var added = FixApplier.Apply(history.Current, new AddJudgeFix(ProviderId.CodexCli, VendorId.OpenAI), environment);
        history.Do(added.State);
        var judge = Assert.Single(history.Current.Roster, a => a.Id == Aid("juez-2"));
        Assert.Equal((AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, (string?)null, MemoryPolicy.None), (judge.Role, judge.Model.Provider, judge.Model.Vendor, judge.Model.Model, judge.Memory));

        var next = Assert.Single(MontageAnalyzer.Analyze(Rebuilt(draft, history.Current)).Findings);
        Assert.Equal(new SwitchJudgeFix(Tid("R1"), Aid("juez-2")), next.Fix);
        history.Do(FixApplier.Apply(history.Current, next.Fix!, environment).State);
        Assert.Empty(MontageAnalyzer.Analyze(Rebuilt(draft, history.Current)).Findings);

        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.False(history.CanUndo);
        Assert.Equal(draft.Context.Roster, history.Current.Roster);
    }

    [Fact]
    public void Without_a_plan_only_the_rules_of_the_team_speak()
    {
        var roster = Roster()
            .With("juez", a => a with { Model = new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic) })
            .With("ejecutor", a => a with { AllowedTools = ["WebFetch"] });

        var findings = MontageAnalyzer.Analyze(Draft(roster, plan: null)).Findings;

        Assert.Equal([FindingCodes.JudgeSameVendor, FindingCodes.HighRiskCapability], findings.Select(f => f.Code));
        Assert.Equal(["juez"], findings[0].Nodes);
        Assert.Equal(new AddJudgeFix(ProviderId.CodexCli, VendorId.OpenAI), findings[0].Fix);
    }

    [Fact]
    public void Without_a_plan_a_second_judge_of_the_vendor_that_produces_is_not_noise()
    {
        // The default template has «juez-claude» for the debates: one judge of another vendor is enough.
        var roster = Roster().Append(Agent("juez-claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic)).ToList();

        Assert.Empty(MontageAnalyzer.Analyze(Draft(roster, plan: null)).Findings);
    }

    [Fact]
    public void An_agent_that_writes_on_Codex_is_a_capability_of_high_risk_without_a_fix()
    {
        var roster = Roster().Append(Agent("ejecutorCodex", AgentRole.Executor, ProviderId.CodexCli, VendorId.OpenAI)).ToList();

        var finding = Assert.Single(MontageAnalyzer.Analyze(Draft(roster, plan: null)).Findings);

        Assert.Equal(FindingCodes.HighRiskCapability, finding.Code);
        Assert.Equal(["ejecutorCodex", "codex-cli"], finding.Arguments);
        Assert.Null(finding.Fix);
        Assert.Empty(MontageAnalyzer.Analyze(Draft(roster, Plan())).Findings); // with a plan, only the agents in it
    }

    [Fact]
    public void A_test_step_that_requires_the_build_or_comes_after_it_is_built()
    {
        Assert.Empty(MontageAnalyzer.Analyze(Draft()).Findings);
        var named = Draft() with { Steps = Steps(testRequiresBuild: false), Plan = Plan([Tasks()[0] with { VerificationSteps = ["build", "test"] }, Tasks()[1] with { VerificationSteps = ["build", "test"] }, Tasks()[2]]) };
        Assert.Empty(MontageAnalyzer.Analyze(named).Findings);
        var unknownProfile = Draft() with { Steps = [] };
        Assert.Empty(MontageAnalyzer.Analyze(unknownProfile).Findings);
    }

    [Fact]
    public void A_note_the_plan_keeps_says_nothing_and_one_that_would_make_a_cycle_has_no_fix()
    {
        var kept = Draft(tasks: [Tasks()[0], Writing("T2", "ejecutor2", "src/Other/").After("T1"), Task("I1", WorkTaskKind.Integration).After("T2") with { VerificationSteps = ["test"] }, Tasks()[2]])
            with { OrderNotes = new OrderNotes([new OrderDependencyNote(Tid("T1"), Tid("T2"), "x")], [new ParallelOrderNote(Tid("T1"), Tid("T2"), "y")], [new HarmfulOrderNote(Tid("T2"), Tid("T1"), "z")]) };
        Assert.Empty(MontageAnalyzer.Analyze(kept).Findings);

        // The note says T1 needs T2, but T2 already waits for T1: adding it would make a cycle.
        var backwards = kept with { OrderNotes = new OrderNotes([new OrderDependencyNote(Tid("T2"), Tid("T1"), "x")], [], []) };
        var finding = Assert.Single(MontageAnalyzer.Analyze(backwards).Findings);
        Assert.Equal(FindingCodes.OrderNoteUnapplied, finding.Code);
        Assert.Null(finding.Fix);

        // A note about a task the plan no longer has says nothing.
        var gone = kept with { OrderNotes = new OrderNotes([new OrderDependencyNote(Tid("T9"), Tid("T1"), "x")], [], []) };
        Assert.Empty(MontageAnalyzer.Analyze(gone).Findings);
    }

    [Fact]
    public void A_plan_whose_graph_is_not_sound_leaves_the_order_to_the_validator()
    {
        var cycle = Draft(tasks: [Tasks()[0].After("R1"), Tasks()[1], Tasks()[2]]);

        var findings = MontageAnalyzer.Analyze(cycle).Findings;

        Assert.All(findings, f => Assert.StartsWith(FindingCodes.PlanViolationPrefix, f.Code, StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Code == FindingCodes.ForViolation(PlanViolationCode.DependencyCycle));
    }

    [Fact]
    public void The_analyzer_lives_in_a_domain_that_references_nothing_so_it_can_never_spend_quota()
    {
        var references = typeof(MontageAnalyzer).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("AgentStudio", StringComparison.Ordinal));
        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib", name));
    }

    [Fact]
    public void The_same_draft_gives_the_same_findings_in_the_same_order()
    {
        var draft = Cases[FindingCodes.ParallelCapacity]().Draft with { Quota = [new QuotaWindowFacts(ProviderId.ClaudeCode, "7d", MetricValue<double>.Exact(0.95, "t"), T0)] };

        static IReadOnlyList<string> Shape(MontageAnalysis analysis) =>
            [.. analysis.Findings.Select(f => $"{f.Code}|{f.Severity}|{string.Join(',', f.Nodes)}|{string.Join(',', f.Arguments)}|{f.Fix}")];

        Assert.Equal(2, MontageAnalyzer.Analyze(draft).Findings.Count);
        Assert.Equal(Shape(MontageAnalyzer.Analyze(draft)), Shape(MontageAnalyzer.Analyze(draft)));
    }

    [Fact]
    public void Each_sentence_and_button_has_its_text_in_Spanish_and_English_with_exactly_its_arguments()
    {
        var files = LocalizationTestSupport.ResourceFiles();
        var (neutralFile, satellites) = files["MontageStrings"];
        var spanish = LocalizationTestSupport.Read(neutralFile);
        var english = LocalizationTestSupport.Read(satellites["en"]);

        void Has(string code, IReadOnlyList<object> arguments)
        {
            var key = "Text_" + code.Replace('.', '_').Replace('-', '_');
            foreach (var (texts, culture) in new[] { (spanish, "es-ES"), (english, "en") })
            {
                Assert.True(texts.TryGetValue(key, out var text), $"{key} is not in MontageStrings.");
                var used = Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Distinct().Order().ToList();
                Assert.True(used.SequenceEqual(Enumerable.Range(0, arguments.Count)), $"{key} uses {{{string.Join(",", used)}}} but has {arguments.Count} arguments.");
                _ = string.Format(System.Globalization.CultureInfo.GetCultureInfo(culture), text, [.. arguments]);
            }
        }

        foreach (var code in FindingCodes.All)
        {
            var finding = Assert.Single(MontageAnalyzer.Analyze(Cases[code]().Draft).Findings);
            var sentence = MontageTexts.SentenceOf(finding);
            Has(sentence.Code, sentence.Arguments);
            if (finding.Fix is { } fix)
            {
                var button = MontageTexts.ButtonOf(fix);
                Has(button.Code, button.Arguments);
            }
        }

        Has(MontageTexts.QuotaWindowNearWithoutReset, ["claude-code", "5h", 82]);
        Fix[] everyFix =
        [
            new AddIntegrationFix(), new AddBuildStepFix(Tid("T1"), "build"), new AddDependencyFix(Tid("T2"), Tid("T1")), new ReassignTaskFix(Tid("T1"), Aid("e")),
            new SwitchJudgeFix(Tid("R1"), Aid("j")), new AddJudgeFix(ProviderId.CodexCli, VendorId.OpenAI), new WidenAgentFoldersFix(Aid("e"), "src/"),
            new RaiseContextBudgetFix(Aid("e"), 30_000), new RaiseRunBudgetFix(20), new ChangeModelFix(Aid("e"), "sonnet"), new ChangeModelFix(Aid("e"), "sonnet", ReasoningEffort.High),
            new SetMemoryFix(Aid("e"), MemoryPolicy.ReadSelected), new SetMemoryFix(Aid("e"), MemoryPolicy.None), new RemoveSkillsFix(Aid("e"), ["a", "b"]), new RemoveCapabilityFix(Aid("e"), "Bash"),
        ];
        Assert.Equal(MontageTexts.FixCodes.Order(StringComparer.Ordinal), everyFix.Select(f => MontageTexts.ButtonOf(f).Code).Distinct().Order(StringComparer.Ordinal));
        foreach (var fix in everyFix)
        {
            var button = MontageTexts.ButtonOf(fix);
            Has(button.Code, button.Arguments);
        }

        foreach (var refusal in typeof(FixRefusals).GetFields().Select(f => (string)f.GetValue(null)!))
        {
            Has(refusal, []);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial System.Text.RegularExpressions.Regex Placeholder();

    [Fact]
    public void Each_finding_and_fix_says_its_sentence_with_text_arguments_only()
    {
        foreach (var code in FindingCodes.All)
        {
            var finding = Assert.Single(MontageAnalyzer.Analyze(Cases[code]().Draft).Findings);
            Assert.All(finding.Arguments, a => Assert.True(AgentStudio.Domain.Common.TextArguments.IsAllowed(a)));
            Assert.Equal(code, MontageTexts.SentenceOf(finding).Code);
            if (finding.Fix is { } fix)
            {
                Assert.Contains(MontageTexts.ButtonOf(fix).Code, MontageTexts.FixCodes);
            }
        }
    }
}
