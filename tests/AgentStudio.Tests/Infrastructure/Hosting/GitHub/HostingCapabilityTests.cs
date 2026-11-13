using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Tests.Infrastructure.Hosting.GitHub;

/// <summary>
/// F10-1 (docs/21 D-09 and D-14), the part of the adapter: what is declared depends on the version of <c>gh</c>, what is not declared is not offered (the call launches nothing), the version goes through the door of
/// Auto Doctor and never through <c>gh</c> itself, and a version older than the tested one declares nothing. The tested version, 2.96.0, is also the minimum: nothing was found about from which version the options
/// that the adapter uses exist.
/// </summary>
public sealed class HostingCapabilityTests
{
    private static readonly RemoteRepository Repo = GhRig.Repo;

    private static string Says(string version) => "gh version " + version + " (2026-07-02)\r\nhttps://github.com/cli/cli/releases/tag/v" + version + "\r\n";

    [Fact]
    public async Task The_tested_version_declares_everything_the_adapter_does_and_is_not_untested()
    {
        using var rig = new GhRig("2.96.0");

        var availability = await rig.Provider.ProbeAsync(CancellationToken.None);

        Assert.True(availability.Installed);
        Assert.Equal(new Version(2, 96, 0), availability.CliVersion);
        Assert.False(availability.Capabilities.UntestedCliVersion);
        Assert.Equal([MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase], availability.Capabilities.MergeMethods);
        foreach (var capability in new[]
        {
            HostingCapability.ReadRepository, HostingCapability.ReadBranchRules, HostingCapability.OpenPullRequest, HostingCapability.UpdatePullRequest, HostingCapability.Comment,
            HostingCapability.RequestReviewers, HostingCapability.ReadReviews, HostingCapability.ReadChecks, HostingCapability.Merge, HostingCapability.ClosePullRequest,
            HostingCapability.DeleteBranch, HostingCapability.CommitStatus, HostingCapability.ReadDeployments, HostingCapability.ConditionalRequests, HostingCapability.DraftPullRequest,
        })
        {
            Assert.True(availability.Capabilities.Has(capability), capability.ToString());
        }
    }

    [Fact]
    public async Task What_no_member_of_the_contract_delivers_is_not_declared()
    {
        using var rig = new GhRig();

        var availability = await rig.Provider.ProbeAsync(CancellationToken.None);

        Assert.False(availability.Capabilities.Has(HostingCapability.ReadWorkflowRuns));
    }

    [Theory]
    [InlineData("2.97.0")]
    [InlineData("2.100.1")]
    [InlineData("3.0.0")]
    [InlineData("3.0.0-rc.1")]
    public async Task A_newer_version_declares_the_same_with_a_warning_that_it_was_not_tested(string version)
    {
        using var rig = new GhRig(version);

        var availability = await rig.Provider.ProbeAsync(CancellationToken.None);

        Assert.True(availability.Capabilities.UntestedCliVersion);
        Assert.True(availability.Capabilities.Has(HostingCapability.Merge));
        Assert.Equal(GitHubCli.Declared, availability.Capabilities.Supported);
    }

    [Theory]
    [InlineData("2.95.9")]
    [InlineData("2.90.0")]
    [InlineData("1.14.0")]
    [InlineData("2.96.0-rc.1")]
    public async Task An_older_version_than_the_tested_one_declares_nothing_but_is_installed(string version)
    {
        using var rig = new GhRig(version);

        var availability = await rig.Provider.ProbeAsync(CancellationToken.None);

        Assert.True(availability.Installed);
        Assert.Equal(HostingCapability.None, availability.Capabilities.Supported);
        Assert.Empty(availability.Capabilities.MergeMethods);
    }

    [Fact]
    public async Task Without_gh_or_with_one_found_only_inside_the_project_or_one_that_does_not_say_its_version_nothing_is_declared()
    {
        using var missing = new GhRig(installed: false);
        var none = await missing.Provider.ProbeAsync(CancellationToken.None);
        Assert.False(none.Installed);
        Assert.Equal(HostingCapabilities.None, none.Capabilities);

        using var inside = new GhRig();
        inside.Door.Fails(DoctorTool.GhVersion, DoctorToolOutcome.InsideProject, @"C:\project\gh.exe");
        var planted = await inside.Provider.ProbeAsync(CancellationToken.None);
        Assert.False(planted.Installed);
        Assert.Equal(HostingCapabilities.None, planted.Capabilities);

        using var strange = new GhRig();
        strange.Door.Says(DoctorTool.GhVersion, "gh: command not found");
        var unreadable = await strange.Provider.ProbeAsync(CancellationToken.None);
        Assert.True(unreadable.Installed);
        Assert.Null(unreadable.CliVersion);
        Assert.Equal(HostingCapabilities.None, unreadable.Capabilities);

        using var failing = new GhRig();
        failing.Door.Says(DoctorTool.GhVersion, Says("2.96.0"), exit: 1);
        Assert.Equal(HostingCapabilities.None, (await failing.Provider.ProbeAsync(CancellationToken.None)).Capabilities);
    }

    [Fact]
    public async Task The_version_is_asked_to_the_door_of_auto_doctor_and_never_launched_by_the_adapter()
    {
        using var rig = new GhRig();

        await rig.Provider.ProbeAsync(CancellationToken.None);

        Assert.Equal([(DoctorTool.GhVersion, (string?)null)], rig.Door.Asked);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task The_declaration_is_read_once_and_kept_until_the_next_probe()
    {
        using var rig = new GhRig("2.96.0");
        rig.Script.Route("GET", "/user", "handmade-user-200.http");

        await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);
        await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);
        Assert.Single(rig.Door.Asked);

        // gh was updated to a version that is older: the next probe says so and the calls stop.
        rig.Door.Says(DoctorTool.GhVersion, Says("2.90.0"));
        await rig.Provider.ProbeAsync(CancellationToken.None);
        rig.Script.Specs.Clear();
        var refused = await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None);

        Assert.Equal(2, rig.Door.Asked.Count);
        Assert.Equal(HostingOutcome.Unsupported, refused.Outcome);
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task What_is_not_declared_launches_nothing_whatever_the_operation()
    {
        using var rig = new GhRig("2.90.0");
        var pr = GhRig.Pr;
        var text = new PullRequestText("t", "b");
        var notice = new VerdictNotice(new RunId("run1"), Gh.Sha1, VerdictStanding.Approved, [], "p", "m", true);

        var outcomes = new[]
        {
            (await rig.Provider.ReadAccountAsync(Repo, CancellationToken.None)).Outcome,
            (await rig.Provider.ReadRepositoryAsync(Repo, null, CancellationToken.None)).Outcome,
            (await rig.Provider.ReadBranchRulesAsync(Repo, GhRig.Main, CancellationToken.None)).Outcome,
            (await rig.Provider.FindPullRequestAsync(Repo, GhRig.RunBranch, CancellationToken.None)).Outcome,
            (await rig.Provider.OpenPullRequestAsync(new PullRequestDraft(Repo, GhRig.RunBranch, GhRig.Main, text, false), CancellationToken.None)).Outcome,
            (await rig.Provider.UpdatePullRequestAsync(pr, text, CancellationToken.None)).Outcome,
            (await rig.Provider.RequestReviewersAsync(pr, [RemoteLogin.TryParse("alice")!], CancellationToken.None)).Outcome,
            (await rig.Provider.ObserveAsync(pr, null, CancellationToken.None)).Outcome,
            (await rig.Provider.PublishVerdictAsync(pr, notice, "c", null, CancellationToken.None)).Outcome,
            (await rig.Provider.MergeAsync(pr, MergeMethod.Squash, Gh.Sha1, CancellationToken.None)).Outcome,
            (await rig.Provider.CloseAsync(pr, CancellationToken.None)).Outcome,
            (await rig.Provider.DeleteRunBranchAsync(Repo, GhRig.RunBranch, Gh.Sha1, CancellationToken.None)).Outcome,
        };

        Assert.All(outcomes, o => Assert.Equal(HostingOutcome.Unsupported, o));
        Assert.Empty(rig.Script.Specs);
    }

    [Fact]
    public async Task What_the_repository_does_not_allow_narrows_the_capabilities_and_never_adds_one()
    {
        using var rig = new GhRig();
        var declared = (await rig.Provider.ProbeAsync(CancellationToken.None)).Capabilities;

        var squashOnly = declared.NarrowedBy(new RemoteRepositoryFacts(GhRig.Main, [MergeMethod.Squash], CanPush: true, Archived: false));
        var readOnly = declared.NarrowedBy(new RemoteRepositoryFacts(GhRig.Main, [MergeMethod.Squash, MergeMethod.Rebase], CanPush: false, Archived: false));
        var noMethods = declared.NarrowedBy(new RemoteRepositoryFacts(GhRig.Main, [], CanPush: true, Archived: false));

        Assert.Equal([MergeMethod.Squash], squashOnly.MergeMethods);
        Assert.True(squashOnly.Has(HostingCapability.Merge));
        Assert.False(readOnly.Has(HostingCapability.OpenPullRequest) || readOnly.Has(HostingCapability.Merge) || readOnly.Has(HostingCapability.CommitStatus));
        Assert.True(readOnly.Has(HostingCapability.ReadRepository) && readOnly.Has(HostingCapability.ReadChecks));
        Assert.False(noMethods.Has(HostingCapability.Merge));
        Assert.All(new[] { squashOnly, readOnly, noMethods }, narrowed => Assert.Equal(HostingCapability.None, narrowed.Supported & ~declared.Supported));
    }

    [Fact]
    public void The_contract_has_no_member_that_sends_a_review_approves_or_merges_as_an_administrator()
    {
        var members = typeof(IHostingProvider).GetMembers().Select(m => m.Name).Order().ToList();

        Assert.Equal(
            new[]
            {
                "ProbeAsync", "ReadAccountAsync", "ReadRepositoryAsync", "ReadBranchRulesAsync", "FindPullRequestAsync", "OpenPullRequestAsync", "UpdatePullRequestAsync", "RequestReviewersAsync",
                "ObserveAsync", "PublishVerdictAsync", "MergeAsync", "CloseAsync", "DeleteRunBranchAsync", "get_Kind", "Kind",
            }.Order(),
            members);
        Assert.DoesNotContain(members, m => m.Contains("Approve", StringComparison.OrdinalIgnoreCase) || m.Contains("SubmitReview", StringComparison.OrdinalIgnoreCase) || m.Contains("Admin", StringComparison.OrdinalIgnoreCase) || m.Contains("Force", StringComparison.OrdinalIgnoreCase));
    }
}
