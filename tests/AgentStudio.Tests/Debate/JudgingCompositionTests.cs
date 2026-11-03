using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Debate;

public sealed class JudgingCompositionTests
{
    /// <summary>What the rest of the app registers (persistence, recorder, providers, clock, paths), here with the in-memory stores of a prepared run.</summary>
    private static ServiceCollection Platform(JudgingWorld world, IProjectAgentProviders providers)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionRecorder>(world.Recorder);
        services.AddSingleton<IRunRegistry>(world.Runs);
        services.AddSingleton<IPlanStore>(world.Plans);
        services.AddSingleton<IRequirementStore>(world.Requirements);
        services.AddSingleton<IEvidenceStore>(world.Evidence);
        services.AddSingleton<IVerdictStore>(world.Verdicts);
        services.AddSingleton(providers);
        services.AddSingleton<TimeProvider>(world.Clock);
        services.AddSingleton(new OrchestrationPaths(Path.Combine(world.Root, "wt"), world.DossierRoot, Path.Combine(world.Root, "skills")));
        return services;
    }

    [Fact]
    public async Task The_review_resolved_from_the_container_judges_a_run_end_to_end()
    {
        using var world = await JudgingWorld.CreateAsync();
        var codex = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (request, _) => JudgeAnswers.Run(PromptData.EchoingApproval(request.Prompt, world.TestEvidence.Id)));
        using var container = Platform(world, new FakeProjectAgentProviders(codex)).AddAgentStudioJudging().BuildServiceProvider();

        var outcome = await container.GetRequiredService<IIndependentReview>().ReviewRunAsync(
            new ReviewRequest(world.ProjectContext, JudgingWorld.Run, JudgingWorld.Review, JudgingWorld.Judge, world.Workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(ReviewDisposition.Approved, outcome.Disposition);
        Assert.Contains(Assert.Single(codex.Requests).Permissions.AdditionalDirectories, d => d.StartsWith(world.DossierRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_dossier_builder_is_one_instance_behind_both_ports_and_writes_under_the_configured_dossier_root()
    {
        using var world = await JudgingWorld.CreateAsync();
        using var container = Platform(world, new FakeProjectAgentProviders()).AddAgentStudioJudging().BuildServiceProvider();

        var contract = container.GetRequiredService<IJudgeDossierBuilder>();

        Assert.Same(contract, container.GetRequiredService<IJudgeDossierPackageBuilder>());
        Assert.IsType<AgentJudgeFactory>(container.GetRequiredService<IAgentJudgeFactory>());
        var package = await container.GetRequiredService<IJudgeDossierPackageBuilder>().BuildPackageAsync(
            new DossierRequest(JudgingWorld.Project, AuditScope.CurrentPlan, JudgingWorld.Run.Value), TestContext.Current.CancellationToken);
        Assert.StartsWith(world.DossierRoot, package.Files.Folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Judging_options_registered_earlier_are_kept()
    {
        using var world = await JudgingWorld.CreateAsync();
        var services = Platform(world, new FakeProjectAgentProviders());
        var other = Path.Combine(world.Root, "otros-dossiers");
        services.AddSingleton(new JudgingOptions(other));

        using var container = services.AddAgentStudioJudging().BuildServiceProvider();
        var package = await container.GetRequiredService<IJudgeDossierPackageBuilder>().BuildPackageAsync(
            new DossierRequest(JudgingWorld.Project, AuditScope.CurrentPlan, JudgingWorld.Run.Value), TestContext.Current.CancellationToken);

        Assert.StartsWith(other, package.Files.Folder, StringComparison.OrdinalIgnoreCase);
    }
}
