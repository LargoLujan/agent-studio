using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Orchestration.Analysis;

/// <summary>
/// The types the analyzer of the assembly shares with the canvas (docs/17 D-18 and D-19): the codes of D-18, the findings with
/// their arguments checked, the typed fixes that say whether they change the template or ask Windows Hello, and the notes of
/// order. The rules themselves are WP1's.
/// </summary>
public sealed class FindingContractsTests
{
    [Fact]
    public void The_codes_of_the_analyzer_are_the_ones_of_the_design_and_wrap_the_validator_without_duplicating_it()
    {
        Assert.Equal(16, FindingCodes.All.Count);
        Assert.Equal(FindingCodes.All.Count, FindingCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("delivery.no-integration", FindingCodes.All);
        Assert.Contains("capability.high-risk", FindingCodes.All);
        Assert.Equal("validator.OverlappingWriteScopes", FindingCodes.ForViolation(PlanViolationCode.OverlappingWriteScopes));
        Assert.DoesNotContain(FindingCodes.All, code => code.StartsWith(FindingCodes.PlanViolationPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void A_finding_carries_a_code_its_nodes_and_checked_arguments_and_an_error_blocks_the_approval()
    {
        var finding = Finding.Of(FindingCodes.ParallelCapacity, FindingSeverity.Warning, ["ejecutor", "ejecutor-2"], null, "claude-code", 4, 2);

        Assert.Equal([(object)"claude-code", 4, 2], finding.Arguments);
        Assert.False(finding.BlocksApproval);
        Assert.True(Finding.Of(FindingCodes.NoIntegration, FindingSeverity.Error, [], new AddIntegrationFix()).BlocksApproval);
        Assert.Throws<ArgumentException>(() => Finding.Of(FindingCodes.ParallelCapacity, FindingSeverity.Warning, [" "]));
        Assert.Throws<ArgumentException>(() => Finding.Of(FindingCodes.ParallelCapacity, FindingSeverity.Warning, [], null, new object()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Finding.Of(FindingCodes.ParallelCapacity, (FindingSeverity)5, []));

        var analysis = new MontageAnalysis(7, [finding, Finding.Of(FindingCodes.NoIntegration, FindingSeverity.Error, [])]);
        Assert.True(analysis.BlocksApproval);
    }

    /// <summary>D-18: a fix that changes the template withdraws the approval of the plan, and one that grants a capability of high risk asks Windows Hello.</summary>
    [Fact]
    public void A_fix_says_whether_it_changes_the_template_and_whether_it_grants_a_capability_of_high_risk()
    {
        Fix[] planOnly = [new AddIntegrationFix(), new AddBuildStepFix(Tid("T3"), "build"), new AddDependencyFix(Tid("T4"), Tid("T2")), new ReassignTaskFix(Tid("T4"), Aid("ejecutor-2")), new RaiseRunBudgetFix(20)];
        Fix[] template = [new ChangeModelFix(Aid("ejecutor"), "sonnet"), new SetMemoryFix(Aid("ejecutor"), MemoryPolicy.ReadSelected), new RemoveCapabilityFix(Aid("ejecutor"), "Bash"), new AddJudgeFix(ProviderId.CodexCli, VendorId.OpenAI)];

        Assert.All(planOnly, fix => Assert.False(fix.ChangesRoster, fix.GetType().Name));
        Assert.All(template, fix => Assert.True(fix.ChangesRoster, fix.GetType().Name));
        Assert.All([.. planOnly, .. template], fix => Assert.False(fix.GrantsHighRiskCapability, fix.GetType().Name));
        Assert.True(new WidenAgentFoldersFix(Aid("ejecutor"), "docs").GrantsHighRiskCapability);
    }

    [Fact]
    public void The_notes_of_order_name_their_tasks_once()
    {
        var notes = new OrderNotes(
            [new OrderDependencyNote(Tid("T2"), Tid("T3"), "T3 necesita la migración de T2")],
            [new ParallelOrderNote(Tid("T3"), Tid("T4"), "No comparten ficheros")],
            [new HarmfulOrderNote(Tid("T5"), Tid("T2"), "T5 antes que T2 rompería la compilación")]);

        Assert.Equal(["T2", "T3", "T4", "T5"], notes.Tasks.Select(t => t.Value));
        Assert.Empty(OrderNotes.None.Tasks);
    }

    [Fact]
    public void A_run_knows_the_concurrency_of_each_provider_with_the_default_of_the_design()
    {
        var run = new MontageRunFacts(3, 12, new Dictionary<ProviderId, int> { [ProviderId.CodexCli] = 1 });

        Assert.Equal(1, run.ConcurrencyOf(ProviderId.CodexCli));
        Assert.Equal(MontageRunFacts.DefaultProviderConcurrency, run.ConcurrencyOf(ProviderId.ClaudeCode));
        Assert.Equal(2, MontageRunFacts.DefaultProviderConcurrency);
    }
}
