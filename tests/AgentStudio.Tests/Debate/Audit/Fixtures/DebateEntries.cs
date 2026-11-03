using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit.Fixtures;

/// <summary>The payloads of a debate as the coordinator records them (docs/15 D-03), with what the audits and the challenges read.</summary>
internal static class DebateEntries
{
    public static DebateOpenedPayload Opened(
        string debate,
        string? challenges = null,
        DebateMode mode = DebateMode.Adversarial,
        IReadOnlyList<VendorId>? producers = null,
        IReadOnlyList<DebateParticipantEntry>? participants = null,
        IReadOnlyList<VendorId>? waived = null,
        DebateJudgingKind judging = DebateJudgingKind.IndependentJudge,
        IReadOnlyList<DebateJudgeEntry>? judges = null) =>
        new(
            mode,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new EvidenceId("ev-pregunta-" + debate),
            new string('d', 64),
            challenges is null ? DebateSubjectKind.None : DebateSubjectKind.Verdict,
            challenges,
            null,
            participants ??
            [
                new DebateParticipantEntry(new AgentId("arquitecto"), DebateRole.Proponent, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku", "P1"),
                new DebateParticipantEntry(new AgentId("critico"), DebateRole.Critic, ProviderId.ClaudeCode, VendorId.Anthropic, "sonnet", "P2"),
            ],
            judges ?? [new DebateJudgeEntry(Judge, ProviderId.CodexCli, VendorId.OpenAI, null)],
            judging,
            producers ?? [],
            waived ?? [],
            4,
            1800,
            400_000,
            10,
            challenges)
        {
            RunId = debate,
        };

    public static DebateClosedPayload Closed(string debate, DebateOutcome outcome, string rule, params string[] verdicts) =>
        new(outcome, verdicts, rule, null, "Cierre del debate.") { RunId = debate };
}
