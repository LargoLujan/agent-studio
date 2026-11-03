using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory;
using AgentStudio.Providers;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Providers.Composition;

/// <summary>Criterion F2-9: every agent provider the app can reach goes through the project's prompt journal.</summary>
public sealed class ProjectAgentProvidersTests
{
    private sealed class RecordingJournals : IPromptJournalFactory
    {
        public List<string> Roots { get; } = [];

        public IPromptJournal ForProject(string projectRoot)
        {
            Roots.Add(projectRoot);
            return new NullJournal();
        }
    }

    private sealed class NullJournal : IPromptJournal
    {
        public Task<JournalEntryId> RecordPromptAsync(PromptJournalEntry entry, CancellationToken ct) =>
            Task.FromResult(new JournalEntryId(new DateOnly(2026, 10, 15), 1));

        public Task RecordOutcomeAsync(JournalEntryId id, PromptOutcome outcome, CancellationToken ct) => Task.CompletedTask;
    }

    private static ServiceProvider BuildPlatform() =>
        new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddAgentStudioInfrastructure()
            .AddAgentStudioMemory()
            .AddAgentStudioPersistence()
            .AddAgentStudioProviders()
            .BuildServiceProvider();

    private static ProjectAgentProviders Create(IPromptJournalFactory journals) =>
        new(new ExecutableLocatorStub(), new RunnerStub(), journals, TimeProvider.System);

    [Fact]
    public void The_container_only_exposes_journaled_providers_for_the_three_clis()
    {
        using var temp = new TempDirectory();
        using var services = BuildPlatform();

        Assert.Empty(services.GetServices<IAgentProvider>());
        var providers = services.GetRequiredService<IProjectAgentProviders>().For(temp.Path);

        Assert.Equal([ProviderId.ClaudeCode, ProviderId.CodexCli, ProviderId.GeminiCli], providers.Select(p => p.Provider));
        Assert.All(providers, provider => Assert.True(AgentProviderPipeline.IsJournaled(provider), $"{provider.Provider} skips the journal"));
    }

    [Fact]
    public void The_same_project_reuses_its_providers_whatever_the_spelling_of_the_path()
    {
        using var temp = new TempDirectory();
        var journals = new RecordingJournals();
        var sut = Create(journals);

        var first = sut.For(temp.Path);
        var second = sut.For(temp.Path.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Same(first, second);
        Assert.Single(journals.Roots);
    }

    [Fact]
    public void Each_project_gets_its_own_journal()
    {
        using var temp = new TempDirectory();
        var one = Directory.CreateDirectory(Path.Combine(temp.Path, "uno")).FullName;
        var two = Directory.CreateDirectory(Path.Combine(temp.Path, "dos")).FullName;
        var journals = new RecordingJournals();
        var sut = Create(journals);

        var a = sut.For(one);
        var b = sut.For(two);

        Assert.Equal([one, two], journals.Roots);
        Assert.NotSame(a[0], b[0]);
    }

    [Fact]
    public void Only_claude_reports_the_context_of_each_step_and_codex_exec_and_gemini_do_not()
    {
        using var temp = new TempDirectory();

        var providers = Create(new RecordingJournals()).For(temp.Path);

        // docs/14 D-09: Claude gives the prompt of every step; codex exec only the thread total; Gemini is parked.
        Assert.Equal(
            [(ProviderId.ClaudeCode, true), (ProviderId.CodexCli, false), (ProviderId.GeminiCli, false)],
            providers.Select(p => (p.Provider, p.Capabilities.ReportsContextOccupancy)));
    }

    [Fact]
    public void Only_claude_announces_its_quota_in_its_runs_and_gemini_exposes_none()
    {
        using var temp = new TempDirectory();

        var providers = Create(new RecordingJournals()).For(temp.Path);

        // docs/14 F4-4: Claude's rate_limit_event comes with every run; codex exec says nothing (its quota is the opt-in
        // read of the app-server, ADR-0020); Gemini is parked and its quota is «no expuesto» (characterization).
        Assert.Equal(
            [(ProviderId.ClaudeCode, true), (ProviderId.CodexCli, false), (ProviderId.GeminiCli, false)],
            providers.Select(p => (p.Provider, p.Capabilities.ReportsSubscriptionQuota)));
    }

    [Fact]
    public void A_blank_project_root_is_rejected()
    {
        var sut = Create(new RecordingJournals());

        Assert.Throws<ArgumentException>(() => sut.For(" "));
    }

    private sealed class ExecutableLocatorStub : IExecutableLocator
    {
        public string? Find(string command) => null;
    }

    private sealed class RunnerStub : IStreamingProcessRunner
    {
        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No process should start while composing providers.");
    }
}
