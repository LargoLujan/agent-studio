using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>
/// docs/15 D-34 and D-37: a chain that does not verify is not audited (the result is the verification itself and nothing
/// is recorded), and the material of the run (plan, header, requirements) is used only as <c>RunStarted</c> anchored it.
/// </summary>
public sealed class OrchestratorAuditIntegrityTests
{
    [Fact]
    public async Task A_log_with_an_entry_edited_behind_the_triggers_gives_its_verification_and_nothing_to_record()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false); // a critical finding, were the log trusted
        var edited = run.Entries.First(e => e.Kind == DecisionKind.TaskAssigned && e.PayloadJson.Contains("ejecutor", StringComparison.Ordinal));
        run.Store.Tamper(AuditWorld.Project, edited.Sequence, e => e with { PayloadJson = e.PayloadJson.Replace("ejecutor", "juez", StringComparison.Ordinal) });

        var outcome = await run.AuditAsync();

        Assert.False(outcome.Log.IsValid);
        Assert.Equal(edited.Sequence, outcome.Log.FirstInvalidSequence);
        Assert.Empty(outcome.Findings);
        Assert.Empty(outcome.NewFindings);
        Assert.Null(outcome.Replan);
        Assert.False(outcome.HasSomethingToRecord);
    }

    [Fact]
    public async Task A_log_whose_end_was_cut_off_gives_its_verification_and_nothing_to_record()
    {
        var run = await PreparedRun.CleanAsync();
        await run.DeliverAsync(approve: false);
        run.LogStore.CutLast = 2;

        var outcome = await run.AuditAsync();

        Assert.False(outcome.Log.IsValid);
        Assert.Contains("removed from its end", outcome.Log.Problem, StringComparison.Ordinal);
        Assert.Empty(outcome.Findings);
        Assert.False(outcome.HasSomethingToRecord);
    }

    [Fact]
    public async Task A_run_the_log_never_started_cannot_be_audited()
    {
        var run = await PreparedRun.CleanAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => run.AuditAsync(input => input with { Run = new RunId("20261019-ffffff") }));

        Assert.Contains("20261019-ffffff", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_that_is_not_the_one_RunStarted_anchored_is_a_replay_finding_and_is_not_used()
    {
        var run = await PreparedRun.CleanAsync();
        var other = Plan(Writer("T1") with { Agent = Judge });

        var outcome = await run.AuditAsync(input => input with { Plan = other });

        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.ReplayCode, finding.Code);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains(other.ContentHash, finding.Message, StringComparison.Ordinal);
        Assert.Contains(run.Plan.ContentHash, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_missing_from_the_store_is_a_replay_finding()
    {
        var run = await PreparedRun.CleanAsync();

        var outcome = await run.AuditAsync(input => input with { Plan = null });

        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.ReplayCode, finding.Code);
        Assert.Contains(run.Plan.ContentHash, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_header_rewritten_after_the_start_is_a_replay_finding_and_its_roster_is_not_used()
    {
        var run = await PreparedRun.CleanAsync();
        run.Runs.Replace(Run, header => header with { Roster = [.. header.Roster.Select(a => a.Id == Executor ? a with { AllowedTools = [.. a.AllowedTools, "Bash"] } : a)] });

        var outcome = await run.AuditAsync();

        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.ReplayCode, finding.Code);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Contains("header", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_started_without_the_hash_of_its_header_is_a_medium_replay_finding()
    {
        var run = PreparedRun.Create();
        await run.StartAsync(anchorHeader: false);
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1");
        await run.ReviewAsync("R1");
        await run.FinishAsync();

        var outcome = await run.AuditAsync();

        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.ReplayCode, finding.Code);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains("version 1", finding.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Replan);
    }

    [Fact]
    public async Task A_profile_rewritten_after_the_start_is_one_replay_finding_and_asks_nothing_of_the_integration()
    {
        // Against the profile RunStarted anchored, an integration without tests is a finding; against an unknown one, what
        // was required is not known, and the header that changed is the one finding.
        var run = PreparedRun.Create();
        await run.StartAsync();
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1", tests: false);
        await run.ReviewAsync("R1");
        await run.FinishAsync();
        run.Runs.Replace(Run, header => header with { Verification = ProfileWithoutTests() });

        var outcome = await run.AuditAsync();

        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.ReplayCode, finding.Code);
        Assert.Contains("header", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_started_as_version_1_is_judged_against_the_profile_its_RunStarted_anchored(bool profileIntact)
    {
        var run = PreparedRun.Create();
        await run.StartAsync(anchorHeader: false);
        await run.WorkAsync("T1");
        await run.IntegrateAsync("I1", tests: false);
        await run.ReviewAsync("R1");
        await run.FinishAsync();
        if (!profileIntact)
        {
            run.Runs.Replace(Run, header => header with { Verification = ProfileWithoutTests() });
        }

        var outcome = await run.AuditAsync();

        if (profileIntact)
        {
            // Version 1 anchors no header, but it anchors the profile: its test step asks for a TestRun.
            Assert.Equal([OrchestratorAudit.IntegrationUnverifiedCode, OrchestratorAudit.ReplayCode], outcome.Findings.Select(f => f.Code));
            Assert.Equal(Severity.Medium, outcome.Findings.Single(f => f.Code == OrchestratorAudit.ReplayCode).Severity);
        }
        else
        {
            Assert.Equal([OrchestratorAudit.ReplayCode, OrchestratorAudit.ReplayCode], outcome.Findings.Select(f => f.Code));
            Assert.Equal([Severity.Medium, Severity.High], outcome.Findings.Select(f => f.Severity));
            Assert.Contains("verification profile", outcome.Findings[1].Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Requirements_that_are_not_the_ones_RunStarted_anchored_are_an_instructions_finding()
    {
        var run = await PreparedRun.CleanAsync();
        var other = FrozenRequirementSet.Freeze([run.Frozen.Requirements[0] with { Statement = "Otra cosa" }], T0);

        var outcome = await run.AuditAsync(input => input with { Requirements = other });

        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(OrchestratorAudit.InstructionsCode, finding.Code);
        Assert.Contains(run.Frozen.ContentHash, finding.Message, StringComparison.Ordinal);
        Assert.Contains(other.ContentHash, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_whose_content_could_not_be_opened_is_named_never_trusted()
    {
        var run = await PreparedRun.CleanAsync();

        var outcome = await run.AuditAsync(input => input with { Evidence = new Dictionary<EvidenceId, ReadOnlyMemory<byte>>() });

        Assert.Equal([OrchestratorAudit.InstructionsCode, OrchestratorAudit.PermissionsCode], outcome.Findings.Select(f => f.Code).Distinct());
        Assert.All(outcome.Findings, f => Assert.Contains("cannot be read", f.Message, StringComparison.Ordinal));
    }
}
