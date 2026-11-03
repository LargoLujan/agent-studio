using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The decision log of another project, read only (docs/14 D-05): it writes no row, migrates nothing, takes no lock,
/// moves no anchor (not even one pending migration, ADR-0010 addendum P-04) and follows no link. Its verdict on the log
/// is the chain checked against the anchor of the project's location, with how that anchor stands.
/// </summary>
public sealed class ReadOnlyProjectLogReaderTests : IDisposable
{
    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public ReadOnlyProjectLogReaderTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private ReadOnlyProjectLogReader Reader() => new(new DecisionLogAnchorOptions(_anchors));

    /// <summary>The documented name of a location's anchor: the SHA-256 of the root's final path in upper case (no link leads to these test folders).</summary>
    private string LocationFile => Path.Combine(_anchors, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_folder.Root).ToUpperInvariant()))) + ".json");

    private string[] AnchorFiles() => Directory.Exists(_anchors) ? Directory.GetFiles(_anchors) : [];

    /// <summary>Opens the project as the app does, records <paramref name="count"/> entries and closes it; returns its id and entries.</summary>
    private async Task<(string ProjectId, IReadOnlyList<DecisionEntry> Entries)> SeedAsync(int count, bool anchored = true)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root, anchorFolder: anchored ? _anchors : null);
        var entries = new List<DecisionEntry>();
        for (var i = 1; i <= count; i++)
        {
            entries.Add(await instance.Recorder.RecordAsync(
                instance.ProjectId,
                System,
                DecisionKind.TaskAssigned,
                $"Asignada {i}",
                new TaskAssignedPayload(Tid("T" + i), Aid("ejecutor")) { RunId = "20261015-a1b2c3" },
                cancellationToken: Ct));
        }

        return (instance.ProjectId, entries);
    }

    private SqliteConnection Connect() => new(new SqliteConnectionStringBuilder { DataSource = _folder.DatabasePath, Pooling = false }.ToString());

    private long Count(string sql)
    {
        using var connection = Connect();
        connection.Open();
        return Scalar(connection, sql);
    }

    [Fact]
    public async Task An_anchored_log_is_read_whole_verified_against_its_anchor_and_trusted()
    {
        var (projectId, entries) = await SeedAsync(2);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(projectId, snapshot.ProjectId);
        Assert.Equal(Path.GetFullPath(_folder.Root), snapshot.Root);
        Assert.Equal(entries.Select(e => e.Hash), snapshot.Entries.Select(e => e.Hash));
        Assert.True(snapshot.Verification.IsValid);
        Assert.Equal(AnchorStatus.Anchored, snapshot.Anchor);
        Assert.True(snapshot.IsTrusted);
    }

    [Fact]
    public async Task Reading_writes_no_row_and_leaves_every_anchor_byte_for_byte()
    {
        await SeedAsync(3);
        var anchorBefore = await File.ReadAllBytesAsync(LocationFile, Ct);
        var rowsBefore = Count("SELECT COUNT(*) FROM decision_log;") + Count("SELECT COUNT(*) FROM projects;");

        await Reader().ReadAsync(_folder.Root, Ct);
        await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal([LocationFile], AnchorFiles());
        Assert.Equal(anchorBefore, await File.ReadAllBytesAsync(LocationFile, Ct));
        Assert.Equal(rowsBefore, Count("SELECT COUNT(*) FROM decision_log;") + Count("SELECT COUNT(*) FROM projects;"));
    }

    [Fact]
    public async Task An_anchor_pending_migration_is_read_and_left_where_it_is()
    {
        var (projectId, entries) = await SeedAsync(2);

        // An Agent Studio from before 2026-10-18 anchored the log by the project id (format 1), and nothing moved it yet.
        File.Delete(LocationFile);
        var legacy = Path.Combine(_anchors, projectId + ".json");
        var legacyJson = $$"""{"format":"agent-studio/decision-log-anchor/1","projectId":"{{projectId}}","entryCount":2,"headHash":"{{entries[^1].Hash}}"}""";
        await File.WriteAllTextAsync(legacy, legacyJson, Ct);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(AnchorStatus.Anchored, snapshot.Anchor);
        Assert.True(snapshot.IsTrusted);
        Assert.Equal([legacy], AnchorFiles());
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(legacy, Ct));
    }

    [Fact]
    public async Task A_log_with_entries_and_no_anchor_for_its_location_is_unanchored_and_not_trusted()
    {
        await SeedAsync(2, anchored: false);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(AnchorStatus.Unanchored, snapshot.Anchor);
        Assert.False(snapshot.Verification.IsValid);
        Assert.False(snapshot.IsTrusted);
        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Empty(AnchorFiles());
    }

    [Fact]
    public async Task An_empty_log_without_an_anchor_has_no_history_and_is_trusted()
    {
        var (projectId, _) = await SeedAsync(0);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(projectId, snapshot.ProjectId);
        Assert.Empty(snapshot.Entries);
        Assert.Equal(AnchorStatus.NoHistory, snapshot.Anchor);
        Assert.True(snapshot.IsTrusted);
        Assert.Empty(AnchorFiles());
    }

    [Fact]
    public async Task The_anchor_of_another_project_at_the_location_is_not_trusted()
    {
        var (projectId, _) = await SeedAsync(2);
        var json = await File.ReadAllTextAsync(LocationFile, Ct);
        await File.WriteAllTextAsync(LocationFile, json.Replace(projectId, "otro-proyecto", StringComparison.Ordinal), Ct);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(AnchorStatus.AnotherProject, snapshot.Anchor);
        Assert.False(snapshot.IsTrusted);
        Assert.Contains("otro-proyecto", snapshot.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anchor_that_cannot_be_read_is_not_trusted()
    {
        await SeedAsync(1);
        await File.WriteAllTextAsync(LocationFile, "esto no es un ancla", Ct);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(AnchorStatus.Unreadable, snapshot.Anchor);
        Assert.False(snapshot.IsTrusted);
        Assert.Equal("esto no es un ancla", await File.ReadAllTextAsync(LocationFile, Ct));
    }

    [Fact]
    public async Task A_log_shorter_than_its_anchor_does_not_verify()
    {
        await SeedAsync(2);
        var json = await File.ReadAllTextAsync(LocationFile, Ct);
        await File.WriteAllTextAsync(LocationFile, json.Replace("\"entryCount\": 2", "\"entryCount\": 5", StringComparison.Ordinal), Ct);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(AnchorStatus.Anchored, snapshot.Anchor);
        Assert.False(snapshot.Verification.IsValid);
        Assert.False(snapshot.IsTrusted);
    }

    [Fact]
    public async Task A_log_rewritten_behind_its_triggers_does_not_verify_whatever_its_anchor_says()
    {
        await SeedAsync(2);
        using (var connection = Connect())
        {
            connection.Open();
            Exec(connection, "DROP TRIGGER decision_log_no_update;");
            Exec(connection, "UPDATE decision_log SET summary = 'Reescrita' WHERE sequence = 1;");
        }

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.False(snapshot.Verification.IsValid);
        Assert.Equal(1, snapshot.Verification.FirstInvalidSequence);
        Assert.False(snapshot.IsTrusted);
    }

    [Fact]
    public async Task A_database_left_at_an_older_schema_is_not_migrated()
    {
        await SeedAsync(1);
        using (var connection = Connect())
        {
            connection.Open();
            Exec(connection, "DELETE FROM schema_migrations WHERE version = 4;");
        }

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.True(snapshot.IsTrusted);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM schema_migrations WHERE version = 4;"));
    }

    [Fact]
    public async Task A_project_open_in_another_instance_is_read_without_its_lock()
    {
        var (projectId, _) = await SeedAsync(1);
        using var open = await ProjectInstance.OpenAsync(_folder.Root, anchorFolder: _anchors);

        var snapshot = await Reader().ReadAsync(_folder.Root, Ct);

        Assert.Equal(projectId, snapshot.ProjectId);
        Assert.True(snapshot.IsTrusted);
    }

    [Fact]
    public async Task A_folder_without_a_database_is_not_a_project()
    {
        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => Reader().ReadAsync(_folder.Root, Ct));

        Assert.False(Directory.Exists(_folder.DataDirectory));
    }

    [Fact]
    public async Task A_junction_at_the_data_folder_is_refused_and_nothing_is_read_through_it()
    {
        _links.Junction(_folder.DataDirectory, Outside);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => Reader().ReadAsync(_folder.Root, Ct));

        Assert.Contains(".agentstudio", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(Outside));
    }

    [Fact]
    public async Task The_persistence_of_the_app_reads_other_logs_with_the_anchors_of_its_composition()
    {
        var (projectId, _) = await SeedAsync(1);
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(_anchors));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var snapshot = await provider.GetRequiredService<IProjectLogReader>().ReadAsync(_folder.Root, Ct);

        Assert.Equal((projectId, AnchorStatus.Anchored), (snapshot.ProjectId, snapshot.Anchor));
    }

    [Fact]
    public async Task A_database_that_is_a_second_name_of_another_file_is_refused_and_that_file_is_not_touched()
    {
        await SeedAsync(1);
        var victim = Path.Combine(Outside, "importante.bin");
        await File.WriteAllTextAsync(victim, "no se toca", Ct);
        File.Delete(_folder.DatabasePath);
        _links.HardLink(_folder.DatabasePath, victim);

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => Reader().ReadAsync(_folder.Root, Ct));

        Assert.Equal("no se toca", await File.ReadAllTextAsync(victim, Ct));
    }
}
