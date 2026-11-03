using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Git;

public sealed class GitCompositionTests
{
    [Fact]
    public void The_git_module_registers_one_manager_shared_by_every_consumer()
    {
        // Mutating operations are serialised per repository inside one manager, so every consumer must get the same one.
        var root = Path.Combine(Path.GetTempPath(), "as-di-" + Guid.NewGuid().ToString("N"));
        using var provider = new ServiceCollection().AddAgentStudioGit(new GitWorktreeOptions(root)).BuildServiceProvider();

        var manager = provider.GetRequiredService<IWorktreeManager>();

        var concrete = Assert.IsType<GitWorktreeManager>(manager);
        Assert.Same(manager, provider.GetRequiredService<IWorktreeManager>());
        Assert.Same(concrete, provider.GetRequiredService<GitWorktreeManager>());
        Assert.Equal(Path.GetFullPath(root), concrete.WorktreeRoot);
        Assert.False(Directory.Exists(root), "Composing the module must not create folders.");
    }

    [Fact]
    public void Without_options_the_default_worktree_root_is_used_and_earlier_process_services_are_kept()
    {
        var runner = new ProcessRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(runner);

        using var provider = services.AddAgentStudioGit().BuildServiceProvider();

        Assert.Equal(GitWorktreeOptions.DefaultWorktreeRoot, provider.GetRequiredService<GitWorktreeManager>().WorktreeRoot);
        Assert.Same(runner, provider.GetRequiredService<IProcessRunner>());
        Assert.NotNull(provider.GetRequiredService<IExecutableLocator>());
    }
}
