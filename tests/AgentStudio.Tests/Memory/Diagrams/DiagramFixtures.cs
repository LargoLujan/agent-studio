using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Memory.Diagrams;

/// <summary>
/// The fixed models of the golden files of the diagrams (docs/20 D-24, F6-10): a model of Auto Graph with folders of the solution, a partial project, namespaces, packages and a
/// relation an AI proposed; a plan with a condition and an approval; and a prepared log of one run. None of it is this repository.
/// </summary>
internal static class DiagramFixtures
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    public const string Run = "run-golden";

    public const string AppProject = "src/App/App.csproj";

    public const string CoreProject = "src/libs/Core/Core.csproj";

    public const string TestsProject = "tests/Core.Tests/Core.Tests.csproj";

    public static readonly DateTimeOffset At = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public static CodeGraph Graph(string? appName = null, bool withInference = true)
    {
        var nodes = new List<GraphNode>
        {
            new("Sample.slnx", GraphNodeKind.Solution, "Sample", "Sample.slnx", false),
            new("folder:/src/", GraphNodeKind.SolutionFolder, "src", null, false),
            new("folder:/src/libs/", GraphNodeKind.SolutionFolder, "libs", null, false),
            new("folder:/empty/", GraphNodeKind.SolutionFolder, "empty", null, false),
            new(AppProject, GraphNodeKind.Project, appName ?? "App", AppProject, false),
            new(CoreProject, GraphNodeKind.Project, "Core", CoreProject, false),
            new(TestsProject, GraphNodeKind.Project, "Core.Tests", TestsProject, true),
            new(AppProject + "|Calc.App", GraphNodeKind.Namespace, "Calc.App", null, false),
            new(CoreProject + "|Calc.Core", GraphNodeKind.Namespace, "Calc.Core", null, false),
            new(CoreProject + "|Calc.Core.Rules", GraphNodeKind.Namespace, "Calc.Core.Rules", null, false),
            new("package:Json", GraphNodeKind.Package, "Json", null, false),
        };
        var edges = new List<GraphEdge>
        {
            Static("Sample.slnx", "folder:/src/", GraphEdgeKind.Contains),
            Static("Sample.slnx", "folder:/empty/", GraphEdgeKind.Contains),
            Static("Sample.slnx", TestsProject, GraphEdgeKind.Contains),
            Static("folder:/src/", "folder:/src/libs/", GraphEdgeKind.Contains),
            Static("folder:/src/", AppProject, GraphEdgeKind.Contains),
            Static("folder:/src/libs/", CoreProject, GraphEdgeKind.Contains),
            Static(AppProject, AppProject + "|Calc.App", GraphEdgeKind.Contains),
            Static(CoreProject, CoreProject + "|Calc.Core", GraphEdgeKind.Contains),
            Static(CoreProject, CoreProject + "|Calc.Core.Rules", GraphEdgeKind.Contains),
            Static(AppProject, CoreProject, GraphEdgeKind.ProjectReference),
            Static(TestsProject, CoreProject, GraphEdgeKind.ProjectReference),
            Static(CoreProject, "package:Json", GraphEdgeKind.PackageReference),
            Static(AppProject + "|Calc.App", CoreProject + "|Calc.Core", GraphEdgeKind.NamespaceUse),
            Static(CoreProject + "|Calc.Core", CoreProject + "|Calc.Core.Rules", GraphEdgeKind.NamespaceUse),
        };
        if (withInference)
        {
            edges.Add(new GraphEdge(AppProject, TestsProject, GraphEdgeKind.ProjectReference, GraphEdgeOrigin.Inferred, new GraphInference("proponente", "modelo", At, "razón")));
        }

        return new CodeGraph(Commit, 1, nodes, edges, [new GraphGap(TestsProject, "graph.unevaluated", TestsProject, 12)]);
    }

    private static GraphEdge Static(string from, string to, GraphEdgeKind kind) => new(from, to, kind, GraphEdgeOrigin.Static);

    public static PlanDefinition Plan(string? hostileTitle = null, string? hostileBranch = null)
    {
        TaskSpec Task(string id, string title, WorkTaskKind kind, params DependencySpec[] depends) => new(
            new TaskId(id), title, kind, null, "objetivo", "instrucciones", depends, JoinPolicy.AllCompleted, [], [], [], null, null, 0, null, WorkspaceMode.None);

        return PlanDefinition.Create(
            "Resumen del plan",
            [
                Task("T1", hostileTitle ?? "Preparar", WorkTaskKind.AgentWork),
                Task("T2", "Implementar", WorkTaskKind.AgentWork, new DependencySpec(new TaskId("T1"))),
                Task("T3", "Comprobar", WorkTaskKind.Condition, new DependencySpec(new TaskId("T2"))),
                Task("T4", "Aprobar", WorkTaskKind.HumanApproval, new DependencySpec(new TaskId("T3"), hostileBranch ?? "si")),
                Task("T5", "Integrar", WorkTaskKind.Integration, new DependencySpec(new TaskId("T4"))),
            ],
            [],
            PlanSource.User,
            null);
    }

    /// <summary>The prepared log of one run: the person starts it, an agent works on two tasks, the system verifies, the judge approves and the run is integrated and ends.</summary>
    public static IReadOnlyList<DecisionEntry> Log(string run = Run, string executor = "ana", string? taskTitle = null)
    {
        var plan = Plan();
        var entries = new List<DecisionEntry>();
        var agent = new AgentId(executor);
        void Add(ActorKind actor, DecisionKind kind, DecisionPayload payload)
        {
            var sequence = entries.Count + 1;
            var previous = entries.Count == 0 ? DecisionLog.GenesisHash : entries[^1].Hash;
            var json = DecisionPayloadSerializer.Serialize(payload);
            entries.Add(new DecisionEntry(
                sequence,
                At.AddMinutes(sequence),
                new Actor(actor, actor.ToString()),
                kind,
                "resumen",
                json,
                null,
                previous,
                ContentHash.Sha256Hex($"{previous}|{sequence}|{json}")));
        }

        Add(
            ActorKind.System,
            DecisionKind.RunStarted,
            new RunStartedPayload(
                plan.ContentHash,
                new string('b', 64),
                Commit,
                "main",
                new string('c', 64),
                [
                    new RosterEntry(new AgentId("orquestador"), AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic),
                    new RosterEntry(agent, AgentRole.Executor, ProviderId.CodexCli, VendorId.OpenAI),
                    new RosterEntry(new AgentId("juez"), AgentRole.Judge, ProviderId.GeminiCli, VendorId.Google),
                ],
                null)
            { RunId = run });
        Add(ActorKind.Orchestrator, DecisionKind.TaskCreated, new TaskCreatedPayload(new TaskId("T1"), taskTitle ?? "Preparar", WorkTaskKind.AgentWork, JoinPolicy.AllCompleted, 1) { RunId = run });
        Add(ActorKind.Orchestrator, DecisionKind.TaskCreated, new TaskCreatedPayload(new TaskId("T2"), "Implementar", WorkTaskKind.AgentWork, JoinPolicy.AllCompleted, 1) { RunId = run });
        Add(ActorKind.System, DecisionKind.AgentRunStarted, new AgentRunStartedPayload(new TaskId("T1"), 1, agent, ProviderId.CodexCli, VendorId.OpenAI, null, "C:\\wt", new EvidenceId("prompt-1"), new string('d', 64), []) { RunId = run });
        Add(ActorKind.System, DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(new TaskId("T1"), 1, AttemptOutcome.Succeeded, 0, null, null, null, null) { RunId = run });
        Add(ActorKind.System, DecisionKind.EvidenceRegistered, new EvidenceRegisteredPayload(new EvidenceId("tests-1"), EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, new string('e', 64), new TaskId("T1"), 1) { RunId = run });
        Add(ActorKind.System, DecisionKind.AgentRunStarted, new AgentRunStartedPayload(new TaskId("T2"), 1, agent, ProviderId.CodexCli, VendorId.OpenAI, null, "C:\\wt", new EvidenceId("prompt-2"), new string('f', 64), []) { RunId = run });
        Add(ActorKind.System, DecisionKind.AgentRunFinished, new AgentRunFinishedPayload(new TaskId("T2"), 1, AttemptOutcome.Failed, 1, null, null, null, null) { RunId = run });
        Add(ActorKind.Orchestrator, DecisionKind.ReviewRequested, new ReviewRequestedPayload(new TaskId("T1"), new AgentId("juez")) { RunId = run });
        Add(ActorKind.Judge, DecisionKind.VerdictIssued, new VerdictIssuedPayload("v-1", new TaskId("T1"), VerdictDecision.Approved, true, [], new string('b', 64), new string('a', 64), null, null) { RunId = run });
        Add(ActorKind.System, DecisionKind.IntegrationPerformed, new IntegrationPerformedPayload(new TaskId("T1"), "agentstudio/run-" + run, ["refs/agentstudio/" + run + "/T1"], new string('9', 40), [], false) { RunId = run });
        Add(ActorKind.Orchestrator, DecisionKind.ApprovalRequested, new ApprovalRequestedPayload("integrate.deliver", "Entregar", RiskLevel.Destructive, null, []) { RunId = run });
        Add(ActorKind.User, DecisionKind.ApprovalGranted, new ApprovalAnsweredPayload("integrate.deliver", true, "persona", null, null) { RunId = run });
        Add(ActorKind.System, DecisionKind.RunFinished, new RunFinishedPayload(RunPhase.Succeeded, "fin", null, null) { RunId = run });
        return entries;
    }

    /// <summary>The text of a golden file as it is written in the repository: <c>\n</c> as the only line end whatever the checkout did to it.</summary>
    public static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Memory", "Diagrams", "Fixtures", name), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Compares <paramref name="actual"/> with the golden file <paramref name="name"/> byte by byte. With the variable <c>AGENTSTUDIO_UPDATE_GOLDEN</c> set it writes the
    /// file in the source tree instead (to be reviewed before it is committed): the goldens are never updated by an ordinary run.
    /// </summary>
    public static void AssertGolden(string name, string actual, [CallerFilePath] string source = "")
    {
        if (Environment.GetEnvironmentVariable("AGENTSTUDIO_UPDATE_GOLDEN") == "1")
        {
            var folder = Path.Combine(Path.GetDirectoryName(source)!, "Fixtures");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name), new UTF8Encoding(false).GetBytes(actual));
            return;
        }

        Assert.Equal(Golden(name), actual);
        Assert.DoesNotContain('\r', actual);
    }
}
