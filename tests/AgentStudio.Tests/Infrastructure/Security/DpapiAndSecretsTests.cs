using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AgentStudio.Application.Skills;
using AgentStudio.Infrastructure.Security;
using AgentStudio.Infrastructure.Skills.GitHub;
using AgentStudio.Tests.Infrastructure.Skills.GitHub;
using static AgentStudio.Tests.Infrastructure.Skills.GitHub.GitHubTestData;
using FakeSecrets = AgentStudio.Tests.Security.FakeSecrets;

// The namespace of the tests of the doctor of skills: the folder says what is under test (Infrastructure/Security), and a namespace of its own
// would hide AgentStudio.Tests.Security for the tests of the other areas that name it.
namespace AgentStudio.Tests.Infrastructure.Skills.GitHub;

/// <summary>
/// The vault of the token and the only road to it (docs/20 D-15, F6-13). Every test writes under a temporary folder: nothing touches
/// <c>%LOCALAPPDATA%</c>, and nothing here reads the credentials of a CLI.
/// </summary>
public sealed class DpapiAndSecretsTests : IDisposable
{
    private readonly string _folder = NewFolder();

    public void Dispose() => DeleteFolder(_folder);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DpapiCredentialVault Vault() => new(_folder);

    [Fact]
    public async Task A_secret_makes_the_round_trip_and_the_file_does_not_hold_it_in_the_clear()
    {
        var secret = FakeSecrets.GitHubFineGrainedToken();
        var vault = Vault();

        await vault.StoreAsync("github-skills-doctor-token", secret, Ct);

        Assert.Equal(secret, await vault.ReadAsync("github-skills-doctor-token", Ct));
        var file = Assert.Single(Directory.GetFiles(_folder));
        Assert.EndsWith(".dpapi", file, StringComparison.Ordinal);
        var bytes = File.ReadAllBytes(file);
        Assert.False(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) >= 0, "The secret is in the file in the clear.");
        Assert.DoesNotContain(secret[..12], Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storing_again_replaces_and_deleting_removes_it()
    {
        var vault = Vault();
        await vault.StoreAsync("t", "one", Ct);
        await vault.StoreAsync("t", "two", Ct);

        Assert.Equal("two", await vault.ReadAsync("t", Ct));
        Assert.True(await vault.ContainsAsync("t", Ct));

        await vault.DeleteAsync("t", Ct);

        Assert.Null(await vault.ReadAsync("t", Ct));
        Assert.False(await vault.ContainsAsync("t", Ct));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Contains_decrypts_nothing_so_it_answers_even_for_a_file_that_cannot_be_decrypted()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "t.dpapi"), [1, 2, 3, 4]);
        var vault = Vault();

        Assert.True(await vault.ContainsAsync("t", Ct));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => vault.ReadAsync("t", Ct));
        Assert.DoesNotContain("1, 2, 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_not_read()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "t.dpapi"), new byte[DpapiCredentialVault.MaxFileBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Vault().ReadAsync("t", Ct));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("C:\\evil")]
    [InlineData("UPPER")]
    [InlineData("")]
    [InlineData("a b")]
    public async Task A_name_that_could_leave_the_folder_is_refused(string name)
    {
        var vault = Vault();

        await Assert.ThrowsAsync<ArgumentException>(() => vault.StoreAsync(name, "x", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.ReadAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.ContainsAsync(name, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => vault.DeleteAsync(name, Ct));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void The_default_folder_is_under_the_local_app_data_of_the_app()
    {
        var folder = DpapiCredentialVault.DefaultFolder();

        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("AgentStudio", "vault"), folder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_presence_the_vault_is_not_read()
    {
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = FakeSecrets.GitHubToken();
        var presence = new StubPresence(answer: false);
        var secrets = new SkillsDoctorSecrets(vault, vault, presence);

        var use = await secrets.UseTokenAsync(Ct);

        Assert.Equal(SkillsDoctorTokenState.PresenceDenied, use.State);
        Assert.Null(use.Token);
        Assert.Equal(0, vault.Reads);
        Assert.Equal([SkillsDoctorPresenceReason.UseToken], presence.Asked);
    }

    [Fact]
    public async Task A_presence_that_fails_to_be_asked_is_a_no_and_the_vault_is_not_read()
    {
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = FakeSecrets.GitHubToken();
        var secrets = new SkillsDoctorSecrets(vault, vault, new StubPresence(answer: true, throws: true));

        var use = await secrets.UseTokenAsync(Ct);

        Assert.Equal(SkillsDoctorTokenState.PresenceDenied, use.State);
        Assert.Equal(0, vault.Reads);
    }

    [Fact]
    public async Task With_the_presence_the_token_is_read_once()
    {
        var token = FakeSecrets.GitHubFineGrainedToken();
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = token;
        var secrets = new SkillsDoctorSecrets(vault, vault, new StubPresence(answer: true));

        var use = await secrets.UseTokenAsync(Ct);

        Assert.Equal(SkillsDoctorTokenState.Granted, use.State);
        Assert.Equal(token, use.Token!.Reveal());
        Assert.Equal(1, vault.Reads);
    }

    [Fact]
    public async Task Without_a_token_there_is_no_presence_and_no_reading()
    {
        var vault = new CountingVault();
        var presence = new StubPresence(answer: false);
        var secrets = new SkillsDoctorSecrets(vault, vault, presence);

        var use = await secrets.UseTokenAsync(Ct);

        Assert.Equal(SkillsDoctorTokenState.NoneStored, use.State);
        Assert.Empty(presence.Asked);
        Assert.Equal(0, vault.Reads);
    }

    [Fact]
    public async Task A_stored_value_that_is_not_a_token_is_the_same_as_none()
    {
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = "has a space";
        var secrets = new SkillsDoctorSecrets(vault, vault, new StubPresence(answer: true));

        Assert.Equal(SkillsDoctorTokenState.NoneStored, (await secrets.UseTokenAsync(Ct)).State);
    }

    [Fact]
    public async Task Storing_replacing_and_deleting_ask_for_the_presence_first_and_write_nothing_without_it()
    {
        var vault = new CountingVault();
        var denied = new StubPresence(answer: false);
        var secrets = new SkillsDoctorSecrets(vault, vault, denied);
        var token = new GitHubToken(FakeSecrets.GitHubToken());

        Assert.Equal(SkillsDoctorTokenChange.PresenceDenied, await secrets.StoreAsync(token, Ct));
        Assert.Equal(0, vault.Stores);

        vault.Secrets[SkillsDoctorSecrets.TokenName] = "kept";
        Assert.Equal(SkillsDoctorTokenChange.PresenceDenied, await secrets.DeleteAsync(Ct));
        Assert.Equal(0, vault.Deletes);
        Assert.Equal([SkillsDoctorPresenceReason.StoreToken, SkillsDoctorPresenceReason.DeleteToken], denied.Asked);

        var granted = new SkillsDoctorSecrets(vault, vault, new StubPresence(answer: true));
        Assert.Equal(SkillsDoctorTokenChange.Done, await granted.StoreAsync(token, Ct));
        Assert.Equal(token.Reveal(), vault.Secrets[SkillsDoctorSecrets.TokenName]);
        Assert.Equal(SkillsDoctorTokenChange.Done, await granted.DeleteAsync(Ct));
        Assert.Equal(SkillsDoctorTokenChange.NothingStored, await granted.DeleteAsync(Ct));
    }

    [Fact]
    public async Task Knowing_whether_there_is_a_token_asks_nothing_and_reads_nothing()
    {
        var vault = new CountingVault();
        var presence = new StubPresence(answer: false);
        var secrets = new SkillsDoctorSecrets(vault, vault, presence);

        Assert.False(await secrets.HasTokenAsync(Ct));
        vault.Secrets[SkillsDoctorSecrets.TokenName] = FakeSecrets.GitHubToken();
        Assert.True(await secrets.HasTokenAsync(Ct));

        Assert.Empty(presence.Asked);
        Assert.Equal(0, vault.Reads);
    }

    [Fact]
    public async Task The_checker_asks_the_presence_once_for_a_whole_batch()
    {
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = FakeSecrets.GitHubFineGrainedToken();
        var presence = new StubPresence(answer: true);
        var transport = new FakeGitHubTransport(Healthy);
        await using var client = new GitHubApiClient(transport);
        var checker = new GitHubSkillHealthChecker(new StubPolicy(true), new SkillsDoctorSecrets(vault, vault, presence), client, new GitHubResponseCache(_folder));

        var result = await checker.CheckAsync([Skill("a"), Skill("b"), Skill("c")], Ct);

        Assert.Equal([SkillsDoctorPresenceReason.UseToken], presence.Asked);
        Assert.Equal(1, vault.Reads);
        Assert.True(result.UsedToken);
        Assert.Equal(9, transport.Sent.Count);
    }

    [Fact]
    public async Task With_the_presence_denied_the_checker_never_reads_the_vault_and_sends_no_token()
    {
        var vault = new CountingVault();
        vault.Secrets[SkillsDoctorSecrets.TokenName] = FakeSecrets.GitHubFineGrainedToken();
        var transport = new FakeGitHubTransport(Healthy);
        await using var client = new GitHubApiClient(transport);
        var checker = new GitHubSkillHealthChecker(new StubPolicy(true), new SkillsDoctorSecrets(vault, vault, new StubPresence(answer: false)), client, new GitHubResponseCache(_folder));

        var result = await checker.CheckAsync([Skill()], Ct);

        Assert.Equal(0, vault.Reads);
        Assert.Equal(SkillsDoctorTokenState.PresenceDenied, result.TokenState);
        Assert.All(transport.Sent, s => Assert.Null(s.Authorization));
    }

    [Fact]
    public async Task A_sentinel_token_is_nowhere_in_what_is_returned_shown_or_written_after_a_batch_with_failures()
    {
        var sentinel = FakeSecrets.GitHubFineGrainedToken();
        var classic = FakeSecrets.GitHubToken();
        var vaultFolder = Path.Combine(_folder, "vault");
        var cacheFolder = Path.Combine(_folder, "cache");
        var vault = new DpapiCredentialVault(vaultFolder);
        var secrets = new SkillsDoctorSecrets(vault, vault, new StubPresence(answer: true));
        Assert.Equal(SkillsDoctorTokenChange.Done, await secrets.StoreAsync(new GitHubToken(sentinel), Ct));

        // A transport that fails with the token in the message of its own exception, as a careless one could, and other failing answers.
        var calls = 0;
        var transport = new FakeGitHubTransport(sent => (Interlocked.Increment(ref calls)) switch
        {
            1 => throw new HttpRequestException("failed: " + sent.Authorization + " " + sent.Url),
            2 => FakeGitHubTransport.Answer("{\"message\":\"Bad credentials " + sentinel + "\"}", HttpStatusCode.Unauthorized),
            3 => FakeGitHubTransport.Answer("{\"echo\":\"" + classic + "\"}"),
            _ => FakeGitHubTransport.Answer("{\"message\":\"" + sentinel + "\"}", HttpStatusCode.Forbidden),
        });
        await using var client = new GitHubApiClient(transport);
        var checker = new GitHubSkillHealthChecker(new StubPolicy(true), secrets, client, new GitHubResponseCache(cacheFolder));

        var seen = new StringBuilder();
        var all = new List<SkillHealthBatchResult>();
        for (var i = 0; i < 4; i++)
        {
            var result = await checker.CheckAsync([Skill("a" + i), Skill("b" + i)], Ct);
            all.Add(result);
            seen.AppendLine(result.ToString());
            foreach (var entry in result.Entries)
            {
                seen.AppendLine(entry.ToString());
                seen.AppendLine(entry.Failure?.ToString());
                foreach (var check in entry.Report.Checks)
                {
                    seen.AppendLine(check.ToString());
                }
            }
        }

        var direct = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetAsync(new Uri(Repo), null, new GitHubToken(sentinel), Ct));
        seen.AppendLine(direct.Message);
        seen.AppendLine(direct.ToString());
        seen.AppendLine(new GitHubToken(sentinel).ToString());
        seen.AppendLine((await secrets.UseTokenAsync(Ct)).ToString());
        if (Directory.Exists(cacheFolder))
        {
            foreach (var file in Directory.GetFiles(cacheFolder, "*", SearchOption.AllDirectories))
            {
                seen.AppendLine(File.ReadAllText(file));
            }
        }

        foreach (var file in Directory.GetFiles(vaultFolder))
        {
            seen.AppendLine(Encoding.Latin1.GetString(File.ReadAllBytes(file)));
        }

        var text = seen.ToString();
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(classic, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel[..20], text, StringComparison.Ordinal);
        Assert.NotEmpty(all);
        Assert.Contains(all.SelectMany(r => r.Entries), e => e.Failure is not null);
    }
}
