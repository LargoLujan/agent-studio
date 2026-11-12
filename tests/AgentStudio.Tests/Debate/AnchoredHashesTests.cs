using AgentStudio.Application.Decisions;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Common;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Debate.Audit.Milestones;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// What Debate anchors or hands to a judge does not change when its on-screen texts become coded (docs/17 D-48, ADR-0054 addendum of
/// 2026-11-12): the header hash of a debate proposal, that of an audit proposal, the report of a milestone (D-49) and the pending
/// problems the dossier of the judge lists. The values were taken on the commit before the change.
/// </summary>
public sealed class AnchoredHashesTests
{
    private const string DebateProposalHash = "7894532f037ea18871f152a0405cd083c2b704ba15ffd658840031ea2d6e168e";

    // Taken again when P23 reached this branch (2026-11-12): the request of a whole-project audit now says which tests its profile does
    // not exercise (ADR-0017 addendum), which is part of the text the header anchors. The value before it was 397cf41b….
    private const string AuditProposalHash = "86af7d6036369e649820f89fc3263a054ccc03ec06b0ac0bf39084c0ca6ebbb7";
    private const string MilestoneReportHash = "b16b76a4b73ac2d45d25176815f3876238a985a985e41583939b48aaf81a6a3b";
    private const string PendingMessagesHash = "e28c510b28852013475e31e5406c70522d5164d45ea0b40a7aa9a8cc2f71e6ed";

    private static void Pinned(string name, string actual, string expected) =>
        Assert.True(string.Equals(actual, expected, StringComparison.Ordinal), $"{name}={actual}");

    [Fact]
    public async Task The_header_hash_of_a_debate_proposal_is_the_one_anchored_before()
    {
        using var world = new ScriptedDebateWorld();

        var proposal = await world.Coordinator.PrepareAsync(world.FreeOnAnthropic(), TestContext.Current.CancellationToken);

        Pinned("debate", proposal.HeaderHash, DebateProposalHash);
    }

    [Fact]
    public async Task The_header_hash_of_an_audit_proposal_is_the_one_anchored_before()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync();
        using var bench = new ProjectAuditBench(run);

        var proposal = await bench.PrepareAsync(bench.Request());

        Pinned("audit", proposal.HeaderHash, AuditProposalHash);
    }

    [Fact]
    public async Task The_report_of_a_milestone_keeps_its_sha256()
    {
        using var bench = await MilestoneBench.WithRunAsync();
        var before = bench.Entries.Count;

        await bench.CloseAsync();

        var closed = DecisionPayloadSerializer.Deserialize<MilestoneClosedPayload>(bench.Entries[^1].PayloadJson);
        var report = bench.Audit.World.Evidence.ContentText(closed.Report)!;
        // What changes from run to run (the audit id, the verdict id and the head of the log, from the ids the scripted world draws) is masked.
        var stable = System.Text.RegularExpressions.Regex.Replace(report, "\"(logHeadHash|boardLogHeadHash|verdictId)\": \"[0-9a-f]+\"|aud-\\d{8}-[0-9a-f]{6}", "<volatile>");
        Pinned("milestone", ContentHash.Sha256Hex(stable), MilestoneReportHash);
        Assert.True(bench.Entries.Count > before);
    }

    [Fact]
    public async Task The_pending_problems_the_judge_reads_keep_their_code_source_and_message()
    {
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");

        var problems = PendingProblems.From(run.Entries);

        var text = string.Join("\n", problems.Select(p => $"{p.Code}|{p.Source}|{p.Message}"));
        Pinned("pending", ContentHash.Sha256Hex(text), PendingMessagesHash);
    }
}
