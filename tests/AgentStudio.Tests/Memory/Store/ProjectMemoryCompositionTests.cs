using System.IO;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory;
using AgentStudio.Memory.Store;
using AgentStudio.Security;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Domain.TestData;
using AgentStudio.Application.Documents;
using AgentStudio.Desktop.Documents;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>
/// The memory as the composition root registers it (<see cref="DependencyInjection.AddAgentStudioProjectMemory"/>): one
/// <see cref="ProjectMemory"/> behind the four ports, over the persistence, the redactor and the activity of the
/// orchestrator. Without that activity it does not build: nothing pretends that no run ever goes on.
/// </summary>
public sealed class ProjectMemoryCompositionTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly string _anchors = Path.Combine(Path.GetTempPath(), "as-anclas-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        _folder.Dispose();
        TempProjectFolder.ForceDelete(_anchors);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(_anchors));
        services.AddAgentStudioSecurity();
        services.AddAgentStudioProjectMemory();
        services.AddSingleton<IUserTextRenderer, ResourceUserTextRenderer>();
        return services;
    }

    [Fact]
    public async Task One_memory_behind_its_ports_records_exports_packs_and_summarizes_a_real_project()
    {
        var services = Services();
        services.AddSingleton<IRunActivity>(new FakeRunActivity());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);

        Assert.Same(provider.GetRequiredService<ProjectMemory>(), provider.GetRequiredService<IProjectMemory>());
        var entry = await provider.GetRequiredService<IProjectMemory>().RecordAsync(project, new MemoryDraft(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.", MemoryOrigin.User, "nota:1"), Ct);
        var export = await provider.GetRequiredService<IMemoryExporter>().ExportAsync(project, "es-ES", Ct);
        var executor = new AgentDefinition(Aid("ejecutor"), "Ejecutor", AgentRole.Executor, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "i", [], [], [], new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.ReadSelected, new ReviewPolicy(false, false));
        var pack = await provider.GetRequiredService<IContextPacker>().PackAsync(project, new ContextPackRequest(executor, "Desplegar", [], 1000), Ct);
        var summary = await provider.GetRequiredService<IDailySummaryService>().SummarizeAsync(project, new DateOnly(2020, 1, 1), Ct);

        Assert.True(export.Exported);
        Assert.Contains("blue green", await File.ReadAllTextAsync(Path.Combine(_folder.DataDirectory, "memory", "tema-decisiones.md"), Ct), StringComparison.Ordinal);
        Assert.Equal([entry.Id], pack.Items.Select(i => i.Id));
        Assert.Equal(0, summary.Prompts);
    }

    /// <summary>Tells the memory that a project was closed, as the persistence does once it releases it.</summary>
    private sealed class ClosedSignal : IProjectClosedSignal
    {
        public event Action<string>? Closed;

        public void Raise(string projectId) => Closed?.Invoke(projectId);
    }

    [Fact]
    public async Task The_memory_forgets_a_project_once_it_is_told_the_project_was_closed()
    {
        var signal = new ClosedSignal();
        SpyMemoryStore? spy = null;
        var services = new ServiceCollection();
        services.AddSingleton<IProjectClosedSignal>(signal);
        services.AddSingleton<IMemoryStore>(p => spy = new SpyMemoryStore(new SqliteMemoryStore(p.GetRequiredService<ProjectDatabases>())));
        services.AddAgentStudioPersistence(new DecisionLogAnchorOptions(_anchors));
        services.AddAgentStudioSecurity();
        services.AddAgentStudioProjectMemory();
        services.AddSingleton<IRunActivity>(new FakeRunActivity());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_folder.Root, Ct);
        var memory = provider.GetRequiredService<IProjectMemory>();
        await memory.RecordAsync(project, new MemoryDraft(MemoryKind.Decision, "Despliegue", "Se despliega con blue green.", MemoryOrigin.User, "nota:1"), Ct);
        await memory.RecallAsync(project, new MemoryQuery(), Ct);
        await memory.RecallAsync(project, new MemoryQuery(), Ct);
        var whole = spy!.WholeReads;

        signal.Raise(project.ProjectId);
        Assert.Single(await memory.RecallAsync(project, new MemoryQuery(), Ct));

        Assert.Equal(whole + 1, spy.WholeReads); // nothing kept: every row read and checked again
    }

    [Fact]
    public void Without_the_activity_of_the_orchestrator_the_memory_does_not_build()
    {
        var services = Services();

        Assert.ThrowsAny<Exception>(() => services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }));
    }
}
