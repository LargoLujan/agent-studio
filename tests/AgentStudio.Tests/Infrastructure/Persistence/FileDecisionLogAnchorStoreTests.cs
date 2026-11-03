using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// The head of each project's decision log, kept in a file outside the project and named by where the project is: the
/// final path of its root, never the id its own database gives (ADR-0010, addenda of 2026-10-16 and 2026-10-18).
/// </summary>
public sealed class FileDecisionLogAnchorStoreTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<string> _links = [];

    public void Dispose()
    {
        foreach (var link in _links.Where(Directory.Exists))
        {
            Directory.Delete(link);
        }

        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Container => Path.GetDirectoryName(_folder.Root)!;

    private Task<ProjectInstance> OpenAsync(string? root = null) => ProjectInstance.OpenAsync(root ?? _folder.Root);

    private FileDecisionLogAnchorStore Store(ProjectInstance instance) => new(new DecisionLogAnchorOptions(_anchors), instance.Databases);

    /// <summary>The documented name of a location's anchor: the SHA-256 of the root's final path in upper case (no link leads to these test folders).</summary>
    private string LocationFile(string root) =>
        Path.Combine(_anchors, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))) + ".json");

    private string LegacyFile(string projectId) => Path.Combine(_anchors, projectId + ".json");

    /// <summary>The anchor file of an Agent Studio from before 2026-10-18: named by the project id, format 1.</summary>
    private async Task WriteLegacyAsync(string projectId, string json)
    {
        Directory.CreateDirectory(_anchors);
        await File.WriteAllTextAsync(LegacyFile(projectId), json, Ct);
    }

    private static string LegacyJson(string projectId, long entryCount, string head, string? next = null) =>
        $$"""{"format":"agent-studio/decision-log-anchor/1","projectId":"{{projectId}}","entryCount":{{entryCount}},"headHash":"{{head}}"{{(next is null ? string.Empty : $",\"nextHash\":\"{next}\"")}}}""";

    /// <summary>A file content of the data of a theory, with the project id and two hashes put in.</summary>
    private static string Filled(string content, string projectId) => content
        .Replace("{project}", projectId, StringComparison.Ordinal)
        .Replace("{hash}", HashA, StringComparison.Ordinal)
        .Replace("{other}", HashB, StringComparison.Ordinal);

    private void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {error}");
        _links.Add(link);
    }

    [Fact]
    public async Task A_project_without_an_anchor_has_none()
    {
        using var project = await OpenAsync();

        Assert.Null(await Store(project).ReadAsync(project.ProjectId, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_anchor_is_read_back_as_it_was_written(bool announcing)
    {
        using var project = await OpenAsync();
        var anchor = new DecisionLogAnchor(3, HashA, announcing ? HashB : null);

        await Store(project).WriteAsync(project.ProjectId, anchor, Ct);

        Assert.Equal(anchor, await Store(project).ReadAsync(project.ProjectId, Ct));
    }

    [Fact]
    public async Task Writing_again_replaces_the_anchor_and_leaves_no_temporary_file_behind()
    {
        using var project = await OpenAsync();
        var store = Store(project);

        await store.WriteAsync(project.ProjectId, new DecisionLogAnchor(1, HashA, HashB), Ct);
        await store.WriteAsync(project.ProjectId, new DecisionLogAnchor(2, HashB), Ct);

        Assert.Equal(new DecisionLogAnchor(2, HashB), await store.ReadAsync(project.ProjectId, Ct));
        Assert.Equal([LocationFile(_folder.Root)], Directory.GetFiles(_anchors));
    }

    [Fact]
    public async Task The_file_is_named_by_the_location_and_records_its_format_project_location_and_head()
    {
        using var project = await OpenAsync();

        await Store(project).WriteAsync(project.ProjectId, new DecisionLogAnchor(4, HashA, HashB), Ct);

        Assert.False(File.Exists(LegacyFile(project.ProjectId)));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(LocationFile(_folder.Root), Ct));
        var root = json.RootElement;
        Assert.Equal("agent-studio/decision-log-anchor/2", root.GetProperty("format").GetString());
        Assert.Equal(project.ProjectId, root.GetProperty("projectId").GetString());
        Assert.Equal(Path.GetFullPath(_folder.Root), root.GetProperty("location").GetString(), ignoreCase: true);
        Assert.Equal(4, root.GetProperty("entryCount").GetInt64());
        Assert.Equal(HashA, root.GetProperty("headHash").GetString());
        Assert.Equal(HashB, root.GetProperty("nextHash").GetString());
    }

    [Fact]
    public async Task A_project_opened_through_a_junction_finds_the_anchor_of_its_real_folder()
    {
        using (var project = await OpenAsync())
        {
            await Store(project).WriteAsync(project.ProjectId, new DecisionLogAnchor(2, HashA), Ct);
        }

        var link = Path.Combine(Container, "enlace");
        Junction(link, _folder.Root);
        using var throughLink = await OpenAsync(link);

        Assert.Equal(new DecisionLogAnchor(2, HashA), await Store(throughLink).ReadAsync(throughLink.ProjectId, Ct));
    }

    [Fact]
    public async Task The_anchor_stays_with_the_location_so_a_project_moved_elsewhere_has_none()
    {
        using (var project = await OpenAsync())
        {
            await Store(project).WriteAsync(project.ProjectId, new DecisionLogAnchor(2, HashA), Ct);
        }

        var moved = Path.Combine(Container, "movido");
        Directory.Move(_folder.Root, moved);
        using var there = await OpenAsync(moved);

        Assert.Null(await Store(there).ReadAsync(there.ProjectId, Ct));
        Assert.True(File.Exists(LocationFile(_folder.Root)));
    }

    [Fact]
    public async Task The_anchor_of_another_project_at_this_location_is_a_mismatch_that_names_that_project()
    {
        string before;
        using (var project = await OpenAsync())
        {
            before = project.ProjectId;
            await Store(project).WriteAsync(project.ProjectId, new DecisionLogAnchor(3, HashA), Ct);
        }

        Directory.Delete(_folder.DataDirectory, recursive: true);
        using var now = await OpenAsync();

        var mismatch = await Assert.ThrowsAsync<DecisionLogAnchorMismatchException>(() => Store(now).ReadAsync(now.ProjectId, Ct));

        Assert.NotEqual(before, now.ProjectId);
        Assert.Equal(before, mismatch.AnchoredProjectId);
        Assert.Equal(3, mismatch.AnchoredEntryCount);
        Assert.Contains(before, mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(now.ProjectId, mismatch.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es JSON")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"otra-cosa/1\",\"projectId\":\"{project}\",\"location\":\"{location}\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/1\",\"projectId\":\"{project}\",\"location\":\"{location}\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"projectId\":\"{project}\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"projectId\":\"{project}\",\"location\":\"C:\\\\otra\\\\carpeta\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"location\":\"{location}\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"projectId\":\"{project}\",\"location\":\"{location}\",\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"projectId\":\"{project}\",\"location\":\"{location}\",\"entryCount\":\"uno\",\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"projectId\":\"{project}\",\"location\":\"{location}\",\"entryCount\":1}")]
    public async Task An_anchor_file_that_is_not_the_anchor_of_this_location_is_refused_as_invalid_data(string content)
    {
        using var project = await OpenAsync();
        Directory.CreateDirectory(_anchors);
        var location = JsonEncodedText.Encode(Path.GetFullPath(_folder.Root)).ToString();
        await File.WriteAllTextAsync(
            LocationFile(_folder.Root),
            content.Replace("{project}", project.ProjectId, StringComparison.Ordinal).Replace("{location}", location, StringComparison.Ordinal).Replace("{hash}", HashA, StringComparison.Ordinal),
            Ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => Store(project).ReadAsync(project.ProjectId, Ct));
    }

    // ---- anchors written before 2026-10-18, named by the project id ----

    [Fact]
    public async Task Reading_a_legacy_anchor_named_by_the_project_id_leaves_it_where_it_is_and_writes_nothing()
    {
        // Read before the log is verified against it: a copy whose log does not verify must not take it (P-04).
        using var project = await OpenAsync();
        await WriteLegacyAsync(project.ProjectId, LegacyJson(project.ProjectId, 5, HashA, HashB));
        var store = Store(project);

        var read = await store.ReadAsync(project.ProjectId, Ct);

        Assert.Equal(new DecisionLogAnchor(5, HashA, HashB), read);
        Assert.Equal([LegacyFile(project.ProjectId)], Directory.GetFiles(_anchors));
        Assert.Equal(read, await store.ReadAsync(project.ProjectId, Ct));
    }

    [Fact]
    public async Task A_legacy_anchor_moves_to_the_location_once_the_log_verified_against_it_is_confirmed()
    {
        using var project = await OpenAsync();
        await WriteLegacyAsync(project.ProjectId, LegacyJson(project.ProjectId, 5, HashA, HashB));
        var store = Store(project);
        var read = await store.ReadAsync(project.ProjectId, Ct);

        await store.ConfirmAsync(project.ProjectId, read!, Ct);

        Assert.Equal([LocationFile(_folder.Root)], Directory.GetFiles(_anchors));
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(LocationFile(_folder.Root), Ct)))
        {
            Assert.Equal("agent-studio/decision-log-anchor/2", json.RootElement.GetProperty("format").GetString());
            Assert.Equal(project.ProjectId, json.RootElement.GetProperty("projectId").GetString());
        }

        Assert.Equal(read, await store.ReadAsync(project.ProjectId, Ct));
    }

    [Fact]
    public async Task A_move_cut_short_after_the_location_anchor_was_written_is_finished_by_the_next_confirmation()
    {
        // The process died after writing the location's anchor and before deleting the file named by the id.
        using var project = await OpenAsync();
        var store = Store(project);
        await store.WriteAsync(project.ProjectId, new DecisionLogAnchor(5, HashA, HashB), Ct);
        await WriteLegacyAsync(project.ProjectId, LegacyJson(project.ProjectId, 5, HashA, HashB));
        var read = await store.ReadAsync(project.ProjectId, Ct);

        await store.ConfirmAsync(project.ProjectId, read!, Ct);

        Assert.Equal([LocationFile(_folder.Root)], Directory.GetFiles(_anchors));
        Assert.Equal(new DecisionLogAnchor(5, HashA, HashB), await store.ReadAsync(project.ProjectId, Ct));
    }

    [Theory]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/1\",\"projectId\":\"{project}\",\"entryCount\":5,\"headHash\":\"{other}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/1\",\"projectId\":\"otro-proyecto\",\"entryCount\":5,\"headHash\":\"{hash}\"}")]
    [InlineData("no es JSON")]
    public async Task A_legacy_file_that_is_not_the_anchor_the_log_verified_against_does_not_move(string content)
    {
        // It changed after it was read: only the anchor the log verified against may take the location's name.
        using var project = await OpenAsync();
        await WriteLegacyAsync(project.ProjectId, Filled(content, project.ProjectId));
        var store = Store(project);

        await store.ConfirmAsync(project.ProjectId, new DecisionLogAnchor(5, HashA), Ct);

        Assert.Equal([LegacyFile(project.ProjectId)], Directory.GetFiles(_anchors));
    }

    [Theory]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/1\",\"projectId\":\"{project}\",\"entryCount\":5,\"headHash\":\"{other}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/1\",\"projectId\":\"otro-proyecto\",\"entryCount\":5,\"headHash\":\"{hash}\"}")]
    [InlineData("no es JSON")]
    public async Task A_legacy_file_that_is_not_the_anchor_of_the_location_is_left_where_it_is_once_the_location_has_one(string content)
    {
        using var project = await OpenAsync();
        var store = Store(project);
        await store.WriteAsync(project.ProjectId, new DecisionLogAnchor(5, HashA), Ct);
        await WriteLegacyAsync(project.ProjectId, Filled(content, project.ProjectId));

        await store.ConfirmAsync(project.ProjectId, new DecisionLogAnchor(5, HashA), Ct);

        Assert.True(File.Exists(LegacyFile(project.ProjectId)));
        Assert.Equal(new DecisionLogAnchor(5, HashA), await store.ReadAsync(project.ProjectId, Ct));
    }

    [Fact]
    public async Task Confirming_the_anchor_of_a_location_with_no_legacy_anchor_rewrites_nothing()
    {
        using var project = await OpenAsync();
        var store = Store(project);
        await store.WriteAsync(project.ProjectId, new DecisionLogAnchor(2, HashA), Ct);
        var writtenAt = File.GetLastWriteTimeUtc(LocationFile(_folder.Root));

        await store.ConfirmAsync(project.ProjectId, new DecisionLogAnchor(2, HashA), Ct);

        Assert.Equal([LocationFile(_folder.Root)], Directory.GetFiles(_anchors));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(LocationFile(_folder.Root)));
    }

    [Fact]
    public async Task A_legacy_anchor_is_not_consulted_once_the_location_has_one()
    {
        using var project = await OpenAsync();
        var store = Store(project);
        await store.WriteAsync(project.ProjectId, new DecisionLogAnchor(2, HashA), Ct);
        await WriteLegacyAsync(project.ProjectId, LegacyJson(project.ProjectId, 5, HashB));

        Assert.Equal(new DecisionLogAnchor(2, HashA), await store.ReadAsync(project.ProjectId, Ct));
        Assert.True(File.Exists(LegacyFile(project.ProjectId)));
    }

    [Theory]
    [InlineData("no es JSON")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/1\",\"projectId\":\"otro-proyecto\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    [InlineData("{\"format\":\"agent-studio/decision-log-anchor/2\",\"projectId\":\"{project}\",\"entryCount\":1,\"headHash\":\"{hash}\"}")]
    public async Task A_legacy_anchor_that_is_not_the_one_of_the_project_is_refused_and_left_where_it_is(string content)
    {
        using var project = await OpenAsync();
        await WriteLegacyAsync(project.ProjectId, content.Replace("{project}", project.ProjectId, StringComparison.Ordinal).Replace("{hash}", HashA, StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(() => Store(project).ReadAsync(project.ProjectId, Ct));

        Assert.True(File.Exists(LegacyFile(project.ProjectId)));
        Assert.False(File.Exists(LocationFile(_folder.Root)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../fuera")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("con espacios")]
    [InlineData("punto.json")]
    public async Task A_project_id_that_is_not_a_plain_file_name_is_refused(string projectId)
    {
        using var databases = new ProjectDatabases();
        var store = new FileDecisionLogAnchorStore(new DecisionLogAnchorOptions(_anchors), databases);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(projectId, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(projectId, new DecisionLogAnchor(1, HashA), Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ConfirmAsync(projectId, new DecisionLogAnchor(1, HashA), Ct));
        Assert.False(Directory.Exists(_anchors) && Directory.EnumerateFileSystemEntries(_anchors).Any());
    }

    [Fact]
    public void By_default_the_anchors_live_under_the_local_application_data_of_agent_studio()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio", "anchors"),
            DecisionLogAnchorOptions.ForCurrentUser().Directory);
    }
}
