using AgentStudio.Debate.Arena;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// What a debate anchors does not change when its on-screen texts become coded (docs/17 D-48, ADR-0054 addendum of 2026-11-12): the
/// requirements of an orchestrator review are frozen with a hash that the judge and the log quote, so their text, in Spanish, is
/// part of the anchor.
/// </summary>
public sealed class DebateAnchoredTextsTests
{
    private const string OrchestratorReviewHash = "b43d4a2baa268ebcd5e5efc7e0785e269d17b0d3e5906975edfc8543448a59c9";

    [Fact]
    public void The_requirements_of_an_orchestrator_review_keep_their_content_hash()
    {
        var first = DebateModes.OrchestratorReview(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var later = DebateModes.OrchestratorReview(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(OrchestratorReviewHash, first.ContentHash);
        Assert.Equal(first.ContentHash, later.ContentHash);
    }
}
