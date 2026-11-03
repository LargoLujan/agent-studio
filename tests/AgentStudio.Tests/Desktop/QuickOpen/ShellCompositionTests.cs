using System.Reflection;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Desktop.QuickOpen;

/// <summary>The real dependency-injection composition of the shell, which the app builds at startup.</summary>
public sealed class ShellCompositionTests
{
    private static ServiceProvider BuildShell(TempDirectory temp)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddAgentStudioInfrastructure();
        services.AddSingleton(new ProviderDiagnostics([]));
        services.AddWindowsDoubles();

        // ShellServices is internal to the app: reach it the way the startup code does, through its extension method.
        var shell = typeof(MainWindow).Assembly.GetType("AgentStudio.Desktop.Composition.ShellServices", throwOnError: true)!;
        var addShellServices = shell.GetMethod("AddShellServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        addShellServices.Invoke(null, [services, new AppDataPaths(temp.Path)]);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheMainViewModel_IsComposedWithTheSingleGoToFileList()
    {
        using var temp = new TempDirectory();
        using var provider = BuildShell(temp);

        var main = provider.GetRequiredService<MainViewModel>();

        Assert.Same(provider.GetRequiredService<QuickOpenViewModel>(), main.QuickOpen);
    }
}
