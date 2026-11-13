using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills.GitHub;

namespace AgentStudio.Tests.Infrastructure.Skills.GitHub;

/// <summary>What reached the wire, copied when it arrived (a request is disposed after it is sent).</summary>
internal sealed record SentRequest(string Url, string? Authorization, string? IfNoneMatch, string UserAgent);

/// <summary>
/// The transport under the guard of the client: it answers with what the test says and records every request. Nothing here opens a socket, so no test of
/// the doctor of skills reaches the network.
/// </summary>
internal sealed class FakeGitHubTransport(Func<SentRequest, HttpResponseMessage> route) : HttpMessageHandler
{
    public List<SentRequest> Sent { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = new SentRequest(
            request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("If-None-Match", out var etag) ? etag.Single() : null,
            request.Headers.UserAgent.ToString());
        lock (Sent)
        {
            Sent.Add(sent);
        }

        return Task.FromResult(route(sent));
    }

    public static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Fixture(string name, string? etag = null) =>
        Answer(Load(name), HttpStatusCode.OK, etag is null ? [] : [("ETag", etag)]);

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location) =>
        Answer(string.Empty, status, ("Location", location));

    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Skills", "GitHub", "Fixtures", name));
}

internal sealed class StubPolicy(bool allowed) : ISkillsDoctorPolicy
{
    public bool NetworkAllowed => allowed;
}

/// <summary>The secrets port as a double: counts the calls and gives what the test says.</summary>
internal sealed class StubDoctorSecrets(SkillsDoctorTokenUse use) : ISkillsDoctorSecrets
{
    public int Calls { get; private set; }

    public Task<SkillsDoctorTokenUse> UseTokenAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(use);
    }
}

internal static class GitHubTestData
{
    public const string Pinned = "6dcb09b5b57875f334f61aebed695e2e4193db5e";

    public const string Repo = "https://api.github.com/repos/octocat/Hello-World";

    public static readonly string PinnedCommits = Repo + "/commits?sha=" + Pinned + "&path=skills%2Ftdd&per_page=1";

    public static readonly string BranchCommits = Repo + "/commits?sha=main&path=skills%2Ftdd&per_page=1";

    public static SkillEntry Skill(string id = "tdd", string repository = "octocat/Hello-World", string? branch = "main", string? commit = Pinned, string path = "skills/tdd") =>
        new(new SkillId(id), id, "Resumen.", SkillSectors.IngenieriaSoftware, [], new SkillSource(repository, path, branch, commit, "MIT", SkillDistribution.Vendored));

    /// <summary>The routes of a healthy skill whose branch has not moved.</summary>
    public static HttpResponseMessage Healthy(SentRequest sent) => sent.Url switch
    {
        Repo => FakeGitHubTransport.Fixture("repo-active.json", "\"repo-1\""),
        var url when url == PinnedCommits => FakeGitHubTransport.Fixture("commits-pinned.json", "\"pinned-1\""),
        var url when url == BranchCommits => FakeGitHubTransport.Fixture("commits-branch-same.json", "\"branch-1\""),
        _ => FakeGitHubTransport.Answer("{}", HttpStatusCode.NotFound),
    };

    public static string NewFolder() => Path.Combine(Path.GetTempPath(), "as doctor " + Guid.NewGuid().ToString("N")[..12]);

    public static void DeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder that stays is not worth failing a test for.
        }
    }
}

/// <summary>A vault in memory that counts what is read: the proof that a denied presence leaves it alone.</summary>
internal sealed class CountingVault : ICredentialVault, ICredentialVaultProbe
{
    public Dictionary<string, string> Secrets { get; } = [];

    public int Reads { get; private set; }

    public int Stores { get; private set; }

    public int Deletes { get; private set; }

    public Task StoreAsync(string name, string secret, CancellationToken cancellationToken)
    {
        Stores++;
        Secrets[name] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Secrets.GetValueOrDefault(name));
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        Deletes++;
        Secrets.Remove(name);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Secrets.ContainsKey(name));
}

internal sealed class StubPresence(bool answer, bool throws = false) : ISkillsDoctorPresence
{
    public List<SkillsDoctorPresenceReason> Asked { get; } = [];

    public Task<bool> ConfirmAsync(SkillsDoctorPresenceReason reason, CancellationToken ct)
    {
        Asked.Add(reason);
        return throws ? throw new InvalidOperationException("Hello is not here") : Task.FromResult(answer);
    }
}
