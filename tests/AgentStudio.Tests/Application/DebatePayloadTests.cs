using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/15 D-03 and §5.2: the payloads of Phase 5. They are written once, in the one format of
/// <see cref="DecisionPayloadSerializer"/>, and the log hashes their text, so each is pinned here as text, and the hash of an
/// entry that carries one is computed by an independent implementation of the canonical form of the log.
/// </summary>
public sealed class DebatePayloadTests
{
    private const string Debate = "deb-20261019-a1b2c3";
    private const string Revision = "deb-20261019-d4e5f6";
    private const string Audit = "aud-20261019-0a1b2c";
    private const string Run = "20261015-a1b2c3";

    private static readonly DebateParticipantEntry Architect = new(Aid("arquitecto"), DebateRole.Proponent, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku", "P1");
    private static readonly DebateParticipantEntry Critic = new(Aid("critico"), DebateRole.Critic, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet", "P2");
    private static readonly DebateJudgeEntry CodexJudge = new(Aid("juez-revision"), ProviderId.CodexCli, VendorId.OpenAI, null);

    private static readonly DebateOpenedPayload Opened = new(
        DebateMode.Adversarial, HashA, HashB, HashA, Eid("ev-pregunta"), HashB, DebateSubjectKind.Commit, CommitA, CommitA,
        [Architect, Critic], [CodexJudge], DebateJudgingKind.IndependentJudge, [], [], 4, 1800, 400_000, 10, null)
    {
        RunId = Debate,
    };

    private static readonly DebateContributionRecordedPayload Contribution = new(
        2, 2, Aid("critico"), ContributionKind.Objection, 0, Eid("ev-c3"), HashA, [Eid("ev-cita-1")], ["src/x.cs:200-210"], 1, false, "r2-critico", 1, T0.AddMinutes(4))
    {
        RunId = Debate,
    };

    private static readonly DebateRoundClosedPayload RoundClosed = new(
        2, DebatePhase.AwaitingJudgment, DebateJudgmentReason.Limit, DebateLimitReached.Tokens,
        new MetricSnapshot(310_500, MetricProvenance.Estimated, "estimate: 4 chars/token", MetricOrigin.EstimatedBySystem), true, 640, "La ronda 3 pasaría del límite de tokens")
    {
        RunId = Debate,
    };

    private static readonly DebateClosedPayload Closed = new(DebateOutcome.Resolved, ["v-1", "v-2"], "agree", "P1", "Los dos jueces eligieron P1") { RunId = Debate };

    private static readonly VerdictChallengedPayload Challenged = new("v-7", "manuel", Eid("ev-razones"), HashB, Revision) { RunId = Run };

    private static readonly ReplanRequestedPayload Replan = new(["orchestrator.review-omitted"], [Tid("T3")], "Nadie revisó T3", null) { RunId = Run };

    private static readonly ProjectAuditStartedPayload AuditStarted = new(
        HashA, AuditScope.WholeProject, CommitA, true, HashB, HashA, CodexJudge, [VendorId.Anthropic], [VendorId.Anthropic])
    {
        RunId = Audit,
    };

    private static readonly ProjectAuditFinishedPayload AuditFinished = new(["v-9"], ReviewDisposition.ChangesRequested, true, "Cumple salvo RQ-04") { RunId = Audit };

    /// <summary>One sample of every Phase 5 payload; <see cref="DecisionPayloadTests"/> adds them to the samples of every payload.</summary>
    internal static readonly DecisionPayload[] Samples = [Opened, Contribution, RoundClosed, Closed, Challenged, Replan, AuditStarted, AuditFinished];

    public static TheoryData<string, string> Golden() => new()
    {
        {
            nameof(DebateOpenedPayload),
            "{\"mode\":\"Adversarial\",\"headerHash\":\"" + HashA + "\",\"approvedProposalHash\":\"" + HashB + "\",\"requirementSetHash\":\"" + HashA + "\","
            + "\"question\":\"ev-pregunta\",\"questionSha256\":\"" + HashB + "\",\"subject\":\"Commit\",\"subjectRef\":\"" + CommitA + "\",\"snapshotCommit\":\"" + CommitA + "\","
            + "\"participants\":[{\"agent\":\"arquitecto\",\"role\":\"Proponent\",\"provider\":\"claude-code\",\"vendor\":\"anthropic\",\"model\":\"haiku\",\"label\":\"P1\"},"
            + "{\"agent\":\"critico\",\"role\":\"Critic\",\"provider\":\"claude-code\",\"vendor\":\"anthropic\",\"model\":\"sonnet\",\"label\":\"P2\"}],"
            + "\"judges\":[{\"agent\":\"juez-revision\",\"provider\":\"codex-cli\",\"vendor\":\"openai\"}],\"judging\":\"IndependentJudge\","
            + "\"producerVendors\":[],\"waivedParticipantVendors\":[],\"maxRounds\":4,\"maxDurationSeconds\":1800,\"maxTotalTokens\":400000,\"maxAgentRuns\":10,"
            + "\"v\":1,\"runId\":\"" + Debate + "\"}"
        },
        {
            nameof(DebateContributionRecordedPayload),
            "{\"index\":2,\"round\":2,\"author\":\"critico\",\"kind\":\"Objection\",\"repliesTo\":0,\"evidence\":\"ev-c3\",\"sha256\":\"" + HashA + "\","
            + "\"cites\":[\"ev-cita-1\"],\"unresolvedCitations\":[\"src/x.cs:200-210\"],\"redactions\":1,\"finished\":false,\"turn\":\"r2-critico\",\"attempt\":1,"
            + "\"answeredAt\":\"2026-01-01T10:04:00+00:00\",\"v\":1,\"runId\":\"" + Debate + "\"}"
        },
        {
            nameof(DebateRoundClosedPayload),
            "{\"round\":2,\"next\":\"AwaitingJudgment\",\"judgment\":\"Limit\",\"limit\":\"Tokens\","
            + "\"tokensUsed\":{\"value\":310500,\"provenance\":\"Estimated\",\"source\":\"estimate: 4 chars/token\",\"origin\":\"EstimatedBySystem\"},"
            + "\"tokensIncludeEstimates\":true,\"elapsedSeconds\":640,\"reason\":\"La ronda 3 pasaría del límite de tokens\",\"v\":1,\"runId\":\"" + Debate + "\"}"
        },
        {
            nameof(DebateClosedPayload),
            "{\"outcome\":\"Resolved\",\"verdicts\":[\"v-1\",\"v-2\"],\"rule\":\"agree\",\"chosenProposal\":\"P1\",\"reason\":\"Los dos jueces eligieron P1\",\"v\":1,\"runId\":\"" + Debate + "\"}"
        },
        {
            nameof(VerdictChallengedPayload),
            "{\"verdict\":\"v-7\",\"challengedBy\":\"manuel\",\"reasons\":\"ev-razones\",\"reasonsSha256\":\"" + HashB + "\",\"revisionDebate\":\"" + Revision + "\",\"v\":1,\"runId\":\"" + Run + "\"}"
        },
        {
            nameof(ReplanRequestedPayload),
            "{\"findings\":[\"orchestrator.review-omitted\"],\"tasks\":[\"T3\"],\"message\":\"Nadie revisó T3\",\"v\":1,\"runId\":\"" + Run + "\"}"
        },
        {
            nameof(ProjectAuditStartedPayload),
            "{\"headerHash\":\"" + HashA + "\",\"scope\":\"WholeProject\",\"headCommit\":\"" + CommitA + "\",\"uncommittedChanges\":true,\"requirementSetHash\":\"" + HashB + "\","
            + "\"verificationProfileHash\":\"" + HashA + "\",\"judge\":{\"agent\":\"juez-revision\",\"provider\":\"codex-cli\",\"vendor\":\"openai\"},"
            + "\"producerVendors\":[\"anthropic\"],\"declaredAuthors\":[\"anthropic\"],\"v\":1,\"runId\":\"" + Audit + "\"}"
        },
        {
            nameof(ProjectAuditFinishedPayload),
            "{\"verdicts\":[\"v-9\"],\"disposition\":\"ChangesRequested\",\"trustworthy\":true,\"summary\":\"Cumple salvo RQ-04\",\"v\":1,\"runId\":\"" + Audit + "\"}"
        },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Each_payload_of_phase_5_is_written_in_one_fixed_text_and_read_back_byte_for_byte(string type, string json)
    {
        var sample = Samples.Single(s => s.GetType().Name == type);

        Assert.Equal(json, DecisionPayloadSerializer.Serialize(sample));
        var read = DecisionPayloadSerializer.Deserialize(sample.GetType(), json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal((sample.RunId, 1), (read.RunId, read.V));
    }

    [Fact]
    public void Each_kind_of_phase_5_reads_its_own_payload_and_replanning_gains_one()
    {
        Assert.Equal(typeof(DebateOpenedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.DebateOpened));
        Assert.Equal(typeof(DebateContributionRecordedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.DebateContributionRecorded));
        Assert.Equal(typeof(DebateRoundClosedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.DebateRoundClosed));
        Assert.Equal(typeof(DebateClosedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.DebateClosed));
        Assert.Equal(typeof(VerdictChallengedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.VerdictChallenged));
        Assert.Equal(typeof(ProjectAuditStartedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ProjectAuditStarted));
        Assert.Equal(typeof(ProjectAuditFinishedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ProjectAuditFinished));
        Assert.Equal(typeof(ReplanRequestedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.ReplanRequested));
    }

    [Fact]
    public void A_round_that_goes_on_names_no_reason_of_judgment_and_a_reply_to_nothing_is_left_out()
    {
        var goesOn = new DebateRoundClosedPayload(1, DebatePhase.Deliberation, null, DebateLimitReached.None, new MetricSnapshot(41_200, MetricProvenance.Exact, "claude result.usage", MetricOrigin.ReportedByProvider), false, 95, "Ronda 1 cerrada") { RunId = Debate };
        var proposal = Contribution with { Index = 0, Round = 1, Kind = ContributionKind.Proposal, RepliesTo = null, Cites = [], UnresolvedCitations = [] };

        Assert.DoesNotContain("judgment", DecisionPayloadSerializer.Serialize(goesOn), StringComparison.Ordinal);
        Assert.DoesNotContain("repliesTo", DecisionPayloadSerializer.Serialize(proposal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_purposes_of_phase_5_are_appended_so_no_stored_start_changes_meaning()
    {
        Assert.Equal(
            ["Planner", "Executor", "Judge", "SkillRecommendation", "DailySummary", "DebateParticipant", "DebateJudge", "ProjectAudit"],
            Enum.GetNames<AgentRunPurpose>());
        Assert.Equal(["WholeProject", "CurrentPlan", "OrchestratorDecisions", "Debate"], Enum.GetNames<AuditScope>());
    }

    /// <summary>
    /// The hash of an entry carrying a Phase 5 payload, against an independent implementation of the canonical form of the
    /// log (every field written as its length, a colon, the value and a semicolon; SHA-256 of the UTF-8 text). A change of
    /// the name of a kind, of the text of a payload or of the canonical form fails here.
    /// </summary>
    [Fact]
    public void An_entry_that_opens_a_debate_hashes_as_the_canonical_form_of_the_log_says()
    {
        var actor = new Actor(ActorKind.System, "agent-studio");
        var json = DecisionPayloadSerializer.Serialize(Opened);
        var log = new DecisionLog();

        var opened = log.Append(T0, actor, DecisionKind.DebateOpened, "Debate adversarial abierto", json);
        var recorded = log.Append(T0.AddMinutes(4), actor, DecisionKind.DebateContributionRecorded, "C3 de P2 publicada", DecisionPayloadSerializer.Serialize(Contribution));

        Assert.Equal(Independent(1, T0, "System", "agent-studio", "DebateOpened", "Debate adversarial abierto", json, DecisionLog.GenesisHash), opened.Hash);
        Assert.Equal(Independent(2, T0.AddMinutes(4), "System", "agent-studio", "DebateContributionRecorded", "C3 de P2 publicada", DecisionPayloadSerializer.Serialize(Contribution), opened.Hash), recorded.Hash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }

    private static string Independent(long sequence, DateTimeOffset at, string actorKind, string actorId, string kind, string summary, string payload, string previous)
    {
        var text = new StringBuilder();
        foreach (var field in new[] { sequence.ToString(CultureInfo.InvariantCulture), at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), actorKind, actorId, kind, summary, payload, string.Empty, previous })
        {
            text.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
