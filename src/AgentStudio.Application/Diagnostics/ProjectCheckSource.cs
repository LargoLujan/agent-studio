using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Application.Diagnostics;

/// <summary>What the layout of a project says, read without opening any file of it (names only) and without running anything (docs/20 D-03).</summary>
/// <param name="HasGit">There is a <c>.git</c> (a folder, or the file of a linked worktree).</param>
/// <param name="HooksReadable">The folder of the hooks could be listed: it is a real folder of the repository, not a link, and <c>.git</c> is a folder.</param>
/// <param name="HookNames">The hooks that are not the <c>.sample</c> files git ships, by name, at most 50.</param>
public sealed record ProjectLayout(bool HasGit, bool HooksReadable, IReadOnlyList<string> HookNames);

/// <summary>The questions of the project section that are answered from the file system and the PATH, without running a program (the implementation is in Infrastructure).</summary>
public interface IProjectEnvironmentProbe
{
    ProjectLayout ReadLayout(string projectRoot);

    /// <summary>The entries of the PATH that lie inside the project, resolved as the locator resolves them (links included); each is a folder whose programs are not used.</summary>
    IReadOnlyList<string> PathFoldersInside(string projectRoot);

    /// <summary>Whether the project declares containers: one of the names of docs/20 D-05 (<c>Dockerfile</c>, <c>compose.yaml</c> and its three variants, <c>.devcontainer/devcontainer.json</c>, <c>.devcontainer.json</c>) exists in its root. Only that it exists: nothing is read.</summary>
    bool DeclaresContainers(string projectRoot);
}

/// <summary>
/// The «Proyecto» section (docs/20 D-03): is it a repository and does the hardened layer of git accept it, are there hooks, the remotes, the log
/// and its chain, the configurator, the verification profile and its tools, and the PATH. Needs an open project. Git is asked through the hardened layer
/// only (<see cref="IWorktreeManager.InspectAsync"/>, <see cref="IRepositoryRemoteReader"/>): that layer starts <c>git</c> in the repository, which is the
/// exception of decision 13 (ADR-0061). Nothing else here starts a process.
/// </summary>
public sealed class ProjectCheckSource(
    IWorktreeManager git,
    IRepositoryRemoteReader remotes,
    IDecisionRecorder decisions,
    ISetupVerificationProfiles profiles,
    IInstalledToolInventory inventory,
    IProjectEnvironmentProbe environment) : IDoctorCheckSource
{
    public DoctorSection Section => DoctorSection.Project;

    public async Task<IReadOnlyList<DoctorCheck>> CheckAsync(ProjectContext? project, CancellationToken ct)
    {
        if (project is null)
        {
            return [];
        }

        var layout = environment.ReadLayout(project.RootPath);
        var checks = new List<DoctorCheck>
        {
            new("doctor.project.repository", DoctorSection.Project, layout.HasGit ? DoctorStatus.Ok : DoctorStatus.Error, []),
        };
        checks.AddRange(await GitChecksAsync(project, layout, ct).ConfigureAwait(false));
        var log = await LogChecksAsync(project, ct).ConfigureAwait(false);
        checks.AddRange(log.Checks);
        checks.AddRange(await ProfileChecksAsync(project, ct).ConfigureAwait(false));

        var inside = environment.PathFoldersInside(project.RootPath);
        checks.Add(new DoctorCheck(
            "doctor.project.path-inside",
            DoctorSection.Project,
            inside.Count == 0 ? DoctorStatus.Ok : DoctorStatus.Warning,
            [.. inside.Take(5).Select(f => DoctorFact.Of("folder", f))]));
        return checks;
    }

    private async Task<IReadOnlyList<DoctorCheck>> GitChecksAsync(ProjectContext project, ProjectLayout layout, CancellationToken ct)
    {
        var checks = new List<DoctorCheck>();
        if (!layout.HasGit)
        {
            checks.Add(new("doctor.project.git-layer", DoctorSection.Project, DoctorStatus.NotApplicable, []));
            checks.Add(new("doctor.project.hooks", DoctorSection.Project, DoctorStatus.NotApplicable, []));
            checks.Add(new("doctor.project.remotes", DoctorSection.Project, DoctorStatus.NotApplicable, []));
            return checks;
        }

        try
        {
            var state = await git.InspectAsync(project.RootPath, ct).ConfigureAwait(false);
            var facts = new List<DoctorFact>();
            if (!string.IsNullOrWhiteSpace(state.HeadBranch))
            {
                facts.Add(DoctorFact.Of("branch", state.HeadBranch));
            }

            facts.Add(DoctorFact.Of(DoctorFactNames.Version, state.GitVersion.ToString()));
            checks.Add(new("doctor.project.git-layer", DoctorSection.Project, DoctorStatus.Ok, facts));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The layer refuses a repository it does not accept (a planted link, a config it does not trust): that is the answer, not a failure of the panel.
        catch (Exception)
#pragma warning restore CA1031
        {
            checks.Add(new("doctor.project.git-layer", DoctorSection.Project, DoctorStatus.Error, []));
        }

        checks.Add(!layout.HooksReadable
            ? new DoctorCheck("doctor.project.hooks", DoctorSection.Project, DoctorStatus.Unknown, [])
            : new DoctorCheck(
                "doctor.project.hooks",
                DoctorSection.Project,
                layout.HookNames.Count == 0 ? DoctorStatus.Ok : DoctorStatus.Warning,
                [DoctorFact.Of("count", layout.HookNames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. layout.HookNames.Take(5).Select(h => DoctorFact.Of("hook", h))]));

        var listing = await remotes.ListRemotesAsync(project.RootPath, ct).ConfigureAwait(false);
        checks.Add(listing.RefusalCode is null
            ? new DoctorCheck(
                "doctor.project.remotes",
                DoctorSection.Project,
                DoctorStatus.Ok,
                [DoctorFact.Of("count", listing.Remotes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))])
            : new DoctorCheck("doctor.project.remotes", DoctorSection.Project, DoctorStatus.Unknown, [DoctorFact.Of("reason", listing.RefusalCode)]));
        return checks;
    }

    private sealed record LogReading(IReadOnlyList<DoctorCheck> Checks);

    private async Task<LogReading> LogChecksAsync(ProjectContext project, CancellationToken ct)
    {
        if (!DoctorProject.IsOpen(project))
        {
            // Nothing opened the project in this session, so it has no identity and no log to read (docs/20 D-03): Auto Doctor does not open it, which would write in it.
            return new LogReading(
            [
                new DoctorCheck("doctor.project.decision-log", DoctorSection.Project, DoctorStatus.Unknown, []),
                new DoctorCheck("doctor.project.setup", DoctorSection.Project, DoctorStatus.Unknown, []),
            ]);
        }

        try
        {
            var state = await decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
            var chain = state.Verification.IsValid
                ? new DoctorCheck("doctor.project.decision-log", DoctorSection.Project, DoctorStatus.Ok, [DoctorFact.Of("entries", state.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))])
                : new DoctorCheck(
                    "doctor.project.decision-log",
                    DoctorSection.Project,
                    DoctorStatus.Error,
                    state.Verification.FirstInvalidSequence is { } sequence ? [DoctorFact.Of("sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))] : []);
            var applied = state.Entries.Any(e => e.Kind == DecisionKind.ProjectSetupApplied);
            var setup = new DoctorCheck(
                "doctor.project.setup",
                DoctorSection.Project,
                applied ? DoctorStatus.Ok : DoctorStatus.Warning,
                [],
                applied ? null : DoctorFixCatalog.OpenSetup);
            return new LogReading([chain, setup]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A log that cannot be read is «sin comprobar», never «correcto».
        catch (Exception)
#pragma warning restore CA1031
        {
            return new LogReading(
            [
                new DoctorCheck("doctor.project.decision-log", DoctorSection.Project, DoctorStatus.Unknown, []),
                new DoctorCheck("doctor.project.setup", DoctorSection.Project, DoctorStatus.Unknown, []),
            ]);
        }
    }

    private async Task<IReadOnlyList<DoctorCheck>> ProfileChecksAsync(ProjectContext project, CancellationToken ct)
    {
        VerificationProfile? profile;
        try
        {
            profile = await profiles.LoadAsync(project, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A profile that cannot be used (InvalidDataException) is an error of the profile; anything else is «sin comprobar».
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var invalid = ex is InvalidDataException;
            return
            [
                new DoctorCheck("doctor.project.verification-profile", DoctorSection.Project, invalid ? DoctorStatus.Error : DoctorStatus.Unknown, [], invalid ? DoctorFixCatalog.OpenSetup : null),
                new DoctorCheck("doctor.project.profile-tools", DoctorSection.Project, DoctorStatus.Unknown, []),
            ];
        }

        if (profile is null)
        {
            return
            [
                new DoctorCheck("doctor.project.verification-profile", DoctorSection.Project, DoctorStatus.Warning, [], DoctorFixCatalog.OpenSetup),
                new DoctorCheck("doctor.project.profile-tools", DoctorSection.Project, DoctorStatus.NotApplicable, []),
            ];
        }

        var profileCheck = new DoctorCheck(
            "doctor.project.verification-profile",
            DoctorSection.Project,
            DoctorStatus.Ok,
            [DoctorFact.Of("name", profile.Name), DoctorFact.Of("steps", profile.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        return [profileCheck, await ProfileToolsAsync(project, profile, ct).ConfigureAwait(false)];
    }

    /// <summary>
    /// Each tool the steps of the profile name, when it is one of the closed list the door can ask for (docs/20 D-02), against the inventory. A step
    /// whose program is not on the list is not asked: it is neither a tool found nor missing, so it is not counted.
    /// </summary>
    private async Task<DoctorCheck> ProfileToolsAsync(ProjectContext project, VerificationProfile profile, CancellationToken ct)
    {
        var wanted = profile.Steps
            .Select(s => Path.GetFileNameWithoutExtension(s.Executable.Trim()).ToLowerInvariant())
            .Where(n => n is "dotnet" or "node" or "npm" or "python" or "py" or "git")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (wanted.Count == 0)
        {
            return new DoctorCheck("doctor.project.profile-tools", DoctorSection.Project, DoctorStatus.NotApplicable, []);
        }

        var installed = await inventory.ReadAsync(project.RootPath, ct).ConfigureAwait(false);
        var missing = wanted.Where(n => !IsInstalled(installed, n)).ToList();
        var facts = new List<DoctorFact> { DoctorFact.Of("tools", string.Join(", ", wanted)) };
        if (missing.Count > 0)
        {
            facts.Add(DoctorFact.Of("missing", string.Join(", ", missing)));
        }

        return new DoctorCheck("doctor.project.profile-tools", DoctorSection.Project, missing.Count == 0 ? DoctorStatus.Ok : DoctorStatus.Warning, facts);
    }

    private static bool IsInstalled(InstalledTools installed, string tool) => tool switch
    {
        "dotnet" => installed.DotNetSdks.Count > 0,
        "node" => installed.Node is not null,
        "npm" => installed.Npm is not null,
        "python" or "py" => installed.Python is not null,
        "git" => installed.Git is not null,
        _ => true,
    };
}
