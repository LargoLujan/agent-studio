using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Debate.Fakes.ScriptedDebateWorld;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// The history and the room read back (docs/15 D-41 and D-42): every record comes from replaying the log, with the texts
/// read against their anchored hashes; and the composition of the arena (<c>AddAgentStudioDebateArena</c>).
/// </summary>
public sealed class DebateHistoryTests
{
    private static async Task<IDebateRun> ClosedAsync(ScriptedDebateWorld world)
    {
        world.Claude.Reply = (r, _) => ScriptedDebateProvider.Valid(r, finished: true);
        world.Judgment.Chosen[0] = "C2";
        var run = await world.OpenAsync(world.FreeOnAnthropic());
        await CompletedAsync(run);
        return run;
    }

    [Fact]
    public async Task The_history_lists_each_debate_with_its_question_outcome_and_decision_from_the_replay()
    {
        using var world = new ScriptedDebateWorld();
        await using var run = await ClosedAsync(world);

        var summary = Assert.Single(await world.Coordinator.ListAsync(world.Context, TestContext.Current.CancellationToken));

        Assert.Equal(run.Id, summary.Debate);
        Assert.Equal((DebateMode.Free, DebateSubjectKind.None, "¿Cómo cacheamos las respuestas del servicio?"), (summary.Mode, summary.Subject, summary.Question));
        Assert.Equal((DebatePhase.Closed, DebateOutcome.Resolved, VerdictDecision.Approved, "C2"), (summary.Phase, summary.Outcome, summary.Decision, summary.ChosenProposal));
        Assert.False(summary.Interrupted);
        Assert.False(summary.Challenged);
        Assert.Empty(summary.Problems);
    }

    [Fact]
    public async Task Reading_a_debate_gives_its_participants_contributions_with_their_text_its_verdicts_and_its_outcome()
    {
        using var world = new ScriptedDebateWorld();
        await using var run = await ClosedAsync(world);

        var transcript = await world.Coordinator.ReadAsync(world.Context, run.Id, TestContext.Current.CancellationToken);

        Assert.Empty(transcript.Problems);
        Assert.Equal((world.Frozen.ContentHash, DebateJudgingKind.IndependentJudge), (transcript.RequirementSetHash, transcript.Judging));
        Assert.Equal(
            [("P1", Arquitecto, ProviderId.ClaudeCode, "haiku"), ("P2", Revisor, ProviderId.ClaudeCode, "sonnet")],
            transcript.Participants.Select(p => (p.Label, p.Agent, p.Provider, p.Model)));
        Assert.Equal(["C1", "C2"], transcript.Contributions.Select(c => c.Label));
        Assert.Equal(["P1", "P2"], transcript.Contributions.Select(c => c.Author));
        Assert.All(transcript.Contributions, c => Assert.Equal(world.Evidence.ContentText(c.Evidence), c.Text));
        Assert.Equal([$"ver-{run.Id}-1"], transcript.Verdicts.Select(v => v.Id));
        Assert.Equal((JudgingPanel.Single, "C2"), (transcript.Outcome!.Rule, transcript.Outcome.ChosenProposal));
        Assert.Equal((DebateLimitReached.None, DebateJudgmentReason.Converged), (transcript.LimitReached, transcript.JudgmentReason));
    }

    [Fact]
    public async Task A_contribution_whose_content_no_longer_matches_its_hash_is_shown_without_its_text_and_with_a_problem()
    {
        using var world = new ScriptedDebateWorld();
        await using var run = await ClosedAsync(world);
        var first = (await world.DebateLogAsync(run.Id)).Contributions[0].Payload.Evidence;
        world.Evidence.TamperContent(first, "{\"kind\":\"Proposal\"}"u8.ToArray());

        var transcript = await world.Coordinator.ReadAsync(world.Context, run.Id, TestContext.Current.CancellationToken);

        Assert.Null(transcript.Contributions[0].Text);
        Assert.NotNull(transcript.Contributions[1].Text);
        Assert.Contains(transcript.Problems, p => p.StartsWith("La contribución C1", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(await world.Coordinator.ListAsync(world.Context, TestContext.Current.CancellationToken)).Problems, p => p.StartsWith("La contribución C1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reading_a_debate_this_project_does_not_have_is_refused()
    {
        using var world = new ScriptedDebateWorld();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => world.Coordinator.ReadAsync(world.Context, new RunId("deb-20261020-abcdef"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_arena_is_composed_over_the_ports_of_application_next_to_the_worktrees()
    {
        using var world = new ScriptedDebateWorld();
        var runner = new ScriptedDebateVerificationRunner(world.Evidence);
        var profiles = new ScriptedDebateTestProfiles(null);
        var services = new ServiceCollection()
            .AddSingleton(new OrchestrationPaths(Path.Combine(world.Root, "AgentStudio", "worktrees"), Path.Combine(world.Root, "AgentStudio", "dossiers"), Path.Combine(world.Root, "AgentStudio", "skills")))
            .AddSingleton<IDecisionRecorder>(world.Recorder)
            .AddSingleton<IEvidenceStore>(world.Evidence)
            .AddSingleton<IVerdictStore>(world.Verdicts)
            .AddSingleton<IRequirementStore>(world.Requirements)
            .AddSingleton<IRunRegistry>(world.Runs)
            .AddSingleton<IDebateRegistry>(world.Debates)
            .AddSingleton<IAgentRoster>(world.Roster)
            .AddSingleton(world.Ports.Providers)
            .AddSingleton<IWorktreeManager>(world.Worktrees)
            .AddSingleton<IApprovalGate>(world.Approvals)
            .AddSingleton<IProjectActivity>(world.Activity)
            .AddSingleton<IDebateJudgment>(world.Judgment)
            .AddSingleton<ISecretRedactor>(world.Redactor)
            .AddSingleton<TimeProvider>(world.Clock)
            .AddSingleton<IVerificationRunner>(runner)
            .AddSingleton<IDebateTestProfiles>(profiles)
            .AddAgentStudioDebateArena()
            .AddAgentStudioDebateArena();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var coordinator = provider.GetRequiredService<IDebateCoordinator>();
        var ports = provider.GetRequiredService<DebateArenaPorts>();

        Assert.IsType<DebateCoordinator>(coordinator);
        Assert.Same(coordinator, provider.GetRequiredService<DebateCoordinator>());
        Assert.Equal(Path.Combine(world.Root, "AgentStudio", "debates"), provider.GetRequiredService<DebateArenaOptions>().DebatesRoot);
        Assert.Same(runner, ports.Verification);
        Assert.Same(profiles, ports.TestProfiles);
        Assert.Null(ports.Dossiers);
        Assert.Null(ports.Audits);
        Assert.Single(services, d => d.ServiceType == typeof(IDebateCoordinator));
    }

    [Fact]
    public void Options_registered_before_the_arena_are_kept()
    {
        var options = new DebateArenaOptions(Path.Combine(Path.GetTempPath(), "agentstudio-test-debates"));
        var services = new ServiceCollection().AddSingleton(options).AddAgentStudioDebateArena();

        Assert.Same(options, Assert.Single(services, d => d.ServiceType == typeof(DebateArenaOptions)).ImplementationInstance);
    }
}
