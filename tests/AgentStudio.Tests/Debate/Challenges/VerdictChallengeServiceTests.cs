using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Debate.Challenges;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Debate.Challenges.Fixtures;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;
using static AgentStudio.Tests.Debate.Challenges.Fixtures.ChallengeBench;

namespace AgentStudio.Tests.Debate.Challenges;

/// <summary>
/// docs/15 D-38 to D-40 and F5-7 with a double of the coordinator of debates: the revision inherits who may not judge and
/// changes the judge, a jury's verdict is revised by a jury, the challenge is anchored in the unit of the verdict with the
/// reasons, nothing of the revision's verdict is written here (the judgment of debates keeps it), and where each challenge
/// stands, with the acceptance of a turn towards an approval.
/// </summary>
public sealed class VerdictChallengeServiceTests
{
    private const string Reasons = "El juez aprobó sin mirar que T1 no prueba el desbordamiento.";

    private static T Payload<T>(DecisionEntry entry)
        where T : DecisionPayload => DecisionPayloadSerializer.Deserialize<T>(entry.PayloadJson);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ChallengeBench Bench, IDebateRun Revision)> ChallengedAsync(VerdictDecision original = VerdictDecision.Approved)
    {
        var bench = await OverRunAsync(decision: original);
        var proposal = await bench.PrepareAsync(bench.Request());
        Assert.Empty(proposal.Problems);
        return (bench, await bench.ChallengeAsync(proposal));
    }

    [Fact]
    public async Task Preparing_a_challenge_of_a_run_inherits_its_producers_changes_the_judge_and_writes_nothing()
    {
        var bench = await OverRunAsync();
        var before = bench.Entries.Count;

        var proposal = await bench.PrepareAsync(bench.Request());

        Assert.Empty(proposal.Problems);
        var asked = Assert.Single(bench.Coordinator.Prepared);
        Assert.Equal((DebateMode.Adversarial, DebateSubjectKind.Verdict, RunVerdict), (asked.Mode, asked.Subject.Kind, asked.Subject.Reference));
        Assert.Equal(Reasons, asked.Question);
        Assert.Equal(bench.World.Frozen.ContentHash, asked.Requirements.ContentHash);
        Assert.Equal(DebateJudgingKind.NonProducerJudge, asked.Judging);
        Assert.Equal([Revisor], asked.Judges);
        Assert.Equal([Revisor], proposal.Judging.Judges.Select(j => j.Agent));
        Assert.Equal(new HashSet<VendorId> { VendorId.Anthropic }, proposal.Judging.ProducerVendors.ToHashSet());
        Assert.Equal(new HashSet<VendorId> { VendorId.OpenAI }, proposal.Judging.WaivedParticipantVendors.ToHashSet());
        Assert.Equal(before, bench.Entries.Count);
    }

    [Fact]
    public async Task With_participants_of_one_vendor_the_revision_is_judged_by_an_independent_judge_of_the_other()
    {
        var bench = await OverRunAsync();
        var revision = bench.Revision(null, (Architect, DebateRole.Proponent), (SecondArchitect, DebateRole.Critic));

        var proposal = await bench.PrepareAsync(bench.Request(revision: revision));

        Assert.Empty(proposal.Problems);
        Assert.Equal(DebateJudgingKind.IndependentJudge, proposal.Judging.Kind);
        Assert.Equal([Revisor], proposal.Judging.Judges.Select(j => j.Agent));
    }

    [Fact]
    public async Task Without_another_eligible_judge_the_same_agent_judges_again_in_a_new_session_and_the_proposal_says_so()
    {
        var bench = await OverRunAsync(template: [.. DefaultTemplate().Where(a => a.Id != Revisor)]);

        var proposal = await bench.PrepareAsync(bench.Request());

        Assert.Empty(proposal.Problems);
        Assert.Equal([Judge], proposal.Judging.Judges.Select(j => j.Agent));
        Assert.Contains(proposal.Estimate.Notes, n => n.Contains("mismo agente", StringComparison.Ordinal) && n.Contains("sesión nueva", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_judge_never_runs_on_a_vendor_that_produced_the_object_even_when_the_card_offers_it()
    {
        var bench = await OverRunAsync();

        var proposal = await bench.PrepareAsync(bench.Request(revision: bench.Revision([ClaudeJudge, Revisor])));

        Assert.DoesNotContain(proposal.Judging.Judges, j => j.Model.Vendor == VendorId.Anthropic);
        Assert.Equal([Revisor], bench.Coordinator.Prepared[^1].Judges);
    }

    [Fact]
    public async Task The_verdict_of_a_jury_is_revised_by_a_jury_preferring_judges_other_than_the_one_challenged()
    {
        var bench = await OverRunAsync();
        const string debate = "deb-20261019-0j0j0j";
        await bench.World.RecordAsync(PreparedRun.System, DecisionKind.DebateOpened, "Debate abierto", DebateEntries.Opened(
            debate,
            mode: DebateMode.Free,
            participants:
            [
                new DebateParticipantEntry(Architect, DebateRole.Proponent, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet", "P1"),
                new DebateParticipantEntry(Critic, DebateRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, null, "P2"),
            ],
            waived: [VendorId.Anthropic, VendorId.OpenAI],
            judging: DebateJudgingKind.CrossVendorPanel,
            judges: [new DebateJudgeEntry(Judge, ProviderId.CodexCli, VendorId.OpenAI, null), new DebateJudgeEntry(ClaudeJudge, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku")]));
        var ids = await bench.JudgeRevisionAsync(debate, string.Empty, debate, DebateOutcome.Resolved, JudgingPanel.Agree, (Judge, VerdictDecision.Approved, true), (ClaudeJudge, VerdictDecision.Approved, true));
        bench.Coordinator.Producers = new HashSet<VendorId>();

        var proposal = await bench.PrepareAsync(bench.Request(verdict: ids[0]));

        Assert.Empty(proposal.Problems);
        Assert.Equal(DebateJudgingKind.CrossVendorPanel, bench.Coordinator.Prepared[^1].Judging);
        Assert.Equal(DebateJudgingKind.CrossVendorPanel, proposal.Judging.Kind);
        Assert.Equal([ClaudeJudge, Revisor], proposal.Judging.Judges.Select(j => j.Agent));
    }

    [Fact]
    public async Task A_coordinator_that_does_not_inherit_who_may_not_judge_leaves_a_problem_and_nothing_opens()
    {
        var bench = await OverRunAsync();
        bench.Coordinator.Producers = new HashSet<VendorId>();

        var proposal = await bench.PrepareAsync(bench.Request());

        Assert.Contains(proposal.Problems, p => p.Contains("anthropic", StringComparison.Ordinal) && p.Contains("hereda", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bench.ChallengeAsync(proposal));
        Assert.Empty(bench.Coordinator.Opened);
    }

    [Theory]
    [InlineData("verdict")]
    [InlineData("requirements")]
    [InlineData("reasons")]
    [InlineData("unannounced")]
    public async Task Without_its_material_a_challenge_has_problems_and_is_never_opened(string missing)
    {
        var bench = await OverRunAsync();
        var request = bench.Request();
        switch (missing)
        {
            case "verdict":
                request = bench.Request(verdict: "no-existe");
                break;
            case "requirements":
                await bench.Verdicts.AppendAsync(AuditWorld.Project, new StoredVerdict("veredicto-x", VerdictOf(VerdictDecision.Approved, CodexJudge, new string('f', 64)), Run.Value, "R1", AuditScope.CurrentPlan, true, [], null), Ct);
                await bench.World.RecordAsync(new Actor(ActorKind.Judge, Judge.Value), DecisionKind.VerdictIssued, "Veredicto",
                    new VerdictIssuedPayload("veredicto-x", Tid("R1"), VerdictDecision.Approved, true, [], new string('f', 64), new string('e', 64), null, null) { RunId = Run.Value });
                request = bench.Request(verdict: "veredicto-x");
                break;
            case "reasons":
                request = bench.Request(reasons: "   ");
                break;
            case "unannounced":
                await bench.Verdicts.AppendAsync(AuditWorld.Project, new StoredVerdict("solo-en-el-almacen", VerdictOf(VerdictDecision.Approved, CodexJudge, bench.World.Frozen.ContentHash), Run.Value, "R1", AuditScope.CurrentPlan, true, [], null), Ct);
                request = bench.Request(verdict: "solo-en-el-almacen");
                break;
        }

        var proposal = await bench.PrepareAsync(request);

        Assert.NotEmpty(proposal.Problems);
        var before = bench.Entries.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bench.ChallengeAsync(proposal));
        Assert.Equal(before, bench.Entries.Count);
        Assert.Empty(bench.Coordinator.Opened);
    }

    [Fact]
    public async Task Challenging_opens_the_revision_announces_the_reasons_in_it_and_anchors_the_challenge_in_the_unit_of_the_verdict_without_writing_any_verdict()
    {
        var bench = await OverRunAsync();
        var proposal = await bench.PrepareAsync(bench.Request());
        var before = bench.Entries.Count;
        var stored = bench.Verdicts.Stored.Count;

        var revision = await bench.ChallengeAsync(proposal);

        var added = bench.Entries.Skip(before).ToList();
        Assert.Equal(
            [DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted, DecisionKind.DebateOpened, DecisionKind.EvidenceRegistered, DecisionKind.VerdictChallenged],
            added.Select(e => e.Kind));
        Assert.Same(bench.Coordinator.Runs[0], revision);
        var challenged = Payload<VerdictChallengedPayload>(added[^1]);
        Assert.Equal((RunVerdict, "usuario", revision.Id.Value, Run.Value), (challenged.Verdict, challenged.ChallengedBy, challenged.RevisionDebate, challenged.RunId));
        Assert.Equal(new Actor(ActorKind.User, "usuario"), added[^1].Actor);
        var registered = Payload<EvidenceRegisteredPayload>(added[^2]);
        Assert.Equal((challenged.Reasons, revision.Id.Value, EvidenceRegisteredPayload.MetadataVersion), (registered.Evidence, registered.RunId, registered.V));
        Assert.Equal(Reasons, bench.World.Evidence.ContentText(challenged.Reasons));
        Assert.Equal(ContentHash.Sha256Hex(Reasons), challenged.ReasonsSha256);
        var draft = bench.World.Evidence.Drafts[^1];
        Assert.Equal((EvidenceKind.HumanNote, EvidenceOrigin.ProvidedByHuman, "usuario", revision.Id.Value), (draft.Kind, draft.Origin, draft.ProducedBy, draft.RunId));
        Assert.Equal(RunVerdict, Payload<DebateOpenedPayload>(added[2]).Challenges);
        Assert.DoesNotContain(added, e => e.Kind == DecisionKind.VerdictIssued);
        Assert.Equal(stored, bench.Verdicts.Stored.Count);
        Assert.Equal(ChallengeStatus.Pending, await bench.StatusAsync());
    }

    [Fact]
    public async Task A_denied_opening_records_no_challenge()
    {
        var bench = await OverRunAsync();
        bench.Coordinator.Approve = false;
        var proposal = await bench.PrepareAsync(bench.Request());

        await Assert.ThrowsAsync<InvalidOperationException>(() => bench.ChallengeAsync(proposal));

        Assert.DoesNotContain(bench.Entries, e => e.Kind == DecisionKind.VerdictChallenged);
        Assert.DoesNotContain(bench.World.Evidence.Drafts, d => d.Kind == EvidenceKind.HumanNote);
        Assert.Equal(ChallengeStatus.None, await bench.StatusAsync());
    }

    [Fact]
    public async Task A_verdict_whose_challenge_is_still_open_cannot_be_challenged_again()
    {
        var (bench, _) = await ChallengedAsync();

        var again = await bench.PrepareAsync(bench.Request());

        Assert.Contains(again.Problems, p => p.Contains("sin cerrar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verdict_never_challenged_has_no_challenge()
    {
        var bench = await OverRunAsync();

        Assert.Equal(ChallengeStatus.None, await bench.StatusAsync());
    }

    [Theory]
    [InlineData(VerdictDecision.Approved, "open", null, ChallengeStatus.Pending)]
    [InlineData(VerdictDecision.Approved, "aborted", null, ChallengeStatus.Pending)]
    [InlineData(VerdictDecision.Approved, "disagree", null, ChallengeStatus.Pending)]
    [InlineData(VerdictDecision.Approved, "invalid", VerdictDecision.Approved, ChallengeStatus.Pending)]
    [InlineData(VerdictDecision.Approved, "user", null, ChallengeStatus.DecidedByUser)]
    [InlineData(VerdictDecision.Approved, "single", VerdictDecision.Approved, ChallengeStatus.Confirmed)]
    [InlineData(VerdictDecision.Approved, "single", VerdictDecision.ChangesRequested, ChallengeStatus.Revoked)]
    [InlineData(VerdictDecision.Approved, "single", VerdictDecision.Rejected, ChallengeStatus.Revoked)]
    [InlineData(VerdictDecision.ChangesRequested, "single", VerdictDecision.Approved, ChallengeStatus.AwaitingAcceptance)]
    [InlineData(VerdictDecision.ChangesRequested, "single", VerdictDecision.Rejected, ChallengeStatus.Confirmed)]
    public async Task Where_a_challenge_stands_follows_how_its_revision_closed(VerdictDecision original, string closing, VerdictDecision? revised, ChallengeStatus expected)
    {
        var (bench, revision) = await ChallengedAsync(original);
        var debate = revision.Id.Value;
        switch (closing)
        {
            case "aborted":
                await bench.JudgeRevisionAsync(debate, RunVerdict, Run.Value, DebateOutcome.Aborted, DebateClosedPayload.AbortedRule);
                break;
            case "disagree":
                await bench.JudgeRevisionAsync(debate, RunVerdict, Run.Value, DebateOutcome.Unresolved, JudgingPanel.Disagree, (Revisor, VerdictDecision.Approved, true), (ClaudeJudge, VerdictDecision.Rejected, true));
                break;
            case "invalid":
                await bench.JudgeRevisionAsync(debate, RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.Single, (Revisor, revised!.Value, false));
                break;
            case "user":
                await bench.JudgeRevisionAsync(debate, RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.User);
                break;
            case "single":
                await bench.JudgeRevisionAsync(debate, RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.Single, (Revisor, revised!.Value, true));
                break;
        }

        Assert.Equal(expected, await bench.StatusAsync());
    }

    [Fact]
    public async Task A_debate_that_opened_against_the_verdict_counts_as_its_challenge_even_before_VerdictChallenged()
    {
        var bench = await OverRunAsync();

        await bench.World.RecordAsync(PreparedRun.System, DecisionKind.DebateOpened, "Debate de revisión abierto", DebateEntries.Opened("deb-20261019-0c0c0c", challenges: RunVerdict));

        Assert.Equal(ChallengeStatus.Pending, await bench.StatusAsync());
    }

    [Fact]
    public async Task A_turn_towards_an_approval_counts_once_a_person_accepts_it_bound_to_that_verdict()
    {
        var (bench, revision) = await ChallengedAsync(VerdictDecision.ChangesRequested);
        var ids = await bench.JudgeRevisionAsync(revision.Id.Value, RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.Single, (Revisor, VerdictDecision.Approved, true));

        await bench.AcceptAsync(ids[0]);

        var asked = Assert.Single(bench.Gate.Requests);
        Assert.Equal((ChallengeLog.RevisionAcceptanceAction, revision.Id.Value), (asked.Action, asked.RunId));
        Assert.False(string.IsNullOrEmpty(asked.Binding));
        Assert.Contains(ids[0], asked.Description, StringComparison.Ordinal);
        Assert.Equal(ChallengeStatus.Accepted, await bench.StatusAsync());
    }

    [Fact]
    public async Task A_denied_acceptance_leaves_the_revision_awaiting()
    {
        var (bench, revision) = await ChallengedAsync(VerdictDecision.ChangesRequested);
        var ids = await bench.JudgeRevisionAsync(revision.Id.Value, RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.Single, (Revisor, VerdictDecision.Approved, true));
        bench.Gate.Approve = false;

        await bench.AcceptAsync(ids[0]);

        Assert.Equal(ChallengeStatus.AwaitingAcceptance, await bench.StatusAsync());
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("unknown")]
    [InlineData("superseded")]
    public async Task Only_the_deciding_verdict_of_a_revision_awaiting_acceptance_can_be_accepted(string which)
    {
        var (bench, revision) = await ChallengedAsync(which == "confirmed" ? VerdictDecision.Approved : VerdictDecision.ChangesRequested);
        var ids = await bench.JudgeRevisionAsync(revision.Id.Value, RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.Single, (Revisor, VerdictDecision.Approved, true));
        var verdict = which switch
        {
            "unknown" => "no-existe",
            "superseded" => RunVerdict,
            _ => ids[0],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => bench.AcceptAsync(verdict));

        Assert.Empty(bench.Gate.Requests);
    }
}
