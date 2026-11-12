using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// The audit says what its tests leave out (docs/08, F5-5; ADR-0017 addendum): it must not promise more than it checks.
/// </summary>
public sealed class ProjectAuditExclusionsTests
{
    private static VerificationStep Test(ProcessIsolation isolation, params string[] arguments) => new(
        "test", "Pruebas", "dotnet", ["test", "--", .. arguments], EvidenceKind.TestRun, TestReportFormat.Trx, "{results}/test.trx", TimeSpan.FromMinutes(10), isolation);

    private static VerificationProfile Profile(params VerificationStep[] steps) => new("p", steps);

    [Fact]
    public void Every_trait_class_method_and_namespace_the_test_step_leaves_out_is_named_with_its_reason()
    {
        var excluded = ProjectAuditExclusions.Of(Profile(Test(
            ProcessIsolation.LowIntegrity,
            "--filter-not-trait", "Category=LocalTools",
            "--filter-not-trait", "Category=Measurement",
            "--filter-not-trait", "Category=Unheard",
            "--filter-not-class", "A.B.CTests",
            "--filter-not-trait", "Category=LocalTools")));

        Assert.Equal(
            ["Category=LocalTools", "Category=Measurement", "Category=Unheard", "A.B.CTests"],
            excluded.Select(e => e.Selector));
        Assert.Contains("git", excluded[0].Reason, StringComparison.Ordinal);
        Assert.Contains("aislamiento", excluded[0].Reason, StringComparison.Ordinal);
        Assert.Contains("sin motivo conocido", excluded[2].Reason, StringComparison.Ordinal);
        Assert.Equal("--filter-not-class", excluded[3].Filter);
        Assert.Contains("sin motivo conocido", excluded[3].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_without_exclusions_or_a_step_that_is_not_isolated_or_not_a_test_run_leaves_nothing_out()
    {
        Assert.Empty(ProjectAuditExclusions.Of(null));
        Assert.Empty(ProjectAuditExclusions.Of(Profile(Test(ProcessIsolation.LowIntegrity))));
        Assert.Empty(ProjectAuditExclusions.Of(Profile(Test(ProcessIsolation.None, "--filter-not-trait", "Category=LocalTools"))));
        Assert.Empty(ProjectAuditExclusions.Of(Profile(Test(ProcessIsolation.LowIntegrity) with { Kind = EvidenceKind.BuildLog, Arguments = ["build", "--filter-not-trait", "x"] })));
    }

    [Fact]
    public void The_line_of_the_request_says_what_is_not_exercised_and_when_there_is_nothing_it_says_so()
    {
        var line = ProjectAuditExclusions.Line(ProjectAuditExclusions.Of(Profile(Test(ProcessIsolation.LowIntegrity, "--filter-not-trait", "Category=LocalTools"))));
        Assert.StartsWith("Pruebas que la auditoría NO ejerce", line, StringComparison.Ordinal);
        Assert.Contains("Category=LocalTools", line, StringComparison.Ordinal);

        Assert.Contains("ninguna", ProjectAuditExclusions.Line([]), StringComparison.Ordinal);
    }
}
