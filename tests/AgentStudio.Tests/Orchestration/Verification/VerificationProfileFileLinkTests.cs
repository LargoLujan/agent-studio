using System.IO;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Orchestration.Verification;

/// <summary>
/// Links left in a project before its verification profile is saved (ADR-0018: in the model). A junction at
/// <c>.agentstudio</c>, or a <c>verification.json</c> that is a second name of another file: the save is refused and
/// nothing is written through the link.
/// </summary>
public sealed class VerificationProfileFileLinkTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();

    public VerificationProfileFileLinkTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    private string ProfileFile => Path.Combine(_folder.DataDirectory, "verification.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    private Task SaveAsync() => VerificationProfileFile.SaveAsync(_folder.Root, DotNetVerificationProfile.Propose(), new ProjectStorageFiles(), Ct);

    private string[] OutsideEntries() => Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories);

    [Fact]
    public async Task A_junction_at_the_data_folder_is_refused_and_no_profile_is_saved_outside()
    {
        _links.Junction(_folder.DataDirectory, Outside);

        var refusal = await Assert.ThrowsAsync<ProjectStorageIntegrityException>(SaveAsync);

        Assert.Contains(".agentstudio", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(OutsideEntries());
    }

    [Fact]
    public async Task A_profile_file_that_is_a_second_name_of_another_file_is_refused_and_that_file_is_not_written()
    {
        var victim = Path.Combine(Outside, "importante.json");
        await File.WriteAllTextAsync(victim, "no se toca", Ct);
        Directory.CreateDirectory(_folder.DataDirectory);
        _links.HardLink(ProfileFile, victim);

        await Assert.ThrowsAsync<ProjectStorageIntegrityException>(SaveAsync);

        Assert.Equal("no se toca", await File.ReadAllTextAsync(victim, Ct));
        Assert.Equal("no se toca", await File.ReadAllTextAsync(ProfileFile, Ct));
    }

    [Fact]
    public async Task A_project_without_links_saves_its_profile_in_its_own_folder()
    {
        await SaveAsync();

        var load = await VerificationProfileFile.LoadAsync(_folder.Root, Ct);
        Assert.Equal(VerificationProfileStatus.Loaded, load.Status);
        Assert.Equal(DotNetVerificationProfile.Propose().ContentHash, load.Profile!.ContentHash);
        Assert.Empty(OutsideEntries());
    }
}
