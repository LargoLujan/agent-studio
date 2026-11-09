using System.Globalization;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Setup;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Setup;
using AgentStudio.Domain.Security;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop.Lock;

namespace AgentStudio.Tests.Desktop.Setup;

/// <summary>F13-12: the adapters of the ports of the configurator, over temporary folders and a Windows Hello that is scripted.</summary>
public sealed class SetupAdaptersTests : IDisposable
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private readonly string _folder = Directory.CreateTempSubdirectory("as-setup-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private ProjectContext Project(string id = "proyecto-1")
    {
        var root = Path.Combine(_folder, "repo");
        Directory.CreateDirectory(root);
        return new ProjectContext(id, root);
    }

    // ---- the repository ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_repository_is_read_from_the_names_of_its_files_without_git_and_without_the_hidden_folders()
    {
        var project = Project();
        foreach (var file in new[] { "package.json", "src/index.js", "node_modules/pkg/x.js", ".git/config" })
        {
            var path = Path.Combine(project.RootPath, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, string.Empty, Ct);
        }

        var facts = await new FileSetupRepositoryReader().ReadAsync(project, Ct);

        Assert.Equal(["package.json", "src/index.js"], facts.Files.Order(StringComparer.Ordinal));
        Assert.Null(facts.Branch);
        Assert.Empty(facts.Remotes);
        Assert.False(facts.FilesTruncated);
        Assert.Equal("node", SetupProposer.ProposeProject(facts).Stack);
    }

    // ---- the profile by stack ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("dotnet", new[] { "App.slnx", "package.json", "pyproject.toml" })]
    [InlineData("node", new[] { "package.json", "pyproject.toml" })]
    [InlineData("python", new[] { "pyproject.toml" })]
    [InlineData("python", new[] { "requirements.txt", "app/main.py" })]
    public void The_profile_proposed_follows_the_stack_by_the_files_with_dotnet_first(string expected, string[] files)
    {
        var profile = new StackSetupVerificationProfiles(new ProjectStorageFiles(), new ExecutableLocator(() => string.Empty)).Propose(files);

        Assert.Equal(expected, profile!.Name);
    }

    [Fact]
    public void Without_a_stack_or_without_its_file_in_the_root_nothing_is_proposed()
    {
        var profiles = new StackSetupVerificationProfiles(new ProjectStorageFiles(), new ExecutableLocator(() => string.Empty));

        Assert.Null(profiles.Propose(["README.md"]));
        Assert.Null(profiles.Propose(["web/package.json"]));
    }

    [Fact]
    public async Task The_profile_is_saved_and_read_by_the_loader_of_the_orchestrator_and_a_broken_file_is_an_error_not_a_default()
    {
        var project = Project();
        var profiles = new StackSetupVerificationProfiles(new ProjectStorageFiles(), new ExecutableLocator(() => string.Empty));
        Assert.Null(await profiles.LoadAsync(project, Ct));

        var proposed = profiles.Propose(["package.json", "package-lock.json"])!;
        await profiles.SaveAsync(project, proposed, Ct);

        Assert.Equal(proposed.ContentHash, (await profiles.LoadAsync(project, Ct))!.ContentHash);

        var path = Path.Combine(project.RootPath, ".agentstudio", "verification.json");
        await File.WriteAllTextAsync(path, "{ not json", Ct);

        await Assert.ThrowsAsync<InvalidDataException>(() => profiles.LoadAsync(project, Ct));
    }

    // ---- the options of the project ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_options_are_kept_per_project_outside_the_repository_and_read_back()
    {
        var store = new LocalProjectOptionsStore(Path.Combine(_folder, "setup"));
        var project = Project();
        Assert.Null(await store.LoadAsync(project, Ct));

        var options = new ProjectRunOptions(20, 2, TimeSpan.FromMinutes(30));
        await store.SaveAsync(project, options, Ct);
        await store.SaveAsync(Project("proyecto-2"), new ProjectRunOptions(5, 1, null), Ct);

        Assert.Equal(options, await store.LoadAsync(project, Ct));
        Assert.Equal(new ProjectRunOptions(5, 1, null), await store.LoadAsync(Project("proyecto-2"), Ct));
        Assert.False(Directory.Exists(Path.Combine(project.RootPath, ".agentstudio")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "setup"), "*.tmp"));
    }

    [Fact]
    public async Task A_file_of_options_that_cannot_be_read_is_no_options_and_an_id_that_is_not_a_file_name_is_refused()
    {
        var folder = Path.Combine(_folder, "setup");
        var store = new LocalProjectOptionsStore(folder);
        var project = Project();
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(Path.Combine(folder, "proyecto-1.json"), "{ not json", Ct);
        Assert.Null(await store.LoadAsync(project, Ct));

        await File.WriteAllTextAsync(Path.Combine(folder, "proyecto-1.json"), "{\"version\":1,\"maxAgentRuns\":0,\"maxParallelTasks\":3}", Ct);
        Assert.Null(await store.LoadAsync(project, Ct));

        Assert.Null(await store.LoadAsync(new ProjectContext(@"..x", project.RootPath), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ProjectContext(" ", project.RootPath), ProjectRunOptions.Defaults, Ct));
    }

    [Theory]
    [InlineData("0a1b2c3d-0000-4000-8000-000000000000", true)]
    [InlineData("proyecto_1", true)]
    [InlineData("a", true)]
    [InlineData("CON", false)]
    [InlineData("nul", false)]
    [InlineData("Com1", false)]
    [InlineData("LPT9", false)]
    [InlineData("COM10", true)]
    [InlineData("a.b", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData("ñ", false)]
    public void Only_letters_digits_hyphens_and_underscores_name_a_file_and_the_names_windows_keeps_are_refused(string id, bool safe)
    {
        Assert.Equal(safe, LocalProjectOptionsStore.IsSafeId(id));
    }

    [Fact]
    public async Task An_unsafe_id_has_no_options_when_asked_and_is_refused_when_saved_and_nothing_is_written()
    {
        var folder = Path.Combine(_folder, "setup");
        var store = new LocalProjectOptionsStore(folder);
        var project = Project();

        foreach (var id in new[] { "NUL", "a.b", @"..\x" })
        {
            Assert.Null(await store.LoadAsync(new ProjectContext(id, project.RootPath), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ProjectContext(id, project.RootPath), ProjectRunOptions.Defaults, Ct));
        }

        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any());
    }

    [Fact]
    public async Task When_the_move_fails_the_temporary_file_is_deleted_and_the_error_is_the_one_that_reaches_the_caller()
    {
        var folder = Path.Combine(_folder, "setup");
        var store = new LocalProjectOptionsStore(folder);
        var project = Project();

        // A folder where the file of the options goes: the Move cannot replace it.
        Directory.CreateDirectory(Path.Combine(folder, project.ProjectId + ".json"));

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(project, ProjectRunOptions.Defaults, Ct));

        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    // ---- the barrier ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_profile_and_the_capabilities_ask_windows_hello_whatever_the_setting_says()
    {
        var verifier = FakeUserPresenceVerifier.Answering(UserPresenceResult.Verified);
        var gate = new HelloSetupPresenceGate(new ReinforcedConfirmation(verifier, () => false), Spanish);

        var profile = await gate.ConfirmAsync(PresenceAction.ChangeVerificationProfile, UserText.Of("setup.hello.subject-profile", "Node"), Ct);
        var capability = await gate.ConfirmAsync(PresenceAction.GrantHighRiskCapability, UserText.Of("setup.hello.subject-team", 4), Ct);

        Assert.True(profile.Confirmed);
        Assert.True(capability.Confirmed);
        Assert.Equal(2, verifier.Messages.Count);
        Assert.Equal(Spanish.Format("Setup_Hello_Message", "el perfil de verificación de Node"), verifier.Messages[0]);
        Assert.Equal(Spanish.Format("Setup_Hello_Message", "el equipo con 4 agentes"), verifier.Messages[1]);
    }

    [Theory]
    [InlineData(UserPresenceResult.Canceled)]
    [InlineData(UserPresenceResult.RetriesExhausted)]
    [InlineData(UserPresenceResult.DeviceBusy)]
    public async Task Only_a_verified_person_confirms_and_any_other_answer_is_a_no_with_its_code(UserPresenceResult answer)
    {
        var gate = new HelloSetupPresenceGate(new ReinforcedConfirmation(FakeUserPresenceVerifier.Answering(answer), () => false), Spanish);

        var result = await gate.ConfirmAsync(PresenceAction.ChangeVerificationProfile, UserText.Of("setup.hello.subject-profile", "Node"), Ct);

        Assert.False(result.Confirmed);
        Assert.Equal("setup.hello.not-confirmed", result.Problem!.Code);
        Assert.True(Spanish.Has(result.Problem.ResourceKey));
    }

    [Fact]
    public async Task Without_windows_hello_and_without_the_lock_of_windows_composed_nothing_confirms()
    {
        var verifier = FakeUserPresenceVerifier.Without(UserPresenceAvailability.NotConfiguredForUser);
        var gate = new HelloSetupPresenceGate(new ReinforcedConfirmation(verifier, () => false), Spanish);

        var result = await gate.ConfirmAsync(PresenceAction.ChangeVerificationProfile, UserText.Of("setup.hello.subject-profile", "Node"), Ct);

        Assert.False(result.Confirmed);
        Assert.Equal("setup.hello.not-asked", result.Problem!.Code);
        Assert.Empty(verifier.Messages);
    }

    [Fact]
    public async Task A_second_confirmation_while_another_waits_is_refused_not_queued()
    {
        var release = new TaskCompletionSource<UserPresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = new FakeUserPresenceVerifier { Verify = (_, _) => release.Task };
        var gate = new HelloSetupPresenceGate(new ReinforcedConfirmation(verifier, () => false), Spanish);

        var first = gate.ConfirmAsync(PresenceAction.ChangeVerificationProfile, UserText.Of("setup.hello.subject-profile", "Node"), Ct);
        while (verifier.Messages.Count == 0)
        {
            await Task.Delay(10, Ct);
        }

        var second = await gate.ConfirmAsync(PresenceAction.GrantHighRiskCapability, UserText.Of("setup.hello.subject-team", 4), Ct);

        Assert.False(second.Confirmed);
        Assert.Equal("setup.hello.another-pending", second.Problem!.Code);
        release.SetResult(UserPresenceResult.Verified);
        Assert.True((await first).Confirmed);
    }
}
