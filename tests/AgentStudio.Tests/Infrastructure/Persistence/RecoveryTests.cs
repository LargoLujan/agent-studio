using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.Orchestration.PlanFixtures;
using static AgentStudio.Tests.Domain.TestData;
using static AgentStudio.Tests.Infrastructure.Persistence.PersistenceTestData;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// Recovery after a restart (docs/13 section 3.7). Everything the judge's dossier and the replay of a run are built
/// from is written through the stores, the app "dies" in the middle of the review, and a fresh instance over the same
/// folder must find all of it: if any piece had lived only in memory, this test fails.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private const string Run = "20261015-a1b2c3";
    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>T1 writes the code, T2 integrates it and T3 has it reviewed by the judge.</summary>
    private static PlanDefinition SmallPlan() => PlanDefinition.Create(
        "Añadir Calc.Multiply con sus tests",
        [
            Spec("T1") with
            {
                Agent = Aid("ejecutor"),
                Workspace = WorkspaceMode.Write,
                WriteScope = ["src/Calc/"],
                Covers = [Rid("R1")],
                VerificationSteps = ["build", "test"],
            },
            Spec("T2", WorkTaskKind.Integration) with { DependsOn = [Dep("T1")] },
            Spec("T3", WorkTaskKind.Review) with { Agent = Aid("juez"), Workspace = WorkspaceMode.ReadOnly, DependsOn = [Dep("T2")], MaxRetries = 0 },
        ],
        ["El build puede tardar"],
        PlanSource.OrchestratorAgent,
        Aid("orquestador"));

    private sealed record Written(
        RunRecord Header,
        string HeadHash,
        int Entries,
        string LiveSignature,
        IReadOnlyList<(EvidenceId Id, byte[] Content)> Evidence,
        IReadOnlyList<StoredVerdict> Verdicts);

    /// <summary>The first instance: plans, starts and runs the plan until the judge has answered, and then dies.</summary>
    private async Task<Written> RunUntilTheAppDiesAsync()
    {
        var clock = new FakeTimeProvider(T0);
        using var app = await ProjectInstance.OpenAsync(_folder.Root, clock);
        var project = app.ProjectId;
        var evidence = new List<(EvidenceId, byte[])>();

        async Task<EvidenceId> CaptureAsync(string text, EvidenceKind kind, EvidenceOrigin origin, string? task, TestRunSummary? tests = null)
        {
            var content = Bytes(text);
            using var stream = new MemoryStream(content);
            var record = await app.Evidence.CaptureAsync(project, new EvidenceDraft(kind, origin, text[..Math.Min(20, text.Length)], "agent-studio", tests, null, Run, task, task is null ? null : 1, "text/plain"), stream, Ct);
            evidence.Add((record.Id, content));
            return record.Id;
        }

        var requirements = PlanFixtures.Requirements();
        var plan = SmallPlan();
        await app.Requirements.SaveAsync(project, requirements, Ct);
        await app.Plans.SaveAsync(project, plan, Ct);
        foreach (var agent in FullRoster())
        {
            await app.Roster.SaveAsync(project, agent, Ct);
        }

        var request = await CaptureAsync("Añade Calc.Multiply(a, b) y sus tests, por favor.", EvidenceKind.Document, EvidenceOrigin.ProvidedByHuman, null);
        var header = new RunRecord(
            new RunId(Run), project, clock.GetUtcNow(), plan.ContentHash, requirements.ContentHash, request,
            "0123456789abcdef0123456789abcdef01234567", "main", Profile, FullRoster(), new RunOptions(), null);
        await app.Runs.CreateAsync(header, Ct);
        await app.Recorder.RecordAsync(
            project,
            System,
            DecisionKind.RunStarted,
            "Inicio de la ejecución",
            new RunStartedPayload(plan.ContentHash, requirements.ContentHash, header.BaseCommit, header.BaseRef, Profile.ContentHash, [], null) { RunId = Run },
            cancellationToken: Ct);

        // The engine mutates the graph through the domain API and records each transition; the replay reads them back.
        var graph = plan.BuildGraph(header.CreatedAt);
        async Task MoveAsync(string task, WorkTaskStatus to, params EvidenceId[] proof)
        {
            clock.Advance(TimeSpan.FromSeconds(1.5));
            var item = graph[Tid(task)];
            var from = item.Status;
            var reason = $"{task}: {from} → {to}";
            if (to == WorkTaskStatus.Completed)
            {
                item.Complete(proof, clock.GetUtcNow(), reason);
            }
            else
            {
                item.TransitionTo(to, clock.GetUtcNow(), reason);
            }

            await app.Recorder.RecordAsync(
                project, System, DecisionKind.TaskStatusChanged, reason, new TaskStatusChangedPayload(Tid(task), from, to, reason, null, proof, 1) { RunId = Run }, cancellationToken: Ct);
        }

        await MoveAsync("T1", WorkTaskStatus.Running);
        var diff = await CaptureAsync("diff --git a/src/Calc/Calc.cs b/src/Calc/Calc.cs\n+Multiply", EvidenceKind.CodeDiff, EvidenceOrigin.CapturedBySystem, "T1");
        var tests = await CaptureAsync("<TestRun passed=\"11\" />", EvidenceKind.TestRun, EvidenceOrigin.CapturedBySystem, "T1", Green);
        await MoveAsync("T1", WorkTaskStatus.Completed, diff, tests);
        await MoveAsync("T2", WorkTaskStatus.Running);
        var build = await CaptureAsync("Compilación correcta. 0 Advertencia(s)", EvidenceKind.BuildLog, EvidenceOrigin.CapturedBySystem, "T2");
        await MoveAsync("T2", WorkTaskStatus.Completed, build);
        await MoveAsync("T3", WorkTaskStatus.Running);

        // The judge answered once, invalidly, and once more after the correction round.
        var verdict = ValidVerdict(Dossier());
        var invalid = new StoredVerdict("v1", verdict with { Confidence = null! }, Run, "T3", AuditScope.CurrentPlan, false, [], null);
        var corrected = new StoredVerdict("v2", verdict, Run, "T3", AuditScope.CurrentPlan, true, [], "v1");
        await app.Verdicts.AppendAsync(project, invalid, Ct);
        await app.Verdicts.AppendAsync(project, corrected, Ct);

        var state = await app.Recorder.LoadAsync(project, Ct);
        return new Written(header, state.HeadHash, state.Entries.Count, LiveRun.Signature(graph), evidence, [invalid, corrected]);

        // Disposing the instance here is the crash: no RunFinished, no cleanup, T3 still Running.
    }

    private static List<RunEvent> RunEvents(IReadOnlyList<DecisionEntry> entries) =>
    [
        .. entries
            .Where(e => e.Kind == DecisionKind.TaskStatusChanged)
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize<TaskStatusChangedPayload>(e.PayloadJson)))
            .Where(x => x.Payload.RunId == Run)
            .Select(x => new TaskTransitioned(x.Entry.RecordedAt, x.Payload.Task, x.Payload.From, x.Payload.To, x.Payload.Reason, x.Payload.Evidence)),
    ];

    [Fact]
    public async Task Everything_the_dossier_and_the_replay_need_is_found_by_a_new_instance_after_a_crash()
    {
        var written = await RunUntilTheAppDiesAsync();

        using var restarted = await ProjectInstance.OpenAsync(_folder.Root, new FakeTimeProvider(T0.AddHours(1)));
        var project = restarted.ProjectId;

        // The log verifies and its head is the one the judge would have quoted.
        var state = await restarted.Recorder.LoadAsync(project, Ct);
        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        Assert.Equal(written.HeadHash, state.HeadHash);
        Assert.Equal(written.Entries, state.Entries.Count);

        // The run header and what it points at.
        var header = await restarted.Runs.FindAsync(project, new RunId(Run), Ct);
        Assert.NotNull(header);
        Assert.Equal(Shape(written.Header), Shape(header));
        var plan = await restarted.Plans.FindAsync(project, header.PlanHash, Ct);
        Assert.Equal(header.PlanHash, plan?.ContentHash);
        Assert.Equal(header.RequirementSetHash, (await restarted.Requirements.FindAsync(project, header.RequirementSetHash, Ct))?.ContentHash);
        Assert.Equal(Shape(FullRoster()), Shape(await restarted.Roster.ListAsync(project, Ct)));

        // The literal request and every piece of evidence of the run, with its content checked against its hash.
        Assert.Equal(written.Evidence.Select(e => e.Id), (await restarted.Evidence.ListByRunAsync(project, Run, Ct)).Select(e => e.Id));
        Assert.Equal(header.OriginalRequest, written.Evidence[0].Id);
        foreach (var (id, content) in written.Evidence)
        {
            await using var stream = await restarted.Evidence.OpenContentAsync(project, id, Ct);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            Assert.Equal(content, copy.ToArray());
        }

        // Both verdicts, the invalid one included, in the order they were issued.
        Assert.Equal(Shape(written.Verdicts), Shape(await restarted.Verdicts.ListAsync(project, Run, Ct)));

        // The state of the run is rebuilt from the log alone and is the one the engine had when the app died.
        var replay = RunStateReplayer.Replay(plan!, header.CreatedAt, RunEvents(state.Entries), header.PlanHash);
        Assert.True(replay.IsResumable, string.Join("; ", replay.Problems));
        Assert.Equal(written.LiveSignature, LiveRun.Signature(replay.Graph));
        Assert.Equal(WorkTaskStatus.Running, replay.Graph[Tid("T3")].Status);
    }

    [Fact]
    public async Task After_the_crash_the_new_instance_records_the_interruption_on_the_same_chain()
    {
        var written = await RunUntilTheAppDiesAsync();

        using var restarted = await ProjectInstance.OpenAsync(_folder.Root, new FakeTimeProvider(T0.AddHours(1)));
        var project = restarted.ProjectId;
        var interrupted = await restarted.Recorder.RecordAsync(
            project,
            System,
            DecisionKind.TaskStatusChanged,
            "T3 interrumpida por el reinicio",
            new TaskStatusChangedPayload(Tid("T3"), WorkTaskStatus.Running, WorkTaskStatus.Blocked, "app restarted", "Interrupted", [], 1) { RunId = Run },
            cancellationToken: Ct);

        Assert.Equal(written.Entries + 1, interrupted.Sequence);
        Assert.Equal(written.HeadHash, interrupted.PreviousHash);
        var state = await restarted.Recorder.LoadAsync(project, Ct);
        Assert.True(state.Verification.IsValid, state.Verification.Problem);
        var header = await restarted.Runs.FindAsync(project, new RunId(Run), Ct);
        var plan = await restarted.Plans.FindAsync(project, header!.PlanHash, Ct);
        var replay = RunStateReplayer.Replay(plan!, header.CreatedAt, RunEvents(state.Entries), header.PlanHash);
        Assert.True(replay.IsResumable, string.Join("; ", replay.Problems));
        Assert.Equal(WorkTaskStatus.Blocked, replay.Graph[Tid("T3")].Status);
        Assert.Equal(WorkTaskStatus.Completed, replay.Graph[Tid("T1")].Status);
        Assert.Equal(0, replay.Graph[Tid("T3")].RetryCount);
    }
}
