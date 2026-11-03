using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

/// <summary>
/// docs/15 D-12 to D-14: who may judge a debate with two vendors (the rule of the producers is never waived; the one of the
/// participants only explicitly) and how the verdicts of a cross-vendor panel combine.
/// </summary>
public sealed class DebateJudgingTests
{
    private static readonly VendorId Anthropic = VendorId.Anthropic;
    private static readonly VendorId OpenAI = VendorId.OpenAI;
    private static readonly VendorId Google = VendorId.Google;

    private static JudgeIdentity JudgeOn(string agent, VendorId vendor, string? model = null) =>
        new(Aid(agent), new ModelBinding(new ProviderId("cli-" + vendor.Value), vendor, model));

    private static DebateParticipant On(string agent, VendorId vendor, DebateRole role = DebateRole.Panelist) =>
        new(Aid(agent), role, new ModelBinding(new ProviderId("cli-" + vendor.Value), vendor));

    private static HashSet<VendorId> Set(params VendorId[] vendors) => [.. vendors];

    private static JudgingArrangement Arrangement(DebateJudgingKind kind, JudgeIdentity[] judges, VendorId[]? producers = null, VendorId[]? waived = null) =>
        new(kind, judges, Set(producers ?? []), Set(waived ?? []));

    private static readonly JudgeIdentity ClaudeJudge = JudgeOn("juez-claude", Anthropic, "haiku");
    private static readonly JudgeIdentity CodexJudge = JudgeOn("juez-revision", OpenAI);
    private static readonly JudgeIdentity GeminiJudge = JudgeOn("juez-gemini", Google);

    private static readonly IReadOnlyList<DebateParticipant> Claudes = [On("arquitecto", Anthropic, DebateRole.Proponent), On("critico", Anthropic, DebateRole.Critic)];
    private static readonly IReadOnlyList<DebateParticipant> Mixed = [On("arquitecto", Anthropic, DebateRole.Proponent), On("critico", OpenAI, DebateRole.Critic)];

    private static JudgingArrangement Panel(JudgeIdentity? claude = null, JudgeIdentity? codex = null) =>
        Arrangement(DebateJudgingKind.CrossVendorPanel, [claude ?? ClaudeJudge, codex ?? CodexJudge], waived: [Anthropic, OpenAI]);

    // ---- the family of a mode ----

    [Theory]
    [InlineData(DebateMode.Free, DebateSubjectKind.None, true)]
    [InlineData(DebateMode.Free, DebateSubjectKind.Commit, true)]
    [InlineData(DebateMode.Adversarial, DebateSubjectKind.None, true)]
    [InlineData(DebateMode.ArchitectureReview, DebateSubjectKind.Commit, true)]
    [InlineData(DebateMode.Tribunal, DebateSubjectKind.None, true)]
    [InlineData(DebateMode.Tribunal, DebateSubjectKind.Run, false)]
    [InlineData(DebateMode.ImplementationAudit, DebateSubjectKind.Run, false)]
    [InlineData(DebateMode.OrchestratorReview, DebateSubjectKind.Run, false)]
    [InlineData(DebateMode.Adversarial, DebateSubjectKind.Verdict, false)]
    public void A_mode_chooses_among_the_contributions_unless_it_reviews_work_that_others_produced(DebateMode mode, DebateSubjectKind subject, bool choice)
    {
        Assert.Equal(choice, DebateJudgingRules.IsChoice(mode, subject));
    }

    [Fact]
    public void The_excluded_vendors_are_the_producers_and_every_participant_vendor_that_was_not_waived()
    {
        IReadOnlyList<DebateParticipant> three = [On("a", Anthropic), On("b", OpenAI), On("c", Google)];
        var arrangement = Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], producers: [Anthropic], waived: [OpenAI]);

        Assert.True(Set(Anthropic, Google).SetEquals(arrangement.ExcludedVendors(three)));
        Assert.True(Set(Anthropic, OpenAI, Google).SetEquals(Arrangement(DebateJudgingKind.IndependentJudge, [GeminiJudge], producers: [Anthropic]).ExcludedVendors(three)));
    }

    [Fact]
    public void Arrangements_are_equal_by_their_content_whatever_set_holds_the_vendors()
    {
        var one = Arrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge, CodexJudge], waived: [Anthropic, OpenAI]);
        var other = new JudgingArrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge, CodexJudge], new SortedSet<VendorId>(Comparer<VendorId>.Create((x, y) => string.CompareOrdinal(x.Value, y.Value))), new HashSet<VendorId> { OpenAI, Anthropic });

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
        Assert.NotEqual(one, one with { Judges = [CodexJudge, ClaudeJudge] });
        Assert.NotEqual(one, one with { WaivedParticipantVendors = Set(Anthropic) });
    }

    // ---- what is possible (D-13) ----

    [Fact]
    public void By_default_the_participants_share_a_vendor_and_the_judge_is_of_the_other_one()
    {
        var possible = DebateJudgingRules.Possible(DebateMode.Free, DebateSubjectKind.None, Claudes, Set(), [ClaudeJudge, CodexJudge]);

        var only = Assert.Single(possible);
        Assert.Equal(Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge]), only);
    }

    [Fact]
    public void Mixing_vendors_in_a_mode_that_chooses_takes_a_cross_vendor_panel()
    {
        var possible = DebateJudgingRules.Possible(DebateMode.Adversarial, DebateSubjectKind.None, Mixed, Set(), [ClaudeJudge, CodexJudge]);

        Assert.Equal([Panel()], possible);
    }

    [Fact]
    public void Mixing_vendors_in_a_review_takes_a_judge_of_the_vendor_that_did_not_produce_the_work()
    {
        var possible = DebateJudgingRules.Possible(DebateMode.ImplementationAudit, DebateSubjectKind.Run, Mixed, Set(Anthropic), [ClaudeJudge, CodexJudge]);

        Assert.Equal([Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], producers: [Anthropic], waived: [OpenAI])], possible);
    }

    [Fact]
    public void When_both_vendors_produced_the_work_nobody_may_judge_it()
    {
        Assert.Empty(DebateJudgingRules.Possible(DebateMode.ImplementationAudit, DebateSubjectKind.Run, Mixed, Set(Anthropic, OpenAI), [ClaudeJudge, CodexJudge]));
        Assert.Empty(DebateJudgingRules.Possible(DebateMode.Free, DebateSubjectKind.None, Mixed, Set(Anthropic), [ClaudeJudge, CodexJudge]));
    }

    [Fact]
    public void A_third_vendor_gives_an_independent_judge_first_and_the_panel_as_an_alternative()
    {
        var possible = DebateJudgingRules.Possible(DebateMode.Free, DebateSubjectKind.None, Mixed, Set(), [ClaudeJudge, CodexJudge, GeminiJudge]);

        Assert.Equal([Arrangement(DebateJudgingKind.IndependentJudge, [GeminiJudge]), Panel()], possible);
    }

    [Fact]
    public void A_participant_is_never_offered_as_a_judge()
    {
        IReadOnlyList<DebateParticipant> withJudge = [On("arquitecto", Anthropic, DebateRole.Proponent), On("juez-revision", Anthropic, DebateRole.Critic)];

        Assert.Empty(DebateJudgingRules.Possible(DebateMode.Free, DebateSubjectKind.None, withJudge, Set(), [CodexJudge]));
    }

    [Fact]
    public void Every_candidate_and_every_pair_of_candidates_of_the_two_vendors_is_offered_in_the_order_of_the_template()
    {
        var other = JudgeOn("juez-claude-2", Anthropic, "sonnet");

        var possible = DebateJudgingRules.Possible(DebateMode.Free, DebateSubjectKind.None, Mixed, Set(), [ClaudeJudge, CodexJudge, other]);

        Assert.Equal([Panel(), Panel(claude: other)], possible);
    }

    /// <summary>A challenge is judged like the object of the verdict it challenges, which the domain cannot see: both families are offered.</summary>
    [Fact]
    public void A_challenge_is_offered_the_dispositions_of_both_families()
    {
        var possible = DebateJudgingRules.Possible(DebateMode.Adversarial, DebateSubjectKind.Verdict, Mixed, Set(), [ClaudeJudge, CodexJudge]);

        Assert.Equal(
            [Panel(), Arrangement(DebateJudgingKind.NonProducerJudge, [ClaudeJudge], waived: [Anthropic]), Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], waived: [OpenAI])],
            possible);
    }

    // ---- what is refused (stable codes) ----

    private static IReadOnlyList<string> Violations(JudgingArrangement arrangement, IReadOnlyList<DebateParticipant>? participants = null, DebateMode mode = DebateMode.Free, DebateSubjectKind subject = DebateSubjectKind.None) =>
        DebateJudgingRules.Violations(arrangement, mode, subject, participants ?? Claudes);

    [Fact]
    public void The_arrangements_that_are_offered_are_allowed()
    {
        Assert.Empty(Violations(Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge])));
        Assert.Empty(Violations(Panel(), Mixed));
        Assert.Empty(Violations(Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], producers: [Anthropic], waived: [OpenAI]), Mixed, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
    }

    [Fact]
    public void A_debate_needs_a_judge()
    {
        Assert.Contains(DebateJudgingRules.JudgeRequired, Violations(Arrangement(DebateJudgingKind.IndependentJudge, [])));
    }

    [Fact]
    public void A_judge_cannot_sit_twice()
    {
        Assert.Contains(DebateJudgingRules.DuplicateJudge, Violations(Arrangement(DebateJudgingKind.CrossVendorPanel, [CodexJudge, CodexJudge], waived: [Anthropic, OpenAI]), Mixed));
    }

    [Fact]
    public void A_judge_cannot_be_a_participant()
    {
        IReadOnlyList<DebateParticipant> withJudge = [On("arquitecto", Anthropic), On("juez-revision", Anthropic)];

        Assert.Equal([DebateJudgingRules.JudgeNotParticipant], Violations(Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge]), withJudge));
    }

    [Fact]
    public void The_rule_of_the_producers_is_never_waived()
    {
        var waivedProducer = Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], producers: [OpenAI], waived: [OpenAI]);

        Assert.Equal([DebateJudgingRules.JudgeProducer], Violations(Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge], producers: [OpenAI])));
        Assert.Contains(DebateJudgingRules.JudgeProducer, Violations(waivedProducer, Mixed, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
        Assert.Contains(DebateJudgingRules.WaiverOfProducer, Violations(waivedProducer, Mixed, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
    }

    [Fact]
    public void A_judge_of_the_vendor_of_a_participant_needs_that_vendor_waived()
    {
        Assert.Equal([DebateJudgingRules.JudgeParticipantNotWaived], Violations(Arrangement(DebateJudgingKind.IndependentJudge, [ClaudeJudge])));
    }

    [Fact]
    public void Only_the_vendor_of_a_participant_can_be_waived()
    {
        Assert.Contains(DebateJudgingRules.WaiverNotParticipant, Violations(Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge], waived: [Google])));
    }

    [Fact]
    public void An_independent_judge_and_a_non_producer_judge_are_one_judge()
    {
        Assert.Contains(DebateJudgingRules.SingleJudge, Violations(Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge, GeminiJudge])));
        Assert.Contains(DebateJudgingRules.SingleJudge, Violations(Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge, GeminiJudge], waived: [OpenAI]), Mixed, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
    }

    [Fact]
    public void A_panel_has_one_judge_of_each_vendor_of_the_participants()
    {
        IReadOnlyList<DebateParticipant> three = [On("a", Anthropic), On("b", OpenAI), On("c", Google)];

        Assert.Contains(DebateJudgingRules.PanelShape, Violations(Arrangement(DebateJudgingKind.CrossVendorPanel, [CodexJudge], waived: [Anthropic, OpenAI]), Mixed));
        Assert.Contains(DebateJudgingRules.PanelShape, Violations(Arrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge, JudgeOn("juez-claude-2", Anthropic)], waived: [Anthropic, OpenAI]), Mixed));
        Assert.Contains(DebateJudgingRules.PanelShape, Violations(Panel(), three));
        Assert.Contains(DebateJudgingRules.PanelShape, Violations(Arrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge], waived: [Anthropic])));
        Assert.Empty(Violations(Arrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge, CodexJudge, GeminiJudge], waived: [Anthropic, OpenAI, Google]), three));
    }

    [Fact]
    public void A_panel_only_judges_a_mode_that_chooses()
    {
        Assert.Equal([DebateJudgingRules.PanelInReview], Violations(Panel(), Mixed, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
        Assert.Equal([DebateJudgingRules.PanelInReview], Violations(Panel(), Mixed, DebateMode.Tribunal, DebateSubjectKind.Run));
    }

    [Fact]
    public void A_non_producer_judge_is_of_the_vendor_of_a_participant_and_waives_only_that_vendor()
    {
        IReadOnlyList<DebateParticipant> three = [On("a", Anthropic), On("b", OpenAI), On("c", Google)];

        Assert.Contains(DebateJudgingRules.NonProducerShape, Violations(Arrangement(DebateJudgingKind.NonProducerJudge, [GeminiJudge], producers: [Anthropic]), Mixed, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
        Assert.Contains(DebateJudgingRules.NonProducerShape, Violations(Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], producers: [Anthropic], waived: [OpenAI, Google]), three, DebateMode.ImplementationAudit, DebateSubjectKind.Run));
    }

    [Fact]
    public void A_non_producer_judge_only_judges_a_review()
    {
        Assert.Equal([DebateJudgingRules.NonProducerInChoice], Violations(Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], waived: [OpenAI]), Mixed));
    }

    [Fact]
    public void A_challenge_may_take_either_family_and_still_never_waives_the_producers()
    {
        var panelOnAProducer = Arrangement(DebateJudgingKind.CrossVendorPanel, [ClaudeJudge, CodexJudge], producers: [Anthropic], waived: [OpenAI]);

        Assert.Empty(Violations(Panel(), Mixed, DebateMode.Adversarial, DebateSubjectKind.Verdict));
        Assert.Empty(Violations(Arrangement(DebateJudgingKind.NonProducerJudge, [CodexJudge], producers: [Anthropic], waived: [OpenAI]), Mixed, DebateMode.Adversarial, DebateSubjectKind.Verdict));
        Assert.Contains(DebateJudgingRules.JudgeProducer, Violations(panelOnAProducer, Mixed, DebateMode.Adversarial, DebateSubjectKind.Verdict));
    }

    /// <summary>
    /// docs/15 §8, F5-1: about 200 random distributions by seed. Whatever is offered is allowed, never seats a judge of a
    /// producer, waives the rule of the participants only for the vendor of a seated judge, and seats a panel only where it
    /// chooses, with one judge per vendor of the participants.
    /// </summary>
    [Fact]
    public void Whatever_is_offered_never_seats_a_producer_and_waives_only_what_a_seated_judge_needs()
    {
        VendorId[] vendors = [Anthropic, OpenAI, Google];
        (DebateMode Mode, DebateSubjectKind Subject)[] debates =
        [
            (DebateMode.Free, DebateSubjectKind.None), (DebateMode.Free, DebateSubjectKind.Commit), (DebateMode.Adversarial, DebateSubjectKind.None),
            (DebateMode.ArchitectureReview, DebateSubjectKind.Commit), (DebateMode.Tribunal, DebateSubjectKind.None), (DebateMode.Tribunal, DebateSubjectKind.Run),
            (DebateMode.ImplementationAudit, DebateSubjectKind.Run), (DebateMode.OrchestratorReview, DebateSubjectKind.Run), (DebateMode.Adversarial, DebateSubjectKind.Verdict),
        ];

        var offered = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var (mode, subject) = debates[random.Next(debates.Length)];
            var participants = Enumerable.Range(0, random.Next(2, 5)).Select(i => On($"p{i}", vendors[random.Next(2 + (seed % 2))])).ToList();
            var producers = vendors.Where(_ => random.Next(4) == 0).ToHashSet();
            var candidates = Enumerable.Range(0, random.Next(1, 5)).Select(i => JudgeOn($"j{i}", vendors[random.Next(vendors.Length)])).ToList();
            if (random.Next(5) == 0)
            {
                candidates.Add(JudgeOn("p0", vendors[random.Next(vendors.Length)]));
            }

            var participantVendors = participants.Select(p => p.Model.Vendor).ToHashSet();
            foreach (var arrangement in DebateJudgingRules.Possible(mode, subject, participants, producers, candidates))
            {
                offered++;
                var because = $"seed {seed}: {arrangement.Kind} in {mode}/{subject}";
                Assert.True(DebateJudgingRules.Violations(arrangement, mode, subject, participants).Count == 0, because);
                Assert.All(arrangement.Judges, j => Assert.DoesNotContain(j.Model.Vendor, producers));
                Assert.All(arrangement.Judges, j => Assert.DoesNotContain(participants, p => p.Agent == j.Agent));
                Assert.All(arrangement.Judges, j => Assert.True(!participantVendors.Contains(j.Model.Vendor) || arrangement.WaivedParticipantVendors.Contains(j.Model.Vendor), because));
                Assert.True(arrangement.WaivedParticipantVendors.SetEquals(arrangement.Judges.Select(j => j.Model.Vendor).Where(participantVendors.Contains)), because);
                Assert.True(producers.SetEquals(arrangement.ProducerVendors), because);
                if (arrangement.Kind == DebateJudgingKind.CrossVendorPanel)
                {
                    Assert.True(subject == DebateSubjectKind.Verdict || DebateJudgingRules.IsChoice(mode, subject), because);
                    Assert.True(arrangement.Judges.Select(j => j.Model.Vendor).ToHashSet().SetEquals(participantVendors), because);
                    Assert.Equal(arrangement.Judges.Count, participantVendors.Count);
                }
                else
                {
                    Assert.Single(arrangement.Judges);
                }
            }
        }

        Assert.True(offered > 100, $"Only {offered} arrangements were offered: the distributions do not exercise the rules.");
    }

    // ---- combining the verdicts (D-14) ----

    private static Verdict VerdictOf(JudgeIdentity judge, VerdictDecision decision = VerdictDecision.Approved, string? chosen = "P1", ConfidenceLevel confidence = ConfidenceLevel.High, string? head = null) =>
        ValidVerdict(Dossier(), decision) with
        {
            Judge = judge,
            Confidence = new Confidence(confidence, "porque"),
            DecisionLogHeadHash = head ?? HashB,
            Resolution = new DebateResolution(chosen, []),
        };

    private static readonly JudgingArrangement IndependentCodex = Arrangement(DebateJudgingKind.IndependentJudge, [CodexJudge]);

    [Theory]
    [InlineData(VerdictDecision.Approved, DebateOutcome.Resolved)]
    [InlineData(VerdictDecision.ChangesRequested, DebateOutcome.Resolved)]
    [InlineData(VerdictDecision.Rejected, DebateOutcome.Resolved)]
    [InlineData(VerdictDecision.Unresolved, DebateOutcome.Unresolved)]
    [InlineData(VerdictDecision.EscalateToHuman, DebateOutcome.EscalatedToUser)]
    public void A_single_valid_verdict_decides(VerdictDecision decision, DebateOutcome outcome)
    {
        var combined = JudgingPanel.Combine(IndependentCodex, [(VerdictOf(CodexJudge, decision), true)]);

        Assert.Equal(new PanelOutcome(outcome, decision, "P1", JudgingPanel.Single) { Confidence = ConfidenceLevel.High }, combined);
    }

    [Fact]
    public void A_single_verdict_that_is_invalid_or_missing_needs_a_person()
    {
        var invalid = new PanelOutcome(DebateOutcome.EscalatedToUser, null, null, JudgingPanel.Invalid);

        Assert.Equal(invalid, JudgingPanel.Combine(IndependentCodex, [(VerdictOf(CodexJudge), false)]));
        Assert.Equal(invalid, JudgingPanel.Combine(IndependentCodex, []));
        Assert.Equal(invalid, JudgingPanel.Combine(IndependentCodex, [(VerdictOf(GeminiJudge), true)]));
        Assert.Equal(invalid, JudgingPanel.Combine(IndependentCodex, [(VerdictOf(CodexJudge), true), (VerdictOf(CodexJudge), true)]));
        Assert.True(invalid.NeedsHuman);
    }

    [Fact]
    public void The_model_a_judge_reported_does_not_make_it_another_judge()
    {
        var reported = VerdictOf(CodexJudge with { Model = CodexJudge.Model with { Model = "gpt-5.5-codex" } });

        Assert.Equal(JudgingPanel.Single, JudgingPanel.Combine(IndependentCodex, [(reported, true)]).Rule);
        Assert.True(JudgingPanel.SameJudge(CodexJudge, reported.Judge));
        Assert.False(JudgingPanel.SameJudge(CodexJudge, CodexJudge with { Model = CodexJudge.Model with { Vendor = Anthropic } }));
        Assert.False(JudgingPanel.SameJudge(CodexJudge, CodexJudge with { Model = CodexJudge.Model with { Provider = new ProviderId("otro") } }));
    }

    [Theory]
    [InlineData(ConfidenceLevel.High, ConfidenceLevel.Medium, ConfidenceLevel.Medium)]
    [InlineData(ConfidenceLevel.Low, ConfidenceLevel.High, ConfidenceLevel.Low)]
    [InlineData(ConfidenceLevel.High, ConfidenceLevel.High, ConfidenceLevel.High)]
    public void A_panel_that_agrees_decides_with_the_lower_confidence(ConfidenceLevel first, ConfidenceLevel second, ConfidenceLevel lower)
    {
        var combined = JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge, confidence: first), true), (VerdictOf(CodexJudge, confidence: second), true)]);

        Assert.Equal(new PanelOutcome(DebateOutcome.Resolved, VerdictDecision.Approved, "P1", JudgingPanel.Agree) { Confidence = lower }, combined);
        Assert.False(combined.NeedsHuman);
    }

    [Fact]
    public void A_panel_that_disagrees_on_the_decision_or_the_proposal_or_with_an_invalid_verdict_is_unresolved()
    {
        var unresolved = new PanelOutcome(DebateOutcome.Unresolved, null, null, JudgingPanel.Disagree);

        Assert.Equal(unresolved, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (VerdictOf(CodexJudge, VerdictDecision.Rejected), true)]));
        Assert.Equal(unresolved, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (VerdictOf(CodexJudge, chosen: "P2"), true)]));
        Assert.Equal(unresolved, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (VerdictOf(CodexJudge), false)]));
        Assert.Equal(unresolved, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), false), (VerdictOf(CodexJudge), false)]));
        Assert.True(unresolved.NeedsHuman);
    }

    [Fact]
    public void Two_verdicts_about_different_heads_of_the_log_are_an_invalid_pair_never_an_agreement()
    {
        var combined = JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (VerdictOf(CodexJudge, head: HashA), true)]);

        Assert.Equal(new PanelOutcome(DebateOutcome.EscalatedToUser, null, null, JudgingPanel.Invalid), combined);
    }

    [Fact]
    public void Two_verdicts_about_different_requirement_sets_are_an_invalid_pair()
    {
        var otherSet = VerdictOf(CodexJudge) with { RequirementSetHash = HashA };

        Assert.Equal(JudgingPanel.Invalid, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (otherSet, true)]).Rule);
    }

    [Fact]
    public void A_panel_needs_the_verdict_of_each_of_its_judges()
    {
        Assert.Equal(JudgingPanel.Invalid, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true)]).Rule);
        Assert.Equal(JudgingPanel.Invalid, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (VerdictOf(ClaudeJudge), true)]).Rule);
        Assert.Equal(JudgingPanel.Invalid, JudgingPanel.Combine(Panel(), [(VerdictOf(ClaudeJudge), true), (VerdictOf(GeminiJudge), true)]).Rule);
        Assert.Equal(JudgingPanel.Invalid, JudgingPanel.Combine(Panel(), []).Rule);
    }

    [Fact]
    public void A_person_decides_with_a_decision_or_leaves_it_unresolved()
    {
        Assert.Equal(new PanelOutcome(DebateOutcome.Resolved, VerdictDecision.Approved, "P1", JudgingPanel.User), JudgingPanel.DecidedByUser(VerdictDecision.Approved, "P1"));
        Assert.Equal(new PanelOutcome(DebateOutcome.Unresolved, null, null, JudgingPanel.User), JudgingPanel.DecidedByUser(null, null));
    }
}
