using System.IO;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Evidence;
using AgentStudio.Infrastructure.Persistence;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// Links left in a project before Agent Studio opens it, or before it first writes evidence (ADR-0018: in the model). A
/// junction at <c>.agentstudio</c>, <c>evidence</c>, <c>tmp</c> or <c>bitacora</c>, or a database file that is a second
/// name of another file, would send the app's own writes somewhere else: the storage is refused, with nothing written
/// through the link. A link above the project root is the user's business: the root is taken at its final path and works
/// as before.
/// </summary>
public sealed class ProjectStorageLinkTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();

    public ProjectStorageLinkTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EvidenceDraft Draft() => new(EvidenceKind.BuildLog, EvidenceOrigin.CapturedBySystem, "salida del build", "agent-studio", MediaType: "text/plain");

    private static async Task<EvidenceRecord> CaptureAsync(ProjectInstance instance, string text)
    {
        using var content = new MemoryStream(Bytes(text));
        return await instance.Evidence.CaptureAsync(instance.ProjectId, Draft(), content, Ct);
    }

    /// <summary>Opens the project once and closes it, as the app does, so its storage exists for the next opening.</summary>
    private async Task CreateStorageAsync()
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        await CaptureAsync(instance, "primera evidencia");
    }

    private string[] OutsideEntries() => Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories);

    private void Junction(string link, string target) => _links.Junction(link, target);

    private void HardLink(string link, string target) => _links.HardLink(link, target);

    [Fact]
    public async Task A_junction_at_the_data_folder_is_refused_before_anything_is_written_through_it()
    {
        Junction(_folder.DataDirectory, Outside);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => ProjectInstance.OpenAsync(_folder.Root));

        Assert.Contains(".agentstudio", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(OutsideEntries());
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("tmp")]
    [InlineData("bitacora")]
    public async Task A_junction_left_in_existing_storage_is_refused_when_the_project_is_opened_again(string folder)
    {
        await CreateStorageAsync();
        var path = Path.Combine(_folder.DataDirectory, folder);
        TempProjectFolder.ForceDelete(path);
        Junction(path, Outside);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => ProjectInstance.OpenAsync(_folder.Root));

        Assert.Contains(folder, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(OutsideEntries());
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("tmp")]
    public async Task A_junction_left_before_the_first_evidence_is_refused_when_the_store_creates_its_folders(string folder)
    {
        using var instance = await ProjectInstance.OpenAsync(_folder.Root);
        Junction(Path.Combine(_folder.DataDirectory, folder), Outside);

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => CaptureAsync(instance, "contenido"));

        Assert.Empty(OutsideEntries());
        Assert.Empty(await instance.Evidence.ListAsync(instance.ProjectId, Ct));
    }

    [Theory]
    [InlineData("agentstudio.db")]
    [InlineData("agentstudio.db-wal")]
    [InlineData("agentstudio.db-shm")]
    [InlineData("agentstudio.db-journal")]
    public async Task A_database_file_that_is_a_second_name_of_another_file_is_refused_and_that_file_is_not_written(string name)
    {
        await CreateStorageAsync();
        var victim = Path.Combine(Outside, "importante.bin");
        await File.WriteAllTextAsync(victim, "no se toca", Ct);
        var path = Path.Combine(_folder.DataDirectory, name);
        File.Delete(path);
        HardLink(path, victim);

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(() => ProjectInstance.OpenAsync(_folder.Root));

        Assert.Equal("no se toca", await File.ReadAllTextAsync(victim, Ct));
    }

    [Fact]
    public async Task A_project_reached_through_a_junction_above_its_root_opens_and_keeps_its_evidence_under_the_real_folder()
    {
        // C:\dev → D:\dev, say: the folders above the project are the user's to arrange.
        var container = Path.GetDirectoryName(_folder.Root)!;
        var real = Path.Combine(container, "real");
        var project = Path.Combine(real, "proyecto");
        Directory.CreateDirectory(project);
        var link = Path.Combine(container, "enlace");
        Junction(link, real);
        var throughLink = Path.Combine(link, "proyecto");

        string projectId;
        using (var instance = await ProjectInstance.OpenAsync(throughLink))
        {
            projectId = instance.ProjectId;
            await CaptureAsync(instance, "a través del enlace");
        }

        using var reopened = await ProjectInstance.OpenAsync(throughLink);
        var evidence = await CaptureAsync(reopened, "otra vez");

        Assert.Equal(projectId, reopened.ProjectId);
        Assert.True(File.Exists(Path.Combine(project, ".agentstudio", "evidence", evidence.ContentSha256)));
        Assert.Equal(2, (await reopened.Evidence.ListAsync(reopened.ProjectId, Ct)).Count);
    }

    [Fact]
    public async Task Storage_without_links_is_opened_again_and_keeps_taking_evidence()
    {
        await CreateStorageAsync();

        using var reopened = await ProjectInstance.OpenAsync(_folder.Root);
        var evidence = await CaptureAsync(reopened, "segunda evidencia");

        Assert.Equal(2, (await reopened.Evidence.ListAsync(reopened.ProjectId, Ct)).Count);
        Assert.True(File.Exists(Path.Combine(_folder.EvidenceDirectory, evidence.ContentSha256)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder.DataDirectory, "tmp")));
    }
}
