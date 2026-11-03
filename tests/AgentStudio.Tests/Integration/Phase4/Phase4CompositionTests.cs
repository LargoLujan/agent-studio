using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Context;
using AgentStudio.Memory.Export;
using AgentStudio.Memory.Store;
using AgentStudio.Observability.Quota;
using AgentStudio.Observability.Usage;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Codex.AppServer;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Integration.Phase3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// The composition of Phase 4 (docs/14 section 9, «Integración»; D-14 to D-16): the container of Phase 3 with, one line
/// per module, the observability, the Codex quota reader, the project memory and the assisted recommendation, and the
/// app as it composes itself (<see cref="AppComposition.AddAgentStudioApp"/>). It validates on build; every agent
/// provider of a project, the auxiliary runs' included, goes through the prompt journal and through the user's quota
/// record; the Codex reader sits next to Gemini's «no expuesto», one per provider; and without the user's opt-in the
/// Codex reader answers «not opted in» without looking anything up nor launching anything. Composing writes nothing,
/// neither into the project nor into the user's record.
/// </summary>
public sealed class Phase4CompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Stands for the executable locator and both process runners: nothing may be looked up nor started.</summary>
    private sealed class NoProcesses : IExecutableLocator, IProcessRunner, IStreamingProcessRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? Find(string command) => Refuse<string?>("find " + command);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) => Refuse<Task<ProcessResult>>("run " + spec.FileName);

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) => Refuse<Task<IProcessSession>>("start " + spec.FileName);

        private T Refuse<T>(string what)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException($"Nothing may be looked up nor launched here ({what}).");
        }
    }

    [Fact]
    public async Task Every_agent_provider_is_journaled_and_quota_observed_and_the_codex_reader_launches_nothing_without_the_opt_in()
    {
        using var temp = new TempDirectory();
        var paths = Phase4Paths.Under(temp.Path);
        var asked = 0;
        var processes = new NoProcesses();
        var services = Phase4Composition.Services(paths, new ScriptedApprovalPrompt(), codexOptedIn: () =>
        {
            Interlocked.Increment(ref asked);
            return false;
        });
        services.Replace(ServiceDescriptor.Singleton<IExecutableLocator>(processes));
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(processes));
        services.Replace(ServiceDescriptor.Singleton<IStreamingProcessRunner>(processes));
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // One provider per CLI for a project, the real ones of ProjectAgentProviders: each only reachable through the
        // prompt journal (F2-9) and the user's quota record (D-14), the auxiliary runs included.
        var root = temp.CreateDirectory("proyecto");
        Assert.IsType<ProjectAgentProviders>(container.GetRequiredService<IProjectAgentProviders>());
        var providers = container.GetRequiredService<IProjectAgentProviders>().For(root);
        Assert.Equal([ProviderId.ClaudeCode, ProviderId.CodexCli, ProviderId.GeminiCli], providers.Select(p => p.Provider));
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p), $"The provider {p.Provider} is not journaled."));
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsQuotaObserved(p), $"The provider {p.Provider} does not keep the quota its runs announce."));
        Assert.All(providers, p => Assert.IsType<ConcurrencyLimitedAgentProvider>(p));
        Assert.Empty(container.GetServices<IAgentProvider>());
        Assert.IsType<AuxiliaryAgentRunner>(container.GetRequiredService<IAuxiliaryAgentRunner>());
        Assert.Same(providers, container.GetRequiredService<IProjectAgentProviders>().For(root));

        // The quota readers: Codex's app-server reader next to Gemini's «no expuesto», one per provider (D-15, D-16).
        var readers = container.GetServices<IProviderQuotaReader>().ToList();
        Assert.Equal([ProviderId.CodexCli, ProviderId.GeminiCli], readers.Select(r => r.Provider).OrderBy(p => p.Value, StringComparer.Ordinal));
        var codex = Assert.IsType<CodexAppServerQuotaReader>(Assert.IsType<SharedQuotaReader>(Assert.Single(readers, r => r.Provider == ProviderId.CodexCli)).Inner);
        Assert.Same(NotExposedQuotaReader.Gemini, Assert.Single(readers, r => r.Provider == ProviderId.GeminiCli));

        // Without the opt-in the Codex reader asks the setting and stops there: nothing is looked up nor launched.
        var codexRead = await codex.ReadAsync(Ct);
        Assert.Equal(QuotaReadStatus.NotOptedIn, codexRead.Status);
        Assert.Empty(codexRead.Observations);
        Assert.Equal((1, 0), (asked, processes.Calls));
        var geminiRead = await NotExposedQuotaReader.Gemini.ReadAsync(Ct);
        Assert.Equal(QuotaReadStatus.NotExposed, geminiRead.Status);
        Assert.False(string.IsNullOrWhiteSpace(geminiRead.Reason));

        // Every port of Phase 4 is the real implementation of its module.
        Assert.IsType<UsageProjector>(container.GetRequiredService<IUsageReport>());
        Assert.IsType<UsageExporter>(container.GetRequiredService<IUsageExporter>());
        Assert.IsType<JsonlQuotaObservations>(container.GetRequiredService<IQuotaObservations>());
        Assert.IsType<ReadOnlyProjectLogReader>(container.GetRequiredService<IProjectLogReader>());
        Assert.IsType<ProjectStorageFiles>(container.GetRequiredService<IProjectStorageReader>());
        Assert.IsType<ProjectMemory>(container.GetRequiredService<IProjectMemory>());
        Assert.IsType<SqliteMemoryStore>(container.GetRequiredService<IMemoryStore>());
        Assert.IsType<ContextPacker>(container.GetRequiredService<IContextPacker>());
        Assert.IsType<MemoryMarkdownExporter>(container.GetRequiredService<IMemoryExporter>());
        Assert.IsType<SkillRecommendationService>(container.GetRequiredService<ISkillRecommendationService>());
        Assert.IsType<CliWriteConfinementProbe>(container.GetRequiredService<IWriteConfinementProbe>());

        // The memory waits on the activity the orchestrator reports to (D-19, D-22), the same instance for the app.
        var orchestrator = Assert.IsType<Orchestrator>(container.GetRequiredService<IOrchestrator>());
        Assert.Same(orchestrator.Activity, container.GetRequiredService<IRunActivity>());

        // Composing wrote nothing: neither into the project nor the user's quota record nor the recommendation folders.
        Assert.False(Directory.Exists(Path.Combine(root, ".agentstudio")), "Composing the app wrote into the project.");
        Assert.False(File.Exists(paths.QuotaLog));
        Assert.False(Directory.Exists(paths.SkillRecommendations));
        Assert.Equal((1, 0), (asked, processes.Calls));
    }

    [Fact]
    public async Task In_the_app_as_it_composes_itself_every_agent_provider_keeps_its_quota_and_codex_launches_nothing_without_the_saved_opt_in()
    {
        using var temp = new TempDirectory();
        var folders = new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);
        var processes = new NoProcesses();
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(folders);
        services.Replace(ServiceDescriptor.Singleton<IExecutableLocator>(processes));
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(processes));
        services.Replace(ServiceDescriptor.Singleton<IStreamingProcessRunner>(processes));
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var root = temp.CreateDirectory("proyecto");
        var providers = container.GetRequiredService<IProjectAgentProviders>().For(root);
        Assert.Equal([ProviderId.ClaudeCode, ProviderId.CodexCli, ProviderId.GeminiCli], providers.Select(p => p.Provider));
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p) && AgentProviderPipeline.IsQuotaObserved(p), $"The provider {p.Provider} skips the journal or the quota record."));
        var quotaLog = container.GetRequiredService<QuotaLogOptions>().FilePath;
        Assert.Equal(Path.Combine(folders.Local.BaseDirectory, "usage", "quota.jsonl"), quotaLog);

        // A new install has no opt-in saved: the reader of the composition answers without launching anything.
        var codex = Assert.IsType<CodexAppServerQuotaReader>(Assert.IsType<SharedQuotaReader>(Assert.Single(container.GetServices<IProviderQuotaReader>(), r => r.Provider == ProviderId.CodexCli)).Inner);
        Assert.Equal(QuotaReadStatus.NotOptedIn, (await codex.ReadAsync(Ct)).Status);
        Assert.Equal(0, processes.Calls);
        Assert.False(File.Exists(quotaLog));
        Assert.False(Directory.Exists(Path.Combine(root, ".agentstudio")), "Composing the app wrote into the project.");
    }
}
