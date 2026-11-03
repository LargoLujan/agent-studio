using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Work;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

public sealed class DecisionRecorderTests
{
    private const string Project = "p1";
    private static readonly Actor System = new(ActorKind.System, "agent-studio");

    /// <summary>The append-only table in memory: it refuses a sequence that is already there, like the primary key does.</summary>
    private sealed class InMemoryDecisionLogStore : IDecisionLogStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, List<DecisionEntry>> _entries = [];

        public int Appends { get; private set; }

        public int Reads { get; private set; }

        /// <summary>The next appends fail before anything is stored.</summary>
        public int FailBeforeStoring { get; set; }

        /// <summary>The next appends are stored and then fail, as when a timeout hides a commit.</summary>
        public int FailAfterStoring { get; set; }

        public int FailReads { get; set; }

        /// <summary>Awaited at the start of every append, to hold a writer in the middle of its write.</summary>
        public Func<string, Task>? BeforeAppend { get; set; }

        public IReadOnlyList<DecisionEntry> Of(string project)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(project, out var list) ? [.. list] : [];
            }
        }

        public void Seed(string project, IEnumerable<DecisionEntry> entries)
        {
            lock (_gate)
            {
                _entries[project] = [.. entries];
            }
        }

        public void Replace(string project, int index, DecisionEntry entry)
        {
            lock (_gate)
            {
                _entries[project][index] = entry;
            }
        }

        public async Task AppendAsync(string projectId, DecisionEntry entry, CancellationToken cancellationToken)
        {
            if (BeforeAppend is { } hook)
            {
                await hook(projectId);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Appends++;
                if (FailBeforeStoring > 0)
                {
                    FailBeforeStoring--;
                    throw new IOException("disk full");
                }

                var list = _entries.TryGetValue(projectId, out var existing) ? existing : _entries[projectId] = [];
                if (list.Any(e => e.Sequence == entry.Sequence))
                {
                    throw new InvalidOperationException($"UNIQUE constraint failed: decision_log.sequence {entry.Sequence}");
                }

                list.Add(entry);
                if (FailAfterStoring > 0)
                {
                    FailAfterStoring--;
                    throw new IOException("the commit succeeded but the connection dropped");
                }
            }
        }

        public Task<IReadOnlyList<DecisionEntry>> ReadAllAsync(string projectId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Reads++;
                if (FailReads > 0)
                {
                    FailReads--;
                    throw new IOException("cannot read");
                }

                return Task.FromResult<IReadOnlyList<DecisionEntry>>(_entries.TryGetValue(projectId, out var list) ? [.. list.OrderBy(e => e.Sequence)] : []);
            }
        }
    }

    private static TaskAssignedPayload Payload(string task = "T1") => new(Tid(task), Aid("ejecutor")) { RunId = "r1" };

    private static (DecisionRecorder Sut, InMemoryDecisionLogStore Store, FakeTimeProvider Clock) Build()
    {
        var store = new InMemoryDecisionLogStore();
        var clock = new FakeTimeProvider(T0);
        return (new DecisionRecorder(store, clock), store, clock);
    }

    private static Task<DecisionEntry> Record(DecisionRecorder sut, string project = Project, string summary = "assigned", DecisionPayload? payload = null, CancellationToken? ct = null) =>
        sut.RecordAsync(project, System, DecisionKind.TaskAssigned, summary, payload ?? Payload(), cancellationToken: ct ?? TestContext.Current.CancellationToken);

    // ---- recording ----

    [Fact]
    public async Task Recording_numbers_the_entries_from_one_and_chains_their_hashes()
    {
        var (sut, store, clock) = Build();

        var first = await Record(sut);
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = await Record(sut, summary: "again");
        var third = await Record(sut);

        Assert.Equal([1L, 2L, 3L], store.Of(Project).Select(e => e.Sequence));
        Assert.Equal(DecisionLog.GenesisHash, first.PreviousHash);
        Assert.Equal(first.Hash, second.PreviousHash);
        Assert.Equal(second.Hash, third.PreviousHash);
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    [Fact]
    public async Task The_entry_carries_the_kind_actor_summary_and_payload_in_the_fixed_format()
    {
        var (sut, store, _) = Build();
        var payload = Payload();

        var entry = await sut.RecordAsync(Project, System, DecisionKind.TaskAssigned, "Tarea asignada", payload, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(entry, store.Of(Project).Single());
        Assert.Equal(DecisionKind.TaskAssigned, entry.Kind);
        Assert.Equal(System, entry.Actor);
        Assert.Equal("Tarea asignada", entry.Summary);
        Assert.Equal(DecisionPayloadSerializer.Serialize(payload), entry.PayloadJson);
        Assert.Equal(T0, entry.RecordedAt);
        Assert.Null(entry.Corrects);
    }

    [Fact]
    public async Task A_correction_references_an_earlier_entry_and_a_wrong_one_is_refused_without_harm()
    {
        var (sut, store, _) = Build();
        await Record(sut);

        await Assert.ThrowsAsync<DomainRuleViolationException>(() =>
            sut.RecordAsync(Project, System, DecisionKind.Correction, "fix", Payload(), corrects: null, cancellationToken: TestContext.Current.CancellationToken));
        var correction = await sut.RecordAsync(Project, System, DecisionKind.Correction, "fix", Payload(), corrects: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, correction.Corrects);
        Assert.Equal([1L, 2L], store.Of(Project).Select(e => e.Sequence));
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_anything_is_written()
    {
        var (sut, store, _) = Build();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.RecordAsync(" ", System, DecisionKind.TaskAssigned, "s", Payload(), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RecordAsync(Project, null!, DecisionKind.TaskAssigned, "s", Payload(), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.RecordAsync(Project, System, DecisionKind.TaskAssigned, " ", Payload(), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RecordAsync(Project, System, DecisionKind.TaskAssigned, "s", null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.LoadAsync(string.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(0, store.Appends);
    }

    [Fact]
    public async Task A_payload_that_cannot_be_serialized_writes_nothing_and_does_not_break_the_next_record()
    {
        var (sut, store, _) = Build();
        await Record(sut);

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => Record(sut, payload: new TaskAssignedPayload(default, Aid("a")) { RunId = "r1" }));
        var next = await Record(sut);

        Assert.Equal(2, next.Sequence);
        Assert.Equal(2, store.Of(Project).Count);
    }

    // ---- one writer per project ----

    [Fact(Timeout = 60_000)]
    public async Task Concurrent_writers_of_one_project_get_contiguous_sequences_and_a_valid_chain()
    {
        var (sut, store, _) = Build();
        store.BeforeAppend = async _ => await Task.Yield();
        var next = 0;
        using var gate = new ManualResetEventSlim(false);
        var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                gate.Wait(TestContext.Current.CancellationToken);
                while (Interlocked.Increment(ref next) <= 200)
                {
                    await Record(sut, ct: TestContext.Current.CancellationToken);
                }
            },
            TestContext.Current.CancellationToken)).ToArray();

        gate.Set();
        await Task.WhenAll(writers);

        var entries = store.Of(Project);
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), entries.Select(e => e.Sequence));
        Assert.True(DecisionLog.Load(entries).Verify().IsValid);
        Assert.Equal(200, store.Appends);
    }

    [Fact(Timeout = 60_000)]
    public async Task Different_projects_do_not_wait_for_each_other()
    {
        var (sut, store, _) = Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeAppend = async project =>
        {
            if (project == "slow")
            {
                entered.TrySetResult();
                await release.Task;
            }
        };

        var slow = Record(sut, "slow");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var fast = await Record(sut, "fast").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, fast.Sequence);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal(1, (await slow).Sequence);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_caller_waiting_for_the_writer_can_cancel_without_writing()
    {
        var (sut, store, _) = Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeAppend = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = Record(sut);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var source = new CancellationTokenSource();
        var waiting = Record(sut, ct: source.Token);

        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        store.BeforeAppend = null;
        release.SetResult();
        await first;

        Assert.Equal([1L], store.Of(Project).Select(e => e.Sequence));
        Assert.Equal(2, (await Record(sut)).Sequence);
    }

    // ---- store failures ----

    [Fact]
    public async Task After_a_failed_write_the_next_entry_takes_the_sequence_the_failed_one_would_have_had()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        store.FailBeforeStoring = 1;

        await Assert.ThrowsAsync<IOException>(() => Record(sut));
        var next = await Record(sut);

        // The domain object had already numbered the failed entry 2; resynchronising from the store must give 2 again, not 3.
        Assert.Equal(2, next.Sequence);
        Assert.Equal([1L, 2L], store.Of(Project).Select(e => e.Sequence));
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    [Fact]
    public async Task A_write_that_reached_the_store_before_the_error_is_not_written_twice()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        store.FailAfterStoring = 1;

        await Assert.ThrowsAsync<IOException>(() => Record(sut, summary: "stored but reported as failed"));
        var next = await Record(sut, summary: "after");

        Assert.Equal([1L, 2L, 3L], store.Of(Project).Select(e => e.Sequence));
        Assert.Equal("stored but reported as failed", store.Of(Project)[1].Summary);
        Assert.Equal(store.Of(Project)[1].Hash, next.PreviousHash);
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    [Fact]
    public async Task If_resynchronising_fails_too_the_next_call_tries_again()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        store.FailBeforeStoring = 1;
        await Assert.ThrowsAsync<IOException>(() => Record(sut));
        store.FailReads = 1;

        await Assert.ThrowsAsync<IOException>(() => Record(sut));
        var next = await Record(sut);

        Assert.Equal(2, next.Sequence);
    }

    [Fact]
    public async Task A_sequence_taken_by_another_writer_is_refused_and_the_recorder_recovers()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        var outsider = DecisionLog.Load(store.Of(Project));
        store.Seed(Project, [.. store.Of(Project), outsider.Append(T0.AddMinutes(1), System, DecisionKind.TaskAssigned, "someone else", "{}")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Record(sut)); // thinks it is entry 2; the store has 2
        var next = await Record(sut);

        Assert.Equal(3, next.Sequence);
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    // ---- the clock ----

    [Fact]
    public async Task A_clock_that_goes_back_never_breaks_the_chronology()
    {
        var (sut, store, clock) = Build();
        clock.SetUtcNow(T0.AddHours(5));
        var first = await Record(sut);

        clock.AdjustTime(T0.AddHours(1)); // NTP corrected the clock backwards (SetUtcNow can only advance)
        var second = await Record(sut);
        clock.AdjustTime(T0.AddHours(6));
        var third = await Record(sut);

        Assert.Equal(T0.AddHours(5), first.RecordedAt);
        Assert.Equal(T0.AddHours(5), second.RecordedAt);
        Assert.Equal(T0.AddHours(6), third.RecordedAt);
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    [Fact]
    public async Task A_new_recorder_continues_a_stored_log_even_if_its_clock_is_behind_the_last_entry()
    {
        var store = new InMemoryDecisionLogStore();
        var earlier = new DecisionRecorder(store, new FakeTimeProvider(T0.AddHours(3)));
        await Record(earlier);
        var restarted = new DecisionRecorder(store, new FakeTimeProvider(T0));

        var entry = await Record(restarted);

        Assert.Equal(2, entry.Sequence);
        Assert.Equal(T0.AddHours(3), entry.RecordedAt);
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    // ---- loading and integrity ----

    [Fact]
    public async Task Loading_an_empty_log_gives_the_genesis_head_and_a_valid_chain()
    {
        var (sut, _, _) = Build();

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.Empty(state.Entries);
        Assert.Equal(DecisionLog.GenesisHash, state.HeadHash);
        Assert.True(state.Verification.IsValid);
    }

    [Fact]
    public async Task Loading_returns_the_entries_the_head_and_the_verification_of_what_is_stored()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        var last = await Record(sut);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.Equal(store.Of(Project), state.Entries);
        Assert.Equal(last.Hash, state.HeadHash);
        Assert.True(state.Verification.IsValid);
    }

    [Fact]
    public async Task Loading_sees_entries_written_behind_its_back_and_writing_continues_after_them()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        var outsider = DecisionLog.Load(store.Of(Project));
        store.Seed(Project, [.. store.Of(Project), outsider.Append(T0.AddMinutes(1), System, DecisionKind.TaskAssigned, "someone else", "{}")]);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);
        var next = await Record(sut);

        Assert.Equal(2, state.Entries.Count);
        Assert.Equal(3, next.Sequence);
        Assert.True(DecisionLog.Load(store.Of(Project)).Verify().IsValid);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_is_reported_by_loading_and_blocks_writing()
    {
        var (sut, store, _) = Build();
        await Record(sut);
        await Record(sut);
        var tampered = store.Of(Project)[0] with { Summary = "forged" };
        store.Replace(Project, 0, tampered);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);
        var refusal = await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));

        Assert.False(state.Verification.IsValid);
        Assert.Equal(1, state.Verification.FirstInvalidSequence);
        Assert.Equal(1, refusal.Verification.FirstInvalidSequence);
        Assert.Equal(2, store.Of(Project).Count);
    }

    [Fact]
    public async Task A_recorder_that_has_never_seen_the_log_also_refuses_to_extend_a_broken_one()
    {
        var store = new InMemoryDecisionLogStore();
        var writer = new DecisionRecorder(store, new FakeTimeProvider(T0));
        await Record(writer);
        await Record(writer);
        store.Replace(Project, 1, store.Of(Project)[1] with { PayloadJson = "{\"forged\":true}" });
        var fresh = new DecisionRecorder(store, new FakeTimeProvider(T0));

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(fresh));
    }

    [Fact]
    public async Task Writing_works_again_once_the_stored_log_is_repaired()
    {
        var (sut, store, _) = Build();
        var original = await Record(sut);
        await Record(sut);
        store.Replace(Project, 0, original with { Summary = "forged" });
        var opened = await sut.LoadAsync(Project, TestContext.Current.CancellationToken); // what opening the project does
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));

        store.Replace(Project, 0, original);
        var next = await Record(sut);

        Assert.False(opened.Verification.IsValid);
        Assert.Equal(3, next.Sequence);
    }

    // ---- the head anchored outside the project (ADR-0010, addendum) ----

    /// <summary>
    /// The anchor file outside the project, in memory: it can fail like a disk does, be found damaged, or belong to another
    /// project (<see cref="Foreign"/>: the location's anchor names another project id) until this project's is written.
    /// </summary>
    private sealed class InMemoryAnchorStore : IDecisionLogAnchorStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, DecisionLogAnchor> _anchors = [];
        private readonly List<DecisionLogAnchor> _written = [];
        private readonly List<DecisionLogAnchor> _confirmed = [];

        /// <summary>Writes that fail (an anchor folder that cannot be written), before anything is replaced.</summary>
        public Func<DecisionLogAnchor, bool> FailWrite { get; set; } = _ => false;

        /// <summary>Confirmations fail, as when the anchor read cannot be moved to the project's location.</summary>
        public bool FailConfirm { get; set; }

        /// <summary>The anchors the recorder confirmed, each once the log verified against it.</summary>
        public IReadOnlyList<DecisionLogAnchor> Confirmed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _confirmed];
                }
            }
        }

        public bool Damaged { get; set; }

        /// <summary>The project id and entry count of another project's anchor found at a project's location.</summary>
        public Dictionary<string, (string Project, long Entries)> Foreign { get; } = [];

        public IReadOnlyList<DecisionLogAnchor> Written
        {
            get
            {
                lock (_gate)
                {
                    return [.. _written];
                }
            }
        }

        public DecisionLogAnchor? Of(string project)
        {
            lock (_gate)
            {
                return _anchors.GetValueOrDefault(project);
            }
        }

        public Task<DecisionLogAnchor?> ReadAsync(string projectId, CancellationToken cancellationToken)
        {
            if (Damaged)
            {
                throw new InvalidDataException("The anchor file is not JSON.");
            }

            lock (_gate)
            {
                if (Foreign.TryGetValue(projectId, out var other))
                {
                    throw new DecisionLogAnchorMismatchException($"The anchor of this location belongs to project '{other.Project}'.", other.Project, other.Entries);
                }
            }

            return Task.FromResult(Of(projectId));
        }

        public Task WriteAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (FailWrite(anchor))
                {
                    throw new IOException("the anchor folder cannot be written");
                }

                Foreign.Remove(projectId);
                _anchors[projectId] = anchor;
                _written.Add(anchor);
            }

            return Task.CompletedTask;
        }

        public Task ConfirmAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken)
        {
            if (FailConfirm)
            {
                throw new IOException("the anchor cannot be moved to the project's location");
            }

            lock (_gate)
            {
                _confirmed.Add(anchor);
            }

            return Task.CompletedTask;
        }
    }

    private static (DecisionRecorder Sut, InMemoryDecisionLogStore Store, InMemoryAnchorStore Anchors) BuildAnchored()
    {
        var store = new InMemoryDecisionLogStore();
        var anchors = new InMemoryAnchorStore();
        return (new DecisionRecorder(store, new FakeTimeProvider(T0), anchors), store, anchors);
    }

    /// <summary>The same log, with entries the recorder never wrote, consistently hashed: what someone rewriting the store does.</summary>
    private static IReadOnlyList<DecisionEntry> Forged(IReadOnlyList<DecisionEntry> keep, params string[] summaries)
    {
        var log = DecisionLog.Load(keep);
        foreach (var summary in summaries)
        {
            log.Append(T0.AddHours(1), System, DecisionKind.TaskAssigned, summary, DecisionPayloadSerializer.Serialize(Payload()));
        }

        return log.Entries;
    }

    [Fact]
    public async Task Each_entry_is_announced_to_the_anchor_before_it_is_stored_and_anchored_once_it_is()
    {
        var (sut, store, anchors) = BuildAnchored();
        var seenWhenStoring = new List<DecisionLogAnchor?>();
        store.BeforeAppend = project =>
        {
            seenWhenStoring.Add(anchors.Of(project));
            return Task.CompletedTask;
        };

        var first = await Record(sut);
        var second = await Record(sut);

        Assert.Equal([new DecisionLogAnchor(0, DecisionLog.GenesisHash, first.Hash), new DecisionLogAnchor(1, first.Hash, second.Hash)], seenWhenStoring);
        Assert.Equal(new DecisionLogAnchor(2, second.Hash), anchors.Of(Project));
    }

    [Fact]
    public async Task A_store_cut_short_behind_the_recorders_back_is_untrusted_and_never_extended()
    {
        var (sut, store, _) = BuildAnchored();
        await Record(sut);
        await Record(sut);
        await Record(sut);
        store.Seed(Project, store.Of(Project).Take(2));

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);
        var refusal = await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid); // the chain alone cannot tell
        Assert.False(state.Verification.IsValid);
        Assert.Equal(3, state.Verification.FirstInvalidSequence);
        Assert.Contains("removed", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Equal(3, refusal.Verification.FirstInvalidSequence);
        Assert.Equal(2, store.Of(Project).Count);
    }

    [Fact]
    public async Task A_store_rewritten_with_recomputed_hashes_is_untrusted_even_by_a_recorder_that_never_saw_it()
    {
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        await Record(sut);
        store.Seed(Project, Forged(store.Of(Project).Take(1).ToList(), "forged"));
        var fresh = new DecisionRecorder(store, new FakeTimeProvider(T0), anchors);

        var state = await fresh.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(2, state.Verification.FirstInvalidSequence);
        Assert.Contains("rewritten", state.Verification.Problem, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(fresh));
    }

    [Fact]
    public async Task Entries_added_to_the_store_behind_the_recorders_back_are_untrusted()
    {
        var (sut, store, _) = BuildAnchored();
        await Record(sut);
        store.Seed(Project, Forged(store.Of(Project), "EvidenceRegistered de una fila insertada a mano"));

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(2, state.Verification.FirstInvalidSequence);
        Assert.Contains("added", state.Verification.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_stored_just_before_its_anchor_was_updated_is_still_trusted_after_a_restart()
    {
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        anchors.FailWrite = a => a is { EntryCount: 2, NextHash: null }; // the process dies right after storing entry 2

        var second = await Record(sut);
        var afterTheCrash = anchors.Of(Project);
        anchors.FailWrite = _ => false;
        var restarted = new DecisionRecorder(store, new FakeTimeProvider(T0), anchors);
        var state = await restarted.LoadAsync(Project, TestContext.Current.CancellationToken);
        var third = await Record(restarted);

        Assert.Equal(new DecisionLogAnchor(1, store.Of(Project)[0].Hash, second.Hash), afterTheCrash);
        Assert.True(state.Verification.IsValid);
        Assert.Equal(3, third.Sequence);
        Assert.Equal(new DecisionLogAnchor(3, third.Hash), anchors.Of(Project));
    }

    [Fact]
    public async Task A_write_that_reached_the_store_before_the_error_is_trusted_by_the_anchor_too()
    {
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        store.FailAfterStoring = 1;

        await Assert.ThrowsAsync<IOException>(() => Record(sut, summary: "stored but reported as failed"));
        var next = await Record(sut, summary: "after");

        Assert.Equal([1L, 2L, 3L], store.Of(Project).Select(e => e.Sequence));
        Assert.Equal(new DecisionLogAnchor(3, next.Hash), anchors.Of(Project));
        Assert.True((await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Verification.IsValid);
    }

    [Fact]
    public async Task An_entry_whose_announcement_cannot_be_written_is_not_stored()
    {
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        anchors.FailWrite = a => a.NextHash is not null;

        await Assert.ThrowsAsync<IOException>(() => Record(sut));
        anchors.FailWrite = _ => false;
        var next = await Record(sut);

        Assert.Equal([1L, 2L], store.Of(Project).Select(e => e.Sequence));
        Assert.Equal(2, next.Sequence);
        Assert.True((await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Verification.IsValid);
    }

    [Fact]
    public async Task A_damaged_anchor_makes_the_log_untrusted()
    {
        var (sut, _, anchors) = BuildAnchored();
        await Record(sut);
        anchors.Damaged = true;

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Contains("anchor", state.Verification.Problem, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));
    }

    [Fact]
    public async Task The_anchor_is_confirmed_to_its_store_only_once_the_log_verified_against_it()
    {
        // What lets the store move an anchor kept under its old name: never for a log that does not verify (P-04).
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        var second = await Record(sut);

        var trusted = await new DecisionRecorder(store, new FakeTimeProvider(T0), anchors).LoadAsync(Project, TestContext.Current.CancellationToken);
        store.Seed(Project, store.Of(Project).Take(1));
        var cut = await new DecisionRecorder(store, new FakeTimeProvider(T0), anchors).LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.True(trusted.Verification.IsValid);
        Assert.False(cut.Verification.IsValid);
        Assert.Equal([new DecisionLogAnchor(2, second.Hash)], anchors.Confirmed);
    }

    [Fact]
    public async Task An_anchor_that_cannot_be_confirmed_leaves_the_log_untrusted_and_nothing_is_recorded()
    {
        // As when the anchor found under its old name cannot be moved to the location: no trust until it is kept there.
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        anchors.FailConfirm = true;
        var restarted = new DecisionRecorder(store, new FakeTimeProvider(T0), anchors);

        var state = await restarted.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Contains("anchor", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Null(state.Unanchored);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(restarted));
        Assert.Single(store.Of(Project));
    }

    // ---- a history no anchor recognises at the project's location (ADR-0010, addendum of 2026-10-18) ----

    /// <summary>A log of <paramref name="count"/> entries written with no anchor at all: moved here, copied from another machine...</summary>
    private static async Task<(DecisionRecorder Sut, InMemoryDecisionLogStore Store, InMemoryAnchorStore Anchors, IReadOnlyList<DecisionEntry> Written)> UnanchoredLogAsync(int count)
    {
        var (elsewhere, store, _) = Build();
        var written = new List<DecisionEntry>();
        for (var i = 0; i < count; i++)
        {
            written.Add(await Record(elsewhere));
        }

        var anchors = new InMemoryAnchorStore();
        return (new DecisionRecorder(store, new FakeTimeProvider(T0.AddHours(1)), anchors), store, anchors, written);
    }

    /// <summary>The question «Aceptar este historial como nuevo origen» and the person's answer, bound to <paramref name="history"/>.</summary>
    private static (ApprovalRequest Request, ApprovalDecision Decision) Acceptance(UnanchoredHistory history, bool approved = true, string decidedBy = "user")
    {
        var binding = history.Binding(Project);
        var request = new ApprovalRequest(UnanchoredHistory.AcceptAction, "Aceptar este historial como nuevo origen.", RiskLevel.Critical, null, [@"C:\proyectos\calc"], Project, Binding: binding);
        return (request, new ApprovalDecision(approved, decidedBy, T0, "Moví yo la carpeta.", binding));
    }

    private static Task<DecisionLogState> AcceptAsync(DecisionRecorder sut, (ApprovalRequest Request, ApprovalDecision Decision) acceptance) =>
        sut.AcceptAsNewOriginAsync(Project, acceptance.Request, acceptance.Decision, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_non_empty_log_without_an_anchor_for_its_location_is_untrusted_and_never_extended()
    {
        var (sut, store, anchors, written) = await UnanchoredLogAsync(2);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);
        var refusal = await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));

        Assert.True(DecisionLog.Load(state.Entries).Verify().IsValid); // the chain alone cannot tell
        Assert.False(state.Verification.IsValid);
        Assert.Contains("no anchor", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Equal(new UnanchoredHistory(2, written[^1].Hash), state.Unanchored);
        Assert.False(refusal.Verification.IsValid);
        Assert.Equal(2, store.Of(Project).Count);
        Assert.Empty(anchors.Written);
    }

    [Fact]
    public async Task An_empty_log_without_an_anchor_is_a_new_project_that_its_first_entry_anchors()
    {
        var (sut, _, anchors) = BuildAnchored();

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);
        var first = await Record(sut);

        Assert.True(state.Verification.IsValid);
        Assert.Null(state.Unanchored);
        Assert.Equal(new DecisionLogAnchor(1, first.Hash), anchors.Of(Project));
    }

    [Fact]
    public async Task Accepting_the_history_records_the_alert_and_the_approval_and_only_then_anchors_its_head()
    {
        var (sut, store, anchors, written) = await UnanchoredLogAsync(2);
        var shown = (await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Unanchored!;

        var accepted = await AcceptAsync(sut, Acceptance(shown));

        var entries = store.Of(Project);
        Assert.True(accepted.Verification.IsValid);
        Assert.Null(accepted.Unanchored);
        Assert.Equal(entries, accepted.Entries);
        Assert.Equal(written, entries.Take(2));
        Assert.Equal([DecisionKind.AuditAlertRaised, DecisionKind.ApprovalRequested, DecisionKind.ApprovalGranted], entries.Skip(2).Select(e => e.Kind));
        Assert.Equal([System, System, new Actor(ActorKind.User, "user")], entries.Skip(2).Select(e => e.Actor));

        var alert = DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(entries[2].PayloadJson);
        Assert.Equal(DecisionRecorder.OriginAcceptedAlert, alert.Code);
        Assert.Contains("2 entries", alert.Message, StringComparison.Ordinal);
        Assert.Contains(written[^1].Hash, alert.Message, StringComparison.Ordinal);
        var requested = DecisionPayloadSerializer.Deserialize<ApprovalRequestedPayload>(entries[3].PayloadJson);
        Assert.Equal((UnanchoredHistory.AcceptAction, "Aceptar este historial como nuevo origen.", RiskLevel.Critical), (requested.Action, requested.Description, requested.Risk));
        var granted = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(entries[4].PayloadJson);
        Assert.Equal((UnanchoredHistory.AcceptAction, true, "user", "Moví yo la carpeta."), (granted.Action, granted.Approved, granted.DecidedBy, granted.Comment));

        // The anchor is written once, at the end: nothing anchors the history before its acceptance is in the log.
        Assert.Equal([new DecisionLogAnchor(5, entries[4].Hash)], anchors.Written);
        Assert.True((await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Verification.IsValid);
        Assert.Equal(6, (await Record(sut)).Sequence);
    }

    /// <summary>
    /// docs/16 D-13, F8-11: accepting a history is a critical question, so with Windows Hello on the person is confirmed with
    /// Hello, or the dialog decides alone when Hello is not there; the answer records it as the approval gate does. The dialog
    /// alone keeps the text of version 1.
    /// </summary>
    [Theory]
    [InlineData(ApprovalVerification.Dialog)]
    [InlineData(ApprovalVerification.WindowsHello)]
    [InlineData(ApprovalVerification.WindowsHelloUnavailable)]
    public async Task Accepting_the_history_records_how_the_person_was_confirmed(ApprovalVerification verification)
    {
        var (sut, store, _, _) = await UnanchoredLogAsync(2);
        var shown = (await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Unanchored!;
        var (request, decision) = Acceptance(shown);

        var accepted = await AcceptAsync(sut, (request, decision with { Verification = verification }));

        Assert.True(accepted.Verification.IsValid);
        var granted = store.Of(Project)[^1];
        Assert.Equal(DecisionKind.ApprovalGranted, granted.Kind);
        var payload = DecisionPayloadSerializer.Deserialize<ApprovalAnsweredPayload>(granted.PayloadJson);
        Assert.Equal(verification, payload.ResolvedVerification());
        if (verification == ApprovalVerification.Dialog)
        {
            var v1 = new ApprovalAnsweredPayload(UnanchoredHistory.AcceptAction, true, "user", "Moví yo la carpeta.", null) { RunId = DecisionPayload.NoRun };
            Assert.Equal(DecisionPayloadSerializer.Serialize(v1), granted.PayloadJson);
        }
        else
        {
            Assert.Equal((ApprovalAnsweredPayload.VerificationVersion, (ApprovalVerification?)verification), (payload.V, payload.Verification));
        }
    }

    [Fact]
    public async Task An_acceptance_that_reaches_the_store_only_in_part_leaves_the_history_unanchored()
    {
        var (sut, store, anchors, _) = await UnanchoredLogAsync(2);
        var shown = (await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Unanchored!;
        var appends = 0;
        store.BeforeAppend = _ => ++appends == 2 ? throw new IOException("disk full") : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => AcceptAsync(sut, Acceptance(shown)));
        store.BeforeAppend = null;
        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.Empty(anchors.Written);
        Assert.False(state.Verification.IsValid);
        Assert.Equal(3, state.Unanchored?.EntryCount); // the alert got in; the person is asked again
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));
    }

    [Fact]
    public async Task An_acceptance_given_for_a_history_that_changed_since_it_was_shown_is_refused_and_writes_nothing()
    {
        var (sut, store, anchors, _) = await UnanchoredLogAsync(2);
        var shown = (await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Unanchored!;
        store.Seed(Project, Forged(store.Of(Project), "entrada añadida después de mostrarlo"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(sut, Acceptance(shown)));

        Assert.Equal(3, store.Of(Project).Count);
        Assert.Empty(anchors.Written);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("another binding")]
    [InlineData("nobody")]
    public async Task An_acceptance_that_is_not_the_persons_approval_of_this_history_is_refused_and_writes_nothing(string how)
    {
        var (sut, store, anchors, _) = await UnanchoredLogAsync(2);
        var shown = (await sut.LoadAsync(Project, TestContext.Current.CancellationToken)).Unanchored!;
        var (request, decision) = Acceptance(shown);
        decision = how switch
        {
            "denied" => decision with { Approved = false },
            "another binding" => decision with { Answers = new UnanchoredHistory(1, HashA).Binding(Project) },
            _ => decision with { DecidedBy = " " },
        };

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => AcceptAsync(sut, (request, decision)));

        Assert.Equal(2, store.Of(Project).Count);
        Assert.Empty(anchors.Written);
    }

    [Fact]
    public async Task A_trusted_log_has_nothing_to_accept()
    {
        var (sut, store, anchors) = BuildAnchored();
        var last = await Record(sut);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(sut, Acceptance(new UnanchoredHistory(1, last.Hash))));

        Assert.Single(store.Of(Project));
        Assert.Equal(new DecisionLogAnchor(1, last.Hash), anchors.Of(Project));
    }

    [Fact]
    public async Task A_log_cut_short_under_its_own_anchor_is_tampering_and_cannot_be_accepted_as_a_new_origin()
    {
        var (sut, store, anchors) = BuildAnchored();
        await Record(sut);
        var second = await Record(sut);
        await Record(sut);
        store.Seed(Project, store.Of(Project).Take(2));

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Null(state.Unanchored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(sut, Acceptance(new UnanchoredHistory(2, second.Hash))));
        Assert.Equal(2, store.Of(Project).Count);
        Assert.Equal(3, anchors.Of(Project)?.EntryCount);
    }

    [Fact]
    public async Task A_broken_chain_without_an_anchor_cannot_be_accepted()
    {
        var (sut, store, _, written) = await UnanchoredLogAsync(2);
        store.Replace(Project, 0, written[0] with { Summary = "forged" });

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Equal(1, state.Verification.FirstInvalidSequence);
        Assert.Null(state.Unanchored);
    }

    [Fact]
    public async Task A_history_under_the_anchor_of_another_project_is_untrusted_and_cannot_be_accepted()
    {
        // What rewriting the project id offline looks like: the location's anchor names the id the project had.
        var (sut, store, anchors, written) = await UnanchoredLogAsync(2);
        anchors.Foreign[Project] = ("otro-proyecto", 7);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.False(state.Verification.IsValid);
        Assert.Contains("otro-proyecto", state.Verification.Problem, StringComparison.Ordinal);
        Assert.Null(state.Unanchored);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(sut, Acceptance(new UnanchoredHistory(2, written[^1].Hash))));
        Assert.Equal(2, store.Of(Project).Count);
        Assert.Empty(anchors.Written);
    }

    [Fact]
    public async Task An_empty_history_under_the_anchor_of_another_project_can_be_accepted_and_the_alert_names_what_lived_there()
    {
        // .agentstudio deleted, or the repository cloned again in the same folder: a new, empty database.
        var (sut, store, anchors) = BuildAnchored();
        anchors.Foreign[Project] = ("otro-proyecto", 7);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => Record(sut));
        var accepted = await AcceptAsync(sut, Acceptance(state.Unanchored!));

        Assert.False(state.Verification.IsValid);
        Assert.Equal(new UnanchoredHistory(0, DecisionLog.GenesisHash, "otro-proyecto", 7), state.Unanchored);
        Assert.True(accepted.Verification.IsValid);
        var alert = DecisionPayloadSerializer.Deserialize<AuditAlertPayload>(store.Of(Project)[0].PayloadJson);
        Assert.Contains("otro-proyecto", alert.Message, StringComparison.Ordinal);
        Assert.Contains("7 entries", alert.Message, StringComparison.Ordinal);
        Assert.Equal(new DecisionLogAnchor(3, store.Of(Project)[2].Hash), anchors.Of(Project));
    }

    [Fact]
    public async Task Without_an_anchor_store_every_log_is_checked_by_its_chain_alone_and_there_is_nothing_to_accept()
    {
        var (sut, store, _) = Build();
        var last = await Record(sut);

        var state = await sut.LoadAsync(Project, TestContext.Current.CancellationToken);

        Assert.True(state.Verification.IsValid);
        Assert.Null(state.Unanchored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AcceptAsync(sut, Acceptance(new UnanchoredHistory(1, last.Hash))));
        Assert.Single(store.Of(Project));
    }
}
