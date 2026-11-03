using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Desktop.Orchestrator.Session;

/// <summary>
/// The trusted verification profile of a project, on top of WP3's strict loader (<see cref="VerificationProfileFile"/>,
/// ADR-0016): read from <c>&lt;root&gt;\.agentstudio\verification.json</c> of the project root, never of a worktree.
/// <list type="bullet">
/// <item>No file: the project has no profile, and <see cref="DotNetVerificationProfile"/> is what is proposed. It
/// becomes the profile only when the user accepts it («Usar perfil propuesto») and it is saved.</item>
/// <item>A file that breaks a rule cannot be used: loading throws with its problems, and nothing is proposed over it
/// (the user's file is never overwritten from the screen).</item>
/// <item>Saving goes through <paramref name="files"/>, the policy of the project's storage: never through a link.</item>
/// </list>
/// </summary>
public sealed class FileVerificationProfileSource(IProjectStorageFiles files) : IVerificationProfileSource
{
    public async Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var load = await VerificationProfileFile.LoadAsync(project.RootPath, cancellationToken).ConfigureAwait(false);
        return load.Status switch
        {
            VerificationProfileStatus.Loaded => load.Profile,
            VerificationProfileStatus.NotFound => null,
            _ => throw new InvalidDataException($"The verification profile '{load.Path}' cannot be used: {string.Join(" ", load.Problems)}"),
        };
    }

    public VerificationProfile? Propose(ProjectContext project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return DotNetVerificationProfile.Propose();
    }

    public Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        return VerificationProfileFile.SaveAsync(project.RootPath, profile, files, cancellationToken);
    }
}
