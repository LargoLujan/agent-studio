using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Graph;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Memory.Consistency;
using AgentStudio.Tests.Memory.Diagrams;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase6;

/// <summary>The head of the repository the history reader of a test reports: it can move, like a commit does.</summary>
internal sealed class FixedHead(string root, string commit) : IRepositoryHistoryReader
{
    public string Commit { get; set; } = commit;

    public Task<RepositorySummary> SummaryAsync(ProjectContext project, CancellationToken cancellationToken) =>
        Task.FromResult(new RepositorySummary(root, true, "main", Commit, false, []));

    public Task<IReadOnlyList<BranchSummary>> BranchesAsync(ProjectContext project, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BranchSummary>>([]);

    public Task<IReadOnlyList<CommitNode>> CommitsAsync(ProjectContext project, CommitGraphQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CommitNode>>([]);
}

/// <summary>The copy of a commit without git: a fixed set of files, whatever the commit asked for.</summary>
internal sealed class FilesSnapshots(params (string Path, string Text)[] files) : IRepositorySnapshots
{
    public Task<IRepositorySnapshot> OpenAsync(ProjectContext project, string commit, CancellationToken ct) =>
        Task.FromResult<IRepositorySnapshot>(new FakeSnapshot(files));
}

/// <summary>The analysis of the code without the worker process: the model the test hands it, which it can change between analyses.</summary>
internal sealed class MovingGraphBuilder(CodeGraph graph) : ICodeGraphBuilder
{
    public CodeGraph Graph { get; set; } = graph;

    public Task<GraphBuild> BuildAsync(IRepositorySnapshot snapshot, CancellationToken ct) => Task.FromResult(new GraphBuild(Graph, NamespaceFiles.Empty));
}

/// <summary>
/// The app as it composes itself (<see cref="AppComposition.AddAgentStudioApp"/>, what <c>App.OnStartup</c> calls) over temporary folders, with the doubles a test puts
/// first (the composition keeps what was registered earlier). Nothing is shown, no <c>App</c> is created, no CLI of a provider is launched and nothing goes to the network.
/// </summary>
internal sealed class Phase6Host : IDisposable
{
    private Phase6Host(TempDirectory temp, ServiceProvider provider)
    {
        Temp = temp;
        Provider = provider;
    }

    public TempDirectory Temp { get; }

    public ServiceProvider Provider { get; }

    public static Phase6Host Create(Action<IServiceCollection>? doubles = null)
    {
        var temp = new TempDirectory();
        try
        {
            var services = new ServiceCollection();
            services.AddWindowsDoubles();
            doubles?.Invoke(services);
            services.AddAgentStudioApp(new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null));
            return new Phase6Host(temp, services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Provider.Dispose();
        Temp.Dispose();
    }

    /// <summary>The commit the doubles without git answer to.</summary>
    public static string Commit => DiagramFixtures.Commit;
}
