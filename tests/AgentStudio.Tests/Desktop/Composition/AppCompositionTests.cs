using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Composition;
using AgentStudio.Domain.Debate;
using AgentStudio.Desktop.Debate;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Arena;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Models;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Observability.Analysis;
using AgentStudio.Observability.Quota;
using Engine = AgentStudio.Orchestration.Engine;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Composition;
using AgentStudio.Security.Approvals;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Desktop.Composition;

/// <summary>
/// The composition the app builds at startup (<see cref="AppComposition.AddAgentStudioApp"/>, what App.OnStartup calls),
/// over temporary folders: the Phase 3 engine and every port it runs on resolve, no agent provider skips the journal,
/// and the orchestrator tab of the open project is connected to the real engine and follows a project switch. Nothing is
/// shown and no CLI is launched: only stores, files and the composed objects are touched.
/// </summary>
public sealed class AppCompositionTests
{
    private static AppFolders FoldersIn(TempDirectory temp, string? defaultSkillsIndex = null) =>
        new(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), defaultSkillsIndex);

    private static ServiceProvider Build(AppFolders folders)
    {
        var services = new ServiceCollection();

        // Nothing of the Windows session nor of its power transitions is hooked by a test (see WindowsDoubles).
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(folders);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static Task OpenOrchestratorAsync(MainViewModel main)
    {
        var activity = main.Activities.Single(a => a.Id == "orchestrator");
        return ((IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);
    }

    [Fact]
    public void The_engine_and_every_port_it_runs_on_resolve()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        using var provider = Build(folders);

        Assert.IsType<Engine.Orchestrator>(provider.GetRequiredService<IOrchestrator>());
        Assert.NotNull(provider.GetRequiredService<IIndependentReview>());
        Assert.IsType<ApprovalGate>(provider.GetRequiredService<IApprovalGate>());
        Assert.IsType<GitWorktreeManager>(provider.GetRequiredService<IWorktreeManager>());
        Assert.NotNull(provider.GetRequiredService<IVerificationRunner>());
        Assert.NotNull(provider.GetRequiredService<IIntegrator>());
        Assert.NotNull(provider.GetRequiredService<IDeliveryService>());
        Assert.NotNull(provider.GetRequiredService<IWorkspaceCleanup>());
        Assert.NotNull(provider.GetRequiredService<IProjectWorkspace>());
        Assert.NotNull(provider.GetRequiredService<IDecisionLogStore>());
        Assert.NotNull(provider.GetRequiredService<IEvidenceStore>());
        Assert.NotNull(provider.GetRequiredService<IRequirementStore>());
        Assert.NotNull(provider.GetRequiredService<IPlanStore>());
        Assert.NotNull(provider.GetRequiredService<IRunRegistry>());
        Assert.NotNull(provider.GetRequiredService<IVerdictStore>());
        Assert.NotNull(provider.GetRequiredService<IAgentRoster>());
        Assert.NotNull(provider.GetRequiredService<IDecisionRecorder>());
        Assert.IsType<SkillProvisioner>(provider.GetRequiredService<ISkillProvisioner>());
        Assert.NotNull(provider.GetRequiredService<ISkillCatalog>());
        Assert.NotNull(provider.GetRequiredService<ISkillActivationStore>());
        Assert.IsType<FileVerificationProfileSource>(provider.GetRequiredService<IVerificationProfileSource>());
        Assert.True(provider.GetRequiredService<OrchestrationSession>().HasEngine);
    }

    [Fact]
    public void The_models_the_estimate_and_the_analysis_of_consumption_resolve_with_one_catalog()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp));

        var catalog = Assert.IsType<BuiltInModelCatalog>(provider.GetRequiredService<IModelCatalogSource>());
        Assert.NotNull(provider.GetRequiredService<IModelLearning>());
        Assert.NotNull(provider.GetRequiredService<IUsageEstimator>());
        Assert.NotNull(provider.GetRequiredService<IConsumptionAnalyzer>());
        Assert.NotNull(provider.GetRequiredService<IAssistedConsumptionAnalysis>());
        Assert.Same(catalog.Current.Catalog, provider.GetRequiredService<ConsumptionAnalysisOptions>().Catalog);
    }

    [Fact]
    public void The_order_review_and_the_chat_of_preparation_resolve_once_and_share_the_one_register_of_the_oks()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp));

        Assert.Same(provider.GetRequiredService<IOrderReview>(), provider.GetRequiredService<IOrderReview>());
        Assert.Same(provider.GetRequiredService<IPreparationChat>(), provider.GetRequiredService<IPreparationChat>());
        var ledger = provider.GetRequiredService<IQuotaConsentLedger>();
        Assert.IsType<QuotaConsentLedger>(ledger);
        var consent = new QuotaConsent("miveritas", DateTimeOffset.UnixEpoch, "Revisar el orden");
        Assert.True(ledger.TrySpend(consent));
        Assert.False(provider.GetRequiredService<IQuotaConsentLedger>().TrySpend(consent)); // the same register for the whole app
    }

    [Fact]
    public void The_engine_works_in_the_local_folders_of_the_app()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        using var provider = Build(folders);

        var paths = provider.GetRequiredService<OrchestrationPaths>();

        Assert.Equal(temp.Combine("local", "wt"), paths.WorktreeRoot);
        Assert.Equal(temp.Combine("local", "dossiers"), paths.DossierRoot);
        Assert.Equal(temp.Combine("local", "skill-sessions"), paths.SkillSessionRoot);
        Assert.Equal(paths.WorktreeRoot, provider.GetRequiredService<GitWorktreeManager>().WorktreeRoot);
    }

    [Fact]
    public void For_the_current_user_every_folder_is_under_the_local_application_data_of_agent_studio()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentStudio");

        var paths = LocalDataPaths.ForCurrentUser().Orchestration;

        Assert.Equal(Path.Combine(root, "wt"), paths.WorktreeRoot);
        Assert.Equal(Path.Combine(root, "dossiers"), paths.DossierRoot);
        Assert.Equal(Path.Combine(root, "skill-sessions"), paths.SkillSessionRoot);
        Assert.Equal(GitWorktreeOptions.DefaultWorktreeRoot, paths.WorktreeRoot);

        // The CLI adapters keep their per-run files in the fourth folder, which the providers fix themselves.
        Assert.Equal(Path.Combine(root, "runs"), RunTempFolder.BaseDirectory);
    }

    [Fact]
    public void Every_agent_provider_a_project_can_reach_is_journaled_and_no_bare_adapter_is_registered()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("proyecto");
        using var provider = Build(FoldersIn(temp));

        var providers = provider.GetRequiredService<IProjectAgentProviders>().For(project);

        Assert.Equal(3, providers.Count);
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p), $"{p.Provider} is not journaled."));
        Assert.Empty(provider.GetServices<IAgentProvider>());
    }

    [Fact]
    public async Task Once_a_project_is_open_the_orchestrator_tab_is_connected_to_the_engine()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateDirectory("calc");
        using var provider = Build(FoldersIn(temp));
        var main = provider.GetRequiredService<MainViewModel>();

        await main.OpenProjectAsync(project);
        await OpenOrchestratorAsync(main);

        var tab = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);
        Assert.True(tab.HasEngine);
        Assert.Equal("Motor de orquestación conectado", tab.EngineStatus);
        Assert.Equal(project, tab.Context?.Project?.RootPath);
        Assert.DoesNotContain(tab.Missing, line => line.StartsWith("Motor no disponible", StringComparison.Ordinal));
        Assert.DoesNotContain(tab.Missing, line => line.StartsWith("Falta", StringComparison.Ordinal));
        Assert.DoesNotContain(tab.Missing, line => line.StartsWith("No se pudo", StringComparison.Ordinal));

        // A new project gets the default roster (decision 7) and is offered, never given, the .NET profile.
        Assert.Equal(["orquestador", "ejecutor", "juez", "arquitecto", "critico", "juez-claude", "juez-revision"], tab.Context!.Roster.Select(a => a.Id.Value));
        Assert.Null(tab.Context.Verification);
        Assert.NotNull(tab.Context.VerificationProposal);
        Assert.False(File.Exists(Path.Combine(project, ".agentstudio", "verification.json")));
    }

    [Fact]
    public async Task Switching_project_moves_the_orchestrator_services_to_the_new_root()
    {
        using var temp = new TempDirectory();
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");
        using var provider = Build(FoldersIn(temp));
        var main = provider.GetRequiredService<MainViewModel>();

        await main.OpenProjectAsync(a);
        await OpenOrchestratorAsync(main);
        var tabOfA = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);
        tabOfA.RequestText = "Añadir Calc.Multiply";
        var editor = tabOfA.Requirements;
        editor.AddRequirementCommand.Execute(null);
        editor.Drafts[0].Statement = "Añadir Calc.Multiply(a, b)";
        editor.Drafts[0].Criteria[0].Description = "Multiply(2, 3) devuelve 6";
        await editor.FreezeCommand.ExecuteAsync(null);
        var projectA = tabOfA.Context!.Project!;
        Assert.NotNull(tabOfA.Context.Requirements);
        await tabOfA.UseProposedProfileCommand.ExecuteAsync(null);
        Assert.NotNull(tabOfA.Context.Verification);

        await main.OpenProjectAsync(b);
        await OpenOrchestratorAsync(main);

        var tabOfB = Assert.IsType<OrchestratorDocumentViewModel>(main.ActiveContent);
        Assert.NotSame(tabOfA, tabOfB);
        var projectB = tabOfB.Context!.Project!;
        Assert.Equal(b, projectB.RootPath);
        Assert.NotEqual(projectA.ProjectId, projectB.ProjectId);
        Assert.Null(tabOfB.Context.Requirements);
        Assert.Null(tabOfB.Context.Verification);
        Assert.Equal(7, tabOfB.Context.Roster.Count);
        Assert.False(tabOfB.RequestPlanCommand.CanExecute(null));
        Assert.True(File.Exists(Path.Combine(b, ".agentstudio", "agentstudio.db")));
        Assert.NotSame(
            provider.GetRequiredService<IProjectAgentProviders>().For(a)[0],
            provider.GetRequiredService<IProjectAgentProviders>().For(b)[0]);

        // Back to the first project: its tab, with what was frozen and accepted there.
        await main.OpenProjectAsync(a);
        await OpenOrchestratorAsync(main);

        Assert.Same(tabOfA, main.ActiveContent);
        Assert.Equal(projectA, tabOfA.Context.Project);
        Assert.Equal("Añadir Calc.Multiply(a, b)", Assert.Single(tabOfA.Context.Requirements!.Requirements).Statement);
        Assert.True(tabOfA.RequestPlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_skills_index_of_the_settings_is_the_catalog_of_the_engine()
    {
        using var temp = new TempDirectory();
        var index = temp.CreateDirectory("skills-index");
        using var provider = Build(FoldersIn(temp, defaultSkillsIndex: index));

        var options = provider.GetRequiredService<SkillCatalogOptions>();
        var report = await provider.GetRequiredService<SkillCatalog>().ListWithReportAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(index, "catalog.json"), options.CatalogPath);
        Assert.Empty(report.Skills);
        Assert.Contains(report.Warnings, w => w.StartsWith("No se encuentra el índice de skills", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_skills_index_the_catalog_says_it_is_not_configured()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp, defaultSkillsIndex: temp.Combine("no-existe")));

        var report = await provider.GetRequiredService<SkillCatalog>().ListWithReportAsync(CancellationToken.None);

        Assert.Null(provider.GetRequiredService<SkillCatalogOptions>().CatalogPath);
        Assert.Empty(report.Skills);
        Assert.Equal(["No hay ningún índice de skills configurado: el catálogo está vacío."], report.Warnings);
        Assert.Empty(await provider.GetRequiredService<ISkillCatalog>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public void Global_skill_activations_live_with_the_settings_of_the_app()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        using var provider = Build(folders);

        var options = provider.GetRequiredService<SkillActivationStoreOptions>();

        Assert.Equal(Path.Combine(folders.Roaming.BaseDirectory, "skill-activations.jsonl"), options.GlobalActivationsFile);
    }

    [Fact]
    public void The_phase_4_screens_and_their_ports_resolve_with_their_files_under_the_local_folder()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        using var provider = Build(folders);

        Assert.NotNull(provider.GetRequiredService<IUsageReport>());
        Assert.NotNull(provider.GetRequiredService<IUsageExporter>());
        Assert.NotNull(provider.GetRequiredService<IQuotaObservations>());
        Assert.NotNull(provider.GetRequiredService<ISkillRecommendationService>());
        Assert.Equal(Path.Combine(folders.Local.BaseDirectory, "usage", "quota.jsonl"), provider.GetRequiredService<QuotaLogOptions>().FilePath);
        Assert.Same(provider.GetRequiredService<UsageDashboardViewModel>(), provider.GetRequiredService<UsageDashboardViewModel>());
        Assert.NotNull(provider.GetRequiredService<MemoryViewModel>());

        var main = provider.GetRequiredService<MainViewModel>();
        var phase4 = main.Activities.Where(a => a.Id is "skills" or "usage" or "memory").ToList();
        Assert.Equal(["skills", "usage", "memory"], phase4.Select(a => a.Id));
        Assert.All(phase4, a => Assert.True(a.IsEnabled));
    }

    [Fact]
    public void The_project_memory_reaches_the_engine_and_the_judge_never_gets_it()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp));

        Assert.NotNull(provider.GetRequiredService<IProjectMemory>());
        Assert.NotNull(provider.GetRequiredService<IContextPacker>());
        Assert.NotNull(provider.GetRequiredService<IMemoryExporter>());
        Assert.NotNull(provider.GetRequiredService<IDailySummaryService>());
        Assert.NotNull(provider.GetRequiredService<IRunActivity>());
        Assert.NotNull(provider.GetRequiredService<IMemoryDerivation>());
        Assert.True(provider.GetRequiredService<ProjectMemoryUpkeep>().IsAvailable);
        Assert.IsType<Engine.Orchestrator>(provider.GetRequiredService<IOrchestrator>());

        var roster = DefaultAgentRoster.Create().ToDictionary(a => a.Id.Value, a => (a.Role, a.Memory));
        Assert.Equal(MemoryPolicy.ReadSelected, roster["orquestador"].Memory);
        Assert.Equal(MemoryPolicy.ReadSelected, roster["ejecutor"].Memory);
        Assert.All(roster.Values.Where(a => a.Role == AgentRole.Judge), a => Assert.Equal(MemoryPolicy.None, a.Memory));

        // The participants of the debates receive no memory by default (docs/15 D-09).
        Assert.Equal(MemoryPolicy.None, roster["arquitecto"].Memory);
        Assert.Equal(MemoryPolicy.None, roster["critico"].Memory);
    }

    [Fact]
    public async Task The_Codex_quota_is_read_only_with_the_saved_opt_in_and_Gemini_says_not_exposed()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp));

        var readers = provider.GetServices<IProviderQuotaReader>().ToList();
        var codex = Assert.Single(readers, r => r.Provider == ProviderId.CodexCli);
        Assert.Single(readers, r => r.Provider == ProviderId.GeminiCli);

        // The settings of a new install: no opt-in, so nothing is launched.
        var read = await codex.ReadAsync(CancellationToken.None);

        Assert.Equal(QuotaReadStatus.NotOptedIn, read.Status);
        Assert.Empty(read.Observations);
    }

    [Fact]
    public void The_debates_the_audits_and_the_challenge_resolve_and_the_shell_is_the_navigation_of_the_review()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp));

        Assert.NotNull(provider.GetRequiredService<IDebateCoordinator>());
        Assert.NotNull(provider.GetRequiredService<IDebateJudgment>());
        Assert.NotNull(provider.GetRequiredService<IProjectAuditService>());
        Assert.NotNull(provider.GetRequiredService<IOrchestratorAuditService>());
        Assert.NotNull(provider.GetRequiredService<IVerdictChallenges>());
        Assert.NotNull(provider.GetRequiredService<IDebateTestProfiles>());
        Assert.NotNull(provider.GetRequiredService<IAuditVerificationProfiles>());

        // The orchestrator review has its fixed requirements; the other modes take the user's.
        var requirements = provider.GetRequiredService<DebateModeRequirements>();
        Assert.NotNull(requirements(DebateMode.OrchestratorReview));
        Assert.Null(requirements(DebateMode.Free));

        var main = provider.GetRequiredService<MainViewModel>();
        Assert.Same(main, provider.GetRequiredService<IReviewNavigation>());
        Assert.True(provider.GetRequiredService<DebateScreens>().CanNavigate);
        Assert.True(main.Activities.Single(a => a.Id == "debates").IsEnabled);

        // A single activity per project for runs, debates and audits (docs/15 decision 12).
        Assert.Same(provider.GetRequiredService<IRunActivity>(), provider.GetRequiredService<IProjectActivity>());
    }
}
