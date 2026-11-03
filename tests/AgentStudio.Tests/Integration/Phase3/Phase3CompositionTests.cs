using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Providers.Composition;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Git;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// The composition root of Phase 3 as the app will build it (docs/13 section 10, "Integración"): every module through
/// its own extension method, and only the folders, the approval dialog and the skill provisioner decided by the root.
/// The container validates on build, every port resolves to its module's implementation, and the agent providers of a
/// project are all composed with its prompt journal; composing launches nothing and writes nothing to a project. Opening
/// a repository through the composed app works against the real stores and leaves the repository clean.
/// </summary>
public sealed class Phase3CompositionTests
{
    [Fact]
    public async Task Every_module_composes_into_one_orchestrator_whose_agent_providers_are_all_journaled()
    {
        using var temp = new TempDirectory();
        var paths = new Phase3Paths(temp.Combine("LocalAppData", "AgentStudio"));
        await using var container = Phase3Composition.Services(paths, new ScriptedApprovalPrompt())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<Orchestrator>(container.GetRequiredService<IOrchestrator>());
        Assert.Same(container.GetRequiredService<IOrchestrator>(), container.GetRequiredService<IOrchestrator>());

        // Each port the engine reaches the world through is the real implementation of its module, once for the app.
        Assert.IsType<GitWorktreeManager>(container.GetRequiredService<IWorktreeManager>());
        Assert.Same(container.GetRequiredService<GitWorktreeManager>(), container.GetRequiredService<IWorktreeManager>());
        Assert.IsType<VerificationRunner>(container.GetRequiredService<IVerificationRunner>());
        Assert.IsType<Integrator>(container.GetRequiredService<IIntegrator>());
        Assert.IsType<DeliveryService>(container.GetRequiredService<IDeliveryService>());
        Assert.IsType<WorkspaceCleanup>(container.GetRequiredService<IWorkspaceCleanup>());
        Assert.IsType<IndependentReview>(container.GetRequiredService<IIndependentReview>());
        Assert.IsType<AgentJudgeFactory>(container.GetRequiredService<IAgentJudgeFactory>());
        Assert.IsType<ApprovalGate>(container.GetRequiredService<IApprovalGate>());
        Assert.IsType<SkillProvisioner>(container.GetRequiredService<ISkillProvisioner>());
        Assert.IsType<DecisionRecorder>(container.GetRequiredService<IDecisionRecorder>());
        Assert.IsType<ProjectWorkspace>(container.GetRequiredService<IProjectWorkspace>());
        Assert.IsType<SqliteDecisionLogStore>(container.GetRequiredService<IDecisionLogStore>());
        Assert.IsType<SqliteEvidenceStore>(container.GetRequiredService<IEvidenceStore>());
        Assert.IsType<SqlitePlanStore>(container.GetRequiredService<IPlanStore>());
        Assert.IsType<SqliteRunRegistry>(container.GetRequiredService<IRunRegistry>());
        Assert.IsType<SqliteRequirementStore>(container.GetRequiredService<IRequirementStore>());
        Assert.IsType<SqliteVerdictStore>(container.GetRequiredService<IVerdictStore>());
        Assert.IsType<SqliteAgentRoster>(container.GetRequiredService<IAgentRoster>());

        // The folders are the ones of the composition root, never the defaults under the real %LOCALAPPDATA%.
        Assert.Equal(paths.Orchestration, container.GetRequiredService<OrchestrationPaths>());
        Assert.Equal(paths.WorktreeRoot, container.GetRequiredService<GitWorktreeOptions>().WorktreeRoot);
        Assert.Equal(paths.DossierRoot, container.GetRequiredService<JudgingOptions>().DossierRoot);

        // One provider per CLI for a project, each one only reachable through the prompt journal (criterion F2-9).
        var root = temp.CreateDirectory("proyecto");
        var providers = container.GetRequiredService<IProjectAgentProviders>().For(root);
        Assert.Equal([ProviderId.ClaudeCode, ProviderId.CodexCli, ProviderId.GeminiCli], providers.Select(p => p.Provider));
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p), $"The provider {p.Provider} is not journaled."));
        Assert.All(providers, p => Assert.IsType<ConcurrencyLimitedAgentProvider>(p));
        Assert.Same(providers, container.GetRequiredService<IProjectAgentProviders>().For(root));
        Assert.False(Directory.Exists(Path.Combine(root, ".agentstudio")), "Composing the app wrote into the project.");
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task The_composed_app_opens_a_repository_and_its_orchestrator_answers_from_the_real_stores()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = TempGitRepo.Create();
        await using var app = await Phase3App.OpenAsync(repo.Root, Phase3Paths.Beside(repo.Root), new ScriptedApprovalPrompt(), providers: null);

        Assert.Empty(await app.Orchestrator.ListRunsAsync(app.Project, ct));
        var log = await app.LoadLogAsync();
        Assert.True(log.Verification.IsValid);
        Assert.Empty(log.Entries);

        // Opening the project wrote .agentstudio/ and excluded its database through info/exclude: the repository is clean.
        var inspected = await app.Worktrees.InspectAsync(repo.Root, ct);
        Assert.Equal(repo.Head, inspected.HeadCommit);
        Assert.Equal("main", inspected.HeadBranch);
        Assert.False(inspected.HasUncommittedChanges);
        Assert.True(File.Exists(app.Storage.DatabasePath));

        // The real providers of the project, every one journaled; nothing was launched, so nothing was journaled yet.
        var providers = app.Services.GetRequiredService<IProjectAgentProviders>().For(repo.Root);
        Assert.Equal(3, providers.Count);
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p)));
        Assert.Empty(Phase3Checks.JournalFiles(repo.Root));
    }
}
