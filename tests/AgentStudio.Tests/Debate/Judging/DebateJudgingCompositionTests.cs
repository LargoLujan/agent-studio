using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Debate.Judging;

/// <summary>docs/15 §9, integration: the judgment of debates comes in with <c>AddAgentStudioJudging</c>, beside the review of a run.</summary>
public sealed class DebateJudgingCompositionTests
{
    private static ServiceCollection Platform(DebateWorld world, IProjectAgentProviders providers)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionRecorder>(world.World.Recorder);
        services.AddSingleton<IRunRegistry>(world.World.Runs);
        services.AddSingleton<IPlanStore>(world.World.Plans);
        services.AddSingleton<IRequirementStore>(world.World.Requirements);
        services.AddSingleton<IEvidenceStore>(world.World.Evidence);
        services.AddSingleton<IVerdictStore>(world.World.Verdicts);
        services.AddSingleton<IDebateRegistry>(world.Debates);
        services.AddSingleton(providers);
        services.AddSingleton<TimeProvider>(world.World.Clock);
        services.AddSingleton(new OrchestrationPaths(Path.Combine(world.World.Root, "wt"), world.World.DossierRoot, Path.Combine(world.World.Root, "skills")));
        return services;
    }

    [Fact]
    public async Task The_judgment_resolved_from_the_container_judges_a_debate_end_to_end()
    {
        using var world = await DebateWorld.CreateAsync();
        await world.ChoiceDebateAsync(DebateWorld.OneVendor());
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => DebateAnswers.Run(DebateAnswers.Choosing(request.Prompt, "P2"), "thread-1", null));
        using var container = Platform(world, new FakeProjectAgentProviders(codex)).AddAgentStudioJudging().BuildServiceProvider();

        var judgment = container.GetRequiredService<IDebateJudgment>();
        var result = await judgment.JudgeAsync(world.World.ProjectContext, DebateWorld.Debate, null, TestContext.Current.CancellationToken);

        Assert.IsType<DebateJudgment>(judgment);
        Assert.Same(container.GetRequiredService<DebateDossierBuilder>(), container.GetRequiredService<DebateDossierBuilder>());
        Assert.Equal((DebateOutcome.Resolved, VerdictDecision.Approved, "P2"), (result.Outcome.Outcome, result.Outcome.Decision, result.Outcome.ChosenProposal));
        Assert.StartsWith(world.World.DossierRoot, Assert.Single(codex.Requests).WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<IndependentReview>(container.GetRequiredService<IIndependentReview>());
    }
}
