using System.IO;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

public sealed class ProjectWorkspaceTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Opening_creates_the_data_folder_and_a_database_with_every_migration()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.True(Directory.Exists(_folder.DataDirectory));
        Assert.True(File.Exists(_folder.DatabasePath));
        using var fresh = SqliteSchema.Open("Data Source=:memory:");
        SqliteSchema.Apply(fresh);
        using var connection = instance.Connect();
        Assert.Equal(Scalar(fresh, "SELECT COUNT(*) FROM schema_migrations"), Scalar(connection, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'orchestration_runs'"));
    }

    [Fact]
    public async Task The_project_gets_a_guid_that_is_kept_in_its_database_across_openings()
    {
        string first;
        using (var instance = await ProjectInstance.OpenAsync(_folder.Root))
        {
            first = instance.ProjectId;
            Assert.True(Guid.TryParse(first, out _), $"'{first}' is not a GUID.");
            Assert.Equal(Path.GetFullPath(_folder.Root), instance.Project.RootPath);
        }

        using var reopened = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Equal(first, reopened.ProjectId);
        using var connection = reopened.Connect();
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM projects"));
        Assert.Equal(first, Text(connection, "SELECT id FROM projects"));
        Assert.Equal("proyecto ñ", Text(connection, "SELECT name FROM projects"));
        Assert.Equal(Path.GetFullPath(_folder.Root), Text(connection, "SELECT root_path FROM projects"));
    }

    [Fact]
    public async Task Each_project_folder_gets_its_own_id()
    {
        using var other = new TempProjectFolder();
        using var a = await ProjectInstance.OpenAsync(_folder.Root);
        using var b = await ProjectInstance.OpenAsync(other.Root);

        Assert.NotEqual(a.ProjectId, b.ProjectId);
    }

    [Fact]
    public async Task Opening_twice_in_the_same_instance_is_harmless_and_gives_the_same_project()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        var again = await instance.Workspace.OpenAsync(_folder.Root + Path.DirectorySeparatorChar, Ct);

        Assert.Equal(instance.Project, again);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_not_created()
    {
        var missing = Path.Combine(_folder.Root, "no existe");
        using var databases = new ProjectDatabases();
        var workspace = new ProjectWorkspace(databases, new FakeTimeProvider(T0));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => workspace.OpenAsync(missing, Ct));

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void The_stores_refuse_a_project_that_was_not_opened()
    {
        using var databases = new ProjectDatabases();

        var ex = Assert.Throws<InvalidOperationException>(() => databases.Get("never-opened"));

        Assert.Contains("never-opened", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registry_tells_where_the_data_of_the_project_lives()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);

        var storage = instance.Databases.Get(instance.ProjectId);

        Assert.Equal(instance.ProjectId, storage.ProjectId);
        Assert.Equal(Path.GetFullPath(_folder.Root), storage.RootPath);
        Assert.Equal(Path.GetFullPath(_folder.DatabasePath), storage.DatabasePath);
        Assert.Equal(Path.GetFullPath(_folder.EvidenceDirectory), storage.EvidenceDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(_folder.DataDirectory, "tmp")), storage.TempDirectory);
    }

    [Fact]
    public async Task A_database_that_holds_more_than_one_project_is_refused()
    {
        using (var instance = await ProjectInstance.OpenAsync(_folder.Root))
        {
            using var connection = instance.Connect();
            Exec(connection, "INSERT INTO projects(id, name, root_path, created_at) VALUES ('intruso', 'x', 'C:/x', '2026-01-01T10:00:00.0000000+00:00')");
        }

        using var databases = new ProjectDatabases();
        var workspace = new ProjectWorkspace(databases, new FakeTimeProvider(T0));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => workspace.OpenAsync(_folder.Root, Ct));

        Assert.Contains("holds 2 projects", ex.Message, StringComparison.Ordinal);
    }

    // ---- one instance per project ----

    [Fact]
    public async Task A_second_instance_cannot_open_a_project_that_another_instance_has_open()
    {
        using var first = await ProjectInstance.OpenAsync(_folder.Root);
        using var databases = new ProjectDatabases();
        var second = new ProjectWorkspace(databases, new FakeTimeProvider(T0));

        var ex = await Assert.ThrowsAsync<ProjectInUseException>(() => second.OpenAsync(_folder.Root, Ct));

        Assert.Contains(Path.GetFullPath(_folder.Root), ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => databases.Get(first.ProjectId));
    }

    [Fact]
    public async Task Closing_the_project_lets_another_instance_open_it()
    {
        var first = await ProjectInstance.OpenAsync(_folder.Root);
        var id = first.ProjectId;
        first.Databases.Close(id);

        using var second = await ProjectInstance.OpenAsync(_folder.Root);

        Assert.Equal(id, second.ProjectId);
        Assert.Throws<InvalidOperationException>(() => first.Databases.Get(id));
        first.Dispose();
    }

    [Fact]
    public async Task Closing_releases_every_handle_so_the_folder_can_be_deleted()
    {
        var instance = await ProjectInstance.OpenAsync(_folder.Root);
        using (var connection = instance.Connect())
        {
            Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM projects"));
        }

        instance.Dispose();

        Directory.Delete(_folder.DataDirectory, recursive: true);
        Assert.False(Directory.Exists(_folder.DataDirectory));
    }
}

public sealed class SqliteConnectionTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_database_file_is_opened_with_write_ahead_logging_and_a_busy_timeout()
    {
        Directory.CreateDirectory(_folder.DataDirectory);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _folder.DatabasePath, Pooling = false }.ToString();

        using var connection = SqliteSchema.Open(connectionString);

        Assert.Equal("wal", Text(connection, "PRAGMA journal_mode"));
        Assert.Equal(SqliteSchema.BusyTimeoutMilliseconds, Scalar(connection, "PRAGMA busy_timeout"));
        Assert.True(SqliteSchema.BusyTimeoutMilliseconds >= 1000);
        Assert.Equal(1, Scalar(connection, "PRAGMA foreign_keys"));
        Assert.Equal(1, Scalar(connection, "PRAGMA recursive_triggers"));
    }

    [Fact]
    public void An_in_memory_database_still_opens()
    {
        using var connection = SqliteSchema.Open("Data Source=:memory:");

        Assert.Equal("memory", Text(connection, "PRAGMA journal_mode"));
        Assert.Equal(SqliteSchema.BusyTimeoutMilliseconds, Scalar(connection, "PRAGMA busy_timeout"));
    }
}
