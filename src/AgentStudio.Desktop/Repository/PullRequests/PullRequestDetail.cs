using AgentStudio.Domain.Hosting;

namespace AgentStudio.Desktop.Repository.PullRequests;

/// <summary>
/// The detail of one pull request (docs/21 section 7.1): the protections of its base branch, the commits before and after the merge and its actions. Each action is here only when the
/// capability is declared (<see cref="HostingCapabilities"/>, narrowed by what the repository allows) and the state admits it (F10-1): a flag that is false has no button.
/// </summary>
public sealed class PullRequestDetail
{
    public string Heading { get; init; } = string.Empty;

    public string BaseText { get; init; } = string.Empty;

    /// <summary>The protections of the base branch, one sentence each; or that they could not be read.</summary>
    public IReadOnlyList<CardLine> Rules { get; init; } = [];

    /// <summary>What could not be read or is not declared, said apart: the settings of the repository, the version of <c>gh</c>.</summary>
    public IReadOnlyList<CardLine> Notes { get; init; } = [];

    /// <summary>The names of the checks that fail, as the remote wrote them (data).</summary>
    public IReadOnlyList<string> FailingChecks { get; init; } = [];

    public string OriginalsText { get; init; } = string.Empty;

    public string ResultsText { get; init; } = string.Empty;

    public bool CanPublishVerdict { get; init; }

    public bool VerdictOnlyAsComment { get; init; }

    /// <summary>«Actualizar el PR…»: publishing the same run again updates the pull request that exists (docs/21 D-18), with the text the person edits.</summary>
    public bool CanUpdate { get; init; }

    public bool CanRequestReviewers { get; init; }

    public bool CanMerge { get; init; }

    public bool CanClose { get; init; }

    public bool CanDeleteBranch { get; init; }

    public bool CanOpenInBrowser { get; init; }

    public bool CanUnfollow { get; init; }

    /// <summary>The ways to merge this pull request: those the provider declares, the repository allows and the branch admits.</summary>
    public IReadOnlyList<MergeMethod> MergeMethods { get; init; } = [];

    public bool HasRules => Rules.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasFailingChecks => FailingChecks.Count > 0;

    public bool HasOriginals => OriginalsText.Length > 0;

    public bool HasResults => ResultsText.Length > 0;

    public override string ToString() => Heading;
}
