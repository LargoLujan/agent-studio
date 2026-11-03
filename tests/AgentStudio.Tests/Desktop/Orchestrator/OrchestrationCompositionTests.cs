using System.Reflection;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Desktop;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Approvals;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The real composition of the shell (ShellServices, which adds OrchestrationUiServices): the orchestrator screen is
/// there, it registers no engine of its own, and it uses one as soon as the composition root provides it.
/// </summary>
public sealed class OrchestrationCompositionTests
{
    private static ServiceProvider BuildShell(TempDirectory temp, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddAgentStudioInfrastructure();
        services.AddSingleton(new ProviderDiagnostics([]));
        services.AddWindowsDoubles();
        before?.Invoke(services);

        // ShellServices is internal to the app: reach it the way the startup code does, through its extension method.
        var shell = typeof(MainWindow).Assembly.GetType("AgentStudio.Desktop.Composition.ShellServices", throwOnError: true)!;
        var addShellServices = shell.GetMethod("AddShellServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        addShellServices.Invoke(null, [services, new AppDataPaths(temp.Path)]);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_shell_composes_the_orchestrator_screen_without_an_engine_of_its_own()
    {
        using var temp = new TempDirectory();
        using var provider = BuildShell(temp);

        Assert.IsType<ApprovalPromptService>(provider.GetRequiredService<IApprovalPrompt>());
        Assert.IsType<WpfApprovalDialog>(provider.GetRequiredService<IApprovalDialog>());
        Assert.Null(provider.GetService<IOrchestrator>());
        Assert.False(provider.GetRequiredService<OrchestrationSession>().HasEngine);
        Assert.NotNull(provider.GetRequiredService<MainViewModel>());

        var document = provider.GetRequiredService<IOrchestratorDocumentFactory>().Create(temp.Path);
        Assert.Equal("Motor no disponible", document.EngineStatus);
    }

    [Fact]
    public void An_engine_provided_by_the_composition_root_is_used()
    {
        using var temp = new TempDirectory();
        var engine = new FakeOrchestrator();
        using var provider = BuildShell(temp, services => services.AddSingleton<IOrchestrator>(engine));

        var session = provider.GetRequiredService<OrchestrationSession>();
        var document = provider.GetRequiredService<IOrchestratorDocumentFactory>().Create(temp.Path);

        Assert.True(session.HasEngine);
        Assert.Equal("Motor de orquestación conectado", document.EngineStatus);
    }

    [Fact]
    public void The_delivery_service_of_the_composition_root_reaches_the_monitor_of_the_screen()
    {
        using var temp = new TempDirectory();
        var delivery = new FakeDeliveryService();
        using var provider = BuildShell(temp, services => services.AddSingleton<IDeliveryService>(delivery));
        using var otherTemp = new TempDirectory();
        using var withoutDelivery = BuildShell(otherTemp);

        Assert.Same(delivery, provider.GetRequiredService<OrchestrationSession>().Delivery);
        Assert.Null(withoutDelivery.GetRequiredService<OrchestrationSession>().Delivery);
    }

    /// <summary>
    /// The screen asks the same probe of the installed Claude Code as the engine, so the warning next to «Aprobar plan»
    /// weighs the executors as the check of the user's repository will (ADR-0018).
    /// </summary>
    [Fact]
    public void The_write_confinement_probe_of_the_composition_root_reaches_the_screen()
    {
        using var temp = new TempDirectory();
        var probe = new FixedWriteConfinement(ProviderId.ClaudeCode);
        using var provider = BuildShell(temp, services => services.AddSingleton<IWriteConfinementProbe>(probe));

        Assert.Same(probe, provider.GetRequiredService<OrchestrationBackend>().WriteConfinement);
    }

    [Fact]
    public void The_decision_recorder_of_the_composition_root_lets_the_screen_accept_a_history_through_the_approval_dialog()
    {
        using var temp = new TempDirectory();
        var recorder = new DecisionRecorder(new InMemoryDecisionLogStore(), TimeProvider.System, new MemoryDecisionLogAnchors());
        using var provider = BuildShell(temp, services => services.AddSingleton<IDecisionRecorder>(recorder).AddSingleton<IDecisionLogOrigin>(recorder));
        using var otherTemp = new TempDirectory();
        using var withoutRecorder = BuildShell(otherTemp);

        // The question goes through the shell's own approval dialog; the acceptance, through the composition root's recorder.
        Assert.True(provider.GetRequiredService<OrchestrationSession>().CanAcceptHistory);
        Assert.False(withoutRecorder.GetRequiredService<OrchestrationSession>().CanAcceptHistory);
    }
}
