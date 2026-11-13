using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Security.Approvals;
using AgentStudio.Tests.Debate.Fakes;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Skills;

/// <summary>A catalog, a roster and the ports of the Skills screen, all in memory: nothing here touches a disk or a CLI.</summary>
internal static class SkillsScreenSamples
{
    public const string ProjectRoot = @"C:\proyectos\calc";

    public const string ProjectId = "6f1c2b0e-0000-4000-8000-0000000000ee";

    public const string Sha = "8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4";

    public static CatalogSkill Skill(string name, string summary, SkillSector sector, string[] tags, bool linked = false, string? license = "MIT", string? pinned = Sha) =>
        new(
            new SkillEntry(new SkillId(name), name, summary, sector, tags, new SkillSource("anthropics/skills", "skills/" + name, "main", pinned, license, linked ? SkillDistribution.Linked : SkillDistribution.Vendored)),
            linked ? null : @"C:\skills-index\domains\x\skills\" + name);

    public static IReadOnlyList<CatalogSkill> Catalog { get; } =
    [
        Skill("spark-optimization", "Optimiza trabajos de Spark y tablas Delta.", SkillSectors.IngenieriaDatos, ["spark", "delta", "ingenieria-datos"]),
        Skill("sql-optimization", "Ajusta consultas SQL lentas.", SkillSectors.IngenieriaDatos, ["sql", "ingenieria-datos"]),
        Skill("wpf-mvvm", "Patrones MVVM para aplicaciones de escritorio en WPF y .NET.", SkillSectors.Escritorio, ["wpf", "dotnet", "escritorio"]),
        Skill("test-driven-development", "Escribe la prueba primero y mírala fallar.", SkillSectors.IngenieriaSoftware, ["tdd", "ingenieria-software"], license: null),
        Skill("design-tokens", "Tokens de diseño y contraste accesible.", SkillSectors.UxUi, ["accessibility", "ux-ui"], linked: true),
    ];

    public static AgentDefinition Agent(string id, AgentRole role, ProviderId? provider = null) =>
        new(Aid(id), id, role, new ModelBinding(provider ?? ProviderId.ClaudeCode, provider == ProviderId.CodexCli ? VendorId.OpenAI : VendorId.Anthropic), string.Empty, [], [], [], new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));

    public static IReadOnlyList<AgentDefinition> Roster { get; } =
        [Agent("orquestador", AgentRole.Orchestrator), Agent("ejecutor", AgentRole.Executor), Agent("juez", AgentRole.Judge, ProviderId.CodexCli)];

    /// <summary>A health report whose overall status is <paramref name="status"/>, with the Phase 6 checks still unverified.</summary>
    public static SkillHealthReport Report(SkillHealthStatus status) => new(
    [
        new SkillHealthCheck(SkillHealthCodes.Frontmatter, status, status == SkillHealthStatus.Healthy ? "Cabecera conforme." : "La cabecera tiene un problema."),
        new SkillHealthCheck(SkillHealthCodes.License, SkillHealthStatus.Healthy, "Licencia permisiva: MIT."),
        new SkillHealthCheck(SkillHealthCodes.Upstream, SkillHealthStatus.Unknown, "Sin comprobar (Fase 6): si el repositorio de origen cambió."),
    ]);
}

/// <summary>The screen's backend over in-memory doubles; every piece can be taken out to see what the screen says.</summary>
internal sealed class SkillsWorld
{
    public FakeWorkspace Workspace { get; } = new();

    public FakeActivations Activations { get; } = new();

    public FakeRoster Roster { get; } = new([.. SkillsScreenSamples.Roster]);

    public FakeHealth Health { get; } = new();

    public FakeRecommendations Recommendations { get; } = new();

    public FakePrompt Prompt { get; } = new();

    /// <summary>The project's decision log, where the approval gate records the consent of the assisted recommendation (F13-20).</summary>
    public InMemoryDecisionLogStore Log { get; } = new();

    /// <summary>The real approval gate over <see cref="Prompt"/> and <see cref="Log"/>: the consent goes through it, as every approval.</summary>
    public ApprovalGate Gate => _gate ??= new ApprovalGate(Prompt, new DecisionRecorder(Log, TimeProvider.System), TimeProvider.System, new ApprovalGateOptions());

    private ApprovalGate? _gate;

    public List<CatalogSkill> Catalog { get; } = [.. SkillsScreenSamples.Catalog];

    public List<string> CatalogWarnings { get; } = [];

    public List<string> ProjectFiles { get; } = ["AgentStudio.slnx", "src/App/App.csproj", "README.md"];

    public int CatalogReads { get; private set; }

    public SkillsBackend Backend(
        bool catalog = true,
        bool activations = true,
        bool roster = true,
        bool health = true,
        bool recommendations = true,
        bool workspace = true,
        bool prompt = true) => new(
        catalog ? ReadCatalogAsync : null,
        activations ? Activations : null,
        roster ? Roster : null,
        health ? Health : null,
        recommendations ? Recommendations : null,
        workspace ? Workspace : null,
        prompt ? Gate : null);

    /// <summary>
    /// The backend with the doctor of skills wired to doubles: the permission on, a stored access key, and scripts in the first skill, so the panels that stay out
    /// of sight without them (the key, the scripts) are drawn and inventoried.
    /// </summary>
    public SkillsBackend DoctorBackend()
    {
        var inspector = new FakeInspector();
        foreach (var skill in Catalog.Where(c => c.LocalPath is not null))
        {
            inspector[skill.LocalPath!] = new SkillScriptsInfo(new string('a', 64), ["run.ps1", "lib/helper.py"]);
        }

        return Backend() with
        {
            Doctor = new FakeDoctor(),
            NetworkSetting = new FakeNetworkSetting(true),
            Tokens = new FakeTokens { Has = true },
            ScriptsInspector = inspector,
            ScriptReviews = new FakeReviews(),
        };
    }

    public SkillsDocumentViewModel Document(SkillsBackend? backend = null, string? projectRoot = SkillsScreenSamples.ProjectRoot, TimeProvider? time = null) =>
        new(projectRoot, backend ?? Backend(), time ?? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0), (_, _) => ProjectFiles);

    private Task<SkillCatalogReport> ReadCatalogAsync(CancellationToken ct)
    {
        CatalogReads++;
        return Task.FromResult(new SkillCatalogReport([.. Catalog], [.. CatalogWarnings]));
    }
}

internal sealed class FakeWorkspace : IProjectWorkspace
{
    public List<string> Opened { get; } = [];

    public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
    {
        Opened.Add(projectRoot);
        return Task.FromResult(new ProjectContext(SkillsScreenSamples.ProjectId, projectRoot));
    }
}

internal sealed class FakeActivations : ISkillActivationStore
{
    public List<SkillActivation> Decisions { get; } = [];

    public List<(string ProjectId, SkillActivation Activation, string DecidedBy)> Appended { get; } = [];

    public Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SkillActivation>>([.. Decisions]);

    public Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct)
    {
        Appended.Add((projectId, activation, decidedBy));
        Decisions.Add(activation);
        return Task.CompletedTask;
    }
}

internal sealed class FakeRoster(List<AgentDefinition> agents) : IAgentRoster
{
    public List<AgentDefinition> Agents { get; } = agents;

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentDefinition>>([.. Agents]);

    public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The Skills screen never changes the roster.");

    public Task DeleteAsync(string projectId, AgentId agent, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The Skills screen never changes the roster.");
}

internal sealed class FakeHealth : ISkillHealthReader
{
    public Dictionary<string, SkillHealthStatus> Statuses { get; } = new(StringComparer.Ordinal);

    public Task<SkillHealthReport> ReadLocalAsync(CatalogSkill skill, CancellationToken ct) =>
        Task.FromResult(SkillsScreenSamples.Report(Statuses.GetValueOrDefault(skill.Entry.Id.Value, SkillHealthStatus.Healthy)));
}

internal sealed class FakeRecommendations : ISkillRecommendationService
{
    public List<(ProjectContext Project, string Task, QuotaConsent Consent)> Asked { get; } = [];

    public List<SkillQuery> Queries { get; } = [];

    public AssistedRecommendationResult Answer { get; set; } = new([], [], null);

    public IReadOnlyList<SkillRecommendation> Recommend(SkillQuery query, IReadOnlyList<CatalogSkill> catalog)
    {
        Queries.Add(query);
        return SkillRecommender.Recommend(query, catalog.Select(c => c.Entry), 10);
    }

    public Task<AssistedRecommendationResult> AskAgentAsync(ProjectContext project, string task, QuotaConsent consent, CancellationToken ct)
    {
        Asked.Add((project, task, consent));
        return Task.FromResult(Answer);
    }
}

internal sealed class FakePrompt : IApprovalPrompt
{
    public bool Approve { get; set; } = true;

    public List<ApprovalRequest> Asked { get; } = [];

    public Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Asked.Add(request);
        return Task.FromResult(new ApprovalDecision(Approve, "user", T0.AddSeconds(Asked.Count), Approve ? "OK" : "No."));
    }
}

// The doctor of skills (docs/20 D-13 to D-15): doubles for its screen, shared by the tests of the view model and the inventory of the interface.
internal sealed class FakeDoctor : ISkillHealthBatchChecker
{
    public int Calls { get; private set; }

    public IReadOnlyList<SkillEntry> Asked { get; private set; } = [];

    public Func<IReadOnlyList<SkillEntry>, SkillHealthBatchResult> Answer { get; set; } =
        entries => new SkillHealthBatchResult([.. entries.Select(e => new SkillHealthBatchEntry(e.Id, new SkillHealthReport([]), null))], false, null);

    public Task<SkillHealthBatchResult> CheckAsync(IReadOnlyList<SkillEntry> skills, CancellationToken ct)
    {
        Calls++;
        Asked = skills;
        return Task.FromResult(Answer(skills));
    }
}

internal sealed class FakeNetworkSetting(bool allowed) : ISkillsDoctorNetworkSetting
{
    public bool NetworkAllowed { get; private set; } = allowed;

    public int Writes { get; private set; }

    public void Set(bool value)
    {
        NetworkAllowed = value;
        Writes++;
    }
}

internal sealed class FakeTokens : ISkillsDoctorTokenManager
{
    public bool Has { get; set; }

    public int Stores { get; private set; }

    public GitHubToken? Stored { get; private set; }

    public SkillsDoctorTokenChange Change { get; set; } = SkillsDoctorTokenChange.Done;

    public Exception? Throw { get; set; }

    public Task<bool> HasTokenAsync(CancellationToken ct) => Task.FromResult(Has);

    public Task<SkillsDoctorTokenChange> StoreAsync(GitHubToken token, CancellationToken ct)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        Stores++;
        if (Change == SkillsDoctorTokenChange.Done)
        {
            Stored = token;
            Has = true;
        }

        return Task.FromResult(Change);
    }

    public Task<SkillsDoctorTokenChange> DeleteAsync(CancellationToken ct)
    {
        Has = false;
        return Task.FromResult(SkillsDoctorTokenChange.Done);
    }
}

internal sealed class FakeInspector : Dictionary<string, SkillScriptsInfo>, ISkillScriptsInspector
{
    public Task<SkillScriptsInfo?> InspectAsync(string skillFolder, CancellationToken ct) => Task.FromResult(TryGetValue(skillFolder, out var info) ? info : null);
}

internal sealed class FakeReviews : ISkillScriptReviews
{
    public Dictionary<SkillId, string> Marked { get; } = [];

    public Task<string?> ReviewedSha256Async(SkillId skill, CancellationToken ct) => Task.FromResult(Marked.TryGetValue(skill, out var hash) ? hash : null);

    public Task MarkReviewedAsync(SkillId skill, string scriptsSha256, CancellationToken ct)
    {
        Marked[skill] = scriptsSha256;
        return Task.CompletedTask;
    }
}

/// <summary>A checker that never answers until it is cancelled: the batch in progress, for the button that cancels it.</summary>
internal sealed class BlockingDoctor : ISkillHealthBatchChecker
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<SkillHealthBatchResult> CheckAsync(IReadOnlyList<SkillEntry> skills, CancellationToken ct)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("Unreachable.");
    }
}
