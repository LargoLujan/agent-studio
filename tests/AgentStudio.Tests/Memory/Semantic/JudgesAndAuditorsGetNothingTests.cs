using System.Reflection;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Semantic;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Semantic;

/// <summary>
/// F12-8 (docs/17 D-06; ADR-0047, decision 9; ADR-0022, decision 7): the judges and the auditors receive nothing of the
/// semantic index, neither in a pack (the packer side is <c>ContextPackerSemanticTests</c>) nor as a tool. Their launch has
/// no MCP server and no <c>&lt;project_memory&gt;</c> block, and no type of the module that judges, audits and debates can
/// even be given the memory, the packer or the index.
/// </summary>
public sealed class JudgesAndAuditorsGetNothingTests
{
    private static readonly Type[] MemoryPorts =
    [
        typeof(IProjectMemory),
        typeof(IContextPacker),
        typeof(ISemanticIndex),
        typeof(ISemanticIndexStore),
        typeof(IEmbedderSource),
        typeof(IEmbedder),
        typeof(ProjectHistorySnapshot),
    ];

    [Theory]
    [InlineData(AgentRole.Judge)]
    [InlineData(AgentRole.Auditor)]
    public async Task The_launch_of_a_judge_or_an_auditor_has_no_tool_of_the_index_and_no_memory(AgentRole role)
    {
        var agent = JudgingWorld.Agent(new AgentId("juez-" + role.ToString().ToLowerInvariant()), role, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5")) with
        {
            AllowedTools = ["Read", "Grep", "Glob"],
        };
        var dossier = Dossier();
        var provider = new ScriptedAgentProvider(ProviderId.CodexCli, VendorId.OpenAI, (_, _) => JudgeAnswers.Run(JudgeAnswers.Approval(dossier, Eid("E1"))));
        var judge = new AgentJudge(provider, new JudgeLaunch(agent, "C:\\wt\\_integration", null), new FakeTimeProvider(T0));

        await judge.EvaluateAsync(dossier, TestContext.Current.CancellationToken);

        var request = Assert.Single(provider.Requests);
        Assert.True(request.McpServers is null or { Count: 0 });
        Assert.DoesNotContain(request.Permissions.AllowedTools, t => t.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.DoesNotContain("<project_memory", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(ProjectHistoryNames.Tool, request.Prompt, StringComparison.Ordinal);
        Assert.Equal(MemoryPolicy.None, request.Agent.Memory);
    }

    [Fact]
    public void No_type_that_judges_audits_or_debates_can_be_given_the_memory_the_packer_or_the_index()
    {
        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var module = typeof(AgentJudge).Assembly;

        var reachable = module.GetTypes()
            .SelectMany(type => type.GetConstructors(Everything).SelectMany(c => c.GetParameters()).Select(p => (Type: type, Port: p.ParameterType))
                .Concat(type.GetFields(Everything).Select(f => (Type: type, Port: f.FieldType)))
                .Concat(type.GetProperties(Everything).Select(p => (Type: type, Port: p.PropertyType))))
            .Where(use => Mentions(use.Port))
            .Select(use => $"{use.Type.FullName} → {use.Port.Name}")
            .Distinct()
            .ToList();

        Assert.Empty(reachable);
        Assert.DoesNotContain(module.GetReferencedAssemblies(), a => a.Name == typeof(AgentStudio.Memory.Semantic.SemanticIndex).Assembly.GetName().Name);
    }

    /// <summary>Whether <paramref name="type"/> is one of the ports, or is made of one (a generic argument, an array, a delegate).</summary>
    private static bool Mentions(Type type) =>
        MemoryPorts.Contains(type)
        || (type.HasElementType && Mentions(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions))
        || (typeof(Delegate).IsAssignableFrom(type) && type.GetMethod("Invoke") is { } invoke
            && (Mentions(invoke.ReturnType) || invoke.GetParameters().Any(p => Mentions(p.ParameterType))));
}
