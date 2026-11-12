using System.IO;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>ListRemotesAsync of the hardened git layer (P51) over real temporary repositories, with no network.</summary>
[Trait("Category", "LocalTools")]
public sealed class RepositoryRemoteReaderTests : IDisposable
{
    private const string Secret = "ghp_S3cretT0ken";

    private readonly TempGitRepo _repo = TempGitRepo.Create();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepositoryRemoteReader NewReader() =>
        new(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task It_lists_the_remotes_in_order_with_their_address_clean_of_credentials()
    {
        _repo.Run("remote", "add", "origin", "https://usuario:" + Secret + "@github.com/org/calc.git");
        _repo.Run("remote", "add", "upstream", "git@github.com:otro/calc.git");
        _repo.Run("remote", "add", "mirror", "https://tokenSolo@gitlab.com/org/calc.git");

        var listing = await NewReader().ListRemotesAsync(_repo.Root, Ct);

        Assert.Null(listing.RefusalCode);
        Assert.Equal(["origin", "upstream", "mirror"], listing.Remotes.Select(r => r.Name));
        Assert.Equal("https://github.com/org/calc.git", listing.Remotes[0].Url);
        Assert.DoesNotContain(listing.Remotes, r => r.Url.Contains(Secret, StringComparison.Ordinal) || r.Url.Contains("tokenSolo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_without_remotes_is_an_empty_list_not_a_refusal()
    {
        var listing = await NewReader().ListRemotesAsync(_repo.Root, Ct);

        Assert.Empty(listing.Remotes);
        Assert.Null(listing.RefusalCode);
    }

    [Fact]
    public async Task A_name_with_dots_and_only_the_first_address_of_a_remote_are_read()
    {
        _repo.Run("config", "--local", "remote.mi.espejo.url", "https://host.example/uno.git");
        _repo.Run("config", "--local", "--add", "remote.mi.espejo.url", "https://host.example/dos.git");

        var listing = await NewReader().ListRemotesAsync(_repo.Root, Ct);

        var remote = Assert.Single(listing.Remotes);
        Assert.Equal("mi.espejo", remote.Name);
        Assert.Equal("https://host.example/uno.git", remote.Url);
    }

    [Fact]
    public async Task An_include_of_the_configuration_is_not_followed()
    {
        var included = Path.Combine(_repo.WorktreeRoot, "extra.config");
        Directory.CreateDirectory(_repo.WorktreeRoot);
        File.WriteAllText(included, "[remote \"colado\"]\n\turl = https://colado.example/x.git\n");
        _repo.Run("remote", "add", "origin", "https://host.example/r.git");
        _repo.Run("config", "--local", "include.path", included);
        _repo.Run("config", "--local", "includeIf.gitdir:**.path", included);

        var listing = await NewReader().ListRemotesAsync(_repo.Root, Ct);

        Assert.Equal(["origin"], listing.Remotes.Select(r => r.Name));
    }

    [Fact]
    public async Task An_enormous_list_is_refused_and_never_cut_to_a_partial_one()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 4000; i++)
        {
            builder.Append("[remote \"r").Append(i).Append("\"]\n\turl = https://host.example/").Append(new string('x', 100)).Append(i).Append(".git\n");
        }

        File.AppendAllText(Path.Combine(_repo.Root, ".git", "config"), builder.ToString());

        var listing = await NewReader().ListRemotesAsync(_repo.Root, Ct);

        Assert.Empty(listing.Remotes);
        Assert.Equal(RemoteListing.OutputTooBig, listing.RefusalCode);
    }

    [Fact]
    public async Task At_most_the_bounded_number_of_remotes_is_kept()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < RepositoryRemoteReader.MaxRemotes + 20; i++)
        {
            builder.Append("[remote \"r").Append(i).Append("\"]\n\turl = https://host.example/").Append(i).Append(".git\n");
        }

        File.AppendAllText(Path.Combine(_repo.Root, ".git", "config"), builder.ToString());

        var listing = await NewReader().ListRemotesAsync(_repo.Root, Ct);

        Assert.Null(listing.RefusalCode);
        Assert.Equal(RepositoryRemoteReader.MaxRemotes, listing.Remotes.Count);
    }

    [Fact]
    public async Task A_linked_worktree_reads_the_remotes_of_the_common_configuration()
    {
        _repo.Run("remote", "add", "origin", "https://u:" + Secret + "@host.example/r.git");
        var linked = Path.Combine(_repo.WorktreeRoot, "..", "enlazado-" + Guid.NewGuid().ToString("N")[..8]);
        linked = Path.GetFullPath(linked);
        _repo.Run("worktree", "add", "-q", "-b", "otra-rama", linked);
        try
        {
            var listing = await NewReader().ListRemotesAsync(linked, Ct);

            Assert.Null(listing.RefusalCode);
            var remote = Assert.Single(listing.Remotes);
            Assert.Equal("origin", remote.Name);
            Assert.Equal("https://host.example/r.git", remote.Url);
        }
        finally
        {
            _repo.Run("worktree", "remove", "--force", linked);
        }
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_or_does_not_exist_is_refused_with_a_code_and_never_throws()
    {
        var plain = Directory.CreateTempSubdirectory("as-noremotes-").FullName;
        try
        {
            var notRepository = await NewReader().ListRemotesAsync(plain, Ct);
            var missing = await NewReader().ListRemotesAsync(Path.Combine(plain, "no existe"), Ct);

            Assert.Empty(notRepository.Remotes);
            Assert.NotNull(notRepository.RefusalCode);
            Assert.Empty(missing.Remotes);
            Assert.Equal("not-a-repository", missing.RefusalCode);
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }
}
