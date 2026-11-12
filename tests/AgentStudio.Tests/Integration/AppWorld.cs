using System.IO;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Agents;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Integration.Phase5;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Tests.Integration;

/// <summary>
/// The app as it composes itself (<see cref="AppComposition.AddAgentStudioApp"/>, with the doubles of Windows) over temporary folders and a
/// temporary git repository, with the only parts that are not real: the two CLIs (scripted, <see cref="Phase5Clis"/>, through the real pipeline
/// of the journal and the quota record), the dialog of approvals (a script) and the probe of the confinement of Claude Code (a fixed answer).
/// Nothing is written outside the temporary folders and no CLI is launched. Disposing it closes the app and deletes both folders.
/// </summary>
internal sealed class AppWorld : IAsyncDisposable
{
    private readonly TempDirectory _folders;

    private AppWorld(TempDirectory folders, TempGitRepo repo, Phase5Clis clis, ScriptedApprovalPrompt person, Phase4App app, AppFolders appFolders)
    {
        _folders = folders;
        Repo = repo;
        Clis = clis;
        Person = person;
        App = app;
        Folders = appFolders;
    }

    public Phase4App App { get; }

    public Phase5Clis Clis { get; }

    public ScriptedApprovalPrompt Person { get; }

    public AppFolders Folders { get; }

    public TempGitRepo Repo { get; }

    public IServiceProvider Services => App.Services;

    /// <summary>Opens the app over a repository (by default the one of the Phase 5 runs, with its check script) and a roster (by default that of Phase 5).</summary>
    public static async Task<AppWorld> OpenAsync(
        TempGitRepo? repo = null,
        Phase5Clis? clis = null,
        IReadOnlyList<AgentDefinition>? roster = null,
        Action<IServiceCollection>? configure = null)
    {
        var temp = new TempDirectory();
        var repository = repo ?? Phase5Runs.Repo();
        var appFolders = new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);
        var scripts = clis ?? new Phase5Clis();
        var person = new ScriptedApprovalPrompt();
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(appFolders);
        services.Replace(ServiceDescriptor.Singleton(scripts.Providers));
        services.Replace(ServiceDescriptor.Singleton<IApprovalPrompt>(person));
        services.WithoutCliProbes();
        configure?.Invoke(services);
        try
        {
            var app = await Phase4App.OpenAsync(repository.Root, services, roster ?? Phase5Roster.Agents());
            return new AppWorld(temp, repository, scripts, person, app, appFolders);
        }
        catch
        {
            temp.Dispose();
            repository.Dispose();
            throw;
        }
    }

    public T Get<T>()
        where T : notnull => App.Get<T>();

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        _folders.Dispose();
        Repo.Dispose();
    }
}
