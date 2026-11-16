using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// What «Inicio» knows of a project to choose its next step (docs/24 §3.2). Null means it could not be read: the step that
/// follows claims nothing about it.
/// </summary>
/// <param name="SetupApplied">
/// The configurator was applied: <c>ProjectSetupApplied</c> is in the decision log, the same fact Auto Doctor checks
/// (<c>ProjectCheckSource</c>), so the two never disagree.
/// </param>
/// <param name="HasFrozenRequirements">The project has a frozen set of requirements.</param>
public sealed record ProjectProgress(bool? SetupApplied, bool? HasFrozenRequirements)
{
    /// <summary>Nothing could be read.</summary>
    public static ProjectProgress Unknown { get; } = new(null, null);

    /// <summary>A project whose storage was never created: nothing was ever applied or frozen in it.</summary>
    public static ProjectProgress Untouched { get; } = new(false, false);
}

/// <summary>
/// Reads <see cref="ProjectProgress"/> from the decision log and the requirement store of the project. A folder without the
/// storage of Agent Studio is not opened, because opening it would create the storage in the user's folder: it is a project
/// where nothing was ever applied. Every read that fails, and a decision log that does not verify, is «not known», never «no».
/// </summary>
public sealed class ProjectProgressReader(IProjectWorkspace? workspace, IDecisionRecorder? decisions, IRequirementStore? requirements)
{
    public async Task<ProjectProgress> ReadAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!HasStorage(projectRoot))
        {
            return ProjectProgress.Untouched;
        }

        if (workspace is null)
        {
            return ProjectProgress.Unknown;
        }

        ProjectContext project;
        try
        {
            project = await workspace.OpenAsync(projectRoot, ct).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A project that cannot be opened has no next step that claims anything: «not known».
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return ProjectProgress.Unknown;
        }

        return new ProjectProgress(await SetupAppliedAsync(project, ct).ConfigureAwait(true), await FrozenAsync(project, ct).ConfigureAwait(true));
    }

    private async Task<bool?> SetupAppliedAsync(ProjectContext project, CancellationToken ct)
    {
        if (decisions is null)
        {
            return null;
        }

        try
        {
            var log = await decisions.LoadAsync(project.ProjectId, ct).ConfigureAwait(true);
            return log.Verification.IsValid ? log.Entries.Any(e => e.Kind == DecisionKind.ProjectSetupApplied) : null;
        }
#pragma warning disable CA1031 // The stores are behind ports: a read that fails is «not known», never «no».
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private async Task<bool?> FrozenAsync(ProjectContext project, CancellationToken ct)
    {
        if (requirements is null)
        {
            return null;
        }

        try
        {
            return await requirements.LatestAsync(project.ProjectId, ct).ConfigureAwait(true) is not null;
        }
#pragma warning disable CA1031 // The stores are behind ports: a read that fails is «not known», never «no».
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>The database of the project exists: Agent Studio already wrote in this folder.</summary>
    private static bool HasStorage(string projectRoot) =>
        File.Exists(Path.Combine(projectRoot, AgentStudioFiles.DataFolder, AgentStudioFiles.DatabaseFiles[0]));
}
