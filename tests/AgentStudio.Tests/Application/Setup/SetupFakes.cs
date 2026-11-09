using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Setup;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Team;

namespace AgentStudio.Tests.Application.Setup;

/// <summary>The fakes of the configurator: they hold what was written, and count every write, so a test sees that a proposal wrote nothing.</summary>
internal static class SetupFakes
{
    public static ProjectContext Project { get; } = new("proyecto-1", @"C:\proyectos\calc");

    public static SetupRepositoryFacts Facts(IEnumerable<string> files, IReadOnlyList<RepositoryRemote>? remotes = null) =>
        new(Project.RootPath, "main", "abc1234", false, [.. files], false, remotes ?? []);

    public static VerificationProfile Profile(string name = "dotnet", string executable = "dotnet") => new(
        name,
        [new VerificationStep("build", "Compilar", executable, ["build"], EvidenceKind.BuildLog, TestReportFormat.None, null, TimeSpan.FromMinutes(10))]);
}

internal sealed class FakeRepositoryReader(SetupRepositoryFacts facts) : ISetupRepositoryReader
{
    public int Reads { get; private set; }

    public Task<SetupRepositoryFacts> ReadAsync(ProjectContext project, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(facts);
    }
}

internal sealed class InMemoryRoster(IEnumerable<AgentDefinition>? agents = null) : IAgentRoster
{
    private readonly List<AgentDefinition> _agents = [.. agents ?? []];

    public int Saves { get; private set; }

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentDefinition>>([.. _agents]);

    public Task SaveAsync(string projectId, AgentDefinition agent, CancellationToken cancellationToken)
    {
        Saves++;
        var at = _agents.FindIndex(a => a.Id == agent.Id);
        if (at >= 0)
        {
            _agents[at] = agent;
        }
        else
        {
            _agents.Add(agent);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeProfiles(string? stackProfileName = "dotnet") : ISetupVerificationProfiles
{
    public VerificationProfile? Stored { get; set; }

    public bool Unreadable { get; set; }

    public int Saves { get; private set; }

    public VerificationProfile? Propose(IReadOnlyList<string> relativeFiles) =>
        stackProfileName is null ? null : SetupFakes.Profile(stackProfileName);

    public Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken ct) =>
        Unreadable ? throw new InvalidDataException("The profile file breaks a rule.") : Task.FromResult(Stored);

    public Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken ct)
    {
        Saves++;
        Stored = profile;
        return Task.CompletedTask;
    }
}

internal sealed class FakeOptionsStore : IProjectOptionsStore
{
    public ProjectRunOptions? Stored { get; private set; }

    public int Saves { get; private set; }

    public Task<ProjectRunOptions?> LoadAsync(ProjectContext project, CancellationToken ct) => Task.FromResult(Stored);

    public Task SaveAsync(ProjectContext project, ProjectRunOptions options, CancellationToken ct)
    {
        Saves++;
        Stored = options;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRecorder : IDecisionRecorder
{
    public List<(Actor Actor, DecisionKind Kind, string Summary, DecisionPayload Payload)> Entries { get; } = [];

    public bool FailWithIntegrity { get; set; }

    public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default)
    {
        if (FailWithIntegrity)
        {
            throw new DecisionLogIntegrityException("The stored log does not verify.");
        }

        Entries.Add((actor, kind, summary, payload));
        return Task.FromResult(new DecisionEntry(Entries.Count, DateTimeOffset.UnixEpoch, actor, kind, summary, "{}", null, new string('0', 64), new string('1', 64)));
    }
}

internal sealed class FakeBrief : IProjectBrief
{
    public ProjectBrief? Stored { get; set; }

    public int Saves { get; private set; }

    public Task<ProjectBrief?> ReadAsync(ProjectContext project, CancellationToken ct) => Task.FromResult(Stored);

    public Task<ProjectBrief> SaveAsync(ProjectContext project, string text, string decidedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Saves++;
        Stored = new ProjectBrief("entry-" + Saves, text.Trim(), ContentHash.Sha256Hex(text.Trim()), DateTimeOffset.UnixEpoch);
        return Task.FromResult(Stored);
    }
}

internal sealed class FakeGate(bool confirm) : ISetupPresenceGate
{
    public List<(PresenceAction Action, string Subject)> Asked { get; } = [];

    public Task<SetupPresenceResult> ConfirmAsync(PresenceAction action, string subject, CancellationToken ct)
    {
        Asked.Add((action, subject));
        return Task.FromResult(confirm ? new SetupPresenceResult(true) : new SetupPresenceResult(false, AgentStudio.Application.Localization.UserText.Of("setup.hello.not-confirmed")));
    }
}

internal sealed class FakeActivations : ISkillActivationStore
{
    public List<(SkillActivation Activation, string DecidedBy)> Appended { get; } = [];

    public Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SkillActivation>>([.. Appended.Select(a => a.Activation)]);

    public Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct)
    {
        Appended.Add((activation, decidedBy));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSkillCatalog(params CatalogSkill[] skills) : ISkillCatalog
{
    public Task<IReadOnlyList<CatalogSkill>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogSkill>>(skills);

    public static CatalogSkill Skill(string name, IEnumerable<string> tags, bool linked = false) => new(
        new SkillEntry(new SkillId(name), name, "resumen", SkillSectors.ArquitecturaSoftware, tags, new SkillSource("owner/repo", "skills/" + name, "main", null, "MIT", linked ? SkillDistribution.Linked : SkillDistribution.Vendored)),
        linked ? null : @"C:\skills\" + name);
}

/// <summary>A template of the default shape, as a project that opened the orchestrator has it.</summary>
internal static class SetupTeams
{
    public static AgentDefinition Agent(string id, AgentRole role, ProviderId provider, VendorId vendor, IReadOnlyList<string>? tools = null) => new(
        new AgentId(id),
        char.ToUpperInvariant(id[0]) + id[1..],
        role,
        new ModelBinding(provider, vendor),
        string.Empty,
        [],
        tools ?? ["Read"],
        [],
        new ExecutionBudget(30, TimeSpan.FromMinutes(15)),
        1,
        role is AgentRole.Judge ? MemoryPolicy.None : MemoryPolicy.ReadSelected,
        new ReviewPolicy(false, false));

    public static IReadOnlyList<AgentDefinition> Default() =>
    [
        Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic),
        Agent("ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Edit", "Write"]),
        Agent("juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, []),
    ];
}
