using System.Globalization;
using AgentStudio.Application.Projects;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Opens a user project for orchestration (docs/13 section 3.2), in this order:
/// <list type="number">
/// <item>takes the final path of the project root, and creates <c>.agentstudio/</c>, checked before and after
/// (<see cref="ProjectStorageGuard"/>: a real folder at its place, never a link);</item>
/// <item>takes the project's lock file, so a second instance of the app cannot orchestrate the same project
/// (<see cref="ProjectInUseException"/>); a link there is refused before it is opened;</item>
/// <item>checks the database files, the evidence, the temporary folder and the prompt journal it reuses
/// (<see cref="ProjectStorageIntegrityException"/>), before anything is written there;</item>
/// <item>adds the database, the evidence and the temporary files to git's <c>info/exclude</c> (see <see cref="GitExclude"/>);</item>
/// <item>creates the database if needed and applies the migrations;</item>
/// <item>makes sure the <c>projects</c> row exists. Its id is a GUID created the first time and kept in the project's
/// own database, so it travels with the folder.</item>
/// </list>
/// Opening a project that this instance already has open returns the same context and does nothing else.
/// </summary>
public sealed class ProjectWorkspace(ProjectDatabases databases, TimeProvider timeProvider) : IProjectWorkspace
{
    public async Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The project folder '{root}' does not exist.");
        }

        await databases.OpenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (databases.FindByRoot(root) is { } open)
            {
                return new ProjectContext(open.ProjectId, open.RootPath);
            }

            var layout = ProjectStorage.For(string.Empty, root, ProjectStorageGuard.FinalRoot(root));

            // Nothing is created, opened or written through a link left in the project: the data folder is checked
            // before it is created and after, the lock file before it is opened, the rest with the lock held.
            ProjectStorageGuard.CreateDataFolder(layout);
            ProjectStorageGuard.CheckLockFile(layout);
            var projectLock = await ProjectLock.AcquireAsync(layout.LockPath, root, cancellationToken).ConfigureAwait(false);
            try
            {
                ProjectStorageGuard.CheckStorage(layout);
                GitExclude.EnsureExcluded(root);
                var projectId = EnsureDatabase(layout.DatabasePath, root);
                databases.Register(layout with { ProjectId = projectId }, projectLock);
                return new ProjectContext(projectId, root);
            }
            catch
            {
                projectLock.Dispose();
                throw;
            }
        }
        finally
        {
            databases.OpenGate.Release();
        }
    }

    private string EnsureDatabase(string databasePath, string root)
    {
        using var connection = SqliteSchema.Open(ProjectDatabases.ConnectionString(databasePath, create: true));
        SqliteSchema.Apply(connection);

        using var transaction = connection.BeginTransaction(deferred: false);
        var ids = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM projects ORDER BY rowid;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        switch (ids.Count)
        {
            case 1:
                return ids[0];
            case > 1:
                // Only this class writes the row, one per database: anything else was put there by someone else.
                throw new InvalidDataException($"The database '{databasePath}' holds {ids.Count} projects; a project database holds exactly one.");
        }

        var projectId = Guid.NewGuid().ToString("D");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO projects(id, name, root_path, created_at) VALUES ($id, $name, $root, $at);";
            insert.Parameters.AddWithValue("$id", projectId);
            insert.Parameters.AddWithValue("$name", ProjectName(root));
            insert.Parameters.AddWithValue("$root", root);
            insert.Parameters.AddWithValue("$at", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return projectId;
    }

    private static string ProjectName(string root)
    {
        var name = Path.GetFileName(root);
        return string.IsNullOrEmpty(name) ? root : name;
    }
}
