using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Debate.Arena;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// F5-1: the six modes, with participants on both scripted CLIs (claude-code and codex-cli): the roles each mode needs, the
/// material of its subject, the disposition of the judgment with its waivers recorded, and the result (docs/15 D-07 to D-14).
/// </summary>
public sealed class DebateModeScenarioTests
{
    public static TheoryData<DebateMode, DebateRole, DebateRole> ChoiceModes => new()
    {
        { DebateMode.Free, DebateRole.Proponent, DebateRole.Critic },
        { DebateMode.Adversarial, DebateRole.Proponent, DebateRole.Critic },
        { DebateMode.ArchitectureReview, DebateRole.Proponent, DebateRole.Panelist },
        { DebateMode.Tribunal, DebateRole.Panelist, DebateRole.Panelist },
    };

    [Theory]
    [MemberData(nameof(ChoiceModes))]
    public async Task A_mode_that_chooses_with_both_vendors_is_judged_by_a_cross_vendor_panel_with_the_waivers_recorded(DebateMode mode, DebateRole first, DebateRole second)
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Codex.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Judgment.Chosen[0] = "C1";

        var request = world.Request(mode, [(Arquitecto, first), (Critico, second)], [JuezClaude, JuezCodex], DebateJudgingKind.CrossVendorPanel);
        await using var run = await world.OpenAsync(request);
        var result = await CompletedAsync(run);

        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved, JudgingPanel.Agree), (result.Phase, result.Outcome, result.Panel!.Rule));
        Assert.Equal("C1", result.Panel.ChosenProposal);
        Assert.Equal(2, result.Verdicts.Count);
        var log = await world.DebateLogAsync(run.Id);
        var opened = log.Opened!;
        Assert.Equal(DebateJudgingKind.CrossVendorPanel, opened.Judging);
        Assert.Equal([VendorId.Anthropic, VendorId.OpenAI], opened.WaivedParticipantVendors.OrderBy(v => v.Value, StringComparer.Ordinal));
        Assert.Equal([JuezClaude, JuezCodex], opened.Judges.Select(j => j.Agent));
        Assert.Equal(["P1", "P2"], opened.Participants.Select(p => p.Label));
        Assert.Equal([ProviderId.ClaudeCode, ProviderId.CodexCli], opened.Participants.Select(p => p.Provider));
        Assert.Single(world.Claude.Requests);
        Assert.Single(world.Codex.Requests);
        if (mode == DebateMode.Tribunal)
        {
            Assert.Equal(DebateJudgmentReason.Converged, result.JudgmentReason);
            Assert.All(log.Contributions, c => Assert.Equal(ContributionKind.Assessment, c.Payload.Kind));
        }
    }

    [Fact]
    public async Task A_tribunal_does_not_deliberate_even_when_its_panelists_have_more_to_say()
    {
        using var world = new ScriptedDebateWorld();

        var request = world.Request(DebateMode.Tribunal, [(Arquitecto, DebateRole.Panelist), (Revisor, DebateRole.Panelist)], [JuezCodex]);
        await using var run = await world.OpenAsync(request);
        var result = await CompletedAsync(run);

        Assert.Equal(DebateJudgmentReason.Tribunal, result.JudgmentReason);
        var log = await world.DebateLogAsync(run.Id);
        Assert.Equal(2, log.Contributions.Count);
        Assert.Equal(DebateJudgmentReason.Tribunal, Assert.Single(log.RoundsClosed).Payload.Judgment);
    }

    [Fact]
    public async Task An_implementation_audit_of_a_run_by_anthropic_is_judged_by_the_non_producer_vendor_on_the_copy_of_its_candidate()
    {
        using var world = new ScriptedDebateWorld();
        await world.RecordFinishedRunAsync();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Codex.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        var request = world.Request(
            DebateMode.ImplementationAudit,
            [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)],
            [JuezClaude, JuezCodex],
            DebateJudgingKind.NonProducerJudge,
            new DebateSubject(DebateSubjectKind.Run, FinishedRun.Value));
        await using var run = await world.OpenAsync(request);
        var result = await CompletedAsync(run);

        Assert.Equal(DebateOutcome.Resolved, result.Outcome);
        var opened = (await world.DebateLogAsync(run.Id)).Opened!;
        Assert.Equal(DebateJudgingKind.NonProducerJudge, opened.Judging);
        Assert.Equal([JuezCodex], opened.Judges.Select(j => j.Agent));
        Assert.Equal([VendorId.Anthropic], opened.ProducerVendors);
        Assert.Equal([VendorId.OpenAI], opened.WaivedParticipantVendors);
        Assert.Equal(Candidate, opened.SnapshotCommit);
        Assert.Contains(world.Worktrees.Archived, a => a.Commit == Candidate);
        Assert.All(world.Codex.Requests.Concat(world.Claude.Requests), r => Assert.EndsWith(Path.Combine("snapshot"), r.WorkingDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_orchestrator_review_is_judged_with_orq_1_to_orq_7_by_the_non_producer_vendor()
    {
        using var world = new ScriptedDebateWorld();
        await world.RecordFinishedRunAsync();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Codex.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);

        var request = world.Request(
            DebateMode.OrchestratorReview,
            [(Revisor, DebateRole.Critic), (Critico, DebateRole.Panelist)],
            [JuezCodex],
            DebateJudgingKind.NonProducerJudge,
            new DebateSubject(DebateSubjectKind.Run, FinishedRun.Value),
            requirements: DebateModes.OrchestratorReview(T0));
        await using var run = await world.OpenAsync(request);
        var result = await CompletedAsync(run);

        Assert.Equal(DebateOutcome.Resolved, result.Outcome);
        var requirements = DebateModes.OrchestratorReview(T0);
        Assert.Equal(["orq-1", "orq-2", "orq-3", "orq-4", "orq-5", "orq-6", "orq-7"], requirements.Requirements.Select(r => r.Id.Value));
        Assert.All(requirements.Requirements, r => Assert.Equal(VerificationMethod.JudgeAssessment, Assert.Single(r.Criteria).Method));
        Assert.Equal(requirements.ContentHash, (await world.DebateLogAsync(run.Id)).Opened!.RequirementSetHash);
    }

    [Fact]
    public async Task An_orchestrator_review_with_other_requirements_is_refused()
    {
        using var world = new ScriptedDebateWorld();
        await world.RecordFinishedRunAsync();

        var proposal = await world.Coordinator.PrepareAsync(
            world.Request(DebateMode.OrchestratorReview, [(Revisor, DebateRole.Critic), (Critico, DebateRole.Panelist)], [JuezCodex], DebateJudgingKind.NonProducerJudge, new DebateSubject(DebateSubjectKind.Run, FinishedRun.Value)),
            TestContext.Current.CancellationToken);

        Assert.Contains(proposal.Problems, p => p.Contains("orq-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_judge_of_the_producers_vendor_is_ever_seated_and_the_audit_does_not_open_without_one()
    {
        using var world = new ScriptedDebateWorld();
        await world.RecordFinishedRunAsync();

        var proposal = await world.Coordinator.PrepareAsync(
            world.Request(DebateMode.ImplementationAudit, [(Arquitecto, DebateRole.Proponent), (Revisor, DebateRole.Critic)], [JuezClaude], DebateJudgingKind.IndependentJudge, new DebateSubject(DebateSubjectKind.Run, FinishedRun.Value)),
            TestContext.Current.CancellationToken);

        Assert.Contains(proposal.Problems, p => p.Contains("IndependentJudge", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken));
        Assert.DoesNotContain((await world.LogAsync()).Entries, e => e.Kind != DecisionKind.RunStarted && e.Kind != DecisionKind.IntegrationPerformed);
    }

    [Fact]
    public async Task An_implementation_audit_without_a_candidate_commit_does_not_open()
    {
        using var world = new ScriptedDebateWorld();

        var proposal = await world.Coordinator.PrepareAsync(
            world.Request(DebateMode.ImplementationAudit, [(Arquitecto, DebateRole.Proponent), (Critico, DebateRole.Critic)], [JuezCodex], DebateJudgingKind.NonProducerJudge, new DebateSubject(DebateSubjectKind.Run, "20261019-aaaaaa")),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(proposal.Problems);
    }

    [Theory]
    [InlineData(DebateMode.Adversarial, DebateRole.Proponent, DebateRole.Proponent, "debate.adversarial-roles")]
    [InlineData(DebateMode.Tribunal, DebateRole.Panelist, DebateRole.Critic, "debate.tribunal-roles")]
    public async Task The_roles_a_mode_needs_are_checked_before_anything_is_written(DebateMode mode, DebateRole first, DebateRole second, string rule)
    {
        using var world = new ScriptedDebateWorld();

        var proposal = await world.Coordinator.PrepareAsync(world.Request(mode, [(Arquitecto, first), (Revisor, second)], [JuezCodex]), TestContext.Current.CancellationToken);

        Assert.Contains(proposal.Problems, p => p.StartsWith(rule, StringComparison.Ordinal));
        Assert.Empty((await world.LogAsync()).Entries);
    }

    [Fact]
    public async Task A_judge_of_the_template_is_never_a_participant_and_a_participant_is_never_a_judge()
    {
        using var world = new ScriptedDebateWorld();

        var proposal = await world.Coordinator.PrepareAsync(
            world.Request(DebateMode.Free, [(Arquitecto, DebateRole.Proponent), (JuezClaude, DebateRole.Critic)], [Revisor]),
            TestContext.Current.CancellationToken);

        Assert.Contains(proposal.Problems, p => p.Contains("juez-claude", StringComparison.Ordinal));
        Assert.Contains(proposal.Problems, p => p.Contains("revisor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_debate_about_the_head_commit_works_on_a_copy_of_it_and_says_uncommitted_changes_do_not_enter()
    {
        using var world = new ScriptedDebateWorld();
        world.Worktrees.HasUncommittedChanges = true;
        var seen = new List<(bool Copy, bool Material)>();
        world.Claude.Reply = (r, _) =>
        {
            lock (seen)
            {
                seen.Add((File.Exists(Path.Combine(r.WorkingDirectory, "src", "Cache.cs")), File.Exists(Path.Combine(r.Permissions.AdditionalDirectories[0], DebateMaterial.FileName))));
            }

            return ScriptedDebateProvider.Valid(r, finished: true);
        };

        var request = world.FreeOnAnthropic(subject: new DebateSubject(DebateSubjectKind.Commit, null));
        var proposal = await world.Coordinator.PrepareAsync(request, TestContext.Current.CancellationToken);
        Assert.Contains(proposal.Estimate.Notes, n => n.Contains("sin confirmar", StringComparison.Ordinal));
        await using var run = await world.Coordinator.OpenAsync(proposal, TestContext.Current.CancellationToken);
        await CompletedAsync(run);

        var (commit, destination) = Assert.Single(world.Worktrees.Archived);
        Assert.Equal(world.Worktrees.HeadCommit, commit);
        Assert.Equal([(true, true), (true, true)], seen);
        Assert.All(world.Claude.Requests, r =>
        {
            Assert.Equal(destination, r.WorkingDirectory);
            Assert.Single(r.Permissions.AdditionalDirectories);
        });
        Assert.Equal(world.Worktrees.HeadCommit, (await world.DebateLogAsync(run.Id)).Opened!.SnapshotCommit);
        Assert.False(Directory.Exists(destination), "the copy of the commit is removed when the debate closes");
    }

    [Fact]
    public async Task A_debate_about_nothing_works_in_the_folder_of_its_turn()
    {
        using var world = new ScriptedDebateWorld();
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        var folders = new List<string>();
        world.Claude.Reply = (r, _) =>
        {
            lock (folders)
            {
                folders.Add(r.WorkingDirectory);
                Assert.True(File.Exists(Path.Combine(r.WorkingDirectory, DebateMaterial.FileName)));
            }

            return ScriptedDebateProvider.Valid(r, finished: true);
        };

        await using var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);

        Assert.Equal(2, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(world.Claude.Requests, r => Assert.Empty(r.Permissions.AdditionalDirectories));
        Assert.Empty(world.Worktrees.Archived);
    }
}
