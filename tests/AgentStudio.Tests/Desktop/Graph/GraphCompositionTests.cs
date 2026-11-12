using System.IO;
using AgentStudio.Application.Graph;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using AgentStudio.Infrastructure.Graph;
using AgentStudio.Tests.TestSupport;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Desktop.Graph;

/// <summary>
/// «Auto Graph» in the composition the app builds at startup (docs/20 section 9.8): the services it stands on resolve, what it keeps lives under the local folder of
/// the app, the shell offers the entry and opens the document on the open project, and what a node asks of the shell (a file in the editor, the work of a task)
/// reaches it.
/// </summary>
public sealed class GraphCompositionTests
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
    public void The_services_of_the_graph_resolve_and_what_it_keeps_lives_in_the_local_folder_of_the_app()
    {
        using var temp = new TempDirectory();
        using var provider = Build(FoldersIn(temp));

        var backend = provider.GetRequiredService<GraphBackend>();

        Assert.NotNull(backend.Analysis);
        Assert.NotNull(backend.Git);
        Assert.NotNull(backend.Inference);
        Assert.NotNull(backend.Tasks);
        Assert.IsType<CommitAnalysisFileCache>(provider.GetRequiredService<ICommitAnalysisCache>());
        Assert.IsType<GraphInferenceFileStore>(provider.GetRequiredService<IGraphInferenceStore>());
        Assert.IsType<GraphWorkerHost>(provider.GetRequiredService<ICodeGraphBuilder>());
        Assert.IsType<LowIntegrityGraphWorkerLauncher>(provider.GetRequiredService<IGraphWorkerLauncher>());
        Assert.Same(provider.GetRequiredService<GraphDocumentViewModel>(), provider.GetRequiredService<GraphDocumentViewModel>());
        Assert.Single(provider.GetRequiredService<GraphDocumentViewModel>().Sections.OfType<GraphMapSection>());
    }

    [Fact]
    public void The_worker_ships_next_to_the_app_where_the_launcher_looks_for_it()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, LowIntegrityGraphWorkerLauncher.ExecutableName)));
    }

    [Fact]
    public async Task The_shell_offers_auto_graph_and_opens_the_document_on_the_open_project()
    {
        using var temp = new TempDirectory();
        using var project = new TempDirectory();
        using var provider = Build(FoldersIn(temp));
        var main = provider.GetRequiredService<MainViewModel>();

        var activity = Assert.Single(main.Activities, a => a.Id == "graph");
        Assert.Equal(Texts.Get("Graph_Title"), activity.Title);
        main.ProjectPath = project.Path;
        await ((IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);

        var document = provider.GetRequiredService<GraphDocumentViewModel>();
        Assert.Contains(document, main.Documents);
        Assert.True(document.HasProject);
        Assert.Equal(project.Path, document.Project!.RootPath);
    }

    [Fact]
    public async Task A_file_asked_from_a_node_opens_in_the_editor_and_a_task_asked_from_a_project_opens_trabajo_on_its_chain()
    {
        using var temp = new TempDirectory();
        using var project = new TempDirectory();
        Directory.CreateDirectory(project.Combine("src"));
        await File.WriteAllTextAsync(project.Combine("src", "Calc.cs"), "namespace Calc { }", Ct);
        using var provider = Build(FoldersIn(temp));
        var main = provider.GetRequiredService<MainViewModel>();
        main.ProjectPath = project.Path;
        var activity = Assert.Single(main.Activities, a => a.Id == "graph");
        await ((IAsyncRelayCommand)activity.Command).ExecuteAsync(activity);
        var section = provider.GetRequiredService<GraphDocumentViewModel>().Sections.OfType<GraphMapSection>().Single();

        section.OpenFileCommand.Execute("src/Calc.cs");
        await WaitForAsync(() => main.Documents.OfType<FileDocumentViewModel>().Any());

        Assert.Equal(project.Combine("src", "Calc.cs"), main.Documents.OfType<FileDocumentViewModel>().Single().FilePath);

        var task = new GraphTaskRef(new RunId("20261111-a1b2c3"), new TaskId("T1"), "Tarea T1", ["src/Calc.cs"]);
        section.OpenTaskCommand.Execute(new GraphTaskRow(task));
        var work = provider.GetRequiredService<WorkDocumentViewModel>();
        await WaitForAsync(() => work.Context.Focus is not null);

        Assert.Contains(work, main.Documents);
        Assert.Equal(TraceRef.Task("20261111-a1b2c3", "T1"), work.Context.Focus);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }
    }
}
