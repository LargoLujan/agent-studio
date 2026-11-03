using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

/// <summary>
/// docs/15 D-01 and D-04: the state of a debate is derived by replaying the log through the public API of
/// <see cref="DebateSession"/>, so a log the domain would not have allowed cannot be resumed.
/// </summary>
public sealed class DebateStateReplayerTests
{
    private static readonly JudgeIdentity CodexJudge = new(Aid("juez-revision"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI));

    private static readonly FrozenRequirementSet Requirements =
        FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Mandatory, "the best design", Crit("c1", VerificationMethod.JudgeAssessment, "el diseño más mantenible"))], T0);

    private static DebateParticipant Claude(string agent, DebateRole role) => new(Aid(agent), role, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"));

    private static readonly IReadOnlyList<DebateParticipant> Pair = [Claude("arquitecto", DebateRole.Proponent), Claude("critico", DebateRole.Critic)];

    private static readonly JudgingArrangement Independent =
        new(DebateJudgingKind.IndependentJudge, [CodexJudge], new HashSet<VendorId>(), new HashSet<VendorId>());

    private static DebateHeader Header(DebateMode mode = DebateMode.Adversarial, IReadOnlyList<DebateParticipant>? participants = null, DebateLimits? limits = null, JudgingArrangement? judging = null) =>
        new(new DebateId("deb-20261019-a1b2c3"), mode, DebateSubjectKind.None, "¿Qué diseño?", Requirements, participants ?? Pair, judging ?? Independent, limits ?? DebateLimits.Default, T0);

    private static DebateSession Live(DebateHeader header) =>
        DebateSession.Open(header.Id, header.Mode, header.Subject, header.Question, header.Requirements, header.Participants, header.Judging, header.Limits, header.StartedAt);

    /// <summary>A contribution as the coordinator publishes it: its content is the reference to its evidence, never the text.</summary>
    private static DebateContribution Said(string author, int round, ContributionKind kind, int? repliesTo = null, int seconds = 1) =>
        new(round, Aid(author), kind, $"EV-{author}-{round}", [], T0.AddSeconds(seconds), repliesTo);

    private static Verdict VerdictOf(VerdictDecision decision) =>
        ValidVerdict(Dossier(), decision) with { Judge = CodexJudge, RequirementSetHash = Requirements.ContentHash, Resolution = new DebateResolution("P1", []) };

    private static void AssertSameState(DebateSession live, DebateSession replayed)
    {
        Assert.Equal(
            (live.Phase, live.Outcome, live.CurrentRound, live.LimitReached, live.JudgmentReason, live.CloseReason),
            (replayed.Phase, replayed.Outcome, replayed.CurrentRound, replayed.LimitReached, replayed.JudgmentReason, replayed.CloseReason));
        Assert.Equal(live.Contributions, replayed.Contributions);
        Assert.Equal(live.Verdicts, replayed.Verdicts);
        Assert.Equal(live.Panel, replayed.Panel);
        Assert.Equal(live.Judging, replayed.Judging);
    }

    [Fact]
    public void A_log_with_no_event_replays_into_the_debate_just_opened()
    {
        var replay = DebateStateReplayer.Replay(Header(), []);

        Assert.True(replay.IsResumable);
        Assert.Empty(replay.Problems);
        AssertSameState(Live(Header()), replay.Session!);
    }

    [Fact]
    public void A_debate_replays_into_the_state_it_reached_live()
    {
        var header = Header(limits: new DebateLimits(2, TimeSpan.FromMinutes(30), 400_000));
        var live = Live(header);
        var verdict = VerdictOf(VerdictDecision.Approved);
        var outcome = JudgingPanel.Combine(header.Judging, [(verdict, true)]);
        var contributions = new[]
        {
            Said("arquitecto", 1, ContributionKind.Proposal),
            Said("critico", 1, ContributionKind.Proposal, seconds: 2),
            Said("critico", 2, ContributionKind.Objection, repliesTo: 0, seconds: 90),
            Said("arquitecto", 2, ContributionKind.Rebuttal, repliesTo: 2, seconds: 95),
        };

        live.Submit(contributions[0]);
        live.Submit(contributions[1]);
        live.AdvanceRound(T0.AddMinutes(1), 100_000);
        live.Submit(contributions[2]);
        live.Submit(contributions[3]);
        live.AdvanceRound(T0.AddMinutes(3), 250_000);
        live.Close(outcome, [verdict]);

        var replay = DebateStateReplayer.Replay(header,
        [
            new ContributionPublished(T0.AddMinutes(1), contributions[0]),
            new ContributionPublished(T0.AddMinutes(1), contributions[1]),
            new RoundClosed(T0.AddMinutes(1), 1, 100_000, null),
            new ContributionPublished(T0.AddMinutes(3), contributions[2]),
            new ContributionPublished(T0.AddMinutes(3), contributions[3]),
            new RoundClosed(T0.AddMinutes(3), 2, 250_000, DebateJudgmentReason.Limit, DebateLimitReached.Rounds),
            new DebateEnded(T0.AddMinutes(5), outcome, [verdict]),
        ]);

        Assert.True(replay.IsResumable);
        AssertSameState(live, replay.Session!);
        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved, DebateLimitReached.Rounds), (replay.Session!.Phase, replay.Session.Outcome, replay.Session.LimitReached));
    }

    [Fact]
    public void A_header_the_domain_does_not_allow_leaves_no_debate_to_resume()
    {
        var sharedVendor = new JudgingArrangement(DebateJudgingKind.IndependentJudge, [new JudgeIdentity(Aid("juez-claude"), Pair[0].Model)], new HashSet<VendorId>(), new HashSet<VendorId>());

        var replay = DebateStateReplayer.Replay(Header(judging: sharedVendor), [new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal))]);

        Assert.Null(replay.Session);
        Assert.False(replay.IsResumable);
        Assert.Contains("debate.judge-participant-not-waived", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_the_log_closed_without_judgment_where_the_domain_reaches_a_limit_is_a_problem()
    {
        var header = Header(limits: new DebateLimits(1, TimeSpan.FromMinutes(30), 400_000));

        var replay = DebateStateReplayer.Replay(header,
        [
            new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal)),
            new ContributionPublished(T0, Said("critico", 1, ContributionKind.Proposal)),
            new RoundClosed(T0.AddMinutes(1), 1, 0, null),
        ]);

        Assert.False(replay.IsResumable);
        Assert.StartsWith("Event 3:", Assert.Single(replay.Problems), StringComparison.Ordinal);
        Assert.Contains("Rounds", replay.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_closed_that_is_not_the_current_one_is_a_problem()
    {
        var replay = DebateStateReplayer.Replay(Header(),
        [
            new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal)),
            new ContributionPublished(T0, Said("critico", 1, ContributionKind.Proposal)),
            new RoundClosed(T0.AddMinutes(1), 2, 0, null),
        ]);

        Assert.StartsWith("Event 3:", Assert.Single(replay.Problems), StringComparison.Ordinal);
        Assert.Equal((DebatePhase.Proposals, 1), (replay.Session!.Phase, replay.Session.CurrentRound));
    }

    [Fact]
    public void A_contribution_the_domain_refuses_stops_the_replay_at_the_state_before_it()
    {
        var replay = DebateStateReplayer.Replay(Header(),
        [
            new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal)),
            new ContributionPublished(T0, Said("critico", 1, ContributionKind.Objection, repliesTo: 0)),
            new ContributionPublished(T0, Said("critico", 1, ContributionKind.Proposal)),
        ]);

        var problem = Assert.Single(replay.Problems);
        Assert.StartsWith("Event 2:", problem, StringComparison.Ordinal);
        Assert.Contains("debate.independent-proposals", problem, StringComparison.Ordinal);
        Assert.Single(replay.Session!.Contributions);
    }

    /// <summary>D-20: the cut a projection of the tokens decides before a round, which the domain alone would not see.</summary>
    [Fact]
    public void A_cut_decided_by_the_projection_of_the_tokens_replays_as_a_judgment_for_tokens()
    {
        var replay = DebateStateReplayer.Replay(Header(),
        [
            new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal)),
            new ContributionPublished(T0, Said("critico", 1, ContributionKind.Proposal)),
            new RoundClosed(T0.AddMinutes(2), 1, 300_000, DebateJudgmentReason.Limit, DebateLimitReached.Tokens),
        ]);

        Assert.True(replay.IsResumable);
        Assert.Equal((DebatePhase.AwaitingJudgment, DebateLimitReached.Tokens, DebateJudgmentReason.Limit, 1), (replay.Session!.Phase, replay.Session.LimitReached, replay.Session.JudgmentReason!.Value, replay.Session.CurrentRound));
    }

    [Fact]
    public void A_tribunal_replays_into_its_synthesis()
    {
        var header = Header(DebateMode.Tribunal, [Claude("panel-1", DebateRole.Panelist), Claude("panel-2", DebateRole.Panelist)]);

        var replay = DebateStateReplayer.Replay(header,
        [
            new ContributionPublished(T0, Said("panel-1", 1, ContributionKind.Assessment)),
            new ContributionPublished(T0, Said("panel-2", 1, ContributionKind.Assessment)),
            new RoundClosed(T0.AddMinutes(1), 1, 80_000, DebateJudgmentReason.Tribunal),
        ]);

        Assert.True(replay.IsResumable);
        Assert.Equal((DebatePhase.AwaitingJudgment, DebateJudgmentReason.Tribunal), (replay.Session!.Phase, replay.Session.JudgmentReason!.Value));
    }

    [Fact]
    public void A_tribunal_that_the_log_sends_to_synthesis_with_an_assessment_missing_is_a_problem()
    {
        var header = Header(DebateMode.Tribunal, [Claude("panel-1", DebateRole.Panelist), Claude("panel-2", DebateRole.Panelist)]);

        var replay = DebateStateReplayer.Replay(header,
        [
            new ContributionPublished(T0, Said("panel-1", 1, ContributionKind.Assessment)),
            new RoundClosed(T0.AddMinutes(1), 1, 80_000, DebateJudgmentReason.Tribunal),
        ]);

        Assert.Contains("debate.missing-proposals", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_aborted_debate_replays_aborted_and_nothing_is_accepted_after_it()
    {
        var replay = DebateStateReplayer.Replay(Header(),
        [
            new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal)),
            new DebateAborted(T0.AddMinutes(1), "Lo abortaste tú."),
            new ContributionPublished(T0.AddMinutes(2), Said("critico", 1, ContributionKind.Proposal)),
        ]);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Aborted, "Lo abortaste tú."), (replay.Session!.Phase, replay.Session.Outcome, replay.Session.CloseReason));
        Assert.StartsWith("Event 3:", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_event_is_a_problem()
    {
        var replay = DebateStateReplayer.Replay(Header(), [null!]);

        Assert.StartsWith("Event 1:", Assert.Single(replay.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_debate_closed_by_a_person_replays_closed_by_a_person()
    {
        var replay = DebateStateReplayer.Replay(Header(),
        [
            new ContributionPublished(T0, Said("arquitecto", 1, ContributionKind.Proposal)),
            new RoundClosed(T0.AddMinutes(1), 1, 50_000, DebateJudgmentReason.TurnMissing),
            new DebateEnded(T0.AddMinutes(2), JudgingPanel.DecidedByUser(VerdictDecision.Rejected, null), []),
        ]);

        Assert.True(replay.IsResumable);
        Assert.Equal((DebateOutcome.Resolved, JudgingPanel.User), (replay.Session!.Outcome, replay.Session.Panel!.Rule));
    }

    /// <summary>
    /// docs/15 §8, «Reproducción»: about 200 random debates by seed (modes, participants, rounds, limits, early judgments,
    /// missing turns and ends). Each one is driven live through the public API while its events are written down, as the
    /// coordinator does, and replaying those events gives exactly the state the live debate reached.
    /// </summary>
    [Fact]
    public void Random_debates_replay_into_the_same_state_they_reached_live()
    {
        DebateMode[] modes = [DebateMode.Free, DebateMode.Adversarial, DebateMode.ArchitectureReview, DebateMode.Tribunal];
        ContributionKind[] deliberation = [ContributionKind.Objection, ContributionKind.Rebuttal, ContributionKind.Evidence];
        var ends = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var mode = modes[random.Next(modes.Length)];
            var count = random.Next(2, 4);
            var participants = Enumerable.Range(0, count).Select(i => Claude($"p{i}", mode switch
            {
                DebateMode.Tribunal => DebateRole.Panelist,
                DebateMode.Adversarial => i == 0 ? DebateRole.Proponent : DebateRole.Critic,
                _ => (DebateRole)random.Next(3),
            })).ToList();
            var header = Header(mode, participants, new DebateLimits(random.Next(1, 5), TimeSpan.FromMinutes(30), 400_000));
            var live = Live(header);
            var events = new List<DebateEvent>();
            var at = T0;
            long tokens = 0;

            while (live.Phase is DebatePhase.Proposals or DebatePhase.Deliberation)
            {
                var round = live.CurrentRound;
                var skipped = false;
                foreach (var participant in participants)
                {
                    if (round == 1 && random.Next(12) == 0)
                    {
                        skipped = true; // the turn failed twice: a person decides (D-22)
                        continue;
                    }

                    at = at.AddSeconds(random.Next(1, 30));
                    var kind = round == 1 ? (mode == DebateMode.Tribunal ? ContributionKind.Assessment : ContributionKind.Proposal) : deliberation[random.Next(deliberation.Length)];
                    int? repliesTo = round > 1 && random.Next(2) == 0 ? random.Next(live.Contributions.Count) : null;
                    var said = new DebateContribution(round, participant.Agent, kind, $"EV-{seed}-{live.Contributions.Count}", [], at, repliesTo);
                    live.Submit(said);
                    events.Add(new ContributionPublished(at, said));
                }

                tokens += random.Next(10_000, 150_000);
                if (skipped)
                {
                    if (live.Contributions.Count == 0)
                    {
                        live.Abort("Ningún participante respondió.");
                        events.Add(new DebateAborted(at, "Ningún participante respondió."));
                        break;
                    }

                    live.RequestJudgment(DebateJudgmentReason.TurnMissing);
                    events.Add(new RoundClosed(at, round, tokens, DebateJudgmentReason.TurnMissing));
                    break;
                }

                switch (random.Next(6))
                {
                    case 0 when mode != DebateMode.Tribunal:
                        live.RequestJudgment(DebateJudgmentReason.Requested);
                        events.Add(new RoundClosed(at, round, tokens, DebateJudgmentReason.Requested));
                        break;
                    case 1 when mode != DebateMode.Tribunal:
                        live.RequestJudgment(DebateJudgmentReason.Converged);
                        events.Add(new RoundClosed(at, round, tokens, DebateJudgmentReason.Converged));
                        break;
                    case 2:
                        live.RequestJudgment(DebateJudgmentReason.Limit, DebateLimitReached.Tokens);
                        events.Add(new RoundClosed(at, round, tokens, DebateJudgmentReason.Limit, DebateLimitReached.Tokens));
                        break;
                    default:
                        live.AdvanceRound(at, tokens);
                        events.Add(live.Phase == DebatePhase.AwaitingJudgment
                            ? new RoundClosed(at, round, tokens, live.JudgmentReason, live.LimitReached)
                            : new RoundClosed(at, round, tokens, null));
                        break;
                }
            }

            if (live.Phase == DebatePhase.AwaitingJudgment)
            {
                at = at.AddMinutes(1);
                var decision = (VerdictDecision)random.Next(5);
                var verdict = VerdictOf(decision);
                switch (random.Next(4))
                {
                    case 0:
                        live.Close(JudgingPanel.DecidedByUser(random.Next(2) == 0 ? decision : null, null), []);
                        events.Add(new DebateEnded(at, live.Panel!, []));
                        break;
                    case 1:
                        live.Abort("Ya no hace falta.");
                        events.Add(new DebateAborted(at, "Ya no hace falta."));
                        break;
                    default:
                        var outcome = JudgingPanel.Combine(header.Judging, [(verdict, random.Next(3) != 0)]);
                        live.Close(outcome, outcome.Rule == JudgingPanel.Single ? [verdict] : []);
                        events.Add(new DebateEnded(at, outcome, live.Verdicts));
                        break;
                }
            }

            var replay = DebateStateReplayer.Replay(header, events);

            Assert.True(replay.IsResumable, $"seed {seed}: {string.Join(" | ", replay.Problems)}");
            AssertSameState(live, replay.Session!);
            var end = $"{live.Outcome}/{live.JudgmentReason}";
            ends[end] = ends.GetValueOrDefault(end) + 1;
        }

        Assert.True(ends.Count >= 8, $"The random debates reached only {ends.Count} kinds of end: {string.Join(", ", ends.Keys)}.");
    }
}
