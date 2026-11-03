using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class DebateSessionTests
{
    private static readonly DebateLimits NormalLimits = new(3, TimeSpan.FromMinutes(30), 1000);

    private static DebateSession Open(
        DebateMode mode = DebateMode.Adversarial,
        string question = "Which design?",
        IReadOnlyList<string>? criteria = null,
        IReadOnlyList<DebateParticipant>? participants = null,
        JudgeIdentity? judge = null,
        DebateLimits? limits = null,
        bool requireIndependentJudge = true) =>
        DebateSession.Open(
            new DebateId("d1"),
            mode,
            question,
            criteria ?? ["testable"],
            Frozen(),
            participants ?? [Participant("a", DebateRole.Proponent, VendorId.Anthropic), Participant("b", DebateRole.Critic, VendorId.Google)],
            judge ?? Judge(),
            limits ?? NormalLimits,
            T0,
            requireIndependentJudge);

    private static void SubmitRound1(DebateSession session)
    {
        var kind = session.Mode == DebateMode.Tribunal ? ContributionKind.Assessment : ContributionKind.Proposal;
        foreach (var p in session.Participants)
        {
            session.Submit(Contribution(p.Agent.Value, 1, kind));
        }
    }

    // ---- Open ----

    [Fact]
    public void A_well_formed_debate_opens_in_round_one_with_proposals()
    {
        var session = Open();

        Assert.Equal(DebatePhase.Proposals, session.Phase);
        Assert.Equal(DebateOutcome.Open, session.Outcome);
        Assert.Equal(1, session.CurrentRound);
        Assert.Equal(DebateLimitReached.None, session.LimitReached);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_question_is_rejected(string question)
    {
        Assert.Equal("debate.question-required", Violation(() => Open(question: question)).Rule);
    }

    [Fact]
    public void Missing_or_blank_criteria_are_rejected()
    {
        Assert.Equal("debate.criteria-required", Violation(() => Open(criteria: [])).Rule);
        Assert.Equal("debate.criteria-required", Violation(() => Open(criteria: ["ok", " "])).Rule);
    }

    [Theory]
    [InlineData(0, 30, 1000L)]
    [InlineData(-1, 30, 1000L)]
    [InlineData(3, 0, 1000L)]
    [InlineData(3, -5, 1000L)]
    [InlineData(3, 30, 0L)]
    [InlineData(3, 30, -1L)]
    public void Non_positive_limits_are_rejected(int rounds, int minutes, long tokens)
    {
        var limits = new DebateLimits(rounds, TimeSpan.FromMinutes(minutes), tokens);

        Assert.Equal("debate.limits-must-be-positive", Violation(() => Open(limits: limits)).Rule);
    }

    [Fact]
    public void A_debate_with_no_token_limit_is_allowed()
    {
        var session = Open(limits: new DebateLimits(3, TimeSpan.FromMinutes(5)));

        Assert.Null(session.Limits.MaxTotalTokens);
    }

    [Fact]
    public void At_least_two_participants_are_needed()
    {
        var one = new[] { Participant("a", DebateRole.Proponent, VendorId.Anthropic) };

        Assert.Equal("debate.two-participants", Violation(() => Open(participants: one)).Rule);
        Assert.Equal("debate.two-participants", Violation(() => Open(participants: [])).Rule);
    }

    [Fact]
    public void Participants_must_be_distinct_agents()
    {
        var twice = new[] { Participant("a", DebateRole.Proponent, VendorId.Anthropic), Participant("a", DebateRole.Critic, VendorId.Google) };

        Assert.Equal("debate.duplicate-participant", Violation(() => Open(participants: twice)).Rule);
    }

    [Fact]
    public void The_judge_cannot_be_a_participant()
    {
        var participants = new[] { Participant("a", DebateRole.Proponent, VendorId.Anthropic), Participant("judge", DebateRole.Critic, VendorId.Google) };

        Assert.Equal("debate.judge-not-participant", Violation(() => Open(participants: participants)).Rule);
    }

    [Theory]
    [InlineData(DebateMode.Adversarial)]
    [InlineData(DebateMode.ImplementationAudit)]
    public void Adversarial_modes_need_a_proponent_and_a_critic(DebateMode mode)
    {
        var onlyProponents = new[] { Participant("a", DebateRole.Proponent, VendorId.Anthropic), Participant("b", DebateRole.Proponent, VendorId.Google) };
        var onlyCritics = new[] { Participant("a", DebateRole.Critic, VendorId.Anthropic), Participant("b", DebateRole.Critic, VendorId.Google) };

        Assert.Equal("debate.adversarial-roles", Violation(() => Open(mode, participants: onlyProponents)).Rule);
        Assert.Equal("debate.adversarial-roles", Violation(() => Open(mode, participants: onlyCritics)).Rule);
        Assert.NotNull(Open(mode));
    }

    [Fact]
    public void A_tribunal_accepts_only_panelists()
    {
        var mixed = new[] { Participant("a", DebateRole.Panelist, VendorId.Anthropic), Participant("b", DebateRole.Critic, VendorId.Google) };
        var panel = new[] { Participant("a", DebateRole.Panelist, VendorId.Anthropic), Participant("b", DebateRole.Panelist, VendorId.Google) };

        Assert.Equal("debate.tribunal-roles", Violation(() => Open(DebateMode.Tribunal, participants: mixed)).Rule);
        Assert.NotNull(Open(DebateMode.Tribunal, participants: panel));
    }

    [Fact]
    public void The_judge_must_run_on_a_different_vendor_than_every_participant()
    {
        Assert.Equal("debate.judge-independence", Violation(() => Open(judge: Judge(VendorId.Google))).Rule);
        Assert.Equal("debate.judge-independence", Violation(() => Open(judge: Judge(VendorId.Anthropic))).Rule);
    }

    [Fact]
    public void Judge_independence_can_be_switched_off()
    {
        Assert.NotNull(Open(judge: Judge(VendorId.Google), requireIndependentJudge: false));
    }

    // ---- round one ----

    [Fact]
    public void Round_one_accepts_one_independent_proposal_per_participant()
    {
        var session = OpenDebate();
        session.Submit(Contribution("a", 1, ContributionKind.Proposal));

        Assert.Equal("debate.one-proposal", Violation(() => session.Submit(Contribution("a", 1, ContributionKind.Proposal))).Rule);
        Assert.Single(session.Contributions);
    }

    [Theory]
    [InlineData(ContributionKind.Objection)]
    [InlineData(ContributionKind.Rebuttal)]
    [InlineData(ContributionKind.Evidence)]
    [InlineData(ContributionKind.Assessment)]
    public void Round_one_of_a_normal_debate_only_accepts_proposals(ContributionKind kind)
    {
        var session = OpenDebate();

        Assert.Equal("debate.independent-proposals", Violation(() => session.Submit(Contribution("a", 1, kind))).Rule);
    }

    [Fact]
    public void Round_one_of_a_tribunal_only_accepts_assessments()
    {
        var session = OpenDebate(DebateMode.Tribunal);

        Assert.Equal("debate.independent-proposals", Violation(() => session.Submit(Contribution("a", 1, ContributionKind.Proposal))).Rule);
        session.Submit(Contribution("a", 1, ContributionKind.Assessment));
        Assert.Single(session.Contributions);
    }

    [Fact]
    public void Round_one_proposals_cannot_reply_to_anything()
    {
        var session = OpenDebate();
        session.Submit(Contribution("a", 1, ContributionKind.Proposal));

        Assert.Equal("debate.independent-proposals", Violation(() => session.Submit(Contribution("b", 1, ContributionKind.Proposal, repliesTo: 0))).Rule);
        Assert.Single(session.Contributions);
    }

    [Fact]
    public void Only_participants_can_contribute_and_only_to_the_current_round()
    {
        var session = OpenDebate();

        Assert.Equal("debate.author-not-participant", Violation(() => session.Submit(Contribution("stranger", 1, ContributionKind.Proposal))).Rule);
        Assert.Equal("debate.author-not-participant", Violation(() => session.Submit(Contribution("judge", 1, ContributionKind.Proposal))).Rule);
        Assert.Equal("debate.wrong-round", Violation(() => session.Submit(Contribution("a", 2, ContributionKind.Proposal))).Rule);
    }

    // ---- advancing ----

    [Fact]
    public void AdvanceRound_requires_every_participant_to_have_contributed_in_round_one()
    {
        var session = OpenDebate();
        session.Submit(Contribution("a", 1, ContributionKind.Proposal));

        Assert.Equal("debate.missing-proposals", Violation(() => session.AdvanceRound(T0, 0)).Rule);
        Assert.Equal(DebatePhase.Proposals, session.Phase);
        Assert.Equal(1, session.CurrentRound);
    }

    [Fact]
    public void AdvanceRound_moves_to_deliberation_when_no_limit_is_reached()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.AdvanceRound(T0.AddMinutes(1), 10);

        Assert.Equal(DebatePhase.Deliberation, session.Phase);
        Assert.Equal(2, session.CurrentRound);
        Assert.Equal(DebateLimitReached.None, session.LimitReached);
    }

    [Fact]
    public void Deliberation_accepts_objections_rebuttals_and_evidence_but_not_proposals()
    {
        var session = OpenDebate();
        SubmitRound1(session);
        session.AdvanceRound(T0, 0);

        session.Submit(Contribution("b", 2, ContributionKind.Objection, repliesTo: 0));
        session.Submit(Contribution("a", 2, ContributionKind.Rebuttal, repliesTo: 2));
        session.Submit(Contribution("a", 2, ContributionKind.Evidence));

        Assert.Equal("debate.deliberation-kinds", Violation(() => session.Submit(Contribution("a", 2, ContributionKind.Proposal))).Rule);
        Assert.Equal("debate.deliberation-kinds", Violation(() => session.Submit(Contribution("a", 2, ContributionKind.Assessment))).Rule);
        Assert.Equal("debate.reply-target", Violation(() => session.Submit(Contribution("a", 2, ContributionKind.Rebuttal, repliesTo: 99))).Rule);
        Assert.Equal(5, session.Contributions.Count);
    }

    [Fact]
    public void Reaching_the_round_limit_sends_the_debate_to_judgment()
    {
        var session = OpenDebate(limits: new DebateLimits(2, TimeSpan.FromHours(1), 1000));
        SubmitRound1(session);
        session.AdvanceRound(T0, 0); // round 2 of 2
        session.Submit(Contribution("a", 2, ContributionKind.Objection, repliesTo: 0));

        session.AdvanceRound(T0, 0); // no round 3

        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
        Assert.Equal(DebateLimitReached.Rounds, session.LimitReached);
    }

    [Fact]
    public void A_single_round_debate_goes_to_judgment_right_after_the_proposals()
    {
        var session = OpenDebate(limits: new DebateLimits(1, TimeSpan.FromHours(1), 1000));
        SubmitRound1(session);

        session.AdvanceRound(T0, 0);

        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
        Assert.Equal(DebateLimitReached.Rounds, session.LimitReached);
    }

    [Fact]
    public void Reaching_the_duration_limit_sends_the_debate_to_judgment()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.AdvanceRound(T0 + NormalLimits.MaxDuration + TimeSpan.FromSeconds(1), 0);

        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
        Assert.Equal(DebateLimitReached.Duration, session.LimitReached);
    }

    [Fact]
    public void Reaching_the_duration_limit_exactly_is_not_yet_exceeding_it()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.AdvanceRound(T0 + NormalLimits.MaxDuration, 0);

        Assert.Equal(DebatePhase.Deliberation, session.Phase);
    }

    [Fact]
    public void Exceeding_the_token_limit_sends_the_debate_to_judgment()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.AdvanceRound(T0, 1001);

        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
        Assert.Equal(DebateLimitReached.Tokens, session.LimitReached);
    }

    [Fact]
    public void Using_exactly_the_token_limit_does_not_stop_the_debate()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.AdvanceRound(T0, 1000);

        Assert.Equal(DebatePhase.Deliberation, session.Phase);
    }

    [Fact]
    public void A_tribunal_goes_straight_to_judgment_with_no_limit_flagged()
    {
        var session = OpenDebate(DebateMode.Tribunal);
        SubmitRound1(session);

        session.AdvanceRound(T0, 0);

        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
        Assert.Equal(DebateLimitReached.None, session.LimitReached);
        Assert.Equal(1, session.CurrentRound);
    }

    [Fact]
    public void Nothing_more_is_accepted_once_the_debate_awaits_judgment()
    {
        var session = OpenDebate(DebateMode.Tribunal);
        SubmitRound1(session);
        session.AdvanceRound(T0, 0);

        Violation(() => session.Submit(Contribution("a", 1, ContributionKind.Assessment)));
        Violation(() => session.AdvanceRound(T0, 0));
    }

    [Fact]
    public void Judgment_can_be_requested_early()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.RequestJudgment();

        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
    }

    // ---- closing ----

    private static DebateSession ReadyForJudgment(DebateMode mode = DebateMode.Adversarial)
    {
        var session = OpenDebate(mode);
        SubmitRound1(session);
        session.RequestJudgment();
        return session;
    }

    [Theory]
    [InlineData(VerdictDecision.Approved, DebateOutcome.Resolved)]
    [InlineData(VerdictDecision.ChangesRequested, DebateOutcome.Resolved)]
    [InlineData(VerdictDecision.Rejected, DebateOutcome.Resolved)]
    [InlineData(VerdictDecision.Unresolved, DebateOutcome.Unresolved)]
    [InlineData(VerdictDecision.EscalateToHuman, DebateOutcome.EscalatedToUser)]
    public void Close_maps_the_verdict_decision_to_the_outcome(VerdictDecision decision, DebateOutcome outcome)
    {
        var session = ReadyForJudgment();
        var verdict = DebateVerdict(session, decision);

        session.Close(verdict);

        Assert.Equal(DebatePhase.Closed, session.Phase);
        Assert.Equal(outcome, session.Outcome);
        Assert.Same(verdict, session.Verdict);
        Assert.Equal(verdict.Summary, session.CloseReason);
    }

    [Theory]
    [InlineData(DebatePhase.Proposals)]
    [InlineData(DebatePhase.Deliberation)]
    public void Close_is_only_possible_while_awaiting_judgment(DebatePhase phase)
    {
        var session = OpenDebate();
        if (phase == DebatePhase.Deliberation)
        {
            SubmitRound1(session);
            session.AdvanceRound(T0, 0);
        }

        Assert.Equal("debate.close-needs-judgment-phase", Violation(() => session.Close(DebateVerdict(session, VerdictDecision.Approved))).Rule);
        Assert.Equal(DebateOutcome.Open, session.Outcome);
    }

    [Fact]
    public void Close_rejects_a_verdict_from_another_judge()
    {
        var session = ReadyForJudgment();

        Assert.Equal("debate.verdict-from-other-judge", Violation(() => session.Close(DebateVerdict(session, VerdictDecision.Approved, judgeAgent: "someone-else"))).Rule);
        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
    }

    [Fact]
    public void Close_rejects_a_verdict_whose_judge_runs_on_another_vendor_or_provider()
    {
        var session = ReadyForJudgment();
        var sameAgentOtherVendor = DebateVerdict(session, VerdictDecision.Approved) with { Judge = Judge(VendorId.Anthropic) };
        var sameAgentOtherProvider = DebateVerdict(session, VerdictDecision.Approved) with
        {
            Judge = new JudgeIdentity(Aid("judge"), new ModelBinding(new ProviderId("other-provider"), VendorId.OpenAI)),
        };

        Assert.Equal("debate.verdict-from-other-judge", Violation(() => session.Close(sameAgentOtherVendor)).Rule);
        Assert.Equal("debate.verdict-from-other-judge", Violation(() => session.Close(sameAgentOtherProvider)).Rule);
        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
    }

    /// <summary>
    /// docs/15 D-26: the judge is compared by agent, provider and vendor, never by model. The system puts in the verdict the
    /// model the session of the CLI reported, which on Claude is the full name (<c>claude-haiku-4-5-…</c>), not the alias
    /// configured (<c>haiku</c>): comparing it would keep the real verdict of a judge on Claude from ever closing a debate.
    /// </summary>
    [Fact]
    public void Close_accepts_the_verdict_of_the_same_judge_whatever_model_its_cli_reported()
    {
        var session = ReadyForJudgment();
        var judge = session.Judge;
        var reported = DebateVerdict(session, VerdictDecision.Approved) with
        {
            Judge = judge with { Model = judge.Model with { Model = "claude-haiku-4-5-20251001" } },
        };

        session.Close(reported);

        Assert.Equal(DebateOutcome.Resolved, session.Outcome);
        Assert.Same(reported, session.Verdict);
    }

    [Fact]
    public void RequestJudgment_rejects_a_debate_without_contributions()
    {
        var session = OpenDebate();

        Assert.Equal("debate.nothing-to-judge", Violation(() => session.RequestJudgment()).Rule);
        Assert.Equal(DebatePhase.Proposals, session.Phase);
    }

    [Fact]
    public void Submit_rejects_a_contribution_dated_after_the_time_limit()
    {
        var session = OpenDebate();
        var late = Contribution("a", 1, ContributionKind.Proposal) with { At = T0 + NormalLimits.MaxDuration + TimeSpan.FromSeconds(1) };
        var onTime = Contribution("a", 1, ContributionKind.Proposal) with { At = T0 + NormalLimits.MaxDuration };

        Assert.Equal("debate.time-limit", Violation(() => session.Submit(late)).Rule);
        Assert.Empty(session.Contributions);
        session.Submit(onTime);
        Assert.Single(session.Contributions);
    }

    [Fact]
    public void Close_rejects_a_verdict_about_a_different_requirement_set()
    {
        var session = ReadyForJudgment();

        Assert.Equal("debate.verdict-requirements", Violation(() => session.Close(DebateVerdict(session, VerdictDecision.Approved, requirementsHash: HashA))).Rule);
        Assert.Null(session.Verdict);
    }

    [Fact]
    public void A_closed_debate_cannot_be_closed_again()
    {
        var session = ReadyForJudgment();
        session.Close(DebateVerdict(session, VerdictDecision.Approved));

        Violation(() => session.Close(DebateVerdict(session, VerdictDecision.Rejected)));
    }

    // ---- aborting ----

    [Fact]
    public void Abort_closes_the_debate_as_aborted()
    {
        var session = OpenDebate();

        session.Abort("user cancelled");

        Assert.Equal(DebatePhase.Closed, session.Phase);
        Assert.Equal(DebateOutcome.Aborted, session.Outcome);
        Assert.Equal("user cancelled", session.CloseReason);
        Violation(() => session.Submit(Contribution("a", 1, ContributionKind.Proposal)));
    }

    [Fact]
    public void Abort_needs_a_reason_and_cannot_reopen_or_override_a_closed_debate()
    {
        var session = OpenDebate();
        Assert.ThrowsAny<ArgumentException>(() => session.Abort(" "));

        session.Abort("stop");
        Violation(() => session.Abort("again"));
    }

    [Fact]
    public void Abort_is_possible_while_awaiting_judgment()
    {
        var session = ReadyForJudgment();

        session.Abort("no longer needed");

        Assert.Equal(DebateOutcome.Aborted, session.Outcome);
    }

    // ---- Phase 5 (docs/15 §5.1): a subject, a judging arrangement, why it went to judgment, and the panel ----

    private static readonly JudgeIdentity ClaudeJudge = new(Aid("juez-claude"), new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"));

    private static readonly JudgeIdentity CodexJudge = new(Aid("juez-codex"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI));

    private static DebateParticipant Claude(string agent, DebateRole role) => new(Aid(agent), role, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "haiku"));

    private static DebateParticipant Codex(string agent, DebateRole role) => new(Aid(agent), role, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI));

    private static readonly IReadOnlyList<DebateParticipant> OneVendor = [Claude("arquitecto", DebateRole.Proponent), Claude("critico", DebateRole.Critic)];

    private static readonly IReadOnlyList<DebateParticipant> Mixed = [Claude("arquitecto", DebateRole.Proponent), Codex("critico", DebateRole.Critic)];

    /// <summary>Requirements of a debate that chooses: one mandatory requirement the judge assesses.</summary>
    private static FrozenRequirementSet Assessed() =>
        FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Mandatory, "the best design", Crit("c1", VerificationMethod.JudgeAssessment, "elige el diseño más mantenible"))], T0);

    private static JudgingArrangement Arrangement(DebateJudgingKind kind, IReadOnlyList<JudgeIdentity> judges, IReadOnlyList<VendorId>? producers = null, IReadOnlyList<VendorId>? waived = null) =>
        new(kind, judges, (producers ?? []).ToHashSet(), (waived ?? []).ToHashSet());

    private static JudgingArrangement Independent(JudgeIdentity? judge = null) => Arrangement(DebateJudgingKind.IndependentJudge, [judge ?? CodexJudge]);

    private static JudgingArrangement Panel() => Arrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge, CodexJudge], waived: [VendorId.Anthropic, VendorId.OpenAI]);

    private static DebateSession OpenPhase5(
        DebateMode mode = DebateMode.Adversarial,
        DebateSubjectKind subject = DebateSubjectKind.None,
        IReadOnlyList<DebateParticipant>? participants = null,
        JudgingArrangement? judging = null,
        FrozenRequirementSet? requirements = null,
        DebateLimits? limits = null) =>
        DebateSession.Open(
            new DebateId("deb-20261019-a1b2c3"),
            mode,
            subject,
            "¿Qué diseño?",
            requirements ?? Assessed(),
            participants ?? OneVendor,
            judging ?? Independent(),
            limits ?? NormalLimits,
            T0);

    /// <summary>A verdict of <paramref name="judge"/> about the session's requirements, echoing <paramref name="head"/> and choosing <paramref name="chosen"/>.</summary>
    private static Verdict VerdictOf(DebateSession session, JudgeIdentity judge, VerdictDecision decision = VerdictDecision.Approved, string head = "", string? chosen = "P1") =>
        ValidVerdict(Dossier(), decision) with
        {
            Judge = judge,
            RequirementSetHash = session.Requirements.ContentHash,
            DecisionLogHeadHash = head.Length == 0 ? HashB : head,
            Resolution = new DebateResolution(chosen, []),
        };

    private static DebateSession PanelReadyForJudgment()
    {
        var session = OpenPhase5(DebateMode.Free, participants: Mixed, judging: Panel());
        SubmitRound1(session);
        session.RequestJudgment();
        return session;
    }

    [Fact]
    public void A_debate_opens_with_its_subject_and_arrangement_and_takes_its_criteria_from_the_requirements()
    {
        var session = OpenPhase5(subject: DebateSubjectKind.Commit);

        Assert.Equal(DebateSubjectKind.Commit, session.Subject);
        Assert.Equal(Independent(), session.Judging);
        Assert.Equal(CodexJudge, session.Judge);
        Assert.Equal(["R1/c1: elige el diseño más mantenible"], session.AcceptanceCriteria);
        Assert.Equal((DebatePhase.Proposals, 1, (DebateJudgmentReason?)null), (session.Phase, session.CurrentRound, session.JudgmentReason));
    }

    [Fact]
    public void A_debate_whose_requirements_carry_no_acceptance_criterion_is_rejected()
    {
        var desirableOnly = FrozenRequirementSet.Freeze([Req("R1", RequirementPriority.Desirable, "nice to have")], T0);

        Assert.Equal("debate.criteria-required", Violation(() => OpenPhase5(requirements: desirableOnly)).Rule);
    }

    [Fact]
    public void The_arrangement_is_checked_when_the_debate_opens()
    {
        Assert.Equal("debate.judge-participant-not-waived", Violation(() => OpenPhase5(judging: Independent(ClaudeJudge))).Rule);
        Assert.Equal("debate.judge-producer", Violation(() => OpenPhase5(judging: Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge], producers: [VendorId.OpenAI]))).Rule);
        Assert.Equal("debate.panel-in-review", Violation(() => OpenPhase5(DebateMode.ImplementationAudit, DebateSubjectKind.Run, Mixed, Panel())).Rule);
        Assert.Equal("debate.judge-not-participant", Violation(() => OpenPhase5(participants: [Claude("arquitecto", DebateRole.Proponent), Codex("juez-codex", DebateRole.Critic)])).Rule);
    }

    [Theory]
    [InlineData(DebateMode.Free, DebateSubjectKind.Run)]
    [InlineData(DebateMode.Free, DebateSubjectKind.Verdict)]
    [InlineData(DebateMode.ArchitectureReview, DebateSubjectKind.Run)]
    [InlineData(DebateMode.Adversarial, DebateSubjectKind.Run)]
    [InlineData(DebateMode.ImplementationAudit, DebateSubjectKind.None)]
    [InlineData(DebateMode.ImplementationAudit, DebateSubjectKind.Commit)]
    [InlineData(DebateMode.OrchestratorReview, DebateSubjectKind.Commit)]
    [InlineData(DebateMode.OrchestratorReview, DebateSubjectKind.Verdict)]
    public void A_mode_refuses_a_subject_it_does_not_debate(DebateMode mode, DebateSubjectKind subject)
    {
        IReadOnlyList<DebateParticipant> participants = mode == DebateMode.OrchestratorReview
            ? [Claude("revisor-1", DebateRole.Critic), Claude("revisor-2", DebateRole.Panelist)]
            : OneVendor;

        Assert.Equal("debate.subject-mode", Violation(() => OpenPhase5(mode, subject, participants)).Rule);
    }

    [Theory]
    [InlineData(DebateMode.Free, DebateSubjectKind.None)]
    [InlineData(DebateMode.Free, DebateSubjectKind.Commit)]
    [InlineData(DebateMode.Adversarial, DebateSubjectKind.Verdict)]
    [InlineData(DebateMode.ArchitectureReview, DebateSubjectKind.Commit)]
    [InlineData(DebateMode.ImplementationAudit, DebateSubjectKind.Run)]
    [InlineData(DebateMode.OrchestratorReview, DebateSubjectKind.Run)]
    [InlineData(DebateMode.Tribunal, DebateSubjectKind.None)]
    [InlineData(DebateMode.Tribunal, DebateSubjectKind.Run)]
    [InlineData(DebateMode.Tribunal, DebateSubjectKind.Verdict)]
    public void Each_mode_opens_on_the_subjects_it_debates(DebateMode mode, DebateSubjectKind subject)
    {
        IReadOnlyList<DebateParticipant> participants = mode switch
        {
            DebateMode.OrchestratorReview => [Claude("revisor-1", DebateRole.Critic), Claude("revisor-2", DebateRole.Panelist)],
            DebateMode.Tribunal => [Claude("panel-1", DebateRole.Panelist), Claude("panel-2", DebateRole.Panelist)],
            _ => OneVendor,
        };

        Assert.Equal(subject, OpenPhase5(mode, subject, participants).Subject);
    }

    [Fact]
    public void An_orchestrator_review_is_made_of_reviewers_only()
    {
        IReadOnlyList<DebateParticipant> withProponent = [Claude("revisor-1", DebateRole.Proponent), Claude("revisor-2", DebateRole.Critic)];

        Assert.Equal("debate.reviewer-roles", Violation(() => OpenPhase5(DebateMode.OrchestratorReview, DebateSubjectKind.Run, withProponent)).Rule);
    }

    [Fact]
    public void The_phase_0_overload_opens_with_an_independent_judge_or_with_every_participant_vendor_waived()
    {
        var independent = OpenDebate();
        var switchedOff = Open(judge: Judge(VendorId.Google), requireIndependentJudge: false);

        Assert.Equal((DebateJudgingKind.IndependentJudge, DebateSubjectKind.None), (independent.Judging.Kind, independent.Subject));
        Assert.Equal([Judge()], independent.Judging.Judges);
        Assert.Empty(independent.Judging.WaivedParticipantVendors);
        Assert.Empty(independent.Judging.ProducerVendors);
        Assert.Equal([VendorId.Anthropic, VendorId.Google], switchedOff.Judging.WaivedParticipantVendors.OrderBy(v => v.Value, StringComparer.Ordinal));
    }

    [Fact]
    public void Asking_for_judgment_with_no_reason_is_asking_for_it_early()
    {
        var session = OpenDebate();
        SubmitRound1(session);

        session.RequestJudgment();

        Assert.Equal((DebatePhase.AwaitingJudgment, DebateJudgmentReason.Requested, DebateLimitReached.None), (session.Phase, session.JudgmentReason!.Value, session.LimitReached));
    }

    [Theory]
    [InlineData(DebateJudgmentReason.Requested, DebateLimitReached.None)]
    [InlineData(DebateJudgmentReason.Converged, DebateLimitReached.None)]
    [InlineData(DebateJudgmentReason.TurnMissing, DebateLimitReached.None)]
    [InlineData(DebateJudgmentReason.Limit, DebateLimitReached.Tokens)]
    [InlineData(DebateJudgmentReason.Limit, DebateLimitReached.Duration)]
    [InlineData(DebateJudgmentReason.Limit, DebateLimitReached.Rounds)]
    public void The_reason_of_a_judgment_and_its_limit_are_kept(DebateJudgmentReason reason, DebateLimitReached limit)
    {
        var session = OpenPhase5();
        SubmitRound1(session);

        session.RequestJudgment(reason, limit);

        Assert.Equal((DebatePhase.AwaitingJudgment, reason, limit, 1), (session.Phase, session.JudgmentReason!.Value, session.LimitReached, session.CurrentRound));
    }

    [Fact]
    public void A_judgment_for_a_limit_names_it_and_no_other_reason_does()
    {
        var session = OpenPhase5();
        SubmitRound1(session);

        Assert.Equal("debate.judgment-limit", Violation(() => session.RequestJudgment(DebateJudgmentReason.Limit)).Rule);
        Assert.Equal("debate.judgment-limit", Violation(() => session.RequestJudgment(DebateJudgmentReason.Requested, DebateLimitReached.Tokens)).Rule);
        Assert.Equal(DebatePhase.Proposals, session.Phase);
    }

    [Fact]
    public void Only_a_tribunal_goes_to_judgment_to_be_synthesised()
    {
        var session = OpenPhase5();
        SubmitRound1(session);

        Assert.Equal("debate.judgment-tribunal", Violation(() => session.RequestJudgment(DebateJudgmentReason.Tribunal)).Rule);
    }

    [Theory]
    [InlineData(DebateJudgmentReason.Converged)]
    [InlineData(DebateJudgmentReason.Tribunal)]
    public void Converging_or_synthesising_needs_every_contribution_of_round_one(DebateJudgmentReason reason)
    {
        var session = reason == DebateJudgmentReason.Tribunal
            ? OpenPhase5(DebateMode.Tribunal, participants: [Claude("panel-1", DebateRole.Panelist), Claude("panel-2", DebateRole.Panelist)])
            : OpenPhase5();
        var kind = session.Mode == DebateMode.Tribunal ? ContributionKind.Assessment : ContributionKind.Proposal;
        session.Submit(Contribution(session.Participants[0].Agent.Value, 1, kind));

        Assert.Equal("debate.missing-proposals", Violation(() => session.RequestJudgment(reason)).Rule);
    }

    /// <summary>D-22: after a turn fails twice a person may ask for judgment with what there is; it never goes on silently.</summary>
    [Fact]
    public void A_person_can_send_round_one_to_judgment_with_a_turn_missing()
    {
        var session = OpenPhase5();
        session.Submit(Contribution("arquitecto", 1, ContributionKind.Proposal));

        session.RequestJudgment(DebateJudgmentReason.TurnMissing);

        Assert.Equal((DebatePhase.AwaitingJudgment, DebateJudgmentReason.TurnMissing), (session.Phase, session.JudgmentReason!.Value));
        Assert.Single(session.Contributions);
    }

    [Fact]
    public void Advancing_into_judgment_records_why()
    {
        var limited = OpenPhase5(limits: new DebateLimits(1, TimeSpan.FromHours(1), 1000));
        SubmitRound1(limited);
        var tribunal = OpenPhase5(DebateMode.Tribunal, participants: [Claude("panel-1", DebateRole.Panelist), Claude("panel-2", DebateRole.Panelist)]);
        SubmitRound1(tribunal);
        var going = OpenPhase5();
        SubmitRound1(going);

        limited.AdvanceRound(T0, 0);
        tribunal.AdvanceRound(T0, 0);
        going.AdvanceRound(T0, 0);

        Assert.Equal((DebateJudgmentReason.Limit, DebateLimitReached.Rounds), (limited.JudgmentReason!.Value, limited.LimitReached));
        Assert.Equal((DebateJudgmentReason.Tribunal, DebateLimitReached.None), (tribunal.JudgmentReason!.Value, tribunal.LimitReached));
        Assert.Null(going.JudgmentReason);
    }

    [Fact]
    public void A_panel_that_agrees_closes_the_debate_with_both_verdicts()
    {
        var session = PanelReadyForJudgment();
        var claude = VerdictOf(session, ClaudeJudge);
        var codex = VerdictOf(session, CodexJudge) with { Confidence = new Confidence(ConfidenceLevel.Medium, "dudas") };
        var outcome = JudgingPanel.Combine(session.Judging, [(claude, true), (codex, true)]);

        session.Close(outcome, [claude, codex]);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved), (session.Phase, session.Outcome));
        Assert.Equal([claude, codex], session.Verdicts);
        Assert.Same(claude, session.Verdict);
        Assert.Equal(outcome, session.Panel);
        Assert.Equal(JudgingPanel.Agree, session.CloseReason);
    }

    [Fact]
    public void Verdicts_that_echo_different_heads_of_the_log_cannot_close_a_debate()
    {
        var session = PanelReadyForJudgment();
        var claude = VerdictOf(session, ClaudeJudge);
        var codex = VerdictOf(session, CodexJudge, head: HashA);

        Assert.Equal("debate.verdict-heads", Violation(() => session.Close(JudgingPanel.Combine(session.Judging, [(claude, true), (codex, true)]), [claude, codex])).Rule);
        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
    }

    [Fact]
    public void Two_verdicts_of_the_same_judge_are_not_a_panel()
    {
        var session = PanelReadyForJudgment();
        var first = VerdictOf(session, CodexJudge);
        var second = VerdictOf(session, CodexJudge);

        Assert.Equal("debate.verdict-duplicate-judge", Violation(() => session.Close(JudgingPanel.DecidedByUser(VerdictDecision.Approved, "P1"), [first, second])).Rule);
    }

    [Fact]
    public void Every_verdict_comes_from_a_judge_of_the_arrangement_and_is_about_the_requirements_of_the_debate()
    {
        var session = PanelReadyForJudgment();
        var stranger = VerdictOf(session, new JudgeIdentity(Aid("otro-juez"), CodexJudge.Model));
        var otherRequirements = VerdictOf(session, CodexJudge) with { RequirementSetHash = HashA };
        var decided = JudgingPanel.DecidedByUser(VerdictDecision.Approved, "P1");

        Assert.Equal("debate.verdict-from-other-judge", Violation(() => session.Close(decided, [VerdictOf(session, ClaudeJudge), stranger])).Rule);
        Assert.Equal("debate.verdict-requirements", Violation(() => session.Close(decided, [otherRequirements])).Rule);
    }

    /// <summary>D-26: «Decidir yo» when no verdict could be had; the person's decision closes the debate with the rule «user».</summary>
    [Fact]
    public void A_person_decides_when_no_verdict_arrived()
    {
        var session = PanelReadyForJudgment();

        session.Close(JudgingPanel.DecidedByUser(VerdictDecision.Rejected, null), []);

        Assert.Equal((DebateOutcome.Resolved, JudgingPanel.User), (session.Outcome, session.Panel!.Rule));
        Assert.Empty(session.Verdicts);
        Assert.Null(session.Verdict);
    }

    [Fact]
    public void An_outcome_the_verdicts_do_not_support_is_refused()
    {
        var session = ReadyForJudgment();
        var rejected = DebateVerdict(session, VerdictDecision.Rejected);
        var approvedAlone = new PanelOutcome(DebateOutcome.Resolved, VerdictDecision.Approved, null, JudgingPanel.Single);
        var settledDisagreement = new PanelOutcome(DebateOutcome.Resolved, VerdictDecision.Approved, null, JudgingPanel.Disagree);

        Assert.Equal("debate.close-unsupported", Violation(() => session.Close(approvedAlone, [rejected])).Rule);
        Assert.Equal("debate.close-unsupported", Violation(() => session.Close(approvedAlone, [])).Rule);
        Assert.Equal("debate.close-unsupported", Violation(() => session.Close(settledDisagreement, [rejected])).Rule);
        Assert.Equal("debate.close-rule", Violation(() => session.Close(approvedAlone with { Rule = "majority" }, [DebateVerdict(session, VerdictDecision.Approved)])).Rule);
        Assert.Equal(DebatePhase.AwaitingJudgment, session.Phase);
    }

    [Theory]
    [InlineData(DebateOutcome.Open)]
    [InlineData(DebateOutcome.Aborted)]
    public void A_debate_is_never_closed_open_nor_aborted(DebateOutcome outcome)
    {
        var session = ReadyForJudgment();

        Assert.Equal("debate.close-outcome", Violation(() => session.Close(new PanelOutcome(outcome, null, null, JudgingPanel.User), [])).Rule);
    }
}
