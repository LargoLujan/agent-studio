using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Decisions;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Store;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Store;

/// <summary>
/// Recording and recalling memory load the decision log every time. They used to read it and verify it whole, and build
/// the ledger of the memory from every entry: recording was quadratic (2 000 entries in 144 s). Now the recorder reads only
/// what was appended and the memory extends the ledger it read last with it. The ledger grown that way must be the one a
/// memory reading the log for the first time builds, at every step of a history with every kind of fact.
/// </summary>
public sealed class IncrementalMemoryLedgerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The real store of the log, counting whole reads and reads of the tail.</summary>
    private sealed class CountingLogStore(SqliteDecisionLogStore inner) : IDecisionLogStore, IDecisionLogTailReader
    {
        public int Whole { get; private set; }

        public int Tails { get; private set; }

        public Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken) => inner.AppendAsync(projectId, entry, cancellationToken);

        public Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken)
        {
            Whole++;
            return inner.ReadAllAsync(projectId, cancellationToken);
        }

        public Task<DecisionLogTail> ReadTailAsync(string projectId, DecisionLogMark? after, CancellationToken cancellationToken)
        {
            if (after is null)
            {
                Whole++;
            }
            else
            {
                Tails++;
            }

            return inner.ReadTailAsync(projectId, after, cancellationToken);
        }
    }

    private static MemoryDraft Draft(int i, MemoryOrigin origin = MemoryOrigin.System, MemoryKind kind = MemoryKind.Decision, string? supersedes = null) =>
        new(kind, $"Tema {i}", $"Contenido {i}: el plan con sus pruebas.", origin, $"registro:r#{i}", supersedes);

    [Fact]
    public async Task Recording_and_recalling_memory_read_the_log_whole_once()
    {
        using var world = await MemoryWorld.CreateAsync();
        var store = new CountingLogStore(world.Instance.DecisionLog);
        var recorder = new DecisionRecorder(store, world.Clock, new FileDecisionLogAnchorStore(new DecisionLogAnchorOptions(world.AnchorFolder), world.Instance.Databases));
        var memory = new ProjectMemory(recorder, world.Spy, world.Activity, world.Redactor, world.Clock);

        for (var i = 1; i <= 80; i++)
        {
            await memory.RecordAsync(world.Project, Draft(i), Ct);
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.NotEmpty(await memory.RecallAsync(world.Project, new MemoryQuery("plan pruebas"), Ct));
        }

        Assert.Equal(1, store.Whole);
        Assert.InRange(store.Tails, 89, 2 * 90);
        Assert.Equal(80, (await memory.RecallAsync(world.Project, new MemoryQuery(MaxEntries: 1000), Ct)).Count);
    }

    [Fact]
    public async Task The_ledger_grown_with_the_log_is_the_one_read_whole_at_every_step()
    {
        using var world = await MemoryWorld.CreateAsync();
        var memory = world.Memory;
        var project = world.Project;

        // At each step, a memory of its own reads the log for the first time: it must see exactly what the grown one sees.
        async Task SameAsReadWholeAsync(string step)
        {
            var fresh = new ProjectMemory(world.Instance.Recorder, world.Spy, world.Activity, world.Redactor, world.Clock);
            var query = new MemoryQuery(MaxEntries: 1000, TrustedOnly: false);
            var grown = (await memory.RecallAsync(project, query, Ct)).Select(e => (e.Id, e.Trust, e.SupersededBy)).ToList();
            var whole = (await fresh.RecallAsync(project, query, Ct)).Select(e => (e.Id, e.Trust, e.SupersededBy)).ToList();
            Assert.True(grown.SequenceEqual(whole), $"{step}: [{string.Join(", ", grown)}] against [{string.Join(", ", whole)}]");
        }

        var decision = await memory.RecordAsync(project, Draft(1), Ct);
        await SameAsReadWholeAsync("a system entry");
        var proposal = await memory.RecordAsync(project, Draft(2, MemoryOrigin.Agent), Ct);
        await SameAsReadWholeAsync("a proposal");
        await memory.ReviewAsync(project, proposal.Id, MemoryReview.Accepted, "manuel", null, Ct);
        await SameAsReadWholeAsync("the proposal accepted");
        await memory.RecordAsync(project, Draft(3, supersedes: decision.Id), Ct);
        await SameAsReadWholeAsync("an entry that supersedes another");
        var other = await memory.RecordAsync(project, Draft(4, MemoryOrigin.Agent), Ct);
        await memory.ReviewAsync(project, other.Id, MemoryReview.Rejected, "manuel", null, Ct);
        await SameAsReadWholeAsync("a proposal rejected");
        var journal = await memory.RecordAsync(project, new MemoryDraft(MemoryKind.JournalEntry, "Bitácora del 2026-03-10 · 10:00:00", "- **10:00:00** · sesión `x` · #prompt · «uno»", MemoryOrigin.Agent, MemorySources.Journal(new DateOnly(2026, 3, 10), new string('a', 64))), Ct);
        await SameAsReadWholeAsync("a journal entry in quarantine");
        await world.Instance.Recorder.RecordAsync(project.ProjectId, new Actor(ActorKind.User, "manuel"), DecisionKind.JournalTrustChanged, "Confianza en la bitácora del proyecto", JournalTrustChangedPayload.Deciding(JournalTrustScope.Project, null, true, "manuel", null), cancellationToken: Ct);
        await SameAsReadWholeAsync("the journal of the project trusted");
        Assert.Equal(MemoryTrust.Trusted, Assert.Single(await memory.RecallAsync(project, new MemoryQuery(Kinds: [MemoryKind.JournalEntry]), Ct)).Trust);

        // The history accepted as a new origin: everything before it is inherited, and the journal's trust with it.
        foreach (var anchor in Directory.GetFiles(world.AnchorFolder))
        {
            File.Delete(anchor);
        }

        var unanchored = (await world.Instance.Recorder.LoadAsync(project.ProjectId, Ct)).Unanchored!;
        var binding = unanchored.Binding(project.ProjectId);
        await world.Instance.Recorder.AcceptAsNewOriginAsync(
            project.ProjectId,
            new ApprovalRequest(UnanchoredHistory.AcceptAction, "Aceptar este historial como nuevo origen.", RiskLevel.Critical, null, [world.Root], project.ProjectId, Binding: binding),
            new ApprovalDecision(true, "manuel", T0, null, binding, ApprovalVerification.WindowsHello),
            Ct);
        await SameAsReadWholeAsync("the history accepted as a new origin");
        var inherited = await memory.RecallAsync(project, new MemoryQuery(MaxEntries: 1000, TrustedOnly: false), Ct);
        Assert.Equal(MemoryTrust.Rejected, Assert.Single(inherited, e => e.Id == other.Id).Trust); // an inherited review may reject, never accept
        Assert.All(inherited.Where(e => e.Id != other.Id), e => Assert.Equal(MemoryTrust.Quarantined, e.Trust));
        Assert.Equal(journal.Id, Assert.Single(await memory.RecallAsync(project, new MemoryQuery(Kinds: [MemoryKind.JournalEntry], TrustedOnly: false), Ct)).Id);

        await memory.RecordAsync(project, Draft(5), Ct);
        await SameAsReadWholeAsync("an entry after the acceptance");
    }
}
