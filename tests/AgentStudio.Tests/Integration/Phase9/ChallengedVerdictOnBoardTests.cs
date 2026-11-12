using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Work;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Tests.Integration.Phase5;

namespace AgentStudio.Tests.Integration.Phase9;

/// <summary>
/// docs/15 D-40 and docs/04 §23.6 with the product writing the log: a verdict challenged through the real service, its revision judged in the real arena by a scripted
/// judge, and the board reading what was really written (the verdict of the revision kept in the run of the original, superseding it, announced before the close). A revision
/// that turns a review that needed a person towards an approval keeps the card in review until the person accepts it, as the delivery does. The unit tests of the board
/// write that verdict by hand: this test is there so that their format cannot drift from the product's.
/// </summary>
[Collection(Phase9Collection.Name)]
[Trait("Category", "LocalTools")]
public sealed class ChallengedVerdictOnBoardTests
{
    private const string Reasons = "La prueba solo comprueba que el fichero existe, no lo que hace la función.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_revision_that_turns_a_review_towards_an_approval_keeps_the_card_in_review_until_the_person_accepts_it()
    {
        // The judge of the run leaves it to a person; the judge of the revision approves.
        var clis = new Phase5Clis
        {
            Executor = Phase9Runs.Executor,
            Decision = r => DebateIds.IsDebate(r.DebateId) ? VerdictDecision.Approved : VerdictDecision.EscalateToHuman,
        };
        await using var world = await AppWorld.OpenAsync(clis: clis);
        var project = world.App.Project;
        var challenges = world.Get<IVerdictChallenges>();
        var board = world.Get<IBoardProjector>();
        var run = await Phase9Runs.RunAsync(world, Phase5Runs.Reviewed(), Phase5Runs.Profile());
        var verdict = run.VerdictId!;
        var milestone = (await Phase9Runs.OpenMilestoneAsync(world)).Header.Id;
        await Phase9Runs.LinkRunAsync(world, milestone, run.Id);

        async Task<IReadOnlyList<BoardCard>> CardsAsync() => (await board.ProjectAsync(project, milestone, Ct)).Requirements;

        Assert.NotEmpty(await CardsAsync());
        Assert.DoesNotContain(await CardsAsync(), c => c.Column == BoardColumn.Done);

        await using var revision = await challenges.ChallengeAsync(await PrepareAsync(world, verdict), Ct);
        var revised = await Phase5Checks.EndAsync(revision);
        Assert.True(revised is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved }, revised.Summary);

        // The revision approved, nobody accepted it: nothing is done and the cards say why.
        Assert.Equal(ChallengeStatus.AwaitingAcceptance, await challenges.StatusAsync(project, verdict, Ct));
        var waiting = await CardsAsync();
        Assert.DoesNotContain(waiting, c => c.Column == BoardColumn.Done);
        Assert.Contains(waiting, c => c.Column == BoardColumn.InReview && c.Reason == BoardCardReasons.Challenged);

        // The person accepts it, through the gate: now what the revision approved is done.
        var kept = Assert.Single(await world.Get<IVerdictStore>().ListAsync(project.ProjectId, run.Id.Value, Ct), v => v.Supersedes == verdict);
        await challenges.AcceptRevisionAsync(project, kept.Id, Ct);
        Assert.Equal(ChallengeStatus.Accepted, await challenges.StatusAsync(project, verdict, Ct));
        Assert.DoesNotContain(await CardsAsync(), c => c.Column == BoardColumn.InReview);
        Assert.Contains(await CardsAsync(), c => c.Column == BoardColumn.Done);
    }

    private static async Task<DebateProposal> PrepareAsync(AppWorld world, string verdict)
    {
        var request = new DebateRequest(
            world.App.Project,
            DebateMode.Adversarial,
            Reasons,
            AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures.Requirements(),
            new DebateSubject(DebateSubjectKind.Verdict, verdict),
            [new DebateParticipantRequest(Phase5Roster.Architect, DebateRole.Proponent), new DebateParticipantRequest(Phase5Roster.GreenCritic, DebateRole.Critic)],
            [],
            DebateJudgingKind.IndependentJudge,
            new DebateLimits(1, TimeSpan.FromMinutes(30), 400_000));
        var proposal = await world.Get<IVerdictChallenges>().PrepareAsync(new VerdictChallengeRequest(world.App.Project, verdict, Reasons, request), Ct);
        Assert.True(proposal.Problems.Count == 0, string.Join(" | ", proposal.Problems));
        return proposal;
    }
}
