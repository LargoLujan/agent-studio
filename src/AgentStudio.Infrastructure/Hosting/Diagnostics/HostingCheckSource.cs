using System.Globalization;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Hosting;
using AgentStudio.Infrastructure.Hosting.GitHub;

namespace AgentStudio.Infrastructure.Hosting.Diagnostics;

/// <summary>
/// The codes of the fixes of the checks of the remote (docs/21 D-31). The texts are in <c>DoctorStrings</c> (<c>Doctor_Fix_install_gh</c>, <c>Doctor_Fix_login_gh</c>, <c>Doctor_Fix_open_pull_requests</c>); the
/// fixes themselves (a guide with the <c>winget</c> command, a guide with <c>gh auth login</c> that the app never runs, and the navigation to «Repositorio › PR») are entries of <c>DoctorFixCatalog</c>, which
/// belongs to the contracts of the P0.
/// </summary>
public static class HostingDoctorFixes
{
    public const string InstallGh = "doctor.fix.install-gh";
    public const string LoginGh = "doctor.fix.login-gh";
    public const string OpenPullRequests = "doctor.fix.open-pull-requests";
}

/// <summary>
/// The two checks of «Herramientas» about <c>gh</c> (docs/21 D-31): that it is installed, with its version, and whether the version is the tested one. The only thing it asks of <c>gh</c> is
/// <c>gh --version</c>, through <see cref="IDoctorToolRunner"/> and its variables that keep <c>gh</c> from the network (F10-13). A <c>gh</c> found only inside the project is not used and is an aviso; one that did
/// not say its version in the form of <c>gh --version</c> is «sin comprobar», never «correcto».
/// </summary>
public sealed class GhToolCheckSource(IDoctorToolRunner door) : IDoctorCheckSource
{
    public DoctorSection Section => DoctorSection.Tools;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        var reading = await GitHubCli.ReadAsync(door, project?.RootPath, ct).ConfigureAwait(false);
        var version = reading.Version is { } v ? new[] { DoctorFact.Of(DoctorFactNames.Version, v.Text) } : [];
        var installed = reading.State switch
        {
            GhInstallState.Installed => new DoctorCheck("doctor.gh.installed", DoctorSection.Tools, DoctorStatus.Ok, version),
            GhInstallState.NotInstalled => new DoctorCheck("doctor.gh.installed", DoctorSection.Tools, DoctorStatus.Error, [], HostingDoctorFixes.InstallGh),
            GhInstallState.InsideProject => new DoctorCheck("doctor.gh.installed", DoctorSection.Tools, DoctorStatus.Warning, []),
            _ => new DoctorCheck("doctor.gh.installed", DoctorSection.Tools, DoctorStatus.Unknown, []),
        };
        var untested = reading.State switch
        {
            GhInstallState.Installed => new DoctorCheck("doctor.gh.version-untested", DoctorSection.Tools, GitHubCli.IsUntested(reading.Version!) ? DoctorStatus.Warning : DoctorStatus.Ok, version),
            GhInstallState.Unreadable => new DoctorCheck("doctor.gh.version-untested", DoctorSection.Tools, DoctorStatus.Unknown, []),
            _ => new DoctorCheck("doctor.gh.version-untested", DoctorSection.Tools, DoctorStatus.NotApplicable, []),
        };
        return [installed, untested];
    }
}

/// <summary>
/// The six checks of «Proyecto» about the remote (docs/21 D-31): what the remote is, how it is addressed, whether the configuration of the repository lets a publication through, whether the project uses LFS,
/// the last known state of the session of <c>gh</c> and whether the synchronisation is allowed. None of them asks the network or GitHub: the remote is read from the configuration of the repository, the
/// configuration and LFS come from the inspection of the mailbox (which holds the project and answers null when it is busy: «sin comprobar»), and the session and the synchronisation from what the mailbox
/// already knows. The services of the mailbox are optional: without them the lines say «sin comprobar» and nothing more.
/// </summary>
public sealed class HostingCheckSource(
    IRepositoryRemoteReader remotes,
    IPublicationService? publication = null,
    IRemoteSync? sync = null,
    IRemoteSyncPolicy? policy = null) : IDoctorCheckSource
{
    public const string RemoteName = "origin";

    public DoctorSection Section => DoctorSection.Project;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        if (project is null)
        {
            return [];
        }

        var listing = await remotes.ListRemotesAsync(project.RootPath, ct).ConfigureAwait(false);
        if (listing.RefusalCode is not null)
        {
            return
            [
                Check("doctor.remote.host", DoctorStatus.Unknown),
                Check("doctor.remote.transport", DoctorStatus.NotApplicable),
                Check("doctor.remote.config", DoctorStatus.Unknown),
                Check("doctor.remote.lfs", DoctorStatus.Unknown),
                Check("doctor.remote.session", DoctorStatus.Unknown),
                Check("doctor.remote.sync", DoctorStatus.NotApplicable),
            ];
        }

        var remote = listing.Remotes.FirstOrDefault(r => r.Name == RemoteName) ?? (listing.Remotes.Count == 1 ? listing.Remotes[0] : null);
        if (remote is null)
        {
            return NotApplicableExceptHost(Check("doctor.remote.host", DoctorStatus.NotApplicable));
        }

        var repository = GitHubRemoteAddress.TryParse(remote.Url, out var bySsh);
        if (repository is null)
        {
            return NotApplicableExceptHost(Check("doctor.remote.host", DoctorStatus.Warning));
        }

        var inspection = publication is null ? null : await publication.InspectAsync(project, ct).ConfigureAwait(false);
        var health = sync?.Health(project.ProjectId);
        return
        [
            Check("doctor.remote.host", DoctorStatus.Ok, HostingDoctorFixes.OpenPullRequests, DoctorFact.Of("repository", repository.Owner + "/" + repository.Name)),
            Check("doctor.remote.transport", bySsh ? DoctorStatus.Warning : DoctorStatus.Ok),
            Configuration(inspection),
            inspection is null || inspection.FailureCode is not null
                ? Check("doctor.remote.lfs", DoctorStatus.Unknown)
                : Check("doctor.remote.lfs", inspection.UsesLfs ? DoctorStatus.Warning : DoctorStatus.Ok),
            Session(health),
            Synchronisation(project, health),
        ];
    }

    private static DoctorCheck Check(string code, DoctorStatus status, string? fixCode = null, params DoctorFact[] facts) =>
        new(code, DoctorSection.Project, status, facts, fixCode);

    private static IReadOnlyList<DoctorCheck> NotApplicableExceptHost(DoctorCheck host) =>
    [
        host,
        Check("doctor.remote.transport", DoctorStatus.NotApplicable),
        Check("doctor.remote.config", DoctorStatus.NotApplicable),
        Check("doctor.remote.lfs", DoctorStatus.NotApplicable),
        Check("doctor.remote.session", DoctorStatus.NotApplicable),
        Check("doctor.remote.sync", DoctorStatus.NotApplicable),
    ];

    /// <summary>The keys that refuse, by name and never by value (the value could be a command or an address of the repository).</summary>
    private static DoctorCheck Configuration(RemoteGitInspection? inspection)
    {
        if (inspection is null || inspection.FailureCode is not null)
        {
            return Check("doctor.remote.config", DoctorStatus.Unknown);
        }

        if (inspection.Accepted && inspection.Refusals.Count == 0)
        {
            return Check("doctor.remote.config", DoctorStatus.Ok);
        }

        var keys = string.Join(", ", inspection.Refusals.Select(r => r.Key).Distinct(StringComparer.Ordinal).Take(8));
        return keys.Length == 0
            ? Check("doctor.remote.config", DoctorStatus.Error)
            : Check("doctor.remote.config", DoctorStatus.Error, null, DoctorFact.Of("keys", keys));
    }

    private static DoctorCheck Session(RemoteSyncHealth? health) => health?.LastOutcome switch
    {
        HostingOutcome.Ok or HostingOutcome.NotModified => health.LastGoodSync is { } at
            ? Check("doctor.remote.session", DoctorStatus.Ok, null, DoctorFact.Of("date", Date(at)))
            : Check("doctor.remote.session", DoctorStatus.Ok),
        HostingOutcome.SignInNeeded => Check("doctor.remote.session", DoctorStatus.Warning, HostingDoctorFixes.LoginGh),
        _ => Check("doctor.remote.session", DoctorStatus.Unknown),
    };

    private DoctorCheck Synchronisation(ProjectContext project, RemoteSyncHealth? health)
    {
        if (policy is null || !policy.PeriodicSyncAllowed(project.ProjectId))
        {
            return Check("doctor.remote.sync", DoctorStatus.NotApplicable);
        }

        return health?.LastGoodSync is { } at
            ? Check("doctor.remote.sync", DoctorStatus.Ok, null, DoctorFact.Of("date", Date(at)))
            : Check("doctor.remote.sync", DoctorStatus.Unknown);
    }

    private static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
