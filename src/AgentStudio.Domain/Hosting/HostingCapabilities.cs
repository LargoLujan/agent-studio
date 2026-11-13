namespace AgentStudio.Domain.Hosting;

/// <summary>What a provider can do with one version of its CLI (docs/21 D-09). A flag that is not set does not appear on the screen.</summary>
[Flags]
public enum HostingCapability
{
    None = 0,
    ReadRepository = 1,
    ReadBranchRules = 2,
    OpenPullRequest = 4,
    UpdatePullRequest = 8,
    Comment = 16,
    RequestReviewers = 32,
    ReadReviews = 64,
    ReadChecks = 128,
    ReadWorkflowRuns = 256,
    Merge = 512,
    ClosePullRequest = 1024,
    DeleteBranch = 2048,
    CommitStatus = 4096,
    ReadDeployments = 8192,
    ConditionalRequests = 16384,
    DraftPullRequest = 32768,
}

/// <summary>What the account may do in a repository and what the repository allows, as the platform said it (docs/21 §5.2). Values of the platform, validated: no text of the remote.</summary>
/// <param name="DefaultBranch">The default branch of the repository.</param>
/// <param name="AllowedMergeMethods">The merge methods the repository allows.</param>
/// <param name="CanPush">Whether the account may write to the repository.</param>
/// <param name="Archived">Whether the repository is archived (read only).</param>
public sealed record RemoteRepositoryFacts(RemoteBranch DefaultBranch, IReadOnlyList<MergeMethod> AllowedMergeMethods, bool CanPush, bool Archived);

/// <summary>The kinds of rule that protect a branch. Appended, never reordered.</summary>
public enum BranchRuleKind
{
    Protected,
    RequiredReviews,
    RequiredChecks,
    SignedCommits,
    LinearHistory,
    MergeQueue,
    Unknown,
}

/// <summary>One rule of a branch; <paramref name="Count"/> is the number of reviews or checks it requires, when it has one.</summary>
public sealed record BranchRule(BranchRuleKind Kind, int? Count);

/// <summary>
/// The active rules of a branch. <paramref name="Readable"/> is false when the platform does not show them to this account: that is never «no rules»
/// (docs/21 D-17), so the screen says it could not read them.
/// </summary>
public sealed record BranchRules(bool Readable, IReadOnlyList<BranchRule> Rules)
{
    /// <summary>The rules of a branch the platform did not show.</summary>
    public static BranchRules Unreadable { get; } = new(false, []);

    public bool Has(BranchRuleKind kind) => Rules.Any(r => r.Kind == kind);
}

/// <summary>
/// What a provider can do with one version of its CLI (docs/21 D-09). What is not declared does not appear on the screen (F10-1).
/// </summary>
/// <param name="Supported">The operations the adapter knows how to do with this version.</param>
/// <param name="MergeMethods">The merge methods it can ask for.</param>
/// <param name="UntestedCliVersion">The version of the CLI is not the one the adapter was tested with (a warning, never a refusal).</param>
public sealed record HostingCapabilities(HostingCapability Supported, IReadOnlyList<MergeMethod> MergeMethods, bool UntestedCliVersion)
{
    private const HostingCapability WritesToTheRepository =
        HostingCapability.OpenPullRequest | HostingCapability.UpdatePullRequest | HostingCapability.Comment | HostingCapability.RequestReviewers
        | HostingCapability.Merge | HostingCapability.ClosePullRequest | HostingCapability.DeleteBranch | HostingCapability.CommitStatus
        | HostingCapability.DraftPullRequest;

    /// <summary>Nothing: no provider, or one that is not installed.</summary>
    public static readonly HostingCapabilities None = new(HostingCapability.None, [], false);

    public bool Has(HostingCapability capability) => capability != HostingCapability.None && (Supported & capability) == capability;

    /// <summary>
    /// What is left of these capabilities for one repository: the merge methods it allows (and no merge at all when none is left), and no write
    /// when the account cannot write to it or it is archived. Reads stay.
    /// </summary>
    /// <param name="repository">What the repository allows.</param>
    /// <param name="baseBranchRules">
    /// The protections of the base branch, when they could be read: a branch that asks for a linear history does not take a merge commit, so that method is left out
    /// (docs/21 §7.2). Rules that could not be read narrow nothing: the platform applies them anyway. It only ever narrows.
    /// </param>
    public HostingCapabilities NarrowedBy(RemoteRepositoryFacts repository, BranchRules? baseBranchRules = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var linear = baseBranchRules is { Readable: true } && baseBranchRules.Has(BranchRuleKind.LinearHistory);
        var methods = MergeMethods.Where(repository.AllowedMergeMethods.Contains).Where(m => !(linear && m == MergeMethod.MergeCommit)).ToArray();
        var supported = Supported;
        if (!repository.CanPush || repository.Archived)
        {
            supported &= ~WritesToTheRepository;
            methods = [];
        }

        if (methods.Length == 0)
        {
            supported &= ~HostingCapability.Merge;
        }

        return this with { Supported = supported, MergeMethods = methods };
    }
}
