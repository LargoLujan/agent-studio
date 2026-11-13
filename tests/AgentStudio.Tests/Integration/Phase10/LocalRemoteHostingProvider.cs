using System.IO;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Hosting;
using AgentStudio.Tests.Infrastructure.Git;

namespace AgentStudio.Tests.Integration.Phase10;

/// <summary>
/// The platform of the integration tests (docs/21 §9, «F10-2 y F10-7, sin rodeos»): a hosting provider with <b>no script</b>. It acts on a bare folder with real git, the way GitHub acts on its
/// repository: it opens a pull request from a branch of the folder, merges it with a squash, a rebase or a merge commit (in a scratch clone, pushing to the folder), reverts a merge, and answers
/// <see cref="ObserveAsync"/> by <b>reading the folder</b>: the commits of the pull request, the commits the merge left and their parents, and the reverts, whose messages carry the standard line
/// of git. Every SHA is one git produced, and the merge mode is deduced from the commits that were read, as the adapter of GitHub does (docs/21 D-27); nothing in a test writes a SHA for it.
/// Only the state a platform keeps in its own database (the numbers and the states of its pull requests, and the time of their last change) is held in memory. Checks and reviews are empty.
/// </summary>
internal sealed class LocalRemoteHostingProvider : IHostingProvider
{
    private static readonly RemoteLogin Merger = RemoteLogin.TryParse("tester")!;
    private readonly string _bare;
    private readonly string _scratch;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Record> _pulls = [];
    private int _scratches;
    private int _clock;

    public LocalRemoteHostingProvider(string bareFolder, string scratchFolder)
    {
        _bare = bareFolder;
        _scratch = scratchFolder;
        Directory.CreateDirectory(_scratch);
    }

    /// <summary>The calls the platform received, in order (for the tests that say what was not asked).</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    private readonly List<string> _calls = [];

    /// <summary>Whether the platform received a call of this name.</summary>
    public bool Called(string name)
    {
        lock (_gate)
        {
            return _calls.Contains(name);
        }
    }

    public HostingProviderKind Kind => HostingProviderKind.GitHub;

    public DateTimeOffset? RateLimitedUntil => null;

    public void RestoreRateLimit(DateTimeOffset? until)
    {
    }

    private sealed class Record(PullRequestRef pull, RemoteBranch head, RemoteBranch @base)
    {
        public PullRequestRef Pull { get; } = pull;

        public RemoteBranch Head { get; } = head;

        public RemoteBranch Base { get; } = @base;

        public PullRequestState State { get; set; } = PullRequestState.Open;

        public string? MergeCommit { get; set; }

        public List<string> Originals { get; set; } = [];

        public List<string> Results { get; set; } = [];

        public string? HeadAtMerge { get; set; }

        public int Stamp { get; set; }
    }

    public Task<HostingAvailability> ProbeAsync(CancellationToken ct) =>
        Task.FromResult(new HostingAvailability(
            true,
            new Version(2, 96, 0),
            new HostingCapabilities(
                HostingCapability.ReadRepository | HostingCapability.ReadBranchRules | HostingCapability.OpenPullRequest | HostingCapability.UpdatePullRequest | HostingCapability.Comment
                | HostingCapability.ReadReviews | HostingCapability.ReadChecks | HostingCapability.Merge | HostingCapability.ClosePullRequest | HostingCapability.DeleteBranch | HostingCapability.CommitStatus,
                [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase],
                false)));

    public Task<HostingResult<RemoteLogin>> ReadAccountAsync(RemoteRepository repository, CancellationToken ct) =>
        Task.FromResult(new HostingResult<RemoteLogin>(HostingOutcome.Ok, Merger));

    public Task<HostingResult<RemoteRepositoryFacts>> ReadRepositoryAsync(RemoteRepository repository, string? etag, CancellationToken ct) =>
        Task.FromResult(new HostingResult<RemoteRepositoryFacts>(HostingOutcome.Ok, new RemoteRepositoryFacts(RemoteBranch.TryParse("main")!, [MergeMethod.MergeCommit, MergeMethod.Squash, MergeMethod.Rebase], true, false)));

    public Task<HostingResult<BranchRules>> ReadBranchRulesAsync(RemoteRepository repository, RemoteBranch branch, CancellationToken ct) =>
        Task.FromResult(new HostingResult<BranchRules>(HostingOutcome.Ok, new BranchRules(true, [])));

    public Task<HostingResult<PullRequestRef?>> FindPullRequestAsync(RemoteRepository repository, RemoteBranch head, CancellationToken ct)
    {
        lock (_gate)
        {
            var found = _pulls.Values.FirstOrDefault(p => p.Head.Name == head.Name && p.State is PullRequestState.Open or PullRequestState.Draft);
            return Task.FromResult(new HostingResult<PullRequestRef?>(HostingOutcome.Ok, found?.Pull));
        }
    }

    public Task<HostingResult<PullRequestRef>> OpenPullRequestAsync(PullRequestDraft draft, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add("open");
            if (Ref(draft.Head.Name) is null)
            {
                return Task.FromResult(new HostingResult<PullRequestRef>(HostingOutcome.Refused, RefusalCode: "head-missing"));
            }

            var number = _pulls.Count + 1;
            var pull = new PullRequestRef(draft.Repository, number);
            _pulls[number] = new Record(pull, draft.Head, draft.Base) { Stamp = ++_clock };
            return Task.FromResult(new HostingResult<PullRequestRef>(HostingOutcome.Ok, pull));
        }
    }

    public Task<HostingResult<bool>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestText text, CancellationToken ct) =>
        Task.FromResult(new HostingResult<bool>(HostingOutcome.Ok, true));

    public Task<HostingResult<bool>> RequestReviewersAsync(PullRequestRef pullRequest, IReadOnlyList<RemoteLogin> reviewers, CancellationToken ct) =>
        Task.FromResult(new HostingResult<bool>(HostingOutcome.Ok, true));

    public Task<HostingResult<PublishedVerdict>> PublishVerdictAsync(PullRequestRef pullRequest, VerdictNotice notice, string statusDescription, string commentBody, long? commentId, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add("verdict");
            return Task.FromResult(new HostingResult<PublishedVerdict>(HostingOutcome.Ok, new PublishedVerdict(true, commentId ?? 1)));
        }
    }

    public Task<HostingResult<bool>> CloseAsync(PullRequestRef pullRequest, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add("close");
            var record = _pulls[pullRequest.Number];
            record.State = PullRequestState.Closed;
            record.Stamp = ++_clock;
            return Task.FromResult(new HostingResult<bool>(HostingOutcome.Ok, true));
        }
    }

    public Task<HostingResult<bool>> DeleteRunBranchAsync(RemoteRepository repository, RemoteBranch runBranch, string expectedCommit, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add("delete");
            if (Ref(runBranch.Name) != expectedCommit)
            {
                return Task.FromResult(new HostingResult<bool>(HostingOutcome.Refused, RefusalCode: "branch-moved"));
            }

            TempGitRepo.Git(_bare, "update-ref", "-d", "refs/heads/" + runBranch.Name);
            return Task.FromResult(new HostingResult<bool>(HostingOutcome.Ok, true));
        }
    }

    public Task<HostingResult<MergeOutcome>> MergeAsync(PullRequestRef pullRequest, MergeMethod method, string expectedHead, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add("merge:" + method);
            var record = _pulls[pullRequest.Number];
            var head = Ref(record.Head.Name);
            if (head != expectedHead)
            {
                return Task.FromResult(new HostingResult<MergeOutcome>(HostingOutcome.Refused, RefusalCode: "head-changed"));
            }

            var clone = Clone();
            var before = Ref(record.Base.Name)!;
            record.Originals = [.. Lines(TempGitRepo.Git(clone, "rev-list", "--reverse", $"origin/{record.Base.Name}..origin/{record.Head.Name}"))];
            TempGitRepo.Git(clone, "checkout", "-q", "-B", record.Base.Name, "origin/" + record.Base.Name);
            switch (method)
            {
                case MergeMethod.Squash:
                    TempGitRepo.Git(clone, "merge", "--squash", "origin/" + record.Head.Name);
                    TempGitRepo.Git(clone, "commit", "-q", "-m", $"Squash of pull request #{pullRequest.Number}");
                    break;
                case MergeMethod.MergeCommit:
                    TempGitRepo.Git(clone, "merge", "--no-ff", "-q", "-m", $"Merge pull request #{pullRequest.Number}", "origin/" + record.Head.Name);
                    break;
                default:
                    TempGitRepo.Git(clone, "checkout", "-q", "-B", "work", "origin/" + record.Head.Name);
                    TempGitRepo.Git(clone, "rebase", "-q", "origin/" + record.Base.Name);
                    TempGitRepo.Git(clone, "checkout", "-q", record.Base.Name);
                    TempGitRepo.Git(clone, "merge", "--ff-only", "-q", "work");
                    break;
            }

            TempGitRepo.Git(clone, "push", "-q", "origin", record.Base.Name);
            record.MergeCommit = Ref(record.Base.Name);
            record.Results = [.. Lines(TempGitRepo.Git(_bare, "rev-list", "--reverse", $"{before}..refs/heads/{record.Base.Name}"))];
            record.HeadAtMerge = head;
            record.State = PullRequestState.Merged;
            record.Stamp = ++_clock;
            return Task.FromResult(new HostingResult<MergeOutcome>(HostingOutcome.Ok, new MergeOutcome(true, record.MergeCommit, null)));
        }
    }

    /// <summary>Somebody else pushes a commit to the base branch (a file of its own), so that a merge has to do real work: a rebase makes new commits, and a merge commit has two parents.</summary>
    public string AdvanceBase(string baseBranch = "main")
    {
        lock (_gate)
        {
            var clone = Clone();
            TempGitRepo.Git(clone, "checkout", "-q", "-B", baseBranch, "origin/" + baseBranch);
            File.WriteAllText(Path.Combine(clone, "elsewhere-" + (++_clock) + ".txt"), "pushed by somebody else\n");
            TempGitRepo.Git(clone, "add", "-A");
            TempGitRepo.Git(clone, "commit", "-q", "-m", "Somebody else's change");
            TempGitRepo.Git(clone, "push", "-q", "origin", baseBranch);
            return Ref(baseBranch)!;
        }
    }

    /// <summary>The revert of a merge made from the web: one <c>git revert</c> of the merge commit (a merge commit) or of each commit the merge left (squash and rebase), pushed to the base branch.</summary>
    public IReadOnlyList<string> Revert(PullRequestRef pullRequest)
    {
        lock (_gate)
        {
            _calls.Add("revert");
            var record = _pulls[pullRequest.Number];
            var clone = Clone();
            TempGitRepo.Git(clone, "checkout", "-q", "-B", record.Base.Name, "origin/" + record.Base.Name);
            var parents = Lines(TempGitRepo.Git(clone, "rev-list", "--parents", "-n", "1", record.MergeCommit!)).Single().Split(' ').Length - 1;
            var before = Ref(record.Base.Name)!;
            if (parents > 1)
            {
                TempGitRepo.Git(clone, "revert", "--no-edit", "-m", "1", record.MergeCommit!);
            }
            else
            {
                foreach (var result in Enumerable.Reverse(record.Results))
                {
                    TempGitRepo.Git(clone, "revert", "--no-edit", result);
                }
            }

            TempGitRepo.Git(clone, "push", "-q", "origin", record.Base.Name);
            return [.. Lines(TempGitRepo.Git(_bare, "rev-list", "--reverse", $"{before}..refs/heads/{record.Base.Name}"))];
        }
    }

    public Task<HostingResult<PullRequestObservation>> ObserveAsync(PullRequestRef pullRequest, ObservationCursor? cursor, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add("observe");
            if (!_pulls.TryGetValue(pullRequest.Number, out var record))
            {
                return Task.FromResult(new HostingResult<PullRequestObservation>(HostingOutcome.NotFound));
            }

            var headNow = Ref(record.Head.Name);
            var merged = record.State == PullRequestState.Merged;
            var head = merged ? record.HeadAtMerge! : headNow ?? record.HeadAtMerge ?? Ref(record.Base.Name)!;
            var originals = merged
                ? record.Originals
                : headNow is null ? [] : Lines(TempGitRepo.Git(_bare, "rev-list", "--reverse", $"refs/heads/{record.Base.Name}..refs/heads/{record.Head.Name}"));

            RemoteMergeMode? mode = null;
            var reverts = new List<string>();
            if (merged)
            {
                // The mode is read from the commits, as the adapter does: two parents is a merge commit; several results are a rebase, and one result for several originals is a squash (a branch with merge commits that a rebase flattens to one commit is read as a squash: the commits cannot tell them apart).
                var parents = Lines(TempGitRepo.Git(_bare, "rev-list", "--parents", "-n", "1", record.MergeCommit!)).Single().Split(' ').Length - 1;
                mode = parents > 1
                    ? RemoteMergeMode.MergeCommit
                    : record.Results.Count > 1 ? RemoteMergeMode.Rebase
                    : record.Originals.Count == 1 ? RemoteMergeMode.SquashOrRebase
                    : RemoteMergeMode.Squash;

                // The standard line of git in the message of a revert names the commit it reverts.
                var targets = parents > 1 ? [record.MergeCommit!] : record.Results;
                foreach (var target in targets)
                {
                    reverts.AddRange(Lines(TempGitRepo.Git(_bare, "log", "--format=%H", $"--grep=This reverts commit {target}", "refs/heads/" + record.Base.Name)));
                }
            }

            var snapshot = new RemoteSnapshot(
                record.Pull,
                record.State,
                head,
                record.Base,
                headNow is not null,
                merged ? record.MergeCommit : null,
                mode,
                merged ? Merger : null,
                [.. originals],
                merged ? record.Results : [],
                false,
                new ChecksSummary(ChecksRollup.None, 0, 0, 0, false),
                new ReviewsSummary(0, 0, 0, []),
                0,
                false,
                [.. reverts.Distinct(StringComparer.Ordinal)],
                null,
                new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).AddSeconds(record.Stamp));
            snapshot.EnsureValid();
            return Task.FromResult(new HostingResult<PullRequestObservation>(HostingOutcome.Ok, new PullRequestObservation(snapshot, new ObservationCursor(new Dictionary<ObservedResource, string>(), snapshot), RemoteDisplayTexts.None)));
        }
    }

    private string? Ref(string branch)
    {
        var text = TempGitRepo.Git(_bare, "for-each-ref", "--format=%(objectname)", "refs/heads/" + branch).Trim();
        return text.Length == 0 ? null : text;
    }

    private string Clone()
    {
        var folder = Path.Combine(_scratch, "clone-" + (++_scratches));
        TempGitRepo.Git(_scratch, "clone", "-q", _bare, folder);
        TempGitRepo.Git(folder, "config", "user.name", "Platform");
        TempGitRepo.Git(folder, "config", "user.email", "platform@example.invalid");
        TempGitRepo.Git(folder, "config", "commit.gpgsign", "false");
        return folder;
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The registry of providers of the integration tests: the one platform for every repository, and the repository that <c>origin</c> names (a GitHub address is never opened: the executor pushes to the folder).</summary>
internal sealed class LocalProviders(IHostingProvider provider, RemoteRepository repository) : IHostingProviders
{
    public IHostingProvider? For(RemoteRepository candidate) => candidate == repository ? provider : null;

    public Task<RemoteRepository?> IdentifyAsync(ProjectContext project, string remoteName, CancellationToken ct) =>
        Task.FromResult<RemoteRepository?>(remoteName == "origin" ? repository : null);
}
