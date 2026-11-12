using AgentStudio.Domain.Decisions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

/// <summary>
/// The decision log hashes <c>kind.ToString()</c>, so the stored chains of existing projects only stay verifiable if
/// the existing kinds keep their names. The kinds of each phase (3, 4, 5 and 8) are appended, never inserted or renamed.
/// </summary>
public sealed class DecisionKindCompatibilityTests
{
    private static readonly string[] ExistingKinds =
    [
        "PlanCreated", "PlanRevised", "TaskCreated", "TaskAssigned", "TaskStatusChanged", "InstructionsIssued",
        "ReviewRequested", "ReviewSkipped", "ApprovalRequested", "ApprovalGranted", "ApprovalDenied", "PermissionDenied",
        "EvidenceRegistered", "IntegrationPerformed", "VerdictIssued", "AuditAlertRaised", "ReplanRequested", "Correction",
    ];

    private static readonly string[] Phase3Kinds =
    [
        "RunStarted", "RunFinished", "AgentRunStarted", "AgentRunFinished", "ConditionEvaluated", "WorktreeCreated", "WorktreeRemoved",
    ];

    [Fact]
    public void Existing_kinds_keep_their_names_and_numeric_values()
    {
        for (var i = 0; i < ExistingKinds.Length; i++)
        {
            Assert.Equal(ExistingKinds[i], ((DecisionKind)i).ToString());
        }
    }

    /// <summary>Phase 4 (docs/14 D-19): the announcement and the review of a memory entry, appended after Phase 3.</summary>
    private static readonly string[] Phase4Kinds = ["MemoryRecorded", "MemoryReviewed"];

    [Fact]
    public void Phase_3_kinds_come_after_the_existing_ones_in_the_declared_order()
    {
        for (var i = 0; i < Phase3Kinds.Length; i++)
        {
            Assert.Equal(Phase3Kinds[i], ((DecisionKind)(ExistingKinds.Length + i)).ToString());
        }
    }

    [Fact]
    public void Phase_4_kinds_come_after_the_phase_3_ones()
    {
        for (var i = 0; i < Phase4Kinds.Length; i++)
        {
            Assert.Equal(Phase4Kinds[i], ((DecisionKind)(ExistingKinds.Length + Phase3Kinds.Length + i)).ToString());
        }
    }

    /// <summary>
    /// Phase 5 (docs/15 D-03): the protocol of a debate, the challenge of a verdict and the audit of a whole project,
    /// appended after Phase 4. <c>ReplanRequested</c> already existed and keeps its place.
    /// </summary>
    private static readonly string[] Phase5Kinds =
    [
        "DebateOpened", "DebateContributionRecorded", "DebateRoundClosed", "DebateClosed", "VerdictChallenged", "ProjectAuditStarted", "ProjectAuditFinished",
    ];

    [Fact]
    public void Phase_5_kinds_come_after_the_phase_4_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length;
        for (var i = 0; i < Phase5Kinds.Length; i++)
        {
            Assert.Equal(Phase5Kinds[i], ((DecisionKind)(before + i)).ToString());
        }
    }

    /// <summary>
    /// Phase 8 (docs/16 D-16, D-38 and D-39): the choice made when the app is left with a run going, a pause or a resume of the
    /// dispatch of a run, the check before going on after a wait and the resume of a run after a restart (WP1, approved by
    /// the coordinator), appended after Phase 5.
    /// </summary>
    private static readonly string[] Phase8Kinds = ["RunExitChosen", "RunDispatchChanged", "AccessChecked", "RunResumed"];

    [Fact]
    public void Phase_8_kinds_come_after_the_phase_5_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length + Phase5Kinds.Length;
        for (var i = 0; i < Phase8Kinds.Length; i++)
        {
            Assert.Equal(Phase8Kinds[i], ((DecisionKind)(before + i)).ToString());
        }
    }

    /// <summary>Phase 12 (docs/17 D-02): the decision «Confiar en la bitácora de este proyecto», appended after Phase 8.</summary>
    private static readonly string[] Phase12Kinds = ["JournalTrustChanged"];

    [Fact]
    public void Phase_12_kinds_come_after_the_phase_8_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length + Phase5Kinds.Length + Phase8Kinds.Length;
        for (var i = 0; i < Phase12Kinds.Length; i++)
        {
            Assert.Equal(Phase12Kinds[i], ((DecisionKind)(before + i)).ToString());
        }
    }

    /// <summary>
    /// Phase 13 (docs/17 D-27 and D-29): the question of an agent, the answer of the person and what the configurator applied,
    /// appended after Phase 12.
    /// </summary>
    private static readonly string[] Phase13Kinds = ["QuestionAsked", "QuestionAnswered", "ProjectSetupApplied"];

    [Fact]
    public void Phase_13_kinds_come_after_the_phase_12_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length + Phase5Kinds.Length + Phase8Kinds.Length + Phase12Kinds.Length;
        for (var i = 0; i < Phase13Kinds.Length; i++)
        {
            Assert.Equal(Phase13Kinds[i], ((DecisionKind)(before + i)).ToString());
        }
    }

    /// <summary>
    /// Phase 14 (docs/17 D-60): the export of a document for clients, appended after Phase 13. The kinds of the Phase 9 (docs/16 §4.6)
    /// come after it; the whole list is pinned in <c>Phase8PayloadTests</c>.
    /// </summary>
    private static readonly string[] Phase14Kinds = ["ClientDocumentExported"];

    [Fact]
    public void Phase_14_kinds_come_after_the_phase_13_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length + Phase5Kinds.Length + Phase8Kinds.Length + Phase12Kinds.Length + Phase13Kinds.Length;
        for (var i = 0; i < Phase14Kinds.Length; i++)
        {
            Assert.Equal(Phase14Kinds[i], ((DecisionKind)(before + i)).ToString());
        }
    }

    /// <summary>
    /// Phase 9 (docs/16 D-17, D-18 and D-29): milestones, the backlog and the links of the trace, appended after Phase 13 and Phase 14. Nothing
    /// else was added by this phase.
    /// </summary>
    private static readonly string[] Phase9Kinds =
    [
        "MilestoneOpened", "MilestoneRequirementsRevised", "MilestoneLinked", "MilestoneClosed", "MilestoneReopened",
        "BacklogItemRecorded", "BacklogItemRetired", "BacklogOrderChanged", "TraceLinkRecorded", "TraceLinkRetracted",
    ];

    [Fact]
    public void Phase_9_kinds_come_after_the_phase_13_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length + Phase5Kinds.Length + Phase8Kinds.Length + Phase12Kinds.Length + Phase13Kinds.Length + Phase14Kinds.Length;
        for (var i = 0; i < Phase9Kinds.Length; i++)
        {
            Assert.Equal(Phase9Kinds[i], ((DecisionKind)(before + i)).ToString());
        }
    }

    /// <summary>
    /// Phase 6 (docs/20 §4.3 and §5): the fixes of Auto Doctor and the relations an AI proposed for Auto Graph, appended after Phase 9. Nothing else
    /// was added by this phase.
    /// </summary>
    private static readonly string[] Phase6Kinds = ["DoctorFixApplied", "GraphRelationsProposed"];

    [Fact]
    public void Phase_6_kinds_come_after_the_phase_9_ones()
    {
        var before = ExistingKinds.Length + Phase3Kinds.Length + Phase4Kinds.Length + Phase5Kinds.Length + Phase8Kinds.Length + Phase12Kinds.Length + Phase13Kinds.Length + Phase14Kinds.Length + Phase9Kinds.Length;
        for (var i = 0; i < Phase6Kinds.Length; i++)
        {
            Assert.Equal(Phase6Kinds[i], ((DecisionKind)(before + i)).ToString());
        }

        Assert.Equal(before + Phase6Kinds.Length, Enum.GetValues<DecisionKind>().Length);
    }

    [Fact]
    public void A_chain_recorded_before_phase_3_still_verifies_and_keeps_its_hashes()
    {
        // Hashes computed with an independent implementation of the canonical form (length-prefixed fields, SHA-256)
        // before the Phase 3 kinds existed.
        var user = new Actor(ActorKind.User, "manuel");
        var orchestrator = new Actor(ActorKind.Orchestrator, "orch");
        var system = new Actor(ActorKind.System, "agent-studio");
        var log = new DecisionLog();

        var first = log.Append(T0, user, DecisionKind.PlanCreated, "plan", "{\"a\":1}");
        var second = log.Append(T0.AddMinutes(1), orchestrator, DecisionKind.TaskStatusChanged, "task one", "{\"id\":\"t1\"}");
        var third = log.Append(T0.AddMinutes(2), system, DecisionKind.Correction, "fix", "{}", corrects: 1);

        Assert.Equal("3b61e1a12a265b9721584a1b7b354162de59624bc7673677dd51023125443c1c", first.Hash);
        Assert.Equal("042874a547e786917e471e3e4d38605cf512f3ead041455a5066fba3292ffd15", second.Hash);
        Assert.Equal("21668becd3ff3da467422a4993703422c36b0c819a6b82c44693c1ad6f145787", third.Hash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }
}
