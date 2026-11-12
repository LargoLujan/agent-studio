using AgentStudio.Application.Coordination;
using AgentStudio.Application.Localization;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Debate.Challenges.Fixtures.ChallengeBench;

namespace AgentStudio.Tests.Debate.Challenges;

/// <summary>
/// What the challenge of a verdict shows (docs/17 D-47, ADR-0054 addendum of 2026-11-12): its problems and notes carry a code
/// in step with each sentence, and the refusals that reach the card are <see cref="UserTextException"/>.
/// </summary>
public sealed class ChallengeCodedTextsTests
{
    [Fact]
    public async Task The_problems_of_the_challenge_go_one_for_one_with_their_codes()
    {
        var bench = await OverRunAsync();
        bench.Coordinator.Producers = new HashSet<VendorId>();

        var proposal = await bench.PrepareAsync(bench.Request());

        Assert.NotNull(proposal.ProblemTexts);
        Assert.Equal(proposal.Problems.Count, proposal.ProblemTexts.Count);
        Assert.Contains(proposal.ProblemTexts, t => t.Code == DebateUserTexts.ChallengeProblemNotInherited && t.Arguments.Contains("anthropic"));
        Assert.NotNull(proposal.Estimate.NoteTexts);
        Assert.Equal(proposal.Estimate.Notes.Count, proposal.Estimate.NoteTexts.Count);
    }

    [Fact]
    public async Task A_proposal_with_problems_is_refused_with_a_code_and_an_english_message()
    {
        var bench = await OverRunAsync();
        bench.Coordinator.Producers = new HashSet<VendorId>();
        var proposal = await bench.PrepareAsync(bench.Request());

        var refusal = await Assert.ThrowsAsync<UserTextException>(() => bench.ChallengeAsync(proposal));

        Assert.Equal(DebateUserTexts.ChallengeCannotOpen, refusal.Text.Code);
        Assert.StartsWith("The challenge cannot be opened", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reasons_left_empty_are_a_problem_with_its_code()
    {
        var bench = await OverRunAsync();

        var proposal = await bench.PrepareAsync(bench.Request(reasons: " "));

        Assert.NotNull(proposal.ProblemTexts);
        Assert.Contains(UserText.Of(DebateUserTexts.ChallengeProblemReasonsMissing), proposal.ProblemTexts);
    }

    [Fact]
    public async Task Accepting_a_revision_that_closed_nothing_is_refused_with_its_code()
    {
        var bench = await OverRunAsync();

        var refusal = await Assert.ThrowsAsync<UserTextException>(() => bench.AcceptAsync("v-nada"));

        Assert.Equal(DebateUserTexts.ChallengeNothingToAccept, refusal.Text.Code);
        Assert.Equal("v-nada", refusal.Text.Arguments[0]);
    }
}
