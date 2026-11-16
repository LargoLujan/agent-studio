using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// What «Inicio» reads of a project to choose its next step (docs/24 §3.2), without opening anything the app did not open
/// already: whether Agent Studio ever wrote in the folder (a file that exists or not), and, for a project the orchestrator has
/// open, whether the configurator was applied to it (<c>ProjectSetupApplied</c> in its decision log, the same fact Auto Doctor
/// checks). The log is read off the UI thread. A read that fails, and a log that does not verify, is «not known», never «no».
/// </summary>
public sealed class ProjectProgressReader(IDecisionRecorder? decisions)
{
    /// <summary>The database of the project exists: Agent Studio already wrote in this folder. Without it nothing was ever applied or frozen there.</summary>
    public static bool HasStorage(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return File.Exists(Path.Combine(projectRoot, AgentStudioFiles.DataFolder, AgentStudioFiles.DatabaseFiles[0]));
    }

    /// <summary>
    /// Whether the configurator was applied to <paramref name="project"/>, which must be open already (the orchestrator opened it):
    /// this never opens a project. Null when it cannot be known.
    /// </summary>
    public async Task<bool?> SetupAppliedAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (decisions is null || project.ProjectId.Length == 0)
        {
            return null;
        }

        try
        {
            var log = await Task.Run(() => decisions.LoadAsync(project.ProjectId, ct), ct).ConfigureAwait(false);
            return log.Verification.IsValid ? log.Entries.Any(e => e.Kind == DecisionKind.ProjectSetupApplied) : null;
        }
#pragma warning disable CA1031 // The log is behind a port: a read that fails is «not known», never «no».
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
