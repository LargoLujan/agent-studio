using System.Collections.Concurrent;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Memory;
using AgentStudio.Observability;
using AgentStudio.Observability.Quota;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Providers;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentStudio.Application.Documents;
using AgentStudio.Desktop.Documents;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// The folders of the Phase 4 integration tests: those of Phase 3 (<see cref="Phase3Paths"/>), plus the user's record of
/// quota observations (<c>%LOCALAPPDATA%\AgentStudio\usage\quota.jsonl</c> in the app) and the folders of the assisted
/// recommendation (<c>%LOCALAPPDATA%\AgentStudio\skill-recommendations</c>), all under the same temporary stand-in. The
/// path of the MCP server is never launched: the adapters are scripted.
/// </summary>
internal sealed record Phase4Paths(Phase3Paths Engine)
{
    public string Root => Engine.Root;

    public string QuotaLog => Path.Combine(Root, "usage", "quota.jsonl");

    public string SkillRecommendations => Path.Combine(Root, "skill-recommendations");

    public string SkillsServer => Path.Combine(Root, "app", "AgentStudio.SkillsMcp.exe");

    /// <summary>The app folder next to a temporary repository (inside its container, never inside the repository).</summary>
    public static Phase4Paths Beside(string repositoryRoot) => new(Phase3Paths.Beside(repositoryRoot));

    /// <summary>The app folder under a temporary folder of the test.</summary>
    public static Phase4Paths Under(string folder) => new(new Phase3Paths(Path.Combine(folder, "LocalAppData", "AgentStudio")));
}

/// <summary>
/// The composition root of Phase 4 without the desktop: the one of Phase 3 (<see cref="Phase3Composition"/>, every module
/// through its own extension) plus, one line per module, what Phase 4 adds (docs/14 section 9, «Integración»): the
/// observability of WP2 (usage report, export and the user's quota record), the Codex quota reader of WP1 with its opt-in,
/// the project memory of WP3 (store policy, packer, export and daily summary) and the assisted recommendation of WP4. The
/// folders are the test's; the opt-in is a function, as the settings give it in the app.
/// </summary>
internal static class Phase4Composition
{
    public static ServiceCollection Services(
        Phase4Paths paths,
        IApprovalPrompt prompt,
        Func<IServiceProvider, IProjectAgentProviders>? providers = null,
        Func<bool>? codexOptedIn = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var services = Phase3Composition.Services(paths.Engine, prompt, providers);
        services
            .AddAgentStudioObservability(new QuotaLogOptions { FilePath = paths.QuotaLog })
            .AddCodexQuotaReader(_ => codexOptedIn ?? (() => false))
            .AddAgentStudioProjectMemory()
            .AddSingleton<IUserTextRenderer, ResourceUserTextRenderer>()
            .AddAgentStudioSkillRecommendation(paths.SkillRecommendations, paths.SkillsServer);
        return services;
    }

    /// <summary>
    /// No CLI is asked anything, not even its version: the confinement of the installed Claude Code (ADR-0018, which the
    /// real probe reads with <c>claude --version</c>) is a fixed answer, the version whose confinement was measured, so a
    /// run starts the same on any machine.
    /// </summary>
    public static ServiceCollection WithoutCliProbes(this ServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IWriteConfinementProbe>(new FixedWriteConfinement(ProviderId.ClaudeCode)));
        return services;
    }

    /// <summary>The skills index of the user is the <c>catalog.json</c> at <paramref name="catalogPath"/>.</summary>
    public static ServiceCollection WithCatalog(this ServiceCollection services, string catalogPath)
    {
        services.Replace(ServiceDescriptor.Singleton(new SkillCatalogOptions(catalogPath)));
        return services;
    }
}

/// <summary>
/// A scripted adapter with the context signals of the CLI it stands for (docs/14 D-09): Claude Code reports the context of
/// each step and its compactions, <c>codex exec</c> does not («no expuesto»). The script itself is a
/// <see cref="ScriptedAgentProvider"/>.
/// </summary>
internal sealed class ScriptedCli(ScriptedAgentProvider script, bool reportsContext) : IAgentProvider
{
    public ScriptedAgentProvider Script { get; } = script;

    public ProviderId Provider => Script.Provider;

    public VendorId Vendor => Script.Vendor;

    public ProviderCapabilities Capabilities { get; } = script.Capabilities with { ReportsContextOccupancy = reportsContext };

    public static ScriptedCli Claude(TimeProvider time) => new(new ScriptedAgentProvider(ProviderId.ClaudeCode, VendorId.Anthropic, time), reportsContext: true);

    public static ScriptedCli Codex(TimeProvider time) => new(new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, time), reportsContext: false);

    public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken) => Script.StartAsync(request, cancellationToken);
}

/// <summary>
/// The providers of a project as <c>ProjectAgentProviders</c> builds them in Phase 4: one per CLI, through
/// <see cref="AgentProviderPipeline.Compose"/> with the project's own Markdown journal and the user's quota record of the
/// container, with scripted adapters in place of the CLIs. No quota is spent, and every prompt still reaches the real
/// journal and every announced quota the real record.
/// </summary>
internal sealed class Phase4ScriptedProviders(IPromptJournalFactory journals, TimeProvider time, IQuotaObservations quota, IReadOnlyList<IAgentProvider> adapters) : IProjectAgentProviders
{
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<IAgentProvider>>> _byProject = new(StringComparer.OrdinalIgnoreCase);

    public static Func<IServiceProvider, IProjectAgentProviders> Over(params IAgentProvider[] adapters) =>
        sp => new Phase4ScriptedProviders(
            sp.GetRequiredService<IPromptJournalFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IQuotaObservations>(),
            adapters);

    public IReadOnlyList<IAgentProvider> For(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        return _byProject.GetOrAdd(root, key => new Lazy<IReadOnlyList<IAgentProvider>>(() =>
        {
            var journal = journals.ForProject(key);
            return [.. adapters.Select(adapter => AgentProviderPipeline.Compose(adapter, journal, time, quota: quota))];
        })).Value;
    }
}

/// <summary>
/// One instance of the app over one project, composed by <see cref="Phase4Composition"/>: the container built with
/// validation, the project opened (database, lock file and <c>info/exclude</c>) and, the first time, its agent template
/// saved. Disposing it is closing the app.
/// </summary>
internal sealed class Phase4App : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private Phase4App(ServiceProvider services, ProjectContext project)
    {
        _services = services;
        Project = project;
    }

    public ProjectContext Project { get; }

    public IServiceProvider Services => _services;

    public IOrchestrator Orchestrator => Get<IOrchestrator>();

    public IEvidenceStore Evidence => Get<IEvidenceStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Phase4App> OpenAsync(string projectRoot, ServiceCollection services, IReadOnlyList<AgentDefinition>? roster = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(projectRoot, Ct);
            foreach (var agent in roster ?? [])
            {
                await provider.GetRequiredService<IAgentRoster>().SaveAsync(project.ProjectId, agent, Ct);
            }

            return new Phase4App(provider, project);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    /// <summary>The verified log of the project, read back from its store.</summary>
    public Task<DecisionLogState> LoadLogAsync() => Get<IDecisionRecorder>().LoadAsync(Project.ProjectId, Ct);

    /// <summary>The entries of one kind of payload, in log order; only those of <paramref name="run"/> when it is given.</summary>
    public async Task<IReadOnlyList<(DecisionEntry Entry, T Payload)>> PayloadsAsync<T>(string? run = null)
        where T : DecisionPayload
    {
        var log = await LoadLogAsync();
        return [.. log.Entries
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is T && (run is null || x.Payload.RunId == run))
            .Select(x => (x.Entry, (T)x.Payload!))];
    }

    /// <summary>
    /// The request of a run on <paramref name="plan"/>, approved on the canvas: with the roster it was validated against
    /// and the warning about writers at medium integrity the canvas shows next to «Aprobar plan» (ADR-0018), read from the
    /// project's roster and the confinement probe of the composition exactly as the engine reads them when it starts.
    /// </summary>
    public async Task<RunRequest> RunRequestAsync(string originalRequest, FrozenRequirementSet requirements, PlanDefinition plan, VerificationProfile profile)
    {
        var roster = await Get<IAgentRoster>().ListAsync(Project.ProjectId, Ct);
        var confined = _services.GetService<IWriteConfinementProbe>() is { } probe
            && await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct);
        var warning = VerificationStepText.PlanWarning(plan, roster, profile, confined) ?? string.Empty;

        // A plan with writers is approved on the canvas only with Windows Hello (docs/17 D-37): the approval says so.
        var method = warning.Length > 0 ? ApprovalVerification.WindowsHello : ApprovalVerification.Dialog;
        return new RunRequest(
            Project,
            null,
            originalRequest,
            requirements,
            plan,
            new ApprovalDecision(true, ScriptedApprovalPrompt.Person, DateTimeOffset.UtcNow, "Plan aprobado en el lienzo.", Verification: method),
            profile,
            new RunOptions(),
            ValidatedRoster: roster,
            ApprovedWarning: warning);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}

/// <summary>The checks the Phase 4 integration tests share.</summary>
internal static class Phase4Checks
{
    /// <summary>The entries of the log as "sequence kind summary", for assertion messages.</summary>
    public static string Describe(DecisionLogState log) =>
        string.Join('\n', log.Entries.Select(e => $"{e.Sequence} {e.Kind} {e.Summary}"));

    /// <summary>How a run ended, in a way an assertion message can show.</summary>
    public static string Describe(RunResult result) =>
        $"{result.Phase}: {result.Summary} (unreachable [{string.Join(", ", result.Health.UnreachableTasks)}], retries exhausted [{string.Join(", ", result.Health.RetryExhaustedTasks)}])";
}
