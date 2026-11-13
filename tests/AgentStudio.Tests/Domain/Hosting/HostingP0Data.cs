using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Tests.Domain.Hosting;

/// <summary>Valid values of the remote for the tests of the P0 of Phase 10 (docs/21 §5): one repository, its branches and a merged pull request.</summary>
internal static class HostingP0Data
{
    public const string Head = "9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7";
    public const string Original1 = "1111111111111111111111111111111111111111";
    public const string Original2 = "2222222222222222222222222222222222222222";
    public const string Result = "3333333333333333333333333333333333333333";
    public const string Revert = "4444444444444444444444444444444444444444";

    public static readonly DateTimeOffset At = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    public static readonly RunId Run = new("20261113-a1b2c3");

    public static RemoteRepository Repository { get; } = RemoteRepository.TryCreateGitHub("LargoLujan", "agent-studio")!;

    public static RemoteBranch Main { get; } = RemoteBranch.TryParse("main")!;

    public static PullRequestRef PullRequest { get; } = new(Repository, 12);

    public static RemoteSnapshot Open(string head = Head) => new(
        PullRequest, PullRequestState.Open, head, Main, true, null, null, null, [Original1, Original2], [], false,
        new ChecksSummary(ChecksRollup.Passing, 3, 0, 0, false), new ReviewsSummary(0, 0, 0, []), 0, false, [], null, At);

    public static RemoteSnapshot Merged(RemoteMergeMode mode = RemoteMergeMode.Squash, string head = Head) => Open(head) with
    {
        State = PullRequestState.Merged,
        MergeCommit = Result,
        MergeMode = mode,
        MergedBy = RemoteLogin.TryParse("manuel"),
        ResultCommits = [Result],
        HeadBranchExists = false,
        Reviews = new ReviewsSummary(1, 0, 0, [RemoteLogin.TryParse("revisora")!]),
        UpdatedAt = At.AddMinutes(5),
    };
}
