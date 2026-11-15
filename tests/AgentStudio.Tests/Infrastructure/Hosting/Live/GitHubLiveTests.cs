using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Git.Remote;
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
    private const string CloneVariable = "AGENTSTUDIO_HOSTING_LIVE_CLONE";
    private const string RunVariable = "AGENTSTUDIO_HOSTING_LIVE_RUN";
    private const string EndVariable = "AGENTSTUDIO_HOSTING_LIVE_END";

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

    private static bool IsPrivate(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && document.RootElement.TryGetProperty("private", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static string Shape(ProcessResult result)
    {
        var first = result.StandardOutput.Split('\n', 2)[0].TrimEnd('\r');
        var headers = result.StandardOutput.Split('\n').Skip(1).TakeWhile(l => l.Trim().Length > 0).Select(l => l.Split(':', 2)[0].Trim().ToLowerInvariant()).ToList();
        return $"exit {result.ExitCode}; first line «{(first.Length > 60 ? first[..60] : first)}»; headers {string.Join(",", headers)}; body {result.StandardOutput.Length} chars; stderr {result.StandardError.Length} chars";
    }

    /// <summary>What was seen of a pull request, for the notes: states, modes and counts only. No commit, no account and no text of the platform.</summary>
    private static string Seen(RemoteSnapshot snapshot) =>
        $"state {snapshot.State}, mode {snapshot.MergeMode?.ToString() ?? "none"}, merge commit {(snapshot.MergeCommit is null ? "no" : "yes")}, original {snapshot.OriginalCommits.Count}, "
        + $"result {snapshot.ResultCommits.Count}, reverts {snapshot.RevertCommits.Count}, head branch {(snapshot.HeadBranchExists ? "there" : "gone")}, checks {snapshot.Checks.Rollup}, "
        + $"reviews {snapshot.Reviews.Approved}/{snapshot.Reviews.ChangesRequested}, force pushes {snapshot.ForcePushes}, deployment {snapshot.Deployment?.State.ToString() ?? "none"}";

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

        LiveTestSupport.Note("round 1: " + Seen(first.Value.Snapshot));
        LiveTestSupport.Note("round 2: " + second.Outcome + (second.Value is { } later ? " · " + Seen(later.Snapshot) : string.Empty));
    }

    /// <summary>
    /// The whole way of a publication through the code the app uses, against the private test repository. The remote executor pushes one commit of a local clone to the branch of a run over HTTPS (with the
    /// person's own credential helper and the allow list of the environment), and the adapter opens the pull request, finds it, changes its text and observes it. It ends as <c>AGENTSTUDIO_HOSTING_LIVE_END</c>
    /// says: merged with one of the three methods and observed twice (the first time as a pull request, the second as one already merged), or closed and its branch deleted. Needs
    /// <c>AGENTSTUDIO_HOSTING_LIVE_WRITE=1</c>, the folder of a clone of the test repository, the id of a run with no branch on the remote yet, and the commit to publish.
    /// </summary>
    [Fact]
    public async Task A_commit_is_pushed_and_its_pull_request_lives_through_the_adapter()
    {
        var repository = Repository();
        Assert.SkipUnless(Environment.GetEnvironmentVariable(WriteVariable) == "1", $"Set {WriteVariable}=1 to let this test push a branch and open, merge or close a pull request of the test repository.");
        var clone = Environment.GetEnvironmentVariable(CloneVariable);
        var runText = Environment.GetEnvironmentVariable(RunVariable);
        var commit = Environment.GetEnvironmentVariable(HeadVariable);
        var end = Environment.GetEnvironmentVariable(EndVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(clone) || !Directory.Exists(clone) || string.IsNullOrWhiteSpace(runText) || !GitHubApiPath.IsCommitId(commit) || end is not ("squash" or "rebase" or "merge" or "close"),
            $"Set {CloneVariable} (a clone of the test repository), {RunVariable} (a run id with no branch on the remote), {HeadVariable} (the commit to publish) and {EndVariable}=squash|rebase|merge|close.");
        var ct = TestContext.Current.CancellationToken;
        var (runner, process, provider) = Build();

        // Nothing is written anywhere but in the private test repository that the owner named: it is read first and it has to say that it is private.
        var info = await runner.RunAsync(GitHubApiPath.Repository(repository).Call(), ct);
        Assert.SkipUnless(info.Http is { Status: 200 } http && IsPrivate(http.Body), $"{RepositoryVariable} must name a private repository: nothing is written to a public one.");
        process.Runs.Clear();

        var run = new RunId(runText!);
        var head = RemoteBranch.ForRun(run);
        var project = new ProjectContext("live-check", clone!);
        var gitProcess = new Capturing(new ProcessRunner());
        var git = new RemoteGitRunner(gitProcess, new ExecutableLocator(), RemoteGitFolders.Under(_temp.Combine("local")), _temp.Combine("worktrees"), RemoteTransport.GitHubHttps);

        try
        {
            var facts = await provider.ReadRepositoryAsync(repository, null, ct);
            Assert.Equal(HostingOutcome.Ok, facts.Outcome);
            var baseBranch = facts.Value!.DefaultBranch;

            var before = await git.ReadBranchAsync(project, repository, head, ct);
            LiveTestSupport.Note($"branch before the push: reached {before.Reached}, there {before.Commit is not null}, code {before.FailureCode ?? "none"}");
            Assert.True(before.Reached);
            Assert.Null(before.Commit);

            var pushed = await git.PushRunBranchAsync(new RemotePush(project, repository, run, commit!), ct);
            LiveTestSupport.Note(
                $"push over https: {pushed.Outcome}, code {pushed.FailureCode ?? "none"}, configuration accepted {pushed.Inspection.Accepted}, refusals {pushed.Inspection.Refusals.Count}, lfs {pushed.Inspection.UsesLfs}, "
                + $"confirmed {string.Equals(pushed.RemoteCommit, commit, StringComparison.Ordinal)}");
            Assert.Equal(RemotePushOutcome.Pushed, pushed.Outcome);
            Assert.Equal(commit, pushed.RemoteCommit);

            var none = await provider.FindPullRequestAsync(repository, head, ct);
            Assert.Equal(HostingOutcome.Ok, none.Outcome);
            Assert.Null(none.Value);

            var opened = await provider.OpenPullRequestAsync(
                new PullRequestDraft(repository, head, baseBranch, new PullRequestText("Comprobación en vivo del adaptador", "Abierto por la prueba en vivo del adaptador. Se puede cerrar."), Draft: false), ct);
            LiveTestSupport.Note($"open: {opened.Outcome}, code {opened.RefusalCode ?? "none"}");
            Assert.Equal(HostingOutcome.Ok, opened.Outcome);
            var pullRequest = opened.Value!;

            var found = await provider.FindPullRequestAsync(repository, head, ct);
            Assert.Equal(pullRequest, found.Value);
            var updated = await provider.UpdatePullRequestAsync(
                pullRequest, new PullRequestText("Comprobación en vivo del adaptador (texto cambiado)", "Texto cambiado por la prueba en vivo del adaptador. Se puede cerrar."), ct);
            LiveTestSupport.Note($"update of the text: {updated.Outcome}, {updated.Value}");
            Assert.Equal(HostingOutcome.Ok, updated.Outcome);

            var open = await provider.ObserveAsync(pullRequest, null, ct);
            Assert.Equal(HostingOutcome.Ok, open.Outcome);
            LiveTestSupport.Note("open · " + Seen(open.Value!.Snapshot));
            Assert.Equal(PullRequestState.Open, open.Value.Snapshot.State);
            Assert.Equal(commit, open.Value.Snapshot.HeadCommit);

            if (end is "close")
            {
                var closed = await provider.CloseAsync(pullRequest, ct);
                LiveTestSupport.Note($"close: {closed.Outcome}, {closed.Value}");
                Assert.Equal(HostingOutcome.Ok, closed.Outcome);
                var seenClosed = await provider.ObserveAsync(pullRequest, null, ct);
                Assert.Equal(HostingOutcome.Ok, seenClosed.Outcome);
                LiveTestSupport.Note("closed · " + Seen(seenClosed.Value!.Snapshot));
                Assert.Equal(PullRequestState.Closed, seenClosed.Value.Snapshot.State);

                var deleted = await provider.DeleteRunBranchAsync(repository, head, commit!, ct);
                LiveTestSupport.Note($"delete of the run branch: {deleted.Outcome}, {deleted.Value}, code {deleted.RefusalCode ?? "none"}");
                Assert.Equal(HostingOutcome.Ok, deleted.Outcome);
                var after = await git.ReadBranchAsync(project, repository, head, ct);
                LiveTestSupport.Note($"branch after the delete: reached {after.Reached}, there {after.Commit is not null}");
                Assert.True(after.Reached);
                Assert.Null(after.Commit);
                var gone = await provider.ObserveAsync(pullRequest, null, ct);
                LiveTestSupport.Note("closed and deleted: " + gone.Outcome + (gone.Value is { } last ? " · " + Seen(last.Snapshot) : string.Empty));
                return;
            }

            var method = end switch { "squash" => MergeMethod.Squash, "rebase" => MergeMethod.Rebase, _ => MergeMethod.MergeCommit };
            HostingResult<MergeOutcome>? merged = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                // Right after it is opened the platform may still be working out whether it can be merged: a refusal then is not a failure of the adapter, and it is asked again.
                merged = await provider.MergeAsync(pullRequest, method, commit!, ct);
                LiveTestSupport.Note(
                    $"merge asked as {method}, attempt {attempt}: {merged.Outcome}, merged {merged.Value?.Merged}, merge commit {(merged.Value?.MergeCommit is null ? "no" : "yes")}, "
                    + $"blocked {merged.Value?.BlockedState ?? "none"}, code {merged.RefusalCode ?? "none"}");
                if (merged is { Outcome: HostingOutcome.Ok, Value.Merged: true })
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }

            Assert.Equal(HostingOutcome.Ok, merged!.Outcome);
            Assert.True(merged.Value!.Merged);

            var first = await provider.ObserveAsync(pullRequest, null, ct);
            Assert.Equal(HostingOutcome.Ok, first.Outcome);
            LiveTestSupport.Note($"merged, asked as {method} · " + Seen(first.Value!.Snapshot));
            Assert.Equal(PullRequestState.Merged, first.Value.Snapshot.State);
            var again = await provider.ObserveAsync(pullRequest, first.Value.Cursor, ct);
            LiveTestSupport.Note("merged, second round: " + again.Outcome + (again.Value is { } later ? " · " + Seen(later.Snapshot) : string.Empty));
        }
        finally
        {
            // The notes are left even when a step fails: what was called and how it answered is what the measurement is for.
            LiveTestSupport.Note($"git: {gitProcess.Runs.Count} runs, exit codes {string.Join(",", gitProcess.Runs.Select(r => r.Result.ExitCode))}");
            foreach (var call in process.Runs)
            {
                LiveTestSupport.Note("life · " + new GhCallSeen(call.Spec) + " · " + Shape(call.Result));
            }
        }
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
        var (runner, process, provider) = Build();
        var pullRequest = new PullRequestRef(repository, n);

        // Nothing is written anywhere but in the private test repository that the owner named: it is read first and it has to say that it is private.
        var info = await runner.RunAsync(GitHubApiPath.Repository(repository).Call(), ct);
        process.Runs.Clear();
        Assert.SkipUnless(info.Http is { Status: 200 } http && IsPrivate(http.Body), $"{RepositoryVariable} must name a private repository: nothing is written to a public one.");
        var notice = new VerdictNotice(new RunId("live-check"), head!, VerdictStanding.InReview, [new RequirementOutcome("R-1", true)], "live-test", "none", HeadIsJudgedCommit: true);

        // Only «pending»: the status of a test must not look like an approval of the commit.
        var first = await provider.PublishVerdictAsync(pullRequest, notice, "Comprobación en vivo del adaptador", "Comprobación en vivo del adaptador. Se puede borrar.", null, ct);
        var edited = await provider.PublishVerdictAsync(pullRequest, notice, "Comprobación en vivo del adaptador", "Comprobación en vivo del adaptador, editada. Se puede borrar.", first.Value?.CommentId, ct);

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
