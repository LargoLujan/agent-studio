using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The verification profile of the orchestrator screen, read from <c>&lt;root&gt;\.agentstudio\verification.json</c> with
/// WP3's strict loader (ADR-0016): a missing file is no profile and a proposal, never a profile applied on its own.
/// </summary>
public sealed class FileVerificationProfileSourceTests
{
    private static string ProfilePath(string root) => Path.Combine(root, ".agentstudio", "verification.json");

    [Fact]
    public async Task Without_the_file_the_project_has_no_profile_and_is_offered_the_dotnet_one()
    {
        using var temp = new TempDirectory();
        var project = new ProjectContext("p-1", temp.Path);
        var source = new FileVerificationProfileSource(new ProjectStorageFiles());

        var profile = await source.LoadAsync(project, CancellationToken.None);
        var proposal = source.Propose(project);

        Assert.Null(profile);
        Assert.NotNull(proposal);
        Assert.Equal(DotNetVerificationProfile.Propose().ContentHash, proposal.ContentHash);
        Assert.Equal(["build", "test"], proposal.Steps.Select(s => s.Id));
        Assert.False(File.Exists(ProfilePath(temp.Path)));
    }

    [Fact]
    public async Task A_saved_proposal_is_the_profile_from_then_on()
    {
        using var temp = new TempDirectory();
        var project = new ProjectContext("p-1", temp.Path);
        var source = new FileVerificationProfileSource(new ProjectStorageFiles());
        var proposal = source.Propose(project)!;

        await source.SaveAsync(project, proposal, CancellationToken.None);
        var profile = await source.LoadAsync(project, CancellationToken.None);

        Assert.True(File.Exists(ProfilePath(temp.Path)));
        Assert.NotNull(profile);
        Assert.Equal(proposal.ContentHash, profile.ContentHash);
    }

    [Fact]
    public async Task A_file_that_breaks_the_rules_is_refused_with_its_problems()
    {
        using var temp = new TempDirectory();
        var project = new ProjectContext("p-1", temp.Path);
        temp.WriteFile(".agentstudio/verification.json", "{ \"version\": 1, \"name\": \"roto\", \"steps\": [] , \"extra\": true }");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new FileVerificationProfileSource(new ProjectStorageFiles()).LoadAsync(project, CancellationToken.None));

        Assert.Contains(ProfilePath(temp.Path), error.Message, StringComparison.Ordinal);
        Assert.Contains("extra", error.Message, StringComparison.Ordinal);
    }
}
