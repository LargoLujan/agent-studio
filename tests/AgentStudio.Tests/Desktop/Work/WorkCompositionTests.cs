using System.IO;
using System.Reflection;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Usage;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Work;

/// <summary>
/// «Trabajo» in the composition the app builds at startup: the services it stands on resolve, the budget store is the one of the local folder of
/// the app, the engine's own gate reads that same store (what the person saves is what stops an attempt), and the shell offers the entry and
/// opens the document of the project.
/// </summary>
public sealed class WorkCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AppFolders FoldersIn(TempDirectory temp) =>
        new(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);

    private static ServiceProvider Build(AppFolders folders)
    {
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(folders);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void The_services_of_the_work_the_close_and_the_budget_resolve_and_the_budget_lives_in_the_local_folder_of_the_app()
    {
        using var temp = new TempDirectory();
        var folders = FoldersIn(temp);
        using var provider = Build(folders);

        var backend = provider.GetRequiredService<WorkBackend>();
        Assert.NotNull(backend.Workspace);
        Assert.NotNull(backend.Milestones);
        Assert.NotNull(backend.Closer);
        Assert.NotNull(backend.Backlog);
        Assert.NotNull(backend.Board);
        Assert.NotNull(backend.Links);
        Assert.NotNull(backend.Suggestions);
        Assert.NotNull(backend.Chain);
        Assert.NotNull(backend.Participation);
        Assert.NotNull(backend.Roster);
        Assert.NotNull(backend.Runs);
        Assert.NotNull(backend.Debates);
        Assert.NotNull(backend.Evidence);
        Assert.NotNull(backend.Verdicts);
        var store = Assert.IsType<LocalProjectBudgetStore>(provider.GetRequiredService<IProjectBudgetStore>());
        Assert.Equal(Path.Combine(folders.Local.BaseDirectory, "budgets"), store.Folder);
        Assert.Same(store, backend.Budgets);
        Assert.Same(provider.GetRequiredService<WorkDocumentViewModel>(), provider.GetRequiredService<WorkDocumentViewModel>());
    }

    [Fact]
    public async Task The_gate_of_the_engine_reads_the_budget_the_person_saved()
    {
        using var temp = new TempDirectory();
        using var project = new TempDirectory();
        using var provider = Build(FoldersIn(temp));
        var context = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(project.Path, Ct);
        await provider.GetRequiredService<IProjectBudgetStore>().SaveAsync(context, new WorkBudget(null, 1, BudgetPeriod.Total), Ct);

        // The orchestrator builds its gate from the optional ports the container gives it: the one of the budget is the store above.
        var orchestrator = provider.GetRequiredService<IOrchestrator>();
        var ports = typeof(AgentStudio.Orchestration.Engine.Orchestrator).GetField("_ports", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(orchestrator)!;
        var gate = Assert.IsType<ProjectBudgetGate>(ports.GetType().GetProperty("BudgetGate")!.GetValue(ports));
        var request = new UsageEstimateRequest(context, new AgentId("ejecutor"), ProviderId.ClaudeCode, "claude-sonnet-5-5", AgentRunPurpose.Executor);

        // With one attempt already in flight, a second one passes the limit of one launch.
        var refused = await gate.CheckAsync(context, "20261112-a1b2c3", request, 1, Ct);
        Assert.False(refused.Allowed);
        Assert.Equal(ProjectBudgetGate.ExceededCode, refused.Code);
        Assert.Equal(BudgetLimit.AgentRuns, refused.Reached);

        await provider.GetRequiredService<IProjectBudgetStore>().SaveAsync(context, null, Ct);
        Assert.True((await gate.CheckAsync(context, "20261112-a1b2c3", request, 1, Ct)).Allowed);
    }

    [Fact]
    public async Task The_shell_offers_trabajo_and_opens_the_document_on_the_open_project()
    {
        using var temp = new TempDirectory();
        using var project = new TempDirectory();
        using var provider = Build(FoldersIn(temp));
        var main = provider.GetRequiredService<MainViewModel>();

        var activity = Assert.Single(main.Activities, a => a.Id == "work");
        Assert.Equal(AgentStudio.Desktop.Localization.Texts.Get("Work_Title"), activity.Title);
        main.ProjectPath = project.Path;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);

        var document = provider.GetRequiredService<WorkDocumentViewModel>();
        Assert.Contains(document, main.Documents);
        Assert.Equal(project.Path, document.ProjectRoot);
        Assert.True(document.HasProject);
        Assert.Null(document.ErrorMessage);
    }
}
