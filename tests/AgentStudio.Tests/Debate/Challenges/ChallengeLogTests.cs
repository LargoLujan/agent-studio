using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Debate.Challenges;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Debate.Challenges.Fixtures;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Challenges;

/// <summary>docs/15 D-39: what a challenged verdict inherits from its object, read from the verified log.</summary>
public sealed class ChallengeLogTests
{
    [Fact]
    public async Task A_run_passes_on_the_vendors_of_who_produced_it_and_never_the_vendor_of_its_judge()
    {
        var bench = await ChallengeBench.OverRunAsync();

        var target = ChallengeLog.ObjectOf(bench.Entries, Run.Value, ChallengeBench.RunVerdict);

        Assert.Equal((ChallengedUnitKind.Run, (string?)null, false, false), (target.Kind, target.JudgedBy, target.FromJury, target.Choice));
        Assert.Equal(new HashSet<VendorId> { VendorId.Anthropic }, target.ExcludedVendors.ToHashSet());
    }

    [Fact]
    public async Task A_debate_passes_on_its_producers_and_the_participants_it_did_not_waive()
    {
        var bench = await ChallengeBench.OverRunAsync();
        const string debate = "deb-20261019-0a0b0c";
        await bench.World.RecordAsync(PreparedRun.System, DecisionKind.DebateOpened, "Debate abierto", DebateEntries.Opened(
            debate,
            mode: DebateMode.ImplementationAudit,
            producers: [VendorId.Google],
            participants:
            [
                new DebateParticipantEntry(ChallengeBench.Architect, DebateRole.Proponent, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet", "P1"),
                new DebateParticipantEntry(ChallengeBench.Critic, DebateRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, null, "P2"),
            ],
            waived: [VendorId.OpenAI],
            judging: DebateJudgingKind.NonProducerJudge));

        var target = ChallengeLog.ObjectOf(bench.Entries, debate, "v-1");

        Assert.Equal((ChallengedUnitKind.Debate, debate, false), (target.Kind, target.JudgedBy, target.FromJury));
        Assert.Equal(new HashSet<VendorId> { VendorId.Google, VendorId.Anthropic }, target.ExcludedVendors.ToHashSet());
        Assert.Equal(DebateJudgingKind.NonProducerJudge, target.Judging);
    }

    [Fact]
    public async Task An_audit_passes_on_the_producers_of_the_project_and_the_declared_authors()
    {
        var bench = await ChallengeBench.OverRunAsync();
        const string audit = "aud-20261019-0a0b0c";
        await bench.World.RecordAsync(PreparedRun.System, DecisionKind.ProjectAuditStarted, "Arranca la auditoría",
            new ProjectAuditStartedPayload(new string('a', 64), AuditScope.WholeProject, CommitOf("head"), false, bench.World.Frozen.ContentHash, bench.World.Profile.ContentHash,
                new DebateJudgeEntry(Judge, ProviderId.CodexCli, VendorId.OpenAI, null), [VendorId.Anthropic], [VendorId.Google]) { RunId = audit });

        var target = ChallengeLog.ObjectOf(bench.Entries, audit, "v-1");

        Assert.Equal(ChallengedUnitKind.Audit, target.Kind);
        Assert.Equal(new HashSet<VendorId> { VendorId.Anthropic, VendorId.Google }, target.ExcludedVendors.ToHashSet());
    }

    [Fact]
    public async Task An_acceptance_asked_before_the_revision_closed_does_not_count()
    {
        var bench = await ChallengeBench.OverRunAsync(decision: VerdictDecision.ChangesRequested);
        var proposal = await bench.PrepareAsync(bench.Request());
        var revision = await bench.ChallengeAsync(proposal);
        await bench.World.RecordAsync(PreparedRun.System, DecisionKind.ApprovalRequested, "Aceptación pedida antes de tiempo",
            new ApprovalRequestedPayload(ChallengeLog.RevisionAcceptanceAction, "Aceptar la revisión.", AgentStudio.Application.Security.RiskLevel.Critical, null, []) { RunId = revision.Id.Value });
        await bench.World.RecordAsync(PreparedRun.Person, DecisionKind.ApprovalGranted, "Aceptada antes de tiempo",
            new ApprovalAnsweredPayload(ChallengeLog.RevisionAcceptanceAction, true, "usuario", null, null) { RunId = revision.Id.Value });
        await bench.JudgeRevisionAsync(revision.Id.Value, ChallengeBench.RunVerdict, Run.Value, DebateOutcome.Resolved, JudgingPanel.Single, (ChallengeBench.Revisor, VerdictDecision.Approved, true));

        Assert.Equal(ChallengeStatus.AwaitingAcceptance, await bench.StatusAsync());
    }
}
