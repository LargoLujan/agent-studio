using System.IO;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Orchestration.Verification;

public sealed class DotNetVerificationProfileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_proposal_builds_and_then_runs_the_xunit_tests_with_a_TRX_report()
    {
        var profile = DotNetVerificationProfile.Propose();

        Assert.Equal("dotnet", profile.Name);
        Assert.Equal(["build", "test"], profile.Steps.Select(s => s.Id));

        var build = profile.Steps[0];
        Assert.Equal("dotnet", build.Executable);
        Assert.Equal(["build"], build.Arguments);
        Assert.Equal(EvidenceKind.BuildLog, build.Kind);
        Assert.Equal(TestReportFormat.None, build.Report);
        Assert.Null(build.ReportPath);

        // Spike S4: --results-directory goes before "--"; each -filename option needs its report option; the xUnit v3
        // flag is --report-xunit-trx (not --report-trx); and zero tests must fail the run.
        var test = profile.Steps[1];
        Assert.Equal("dotnet", test.Executable);
        Assert.Equal(
            ["test", "--no-build", "--results-directory", "{results}", "--", "--report-xunit-trx", "--report-xunit-trx-filename", "test.trx", "--zero-tests-policy", "strict"],
            test.Arguments);
        Assert.Equal(EvidenceKind.TestRun, test.Kind);
        Assert.Equal(TestReportFormat.Trx, test.Report);
        Assert.Equal("{results}/test.trx", test.ReportPath);
    }

    [Fact]
    public void The_test_requires_the_build_so_a_plan_that_names_only_the_test_builds_first()
    {
        // B6: a verification starts from a fresh copy of the commit, where «dotnet test --no-build» finds nothing built.
        var profile = DotNetVerificationProfile.Propose();

        Assert.Equal(["build"], profile.Find("test")!.Requires);
        Assert.Null(profile.Find("build")!.Requires);
        Assert.Equal(["build", "test"], profile.StepsToRun(["test"]).Select(s => s.Id));
    }

    [Fact]
    public void The_proposal_follows_the_rules_of_a_profile()
    {
        Assert.Empty(VerificationRules.Check(DotNetVerificationProfile.Propose()));
    }

    [Fact]
    public async Task The_proposal_is_only_a_proposal_until_it_is_saved()
    {
        var root = Directory.CreateTempSubdirectory("as-profile-default-").FullName;
        try
        {
            Assert.Equal(VerificationProfileStatus.NotFound, (await VerificationProfileFile.LoadAsync(root, Ct)).Status);

            await VerificationProfileFile.SaveAsync(root, DotNetVerificationProfile.Propose(), new ProjectStorageFiles(), Ct);

            var load = await VerificationProfileFile.LoadAsync(root, Ct);
            Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
            Assert.Equal(DotNetVerificationProfile.Propose().ContentHash, load.Profile!.ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
