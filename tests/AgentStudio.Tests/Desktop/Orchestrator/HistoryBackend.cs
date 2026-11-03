using AgentStudio.Application.Decisions;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Decisions;
using AgentStudio.Tests.Application;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The decision log behind the orchestrator screen: the real recorder over a log in memory, checked against anchors in
/// memory, and the person behind the approval dialog, scripted. The project is the one <see cref="FakeWorkspace"/> opens.
/// </summary>
internal sealed class HistoryBackend
{
    public const string ProjectId = "p-1";

    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    public HistoryBackend(bool approve = true)
    {
        Prompt = new ScriptedApprovalPrompt { Approve = _ => approve };
        Recorder = new DecisionRecorder(Log, new FakeTimeProvider(T0.AddHours(1)), Anchors);
    }

    public InMemoryDecisionLogStore Log { get; } = new();

    public MemoryDecisionLogAnchors Anchors { get; } = new();

    public ScriptedApprovalPrompt Prompt { get; }

    public DecisionRecorder Recorder { get; }

    public IReadOnlyList<DecisionEntry> Entries => Log.Entries(ProjectId);

    /// <summary>Entries written with no anchor at all: a folder moved or copied here, or a log from another machine.</summary>
    public async Task WriteUnanchoredAsync(int count)
    {
        var elsewhere = new DecisionRecorder(Log, new FakeTimeProvider(T0));
        for (var i = 1; i <= count; i++)
        {
            await elsewhere.RecordAsync(ProjectId, System, DecisionKind.TaskAssigned, $"Entrada {i}", new TaskAssignedPayload(Tid("T" + i), Aid("ejecutor")) { RunId = "r1" }, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Entries written and anchored here, and then one of them rewritten behind the recorder's back.</summary>
    public async Task WriteTamperedAsync()
    {
        for (var i = 1; i <= 2; i++)
        {
            await Recorder.RecordAsync(ProjectId, System, DecisionKind.TaskAssigned, $"Entrada {i}", new TaskAssignedPayload(Tid("T" + i), Aid("ejecutor")) { RunId = "r1" }, cancellationToken: TestContext.Current.CancellationToken);
        }

        Log.Tamper(ProjectId, 0, e => e with { Summary = "reescrita" });
    }

    /// <summary><paramref name="backend"/> with this log: the recorder reads it, accepts a history and the prompt asks the person.</summary>
    public OrchestrationBackend On(OrchestrationBackend backend) => backend with { Decisions = Recorder, Origins = Recorder, Prompt = Prompt };
}
