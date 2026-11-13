using System.IO;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Team;
using AgentStudio.Infrastructure.Hosting.Diagnostics;
using AgentStudio.Tests.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Infrastructure.Hosting.Diagnostics;

/// <summary>
/// docs/21 D-31 and F10-13: the checks of Auto Doctor about <c>gh</c> and the remote. Nothing asks the network or GitHub: the only thing asked of <c>gh</c> is <c>--version</c>, through the door, and the rest comes
/// from the configuration of the repository and from what the mailbox already knows. «Sin comprobar» weighs more than «correcto», and every line is a state that has its text.
/// </summary>
public sealed class HostingCheckSourceTests
{
    private static readonly ProjectContext Project = new("p1", @"C:\work\project");

    private sealed class Remotes(RemoteListing listing) : IRepositoryRemoteReader
    {
        public int Asked { get; private set; }

        public Task<RemoteListing> ListRemotesAsync(string projectRoot, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(listing);
        }
    }

    private sealed class Publication(RemoteGitInspection? inspection) : IPublicationService
    {
        public int Inspected { get; private set; }

        public Task<RemoteGitInspection?> InspectAsync(ProjectContext project, CancellationToken ct)
        {
            Inspected++;
            return Task.FromResult(inspection);
        }

        public Task<PublicationPreview> PreviewAsync(ProjectContext project, RunId run, RemoteBranch? baseBranch, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteWorkTicket> PublishAsync(PublicationRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteWorkTicket> UpdateVerdictAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteWorkTicket> RequestReviewersAsync(ProjectContext project, PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteWorkTicket> MergeAsync(ProjectContext project, PullRequestRef pullRequest, MergeMethod method, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteWorkTicket> CloseAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteWorkTicket> DeleteRunBranchAsync(ProjectContext project, RunId run, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Sync(RemoteSyncHealth health) : IRemoteSync
    {
        public int Asked { get; private set; }

#pragma warning disable CS0067 // Part of the contract; the test never raises it.
        public event EventHandler<string>? Changed;
#pragma warning restore CS0067

        public RemoteSyncHealth Health(string projectId)
        {
            Asked++;
            return health;
        }

        public Task SyncNowAsync(ProjectContext project, CancellationToken ct) => throw new NotSupportedException();

        public Task FollowAsync(ProjectContext project, PullRequestRef pullRequest, RunId? run, CancellationToken ct) => throw new NotSupportedException();

        public Task UnfollowAsync(ProjectContext project, PullRequestRef pullRequest, CancellationToken ct) => throw new NotSupportedException();

        public Task CancelAsync(string projectId, string ticketId, CancellationToken ct) => throw new NotSupportedException();

        public IReadOnlyList<RemoteWorkStatus> Pending(string projectId) => [];
    }

    private sealed class Policy(bool allowed) : IRemoteSyncPolicy
    {
        public bool PeriodicSyncAllowed(string projectId) => allowed;

        public TimeSpan Interval => TimeSpan.FromMinutes(5);
    }

    /// <summary>The remotes as the git layer of the app keeps them: clean, so the user of an ssh address (<c>git@</c>) is already gone.</summary>
    private static RemoteListing Listing(params (string Name, string Url)[] remotes) => new([.. remotes.Select(r => RepositoryRemote.Cleaned(r.Name, r.Url))], null);

    private static RemoteGitInspection Clean => new(true, [], false, null);

    private static DoctorCheck Of(IReadOnlyList<DoctorCheck> checks, string code) => checks.Single(c => c.Code == code);

    private static Dictionary<string, DoctorStatus> States(IReadOnlyList<DoctorCheck> checks) => checks.ToDictionary(c => c.Code, c => c.Status);

    // ---- «Herramientas»: gh and its version ----

    [Fact]
    public async Task The_tested_version_is_installed_and_tested_and_the_only_question_is_gh_version_for_the_open_project()
    {
        var door = new ScriptedDoor().Says(DoctorTool.GhVersion, "gh version 2.96.0 (2026-07-02)\r\n");

        var checks = await new GhToolCheckSource(door).CheckAsync(Project, CancellationToken.None);

        Assert.Equal([(DoctorTool.GhVersion, (string?)Project.RootPath)], door.Asked);
        Assert.Equal(DoctorSection.Tools, new GhToolCheckSource(door).Section);
        Assert.All(checks, c => Assert.Equal(DoctorSection.Tools, c.Section));
        var installed = Of(checks, "doctor.gh.installed");
        Assert.Equal(DoctorStatus.Ok, installed.Status);
        Assert.Equal("2.96.0", installed.Facts[0].Value);
        Assert.Equal(DoctorStatus.Ok, Of(checks, "doctor.gh.version-untested").Status);
    }

    [Fact]
    public async Task Another_version_is_a_warning_that_it_was_not_tested_with_that_version_as_the_first_fact()
    {
        var door = new ScriptedDoor().Says(DoctorTool.GhVersion, "gh version 2.97.1 (2026-08-02)\n");

        var checks = await new GhToolCheckSource(door).CheckAsync(null, CancellationToken.None);

        var untested = Of(checks, "doctor.gh.version-untested");
        Assert.Equal(DoctorStatus.Warning, untested.Status);
        Assert.Equal("2.97.1", untested.Facts[0].Value);
        Assert.Equal(DoctorStatus.Ok, Of(checks, "doctor.gh.installed").Status);
    }

    [Fact]
    public async Task Without_gh_it_is_an_error_with_the_guide_to_install_it_and_the_version_line_does_not_apply()
    {
        var door = new ScriptedDoor().Fails(DoctorTool.GhVersion, DoctorToolOutcome.NotInstalled);

        var checks = await new GhToolCheckSource(door).CheckAsync(Project, CancellationToken.None);

        Assert.Equal(DoctorStatus.Error, Of(checks, "doctor.gh.installed").Status);
        Assert.Equal(HostingDoctorFixes.InstallGh, Of(checks, "doctor.gh.installed").FixCode);
        Assert.Equal(DoctorStatus.NotApplicable, Of(checks, "doctor.gh.version-untested").Status);
    }

    [Fact]
    public async Task A_gh_found_only_inside_the_project_is_a_warning_and_one_that_does_not_say_its_version_is_not_checked()
    {
        var inside = await new GhToolCheckSource(new ScriptedDoor().Fails(DoctorTool.GhVersion, DoctorToolOutcome.InsideProject, @"C:\work\project\gh.exe")).CheckAsync(Project, CancellationToken.None);
        var strange = await new GhToolCheckSource(new ScriptedDoor().Says(DoctorTool.GhVersion, "bash: gh: command not found")).CheckAsync(Project, CancellationToken.None);
        var timedOut = await new GhToolCheckSource(new ScriptedDoor().Fails(DoctorTool.GhVersion, DoctorToolOutcome.TimedOut)).CheckAsync(Project, CancellationToken.None);

        Assert.Equal(DoctorStatus.Warning, Of(inside, "doctor.gh.installed").Status);
        Assert.Equal(DoctorStatus.NotApplicable, Of(inside, "doctor.gh.version-untested").Status);
        Assert.Equal(DoctorStatus.Unknown, Of(strange, "doctor.gh.installed").Status);
        Assert.Equal(DoctorStatus.Unknown, Of(strange, "doctor.gh.version-untested").Status);
        Assert.Equal(DoctorStatus.Unknown, Of(timedOut, "doctor.gh.installed").Status);
    }

    // ---- «Proyecto»: the remote ----

    [Fact]
    public async Task Without_a_project_there_is_nothing_to_say_about_the_remote()
    {
        var remotes = new Remotes(Listing(("origin", "https://github.com/octo/hello.git")));

        var checks = await new HostingCheckSource(remotes).CheckAsync(null, CancellationToken.None);

        Assert.Empty(checks);
        Assert.Equal(0, remotes.Asked);
    }

    [Fact]
    public async Task A_github_remote_by_https_with_everything_known_is_all_correct_and_nothing_goes_to_the_network()
    {
        var remotes = new Remotes(Listing(("origin", "https://github.com/octo/hello.git")));
        var sync = new Sync(new RemoteSyncHealth(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero), HostingOutcome.Ok, null));
        var source = new HostingCheckSource(remotes, new Publication(Clean), sync, new Policy(true));

        var checks = await source.CheckAsync(Project, CancellationToken.None);

        Assert.Equal(DoctorSection.Project, source.Section);
        Assert.All(checks, c => Assert.Equal(DoctorSection.Project, c.Section));
        Assert.Equal(
            new[] { "doctor.remote.host", "doctor.remote.transport", "doctor.remote.config", "doctor.remote.lfs", "doctor.remote.session", "doctor.remote.sync" },
            checks.Select(c => c.Code));
        Assert.All(checks, c => Assert.Equal(DoctorStatus.Ok, c.Status));
        Assert.Equal("octo/hello", Of(checks, "doctor.remote.host").Facts[0].Value);
        Assert.Equal(HostingDoctorFixes.OpenPullRequests, Of(checks, "doctor.remote.host").FixCode);
        Assert.Equal(2, Of(checks, "doctor.remote.session").Facts.Count + Of(checks, "doctor.remote.sync").Facts.Count);
    }

    [Fact]
    public async Task A_remote_by_ssh_is_a_warning_that_agent_studio_publishes_by_https()
    {
        var checks = await new HostingCheckSource(new Remotes(Listing(("origin", "git@github.com:octo/hello.git")))).CheckAsync(Project, CancellationToken.None);

        Assert.Equal(DoctorStatus.Ok, Of(checks, "doctor.remote.host").Status);
        Assert.Equal(DoctorStatus.Warning, Of(checks, "doctor.remote.transport").Status);
    }

    [Fact]
    public async Task No_remote_or_one_that_is_not_github_make_the_rest_not_apply_and_the_host_line_says_which()
    {
        var none = await new HostingCheckSource(new Remotes(Listing())).CheckAsync(Project, CancellationToken.None);
        var other = await new HostingCheckSource(new Remotes(Listing(("origin", "https://gitlab.com/octo/hello.git")))).CheckAsync(Project, CancellationToken.None);
        var credentials = await new HostingCheckSource(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"), ("other", "https://github.com/x/y.git"), ("third", "https://github.com/z/w.git")))).CheckAsync(Project, CancellationToken.None);

        Assert.Equal(DoctorStatus.NotApplicable, Of(none, "doctor.remote.host").Status);
        Assert.Equal(DoctorStatus.Warning, Of(other, "doctor.remote.host").Status);
        foreach (var checks in new[] { none, other })
        {
            Assert.Equal(6, checks.Count);
            Assert.All(checks.Where(c => c.Code != "doctor.remote.host"), c => Assert.Equal(DoctorStatus.NotApplicable, c.Status));
        }

        // With several remotes the one that counts is origin.
        Assert.Equal("octo/hello", Of(credentials, "doctor.remote.host").Facts[0].Value);
    }

    [Fact]
    public async Task A_remote_that_could_not_be_read_is_not_checked_and_never_correct()
    {
        var checks = await new HostingCheckSource(new Remotes(RemoteListing.Refused("git-failed"))).CheckAsync(Project, CancellationToken.None);

        var states = States(checks);
        Assert.Equal(DoctorStatus.Unknown, states["doctor.remote.host"]);
        Assert.Equal(DoctorStatus.Unknown, states["doctor.remote.config"]);
        Assert.Equal(DoctorStatus.Unknown, states["doctor.remote.lfs"]);
        Assert.Equal(DoctorStatus.Unknown, states["doctor.remote.session"]);
        Assert.DoesNotContain(checks, c => c.Status == DoctorStatus.Ok);
    }

    [Fact]
    public async Task With_the_project_busy_the_inspection_is_null_and_the_configuration_and_lfs_are_not_checked()
    {
        var publication = new Publication(null);
        var checks = await new HostingCheckSource(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"))), publication).CheckAsync(Project, CancellationToken.None);

        Assert.Equal(1, publication.Inspected);
        Assert.Equal(DoctorStatus.Unknown, Of(checks, "doctor.remote.config").Status);
        Assert.Equal(DoctorStatus.Unknown, Of(checks, "doctor.remote.lfs").Status);
    }

    [Fact]
    public async Task The_keys_that_refuse_are_named_without_their_values_and_lfs_is_a_warning()
    {
        var inspection = new RemoteGitInspection(
            false,
            [new RemoteConfigRefusal("credential.helper", "local", ".git/config"), new RemoteConfigRefusal("url.https://evil/.insteadOf", "local", ".git/config"), new RemoteConfigRefusal("credential.helper", "worktree", "x")],
            true,
            null);
        var checks = await new HostingCheckSource(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"))), new Publication(inspection)).CheckAsync(Project, CancellationToken.None);

        var config = Of(checks, "doctor.remote.config");
        Assert.Equal(DoctorStatus.Error, config.Status);
        Assert.Equal("credential.helper, url.https://evil/.insteadOf", config.Facts[0].Value);
        Assert.Equal(DoctorStatus.Warning, Of(checks, "doctor.remote.lfs").Status);
    }

    [Fact]
    public async Task An_inspection_that_failed_is_not_checked_and_never_correct()
    {
        var checks = await new HostingCheckSource(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"))), new Publication(new RemoteGitInspection(false, [], false, "git-unavailable"))).CheckAsync(Project, CancellationToken.None);

        Assert.Equal(DoctorStatus.Unknown, Of(checks, "doctor.remote.config").Status);
        Assert.Equal(DoctorStatus.Unknown, Of(checks, "doctor.remote.lfs").Status);
    }

    [Fact]
    public async Task The_session_is_the_last_known_result_and_a_missing_one_is_not_checked_and_a_gone_one_gives_the_guide_to_sign_in()
    {
        var source = (RemoteSyncHealth? health) => new HostingCheckSource(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"))), new Publication(Clean), health is null ? null : new Sync(health));
        var at = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

        var never = Of(await source(null).CheckAsync(Project, CancellationToken.None), "doctor.remote.session");
        var unknown = Of(await source(new RemoteSyncHealth(null, null, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.session");
        var good = Of(await source(new RemoteSyncHealth(at, HostingOutcome.NotModified, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.session");
        var gone = Of(await source(new RemoteSyncHealth(at, HostingOutcome.SignInNeeded, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.session");
        var offline = Of(await source(new RemoteSyncHealth(at, HostingOutcome.Unreachable, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.session");

        Assert.Equal(DoctorStatus.Unknown, never.Status);
        Assert.Equal(DoctorStatus.Unknown, unknown.Status);
        Assert.Equal(DoctorStatus.Ok, good.Status);
        Assert.Equal(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), good.Facts[0].Value);
        Assert.Equal(DoctorStatus.Warning, gone.Status);
        Assert.Equal(HostingDoctorFixes.LoginGh, gone.FixCode);
        var seen = at.AddDays(1);
        var withDate = Of(await source(new RemoteSyncHealth(at, HostingOutcome.SignInNeeded, null, seen)).CheckAsync(Project, CancellationToken.None), "doctor.remote.session");
        Assert.Equal(seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), withDate.Facts[0].Value);
        Assert.Empty(gone.Facts);
        Assert.Equal(DoctorStatus.Unknown, offline.Status);
    }

    [Fact]
    public async Task The_synchronisation_is_not_applicable_unless_it_is_allowed_and_not_checked_until_a_good_one_exists()
    {
        var at = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        HostingCheckSource Source(IRemoteSyncPolicy? policy, RemoteSyncHealth health) => new(new Remotes(Listing(("origin", "https://github.com/octo/hello.git"))), new Publication(Clean), new Sync(health), policy);

        var off = Of(await Source(new Policy(false), new RemoteSyncHealth(at, HostingOutcome.Ok, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.sync");
        var none = Of(await Source(null, new RemoteSyncHealth(at, HostingOutcome.Ok, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.sync");
        var allowedWithoutGood = Of(await Source(new Policy(true), new RemoteSyncHealth(null, null, null)).CheckAsync(Project, CancellationToken.None), "doctor.remote.sync");

        Assert.Equal(DoctorStatus.NotApplicable, off.Status);
        Assert.Equal(DoctorStatus.NotApplicable, none.Status);
        Assert.Equal(DoctorStatus.Unknown, allowedWithoutGood.Status);
    }

    // ---- Every line is a state that has its text ----

    [Fact]
    public async Task Every_state_that_the_sources_can_say_has_its_text_in_the_resources_of_auto_doctor()
    {
        var resources = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AgentStudio.Desktop", "Resources", "DoctorStrings.resx"));
        var at = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var all = new List<DoctorCheck>();
        foreach (var door in new[]
        {
            new ScriptedDoor().Says(DoctorTool.GhVersion, "gh version 2.96.0\n"), new ScriptedDoor().Says(DoctorTool.GhVersion, "gh version 9.0.0\n"), new ScriptedDoor().Fails(DoctorTool.GhVersion, DoctorToolOutcome.NotInstalled),
            new ScriptedDoor().Fails(DoctorTool.GhVersion, DoctorToolOutcome.InsideProject), new ScriptedDoor().Says(DoctorTool.GhVersion, "?"),
        })
        {
            all.AddRange(await new GhToolCheckSource(door).CheckAsync(Project, CancellationToken.None));
        }

        var urls = new[] { "https://github.com/octo/hello.git", "git@github.com:octo/hello.git", "https://gitlab.com/o/r.git" };
        var inspections = new RemoteGitInspection?[] { Clean, null, new(false, [new RemoteConfigRefusal("credential.helper", "local", "f")], true, null), new(false, [], false, "x") };
        var healths = new RemoteSyncHealth?[]
        {
            null, new(null, null, null), new(at, HostingOutcome.Ok, null), new(at, HostingOutcome.SignInNeeded, null), new(at, HostingOutcome.RateLimited, at), new(null, HostingOutcome.SignInNeeded, null),
        };
        foreach (var url in urls)
        {
            foreach (var inspection in inspections)
            {
                foreach (var health in healths)
                {
                    foreach (var allowed in new[] { true, false })
                    {
                        all.AddRange(await new HostingCheckSource(new Remotes(Listing(("origin", url))), new Publication(inspection), health is null ? null : new Sync(health), new Policy(allowed)).CheckAsync(Project, CancellationToken.None));
                    }
                }
            }
        }

        all.AddRange(await new HostingCheckSource(new Remotes(Listing())).CheckAsync(Project, CancellationToken.None));
        all.AddRange(await new HostingCheckSource(new Remotes(RemoteListing.Refused("git-failed"))).CheckAsync(Project, CancellationToken.None));
        foreach (var check in all.DistinctBy(c => (c.Code, c.Status)))
        {
            var part = check.Code["doctor.".Length..].Replace('.', '_').Replace('-', '_');
            Assert.True(resources.Contains($"name=\"Doctor_Check_{part}_{check.Status}\"", StringComparison.Ordinal), $"{check.Code} {check.Status}");
            Assert.True(resources.Contains($"name=\"Doctor_Check_{part}_Title\"", StringComparison.Ordinal), check.Code);
            if (check.FixCode is not null)
            {
                Assert.True(resources.Contains($"name=\"Doctor_Fix_{check.FixCode["doctor.fix.".Length..].Replace('-', '_')}\"", StringComparison.Ordinal), check.FixCode);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentStudio.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The solution was not found above the test folder.");
    }
}
