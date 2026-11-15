using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Security;
using AgentStudio.Infrastructure.Security;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Infrastructure.Skills.GitHub.GitHubTestData;

// Keep this namespace separate from AgentStudio.Tests.Security, as the existing DPAPI tests do.
namespace AgentStudio.Tests.Infrastructure.Skills.GitHub;

public sealed class ProjectCredentialVaultTests : IDisposable
{
    private readonly string _folder = NewFolder();

    private string Root => Path.Combine(_folder, "vault");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => DeleteFolder(_folder);

    [Fact]
    public async Task The_same_secret_name_is_separate_for_two_projects_and_survives_reopening()
    {
        var vaults = new ProjectCredentialVaults(Root);
        var a = vaults.ForProject("project-a");
        var b = vaults.ForProject("project-b");
        Assert.Same(a, vaults.ForProject("project-a"));
        Assert.Equal("project-a", a.ProjectId);
        Assert.Null(await b.ReadAsync("key", Ct));
        Assert.False(Directory.Exists(Root));

        await a.StoreAsync("key", "fictional-project-a-secret", Ct);
        await b.StoreAsync("key", "fictional-project-b-secret", Ct);

        var reopened = new ProjectCredentialVaults(Root);
        Assert.Equal("fictional-project-a-secret", await reopened.ForProject("project-a").ReadAsync("key", Ct));
        Assert.Equal("fictional-project-b-secret", await reopened.ForProject("project-b").ReadAsync("key", Ct));
        Assert.Equal(2, Directory.GetDirectories(Root).Length);
        await a.DeleteAsync("key", Ct);
        Assert.False(await a.ContainsAsync("key", Ct));
        Assert.True(await b.ContainsAsync("key", Ct));
    }

    [Fact]
    public async Task Copying_ciphertext_between_projects_does_not_make_the_secret_usable()
    {
        var vaults = new ProjectCredentialVaults(Root);
        var a = vaults.ForProject("project-a");
        var b = vaults.ForProject("project-b");
        await a.StoreAsync("key", "fictional-project-a-secret", Ct);
        var aFile = Assert.Single(Directory.GetFiles(Root, "*.dpapi", SearchOption.AllDirectories));
        await b.StoreAsync("key", "fictional-project-b-secret", Ct);
        var bFile = Assert.Single(Directory.GetFiles(Root, "*.dpapi", SearchOption.AllDirectories), file => file != aFile);
        File.Copy(aFile, bFile, overwrite: true);

        Assert.True(await b.ContainsAsync("key", Ct)); // Probing does not decrypt, even a foreign ciphertext.
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => b.ReadAsync("key", Ct));
        Assert.DoesNotContain("fictional-project-a-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal("fictional-project-a-secret", await a.ReadAsync("key", Ct));
    }

    [Fact]
    public async Task Disk_bytes_are_current_user_dpapi_with_project_entropy_and_never_plaintext()
    {
        const string projectId = "project-a";
        const string sentinel = "fictional-vault-sentinel-7c5d2e10";
        await new ProjectCredentialVaults(Root).ForProject(projectId).StoreAsync("key", sentinel, Ct);
        var file = Assert.Single(Directory.GetFiles(Root, "*", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(file, Ct);
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("AgentStudio.ProjectVault.v1\0" + projectId));
        var plain = ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser);
        try
        {
            Assert.Equal(sentinel, Encoding.UTF8.GetString(plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        Assert.DoesNotContain(sentinel, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes("AgentStudio.Vault.v1"), DataProtectionScope.CurrentUser));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Opaque_paths_keep_project_ids_and_windows_device_secret_names_out_of_the_filesystem()
    {
        var vaults = new ProjectCredentialVaults(Root);
        var vault = vaults.ForProject("../project:outside\\nested");
        await vault.StoreAsync("con", "fictional-device-name-secret", Ct);
        Assert.Equal("fictional-device-name-secret", await vault.ReadAsync("con", Ct));

        var projectFolder = Assert.Single(Directory.GetDirectories(Root));
        Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(projectFolder));
        Assert.Matches("^[0-9a-f]{64}\\.dpapi$", Path.GetFileName(Assert.Single(Directory.GetFiles(projectFolder))));
        Assert.Single(Directory.GetDirectories(_folder));
        Assert.NotSame(vaults.ForProject("Project-A"), vaults.ForProject("project-a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("project\0a")]
    public void Invalid_project_ids_do_not_create_storage(string projectId)
    {
        Assert.Throws<ArgumentException>(() => new ProjectCredentialVaults(Root).ForProject(projectId));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Malformed_or_oversized_project_ids_cannot_alias_another_vault()
    {
        var vaults = new ProjectCredentialVaults(Root);
        Assert.Throws<ArgumentException>(() => vaults.ForProject(new string('\ud800', 1)));
        Assert.Throws<ArgumentException>(() => vaults.ForProject(new string('a', 513)));
        Assert.False(Directory.Exists(_folder));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("name:stream")]
    [InlineData("")]
    public async Task Invalid_secret_names_are_rejected_before_any_storage_is_created(string name)
    {
        var vault = new ProjectCredentialVaults(Root).ForProject("project-a");
        await Assert.ThrowsAsync<ArgumentException>(() => vault.StoreAsync(name, "fictional-secret", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.ReadAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.ContainsAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.DeleteAsync(name, Ct));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task Oversized_values_and_ciphertexts_fail_without_replacing_the_previous_secret()
    {
        var vault = new ProjectCredentialVaults(Root).ForProject("project-a");
        await vault.StoreAsync("key", "fictional-kept-secret", Ct);
        var file = Assert.Single(Directory.GetFiles(Root, "*.dpapi", SearchOption.AllDirectories));
        var before = await File.ReadAllBytesAsync(file, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => vault.StoreAsync("key", new string('x', DpapiCredentialVault.MaxFileBytes + 1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.StoreAsync("key", new string('x', DpapiCredentialVault.MaxFileBytes), Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(file, Ct));
        Assert.Equal("fictional-kept-secret", await vault.ReadAsync("key", Ct));
        await File.WriteAllBytesAsync(file, new byte[DpapiCredentialVault.MaxFileBytes + 1], Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync("key", Ct));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancellation_leaves_no_partial_file_or_changed_secret()
    {
        var vault = new ProjectCredentialVaults(Root).ForProject("project-a");
        await vault.StoreAsync("key", "fictional-kept-secret", Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.StoreAsync("key", "fictional-replacement", cancelled.Token));
        Assert.Equal("fictional-kept-secret", await vault.ReadAsync("key", Ct));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("project")]
    [InlineData("file")]
    public async Task A_planted_junction_in_the_store_is_refused(string replace)
    {
        var vault = new ProjectCredentialVaults(Root).ForProject("project-a");
        await vault.StoreAsync("key", "fictional-kept-secret", Ct);
        var file = Assert.Single(Directory.GetFiles(Root, "*.dpapi", SearchOption.AllDirectories));
        var entry = replace switch
        {
            "root" => Root,
            "project" => Path.GetDirectoryName(file)!,
            _ => file,
        };
        var outside = Path.Combine(_folder, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, Path.GetFileName(file));
        await File.WriteAllTextAsync(outsideFile, "untouched-outside-sentinel", Ct);
        if (replace == "file")
        {
            File.Delete(entry);
        }
        else
        {
            Directory.Delete(entry, recursive: true);
        }

        using var links = new PlantedLinks();
        links.Junction(entry, outside);

        await Assert.ThrowsAsync<InvalidDataException>(() => vault.StoreAsync("key", "fictional-replacement", Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync("key", Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.ContainsAsync("key", Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.DeleteAsync("key", Ct));
        Assert.Equal("untouched-outside-sentinel", await File.ReadAllTextAsync(outsideFile, Ct));
        Assert.Single(Directory.GetFiles(outside));
    }

    [Fact]
    public async Task Registration_preserves_the_global_doctor_vault_and_project_reads_never_fall_back_to_it()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioSkillsDoctor(Path.Combine(_folder, "cache"), Path.Combine(_folder, "global"));
        services.AddAgentStudioProjectCredentialVaults(Root);
        using var provider = services.BuildServiceProvider();
        var projects = Assert.IsType<ProjectCredentialVaults>(provider.GetRequiredService<IProjectCredentialVaults>());
        var global = Assert.IsType<DpapiCredentialVault>(provider.GetRequiredService<ICredentialVault>());
        Assert.Same(provider.GetRequiredService<ICredentialVault>(), provider.GetRequiredService<ICredentialVaultProbe>());
        Assert.False(Directory.Exists(_folder));

        const string name = "github-skills-doctor-token";
        await global.StoreAsync(name, "fictional-global-doctor-token", Ct);
        Assert.Null(await projects.ForProject("project-a").ReadAsync(name, Ct));
        Assert.False(await projects.ForProject("project-a").ContainsAsync(name, Ct));
        Assert.False(Directory.Exists(Root));
        Assert.Equal("fictional-global-doctor-token", await global.ReadAsync(name, Ct));
    }
}
