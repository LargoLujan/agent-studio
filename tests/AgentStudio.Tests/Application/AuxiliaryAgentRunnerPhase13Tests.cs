using System.IO;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Debate.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/17 §8.2 (ADR-0052): the auxiliary runs of Phase 13 («Revisar el orden», the analysis of consumption with an agent and the
/// chat of preparation) are run and recorded as auxiliary runs, and their end is <c>AgentRunFinished</c> version 3 with its class;
/// the earlier ones keep version 2.
/// </summary>
public sealed class AuxiliaryAgentRunnerPhase13Tests
{
    private static readonly ProjectContext Project = new("6f1c2b0e-0000-4000-8000-0000000000ab", "C:\\repo");

    private static readonly AgentDefinition Analyst = new(
        Aid("analista-consumo"),
        "Analista",
        AgentRole.Auditor,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic, "claude-haiku-4-5"),
        string.Empty,
        [],
        [],
        [],
        new ExecutionBudget(3, TimeSpan.FromMinutes(5)),
        1,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    [Theory]
    [InlineData(AgentRunPurpose.OrderReview, true)]
    [InlineData(AgentRunPurpose.ConsumptionAnalysis, true)]
    [InlineData(AgentRunPurpose.PreparationChat, true)]

    // Phase 14 (docs/17 §8.2): the agent of the help and the draft of a document for clients, also with the class of their end.
    [InlineData(AgentRunPurpose.Help, true)]
    [InlineData(AgentRunPurpose.ClientDocumentDraft, true)]
    [InlineData(AgentRunPurpose.GraphRelations, true)]
    [InlineData(AgentRunPurpose.SkillRecommendation, false)]
    [InlineData(AgentRunPurpose.DailySummary, false)]
    public async Task The_auxiliary_runs_of_phase_13_record_their_end_with_its_class(AgentRunPurpose purpose, bool endClass)
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var recorder = new DecisionRecorder(store, clock);
        var outcome = new AgentRunOutcome(AgentRunStatus.Failed, 1, null, null, "s", "sin ventana", new AgentRunEnd(AttemptEndClass.ContextExhausted));
        var runner = new AuxiliaryAgentRunner(recorder, new FakeEvidenceStore(clock), new Providers(new ScriptedProvider(outcome)), clock);

        using var folder = EmptyFolder();
        var result = await runner.RunAsync(Project, new AuxiliaryRunRequest(purpose, new AgentRunRequest(Analyst, "Analiza.", folder.Path, new PermissionProfile(false, false, false, ["Read", "Grep", "Glob"], [], []))), null, TestContext.Current.CancellationToken);

        Assert.True(AuxiliaryAgentRunner.IsAuxiliary(purpose));
        Assert.StartsWith(AuxiliaryAgentRunner.RunPrefix, result.Run.Value, StringComparison.Ordinal);
        var entries = store.Of(Project.ProjectId);
        var started = DecisionPayloadSerializer.Deserialize<AgentRunStartedPayload>(Assert.Single(entries, e => e.Kind == DecisionKind.AgentRunStarted).PayloadJson);
        var finished = DecisionPayloadSerializer.Deserialize<AgentRunFinishedPayload>(Assert.Single(entries, e => e.Kind == DecisionKind.AgentRunFinished).PayloadJson);
        Assert.Equal(purpose, started.Purpose);
        Assert.Equal(endClass ? AgentRunFinishedPayload.EndClassVersion : AgentRunFinishedPayload.UsageVersion, finished.V);
        Assert.Equal(endClass ? AttemptEndClass.ContextExhausted : AttemptEndClass.Other, finished.ResolvedEnd().Class);
    }

    private sealed class Folder : IDisposable
    {
        public Folder() => Path = Directory.CreateTempSubdirectory("aux-").FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static Folder EmptyFolder() => new();

    private static AgentRunRequest Launch(string folder, PermissionProfile? permissions = null) =>
        new(Analyst, "Ayuda.", folder, permissions ?? new PermissionProfile(false, false, false, [], [], []));

    private static readonly PermissionProfile ReadOnly = new(false, false, false, ["Read", "Grep", "Glob"], [], []);

    /// <summary>The help and the draft of a document are read-only in an empty folder: the runner refuses anything else before it records or launches.</summary>
    [Theory]
    [InlineData(AgentRunPurpose.Help)]
    [InlineData(AgentRunPurpose.ClientDocumentDraft)]
    [InlineData(AgentRunPurpose.GraphRelations)]
    public async Task A_launch_that_is_not_read_only_in_an_empty_folder_is_refused_before_anything_is_recorded(AgentRunPurpose purpose)
    {
        var clock = new FakeTimeProvider(T0);
        var store = new InMemoryDecisionLogStore();
        var runner = new AuxiliaryAgentRunner(new DecisionRecorder(store, clock), new FakeEvidenceStore(clock), new Providers(new ScriptedProvider(new AgentRunOutcome(AgentRunStatus.Succeeded, 0, null, null, "s", null))), clock);
        using var folder = EmptyFolder();
        using var full = EmptyFolder();
        File.WriteAllText(System.IO.Path.Combine(full.Path, "x.txt"), "x");
        var mcp = new McpServerSpec("local", "node", [], []);

        var refused = new[]
        {
            Launch(folder.Path, ReadOnly with { AllowFileWrites = true }),
            Launch(folder.Path, ReadOnly with { AllowShell = true }),
            Launch(folder.Path, ReadOnly with { AllowNetwork = true }),
            Launch(folder.Path, ReadOnly with { AllowedTools = ["Read", "Bash"] }),
            Launch(folder.Path, ReadOnly with { AllowedTools = [] }),
            Launch(folder.Path, ReadOnly with { AdditionalDirectories = [@"C:\repo"] }),
            Launch(folder.Path) with { McpServers = [mcp] },
            Launch(folder.Path) with { SkillsDirectory = @"C:\skills" },
            Launch(folder.Path) with { ResumeSessionId = "sesion-1" },
            Launch(full.Path),
            Launch(System.IO.Path.Combine(folder.Path, "no-existe")),
        };

        foreach (var launch in refused)
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(Project, new AuxiliaryRunRequest(purpose, launch), null, TestContext.Current.CancellationToken));
            Assert.Contains("read-only in an empty folder", ex.Message, StringComparison.Ordinal);
            Assert.NotEmpty(AuxiliaryAgentRunner.ReadOnlyViolations(launch));
        }

        Assert.Empty(store.Of(Project.ProjectId));
        Assert.Empty(AuxiliaryAgentRunner.ReadOnlyViolations(Launch(folder.Path, ReadOnly)));
        Assert.True(AuxiliaryAgentRunner.RequiresReadOnlyLaunch(purpose));
        Assert.False(AuxiliaryAgentRunner.RequiresReadOnlyLaunch(AgentRunPurpose.PreparationChat));
    }

    private sealed class Providers(params IAgentProvider[] providers) : IProjectAgentProviders
    {
        public IReadOnlyList<IAgentProvider> For(string projectRoot) => providers;
    }

    private sealed class ScriptedProvider(AgentRunOutcome outcome) : IAgentProvider
    {
        public ProviderId Provider => ProviderId.ClaudeCode;

        public VendorId Vendor => VendorId.Anthropic;

        public ProviderCapabilities Capabilities { get; } = new(true, true, true, false, true, true, 1);

        public Task<IAgentRun> StartAsync(AgentRunRequest request, CancellationToken cancellationToken) => Task.FromResult<IAgentRun>(new Run(outcome));
    }

    private sealed class Run(AgentRunOutcome outcome) : IAgentRun
    {
        public string RunId => "fake";

        public IAsyncEnumerable<AgentEvent> Events => None();

        public Task<AgentRunOutcome> Completion => Task.FromResult(outcome);

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<AgentEvent> None([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
