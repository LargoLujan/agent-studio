using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Debate.Judging;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Engine = AgentStudio.Orchestration.Engine;

namespace AgentStudio.Tests.Integration.Phase12;

/// <summary>
/// F12-8 (docs/17 D-06; ADR-0047, decision 9; ADR-0022, decision 7) in the app as it composes itself, with the index and
/// the planner's tool composed: a judge and an auditor receive nothing of the index on either path. Their pack carries no
/// memory and does not even ask the index (the path of the context pack), and the history tool is never offered to them,
/// nor is a snapshot written for them (the path of the MCP server). Only the planner gets the tool, and in the engine only
/// the planner can reach it: no type of the module that judges, audits and debates, and no type of the engine but its
/// ports, the orchestrator and the planner, can be given it. WP3 fixes the same for the packer over fakes
/// (<c>ContextPackerSemanticTests</c>) and for the launch of a judge (<c>JudgesAndAuditorsGetNothingTests</c>).
/// </summary>
public sealed class JudgesAndAuditorsGetNothingInTheAppTests
{
    /// <summary>The folder of the snapshots under the local data of the app (<c>SemanticIndexServices.HistorySnapshotsFolder</c>).</summary>
    private const string SemanticIndexServicesFolder = "history-snapshots";

    private static readonly PermissionProfile ReadOnly = new(false, false, false, ["Read", "Grep", "Glob"], [], []);

    private static readonly Type[] HistoryPorts =
    [
        typeof(IProjectHistoryTool),
        typeof(ProjectHistoryTool),
        typeof(ProjectHistoryOffer),
        typeof(IProjectHistoryFolders),
        typeof(ProjectHistoryFolder),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentDefinition Agent(string id, AgentRole role) => new(
        new AgentId(id), id, role, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "i", [], ["Read", "Grep", "Glob"], [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.ReadSelected, new ReviewPolicy(false, false));

    [Theory]
    [InlineData(AgentRole.Judge)]
    [InlineData(AgentRole.Auditor)]
    public async Task A_judge_or_an_auditor_with_memory_gets_neither_a_pack_nor_the_tool_while_the_planner_gets_the_tool(AgentRole role)
    {
        using var temp = new TempDirectory();
        var folders = new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddAgentStudioApp(folders);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var root = temp.CreateDirectory("calc");
        var project = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        var entry = await provider.GetRequiredService<IProjectMemory>().RecordAsync(
            project,
            new MemoryDraft(MemoryKind.KnownError, "Facturas", "Test en rojo en el módulo de facturas.", MemoryOrigin.System, "nota:1"),
            Ct);
        Assert.True((await provider.GetRequiredService<ISemanticIndex>().RefreshAsync(project, Ct)).Completed);
        var packer = provider.GetRequiredService<IContextPacker>();
        var tool = provider.GetRequiredService<IProjectHistoryTool>();
        var snapshots = Path.Combine(folders.Local.BaseDirectory, SemanticIndexServicesFolder);
        var reviewer = Agent("revisor", role);

        // The path of the context pack: nothing of the memory, and the index is not even asked.
        var pack = await packer.PackAsync(project, new ContextPackRequest(reviewer, "facturas", [], 10_000), Ct);
        Assert.Empty(pack.Items);
        Assert.Empty(pack.Omitted);
        Assert.Null(pack.Relevance);

        // The path of the MCP server: no tool and no snapshot, read-only on Claude Code and with memory as it may be.
        using (var offer = await tool.OfferAsync(project, reviewer, ReadOnly, Ct))
        {
            Assert.False(offer.Offered);
            Assert.Null(offer.Server);
            Assert.Equal(ProjectHistoryTool.NotThePlannerReason, offer.Withheld);
        }

        Assert.False(Directory.Exists(snapshots), "A snapshot was written for a judge or an auditor.");

        // The controls: an executor with memory gets the entry in its pack; the planner gets the tool over a snapshot of it.
        var executor = await packer.PackAsync(project, new ContextPackRequest(Agent("ejecutor", AgentRole.Executor), "facturas", [], 10_000), Ct);
        Assert.Contains(executor.Items, i => i.Id == entry.Id);
        using (var planner = await tool.OfferAsync(project, Agent("orquestador", AgentRole.Orchestrator), ReadOnly, Ct))
        {
            Assert.True(planner.Offered, planner.Withheld);
            Assert.Equal(ProjectHistoryNames.SnapshotArgument, planner.Server!.Arguments[0]);
            var snapshot = ProjectHistorySnapshotJson.Read(await File.ReadAllTextAsync(planner.Server.Arguments[1], Ct));
            Assert.Contains(snapshot.Fragments, f => f.Text.Contains("Test en rojo", StringComparison.Ordinal));
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(snapshots));
    }

    [Fact]
    public void Only_the_ports_the_orchestrator_and_the_planner_can_reach_the_tool_and_nothing_that_judges_audits_or_debates()
    {
        var engine = Reaching(typeof(Engine.Orchestrator).Assembly);
        var debate = Reaching(typeof(AgentJudge).Assembly);

        Assert.Equal(["AgentStudio.Orchestration.Engine.EnginePorts", "AgentStudio.Orchestration.Engine.Orchestrator", "AgentStudio.Orchestration.Planning.Planner"], engine);
        Assert.Empty(debate);
    }

    /// <summary>
    /// The types of <paramref name="module"/> that can hold one of the history ports: through a constructor, a field (an
    /// async method's included, counted for the type that declares it) or a property.
    /// </summary>
    private static List<string> Reaching(Assembly module)
    {
        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return [.. module.GetTypes()
            .Where(type => type.GetConstructors(Everything).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(type.GetFields(Everything).Select(f => f.FieldType))
                .Concat(type.GetProperties(Everything).Select(p => p.PropertyType))
                .Any(Mentions))
            .Select(Owner)
            .Select(type => type.FullName!)
            .Distinct()
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The type the code of <paramref name="type"/> was written in: itself, or the one that declares a generated type.</summary>
    private static Type Owner(Type type)
    {
        while (type.IsNested && type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
        {
            type = type.DeclaringType!;
        }

        return type;
    }

    private static bool Mentions(Type type) =>
        HistoryPorts.Contains(type)
        || (type.HasElementType && Mentions(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions))
        || (typeof(Delegate).IsAssignableFrom(type) && type.GetMethod("Invoke") is { } invoke
            && (Mentions(invoke.ReturnType) || invoke.GetParameters().Any(p => Mentions(p.ParameterType))));
}
