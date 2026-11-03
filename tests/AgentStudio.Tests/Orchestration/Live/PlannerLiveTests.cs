using System.Diagnostics;
using System.Globalization;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Providers.Live;

namespace AgentStudio.Tests.Orchestration.Live;

/// <summary>
/// The planner against the real Claude Code CLI (spike S3 made real): read-only tools (Read, Grep, Glob, <c>dontAsk</c>)
/// and the plan schema on a small repository, with Haiku. It spends subscription quota, so it needs
/// AGENTSTUDIO_PROVIDER_TESTS=1; run it on its own with <c>--filter-class</c> (one or two agent runs). Only the Claude
/// adapter is real: stores and worktrees are the in-memory fakes, the <c>_base</c> worktree a folder with the files below.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class PlannerLiveTests
{
    private static readonly Dictionary<string, string> Repository = new()
    {
        ["README.md"] = "# CalcLib\n\nUna biblioteca mínima de cálculo con sus pruebas xUnit.\n",
        ["src/Calc/Calc.cs"] = "namespace CalcLib;\n\npublic static class Calc\n{\n    public static int Add(int a, int b) => a + b;\n}\n",
        ["src/Calc/Calc.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n",
        ["tests/Calc.Tests/CalcTests.cs"] = "using CalcLib;\n\npublic sealed class CalcTests\n{\n    [Fact]\n    public void Add_suma() => Assert.Equal(5, Calc.Add(2, 3));\n}\n",
        ["tests/Calc.Tests/Calc.Tests.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup><ProjectReference Include=\"..\\..\\src\\Calc\\Calc.csproj\" /></ItemGroup>\n</Project>\n",
    };

    [Fact]
    public async Task Real_claude_plans_a_small_repository_against_the_portable_schema()
    {
        LiveTestSupport.RequireOptIn();
        var executable = new ExecutableLocator().Find("claude");
        Assert.SkipUnless(
            executable is not null && !executable.TrimEnd('.', ' ').EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !executable.TrimEnd('.', ' ').EndsWith(".bat", StringComparison.OrdinalIgnoreCase),
            "The planner needs the native claude executable (the inline --json-schema cannot go through a .cmd shim).");

        using var harness = new EngineHarness(repositoryFiles: Repository);
        var adapter = new ClaudeCodeAgentProvider(new ExecutableLocator(), new StreamingProcessRunner(), TimeProvider.System);
        harness.Providers = new FakeProjectAgentProviders([AgentProviderPipeline.Compose(adapter, harness.Journal, TimeProvider.System)]);
        var orchestrator = OrchestrationFixtures.Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob"]) with
        {
            Budget = new ExecutionBudget(12, TimeSpan.FromMinutes(6)),
            SystemInstructions = string.Empty,
        };
        IReadOnlyList<AgentDefinition> roster = [orchestrator, .. OrchestrationFixtures.Roster().Where(a => a.Role != AgentRole.Orchestrator)];
        var request = harness.PlanningRequest("Añade el método Multiply(a, b) a la clase Calc de src/Calc/Calc.cs, con una prueba xUnit en tests/Calc.Tests/.") with
        {
            Roster = roster,
        };
        var events = new List<AgentEvent>();
        var progress = new Collector(events);
        var clock = Stopwatch.StartNew();

        var proposal = await harness.CreateOrchestrator().PlanAsync(request, progress, TestContext.Current.CancellationToken);
        clock.Stop();

        // Evidence first, so it survives a failing assertion below.
        var usage = events.OfType<UsageReported>().LastOrDefault()?.Usage;
        var runs = harness.Payloads<AgentRunFinishedPayload>().Select(p => p.Payload).ToList();
        LiveTestSupport.Note(string.Create(CultureInfo.InvariantCulture, $"planner live: status {proposal.Status}, corrected {proposal.Corrected}, runs {runs.Count}, {clock.Elapsed.TotalSeconds:0.0} s, schema {Encoding().GetByteCount(PlanSchema.Build(OrchestrationFixtures.Context(roster: roster)))} bytes"));
        LiveTestSupport.Note($"planner live: cost {usage?.CostUsd.Value?.ToString(CultureInfo.InvariantCulture) ?? "n/a"} USD ({usage?.CostUsd.Source ?? "-"}), input {usage?.InputTokens.Value?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}, output {usage?.OutputTokens.Value?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
        LiveTestSupport.Note($"planner live: tools used {string.Join(", ", events.OfType<ToolInvoked>().Select(t => t.Tool))}");
        LiveTestSupport.Note($"planner live: error {proposal.Error ?? "-"}; violations {string.Join("; ", proposal.Violations.Select(v => $"[{v.Code}] {v.Message}"))}");
        if (proposal.Plan is { } plan)
        {
            foreach (var task in plan.Tasks)
            {
                LiveTestSupport.Note($"planner live: task {task.Id} {task.Kind} agent={task.Agent?.Value ?? "-"} workspace={task.Workspace} scope=[{string.Join(", ", task.WriteScope)}] steps=[{string.Join(", ", task.VerificationSteps)}] deps=[{string.Join(", ", task.DependsOn.Select(d => d.Prerequisite.Value))}]");
            }
        }

        if (proposal.RawPlanEvidence is { } raw)
        {
            LiveTestSupport.Note("planner live: raw plan " + harness.Evidence.Text(raw));
        }

        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.NotNull(proposal.Plan);
        Assert.Empty(proposal.Violations);
        Assert.Contains(proposal.Plan.Tasks, t => t.Covers.Contains(new RequirementId("RQ-01")));
        Assert.Contains(events, e => e is ToolInvoked { Tool: "Read" or "Grep" or "Glob" });
        Assert.All(runs, r => Assert.Equal(AttemptOutcome.Succeeded, r.Status));
        Assert.Equal(EvidenceKind.AgentStatement, harness.Evidence.Record(proposal.RawPlanEvidence!.Value).Kind);
        Assert.Single(harness.Payloads<PlanCreatedPayload>());
        Assert.Empty(harness.Payloads<AuditAlertPayload>());
    }

    private static System.Text.Encoding Encoding() => System.Text.Encoding.UTF8;

    private sealed class Collector(List<AgentEvent> events) : IProgress<AgentEvent>
    {
        private readonly Lock _gate = new();

        public void Report(AgentEvent value)
        {
            lock (_gate)
            {
                events.Add(value);
            }
        }
    }
}
