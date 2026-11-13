using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Infrastructure.Hosting.GitHub;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Providers.Live;

namespace AgentStudio.Tests.Infrastructure.Hosting.Live;

/// <summary>
/// The real <c>gh</c> against a private test repository that the owner creates (docs/21 §9, decision 14). They are written and NOT run: they need the OK of the owner, who runs them by hand with
/// <c>AGENTSTUDIO_PROVIDER_TESTS=1</c> and <c>AGENTSTUDIO_HOSTING_LIVE_REPO=owner/name</c>; without them every test here is skipped, so the suite and the CI never touch the network or the account. The first
/// test is the first thing to measure: whether <c>gh api -i</c> lets the status line of a 304 be read, and with what exit code (<c>Live/README.md</c> has the whole script). Whatever the tests note is only
/// status lines, header names, exit codes and counts, never a body, an account or a token; the writing ones also need <c>AGENTSTUDIO_HOSTING_LIVE_WRITE=1</c> and the number and head of a pull request of that repository.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class GitHubLiveTests : IDisposable
{
    private const string RepositoryVariable = "AGENTSTUDIO_HOSTING_LIVE_REPO";
    private const string WriteVariable = "AGENTSTUDIO_HOSTING_LIVE_WRITE";
    private const string PullRequestVariable = "AGENTSTUDIO_HOSTING_LIVE_PR";
    private const string HeadVariable = "AGENTSTUDIO_HOSTING_LIVE_HEAD";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>The real process runner, with a copy of every result that the test can read: the exit code and the shape of the output, never kept anywhere.</summary>
    private sealed class Capturing(IProcessRunner inner) : IProcessRunner
    {
        public List<(ProcessSpec Spec, ProcessResult Result)> Runs { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(spec, cancellationToken);
            Runs.Add((spec, result));
            return result;
        }
    }

    private static RemoteRepository Repository()
    {
        LiveTestSupport.RequireOptIn();
        var text = Environment.GetEnvironmentVariable(RepositoryVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(text), $"Set {RepositoryVariable}=owner/name (a private test repository you created) to run the hosting tests against GitHub.");
        var parts = text!.Split('/');
        var repository = parts.Length == 2 ? RemoteRepository.TryCreateGitHub(parts[0], parts[1]) : null;
        Assert.NotNull(repository);
        return repository;
    }

    private (GhRunner Runner, Capturing Process, GitHubHostingProvider Provider) Build()
    {
        var folders = GhFolders.Under(_temp.Combine("local"));
        var process = new Capturing(new ProcessRunner());
        var runner = new GhRunner(process, folders);
        var door = new DoctorToolRunner(new ExecutableLocator(), new ProcessRunner(), DoctorFolders.Under(_temp.Combine("local")));
        return (runner, process, new GitHubHostingProvider(new GhQueue(runner), door));
    }

    private static string Shape(ProcessResult result)
    {
        var first = result.StandardOutput.Split('\n', 2)[0].TrimEnd('\r');
        var headers = result.StandardOutput.Split('\n').Skip(1).TakeWhile(l => l.Trim().Length > 0).Select(l => l.Split(':', 2)[0].Trim().ToLowerInvariant()).ToList();
        return $"exit {result.ExitCode}; first line «{(first.Length > 60 ? first[..60] : first)}»; headers {string.Join(",", headers)}; body {result.StandardOutput.Length} chars; stderr {result.StandardError.Length} chars";
    }

    /// <summary>
    /// THE FIRST THING TO MEASURE (docs/21 §13): a 304 is an error for <c>gh</c> (exit code other than 0, «gh: HTTP 304» on its standard error). Does <c>gh api -i</c> still print the status line and the
    /// headers of a 304 on the standard output, so that the poll can be conditional? It reads the repository, then reads it again with its <c>ETag</c>, and notes both.
    /// </summary>
    [Fact]
    public async Task The_status_line_of_a_304_can_be_read_and_so_is_its_exit_code()
    {
        var repository = Repository();
        var ct = TestContext.Current.CancellationToken;
        var (runner, process, _) = Build();
        var path = GitHubApiPath.Repository(repository);

        var first = await runner.RunAsync(path.Call(), ct);
        Assert.Equal(GhRunStatus.Response, first.Status);
        Assert.Equal(200, first.Http!.Status);
        Assert.NotNull(first.Http.ETag);

        var second = await runner.RunAsync(path.Call(eTag: first.Http.ETag), ct);

        LiveTestSupport.Note("gh api -i, first read: " + Shape(process.Runs[0].Result));
        LiveTestSupport.Note("gh api -i, read with If-None-Match: " + Shape(process.Runs[1].Result));
        Assert.Equal(GhRunStatus.Response, second.Status);
        Assert.Equal(304, second.Http!.Status);
        Assert.Equal(string.Empty, second.Http.Body);
        LiveTestSupport.Note("A 304 was readable by its status line. Exit code: " + process.Runs[1].Result.ExitCode + ".");
    }

    [Fact]
    public async Task The_repository_is_read_and_a_read_with_its_etag_is_not_modified()
    {
        var repository = Repository();
        var ct = TestContext.Current.CancellationToken;
        var (_, process, provider) = Build();

        var availability = await provider.ProbeAsync(ct);
        LiveTestSupport.Note($"gh {availability.CliVersion}; untested: {availability.Capabilities.UntestedCliVersion}; capabilities: {availability.Capabilities.Supported}");
        var account = await provider.ReadAccountAsync(repository, ct);
        var facts = await provider.ReadRepositoryAsync(repository, null, ct);
        var again = await provider.ReadRepositoryAsync(repository, facts.ETag, ct);

        LiveTestSupport.Note($"account {account.Outcome}; repository {facts.Outcome} (modes {string.Join("/", facts.Value?.AllowedMergeMethods ?? [])}, push {facts.Value?.CanPush}); again {again.Outcome}");
        Assert.Equal(HostingOutcome.Ok, account.Outcome);
        Assert.Equal(HostingOutcome.Ok, facts.Outcome);
        Assert.Equal(HostingOutcome.NotModified, again.Outcome);
        Assert.All(process.Runs, run => Assert.Equal("github.com", run.Spec.Arguments[run.Spec.Arguments.ToList().IndexOf("--hostname") + 1]));

        var rules = await provider.ReadBranchRulesAsync(repository, facts.Value!.DefaultBranch, ct);
        LiveTestSupport.Note($"rules of {facts.Value.DefaultBranch}: {rules.Outcome}, readable {rules.Value?.Readable}, kinds {string.Join(",", rules.Value?.Rules.Select(r => r.Kind) ?? [])}");
        Assert.Equal(HostingOutcome.Ok, rules.Outcome);
    }

    /// <summary>Observes a pull request twice and notes how many requests answered 304 (the poll costs four per open pull request without changes). Needs the number of a pull request of the repository.</summary>
    [Fact]
    public async Task A_pull_request_is_observed_and_the_second_round_is_not_modified()
    {
        var repository = Repository();
        var number = Environment.GetEnvironmentVariable(PullRequestVariable);
        Assert.SkipWhen(!int.TryParse(number, out var n) || n < 1, $"Set {PullRequestVariable}=<number of a pull request of the test repository>.");
        var ct = TestContext.Current.CancellationToken;
        var (_, process, provider) = Build();
        var pullRequest = new PullRequestRef(repository, n);

        var first = await provider.ObserveAsync(pullRequest, null, ct);
        Assert.Equal(HostingOutcome.Ok, first.Outcome);
        foreach (var run in process.Runs)
        {
            LiveTestSupport.Note("round 1 · " + new GhCallSeen(run.Spec) + " · " + Shape(run.Result));
        }

        process.Runs.Clear();
        var second = await provider.ObserveAsync(pullRequest, first.Value!.Cursor, ct);
        foreach (var run in process.Runs)
        {
            LiveTestSupport.Note("round 2 · " + new GhCallSeen(run.Spec) + " · " + Shape(run.Result));
        }

        LiveTestSupport.Note($"state {first.Value.Snapshot.State}, mode {first.Value.Snapshot.MergeMode}, checks {first.Value.Snapshot.Checks}, reviews {first.Value.Snapshot.Reviews.Approved}/{first.Value.Snapshot.Reviews.ChangesRequested}, force pushes {first.Value.Snapshot.ForcePushes}, deployment {first.Value.Snapshot.Deployment}");
        LiveTestSupport.Note("round 2: " + second.Outcome);
    }

    /// <summary>Writes a status and a tagged comment to a pull request of the test repository, then edits the comment in place by its id. Needs <c>AGENTSTUDIO_HOSTING_LIVE_WRITE=1</c>, the number of a pull request and its head.</summary>
    [Fact]
    public async Task The_verdict_is_published_as_a_status_and_a_tagged_comment_and_edited_in_place()
    {
        var repository = Repository();
        Assert.SkipUnless(Environment.GetEnvironmentVariable(WriteVariable) == "1", $"Set {WriteVariable}=1 to let this test write a status and a comment to a pull request of the test repository.");
        var head = Environment.GetEnvironmentVariable(HeadVariable);
        Assert.SkipWhen(!int.TryParse(Environment.GetEnvironmentVariable(PullRequestVariable), out var n) || n < 1 || !GitHubApiPath.IsCommitId(head), $"Set {PullRequestVariable} and {HeadVariable} (the head commit of that pull request).");
        var ct = TestContext.Current.CancellationToken;
        var (_, process, provider) = Build();
        var pullRequest = new PullRequestRef(repository, n);
        var notice = new VerdictNotice(new RunId("live-check"), head!, VerdictStanding.InReview, [new RequirementOutcome("R-1", true)], "live-test", "none", HeadIsJudgedCommit: true);

        var first = await provider.PublishVerdictAsync(pullRequest, notice, "Comprobación en vivo del adaptador. Se puede borrar.", null, ct);
        var edited = await provider.PublishVerdictAsync(pullRequest, notice with { Standing = VerdictStanding.Approved }, "Comprobación en vivo del adaptador, editada. Se puede borrar.", first.Value?.CommentId, ct);

        foreach (var run in process.Runs)
        {
            LiveTestSupport.Note("verdict · " + new GhCallSeen(run.Spec) + " · " + Shape(run.Result));
        }

        Assert.Equal(HostingOutcome.Ok, first.Outcome);
        Assert.Equal(HostingOutcome.Ok, edited.Outcome);
        Assert.Equal(first.Value!.CommentId, edited.Value!.CommentId);
        Assert.DoesNotContain(process.Runs, run => run.Spec.Arguments.Any(a => a.Contains("review", StringComparison.OrdinalIgnoreCase)));
    }
}
