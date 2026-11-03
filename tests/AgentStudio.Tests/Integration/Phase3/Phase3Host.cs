using System.Collections.Concurrent;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Debate;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Memory;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Providers;
using AgentStudio.Providers.Composition;
using AgentStudio.Security;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// The folders the composition root gives the engine, under a temporary stand-in for <c>%LOCALAPPDATA%\AgentStudio</c>:
/// worktrees, dossiers, skill sessions, the anchors of the decision logs, the scratch folder of the verification runner and the global skill activations
/// (which the app keeps in <c>%APPDATA%</c>). Nothing is written outside it and the project folder.
/// </summary>
internal sealed record Phase3Paths(string Root)
{
    public string WorktreeRoot => Path.Combine(Root, "wt");

    public string DossierRoot => Path.Combine(Root, "dossiers");

    public string SkillSessionRoot => Path.Combine(Root, "skills");

    public string VerificationScratch => Path.Combine(Root, "verification");

    public string GlobalSkillActivations => Path.Combine(Root, "skill-activations.jsonl");

    /// <summary>The anchors of the decision logs (ADR-0010, addendum), outside the project as in the app.</summary>
    public string AnchorRoot => Path.Combine(Root, "anchors");

    public OrchestrationPaths Orchestration => new(WorktreeRoot, DossierRoot, SkillSessionRoot);

    /// <summary>The app folder next to a temporary repository (inside its container, never inside the repository).</summary>
    public static Phase3Paths Beside(string repositoryRoot) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(repositoryRoot))!, "la", "AgentStudio"));
}

/// <summary>
/// The composition root of Phase 3 as the desktop app will have it, without the desktop: every module through its own
/// extension, plus the three things only a composition root decides: the folders (<see cref="OrchestrationPaths"/> and
/// the options of git, verification and skills), the <see cref="IApprovalPrompt"/> (a dialog in the app, a script here)
/// and the <see cref="ISkillProvisioner"/>, which needs the session folder of the runs. The clock is the system's: the
/// processes these tests start (git, dotnet, pwsh) and their timeouts live in real time.
/// </summary>
internal static class Phase3Composition
{
    public static ServiceCollection Services(Phase3Paths paths, IApprovalPrompt prompt, Func<IServiceProvider, IProjectAgentProviders>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(prompt);
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services
            .AddAgentStudioInfrastructure()
            .AddAgentStudioMemory()
            .AddAgentStudioProviders()
            .AddAgentStudioSecurity()
            .AddAgentStudioPersistence(new DecisionLogAnchorOptions(paths.AnchorRoot))
            .AddAgentStudioSkills(new SkillCatalogOptions(null), new SkillActivationStoreOptions(paths.GlobalSkillActivations))
            .AddAgentStudioGit(new GitWorktreeOptions(paths.WorktreeRoot))
            .AddAgentStudioVerification(new VerificationRunnerOptions(paths.VerificationScratch))
            .AddAgentStudioIntegration()
            .AddAgentStudioJudging()
            .AddAgentStudioApprovals()
            .AddAgentStudioOrchestration();

        services.AddSingleton(paths.Orchestration);
        services.AddSingleton(prompt);
        services.AddSingleton<ISkillProvisioner>(sp => new SkillProvisioner(
            sp.GetRequiredService<ISkillCatalog>(),
            sp.GetRequiredService<ISkillActivationStore>(),
            sp.GetRequiredService<ISkillMaterializer>(),
            new SkillProvisioningOptions(paths.SkillSessionRoot)));
        if (providers is not null)
        {
            // The CLIs are the only part that is not real: the scripted adapters still go through the real pipeline.
            services.Replace(ServiceDescriptor.Singleton(providers));
        }

        return services;
    }
}

/// <summary>
/// The providers of a project as <c>ProjectAgentProviders</c> builds them (one per CLI, through
/// <see cref="AgentProviderPipeline.Compose"/> with the project's own Markdown journal), with scripted adapters in place
/// of the CLIs, so no quota is spent and every prompt still reaches the real journal first.
/// </summary>
internal sealed class ScriptedProjectAgentProviders(IPromptJournalFactory journals, TimeProvider time, IReadOnlyList<ScriptedAgentProvider> adapters) : IProjectAgentProviders
{
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<IAgentProvider>>> _byProject = new(StringComparer.OrdinalIgnoreCase);

    public static Func<IServiceProvider, IProjectAgentProviders> Over(params ScriptedAgentProvider[] adapters) =>
        sp => new ScriptedProjectAgentProviders(sp.GetRequiredService<IPromptJournalFactory>(), sp.GetRequiredService<TimeProvider>(), adapters);

    public IReadOnlyList<IAgentProvider> For(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        return _byProject.GetOrAdd(root, key => new Lazy<IReadOnlyList<IAgentProvider>>(() =>
        {
            var journal = journals.ForProject(key);
            return [.. adapters.Select(adapter => AgentProviderPipeline.Compose(adapter, journal, time))];
        })).Value;
    }
}

/// <summary>The person behind the approval dialog, scripted: it approves unless <see cref="Approve"/> says otherwise, and keeps every question.</summary>
internal sealed class ScriptedApprovalPrompt : IApprovalPrompt
{
    public const string Person = "usuario";

    private readonly Lock _gate = new();
    private readonly List<ApprovalRequest> _requests = [];

    public Func<ApprovalRequest, bool> Approve { get; init; } = _ => true;

    public IReadOnlyList<ApprovalRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _requests.Add(request);
        }

        var approved = Approve(request);
        return Task.FromResult(new ApprovalDecision(approved, Person, DateTimeOffset.UtcNow, approved ? "Aprobado en la prueba." : "Denegado en la prueba."));
    }
}

/// <summary>
/// One instance of the app over one project: the container of <see cref="Phase3Composition"/> with the project opened
/// (database, lock file and <c>info/exclude</c>) and, the first time, its agent template saved. Disposing it is closing
/// the app: the project lock and every database handle are released, so another instance can open the same folder and
/// see exactly what was persisted.
/// </summary>
internal sealed class Phase3App : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private Phase3App(ServiceProvider services, ProjectContext project)
    {
        _services = services;
        Project = project;
    }

    public ProjectContext Project { get; }

    public IServiceProvider Services => _services;

    public IOrchestrator Orchestrator => _services.GetRequiredService<IOrchestrator>();

    public IDecisionRecorder Decisions => _services.GetRequiredService<IDecisionRecorder>();

    public IEvidenceStore Evidence => _services.GetRequiredService<IEvidenceStore>();

    public IWorktreeManager Worktrees => _services.GetRequiredService<IWorktreeManager>();

    public IRunRegistry Runs => _services.GetRequiredService<IRunRegistry>();

    public IVerdictStore Verdicts => _services.GetRequiredService<IVerdictStore>();

    public ProjectStorage Storage => _services.GetRequiredService<ProjectDatabases>().Get(Project.ProjectId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Phase3App> OpenAsync(
        string projectRoot,
        Phase3Paths paths,
        IApprovalPrompt prompt,
        Func<IServiceProvider, IProjectAgentProviders>? providers,
        IReadOnlyList<AgentDefinition>? roster = null)
    {
        var services = Phase3Composition.Services(paths, prompt, providers)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            var project = await services.GetRequiredService<IProjectWorkspace>().OpenAsync(projectRoot, Ct);
            foreach (var agent in roster ?? [])
            {
                await services.GetRequiredService<IAgentRoster>().SaveAsync(project.ProjectId, agent, Ct);
            }

            return new Phase3App(services, project);
        }
        catch
        {
            await services.DisposeAsync();
            throw;
        }
    }

    /// <summary>The verified log of the project, read back from its store.</summary>
    public Task<DecisionLogState> LoadLogAsync() => Decisions.LoadAsync(Project.ProjectId, Ct);

    /// <summary>The project's roster as its store lists it: what the canvas validates a plan against before it is approved.</summary>
    public Task<IReadOnlyList<AgentDefinition>> RosterAsync() => _services.GetRequiredService<IAgentRoster>().ListAsync(Project.ProjectId, Ct);

    /// <summary>
    /// The warning about writers at medium integrity the canvas shows next to «Aprobar plan» for <paramref name="plan"/>
    /// with <paramref name="profile"/>, read as it reads it (ADR-0018): from the project's roster and what the app's
    /// confinement probe answers now of the installed Claude Code (<c>--version</c> only, no quota); empty when there is
    /// none. It is what a run request carries as <see cref="RunRequest.ApprovedWarning"/>.
    /// </summary>
    public async Task<string> ApprovedWarningAsync(PlanDefinition plan, VerificationProfile profile)
    {
        var confined = _services.GetService<IWriteConfinementProbe>() is { } probe
            && await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct);
        return VerificationStepText.PlanWarning(plan, await RosterAsync(), profile, confined) ?? string.Empty;
    }

    /// <summary>The entries of one run with their typed payloads, in log order.</summary>
    public async Task<IReadOnlyList<(DecisionEntry Entry, DecisionPayload Payload)>> RunEntriesAsync(RunId run)
    {
        var log = await LoadLogAsync();
        return [.. log.Entries
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is not null && x.Payload.RunId == run.Value)
            .Select(x => (x.Entry, x.Payload!))];
    }

    /// <summary>The entries of one kind of payload of a run.</summary>
    public async Task<IReadOnlyList<(DecisionEntry Entry, T Payload)>> PayloadsAsync<T>(RunId run)
        where T : DecisionPayload =>
        [.. (await RunEntriesAsync(run)).Where(x => x.Payload is T).Select(x => (x.Entry, (T)x.Payload))];

    /// <summary>The transitions of a run as "T1 Pending>Running", in log order.</summary>
    public async Task<IReadOnlyList<string>> TransitionsAsync(RunId run, string? task = null) =>
        [.. (await PayloadsAsync<TaskStatusChangedPayload>(run))
            .Where(p => task is null || p.Payload.Task.Value == task)
            .Select(p => $"{p.Payload.Task} {p.Payload.From}>{p.Payload.To}")];

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
