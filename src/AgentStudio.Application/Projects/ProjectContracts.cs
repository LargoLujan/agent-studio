namespace AgentStudio.Application.Projects;

// Phase 3 contract (docs/13 section 3.2).

/// <summary>An open user project: the id stored in its database and the folder it lives in.</summary>
public sealed record ProjectContext(string ProjectId, string RootPath);

/// <summary>
/// Opens a user project for orchestration: creates <c>.agentstudio/</c> and its database, applies the migrations,
/// makes sure the <c>projects</c> row exists (its id is a GUID) and adds <c>.agentstudio/*.db*</c>, <c>evidence/</c>
/// and <c>tmp/</c> to <c>.git/info/exclude</c>, never to the user's <c>.gitignore</c>. Opening twice is harmless.
/// </summary>
public interface IProjectWorkspace
{
    Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken);
}
