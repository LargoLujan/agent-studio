using AgentStudio.Domain.Decisions;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain;

public sealed class DecisionLogTests
{
    private static readonly Actor User = new(ActorKind.User, "manuel");
    private static readonly Actor Orchestrator = new(ActorKind.Orchestrator, "orch");

    private static DecisionLog ThreeEntries()
    {
        var log = new DecisionLog();
        log.Append(T0, User, DecisionKind.PlanCreated, "plan", "{\"a\":1}");
        log.Append(T0.AddMinutes(1), Orchestrator, DecisionKind.TaskCreated, "task one", "{\"id\":\"t1\"}");
        log.Append(T0.AddMinutes(2), Orchestrator, DecisionKind.TaskAssigned, "assign", "{\"id\":\"t1\"}");
        return log;
    }

    private static DecisionLogVerification VerifyCopy(IEnumerable<DecisionEntry> entries) => DecisionLog.Load(entries).Verify();

    private static DecisionEntry ConsistentlyHashed(long sequence, DateTimeOffset at, string previousHash)
    {
        var hash = DecisionLog.ComputeHash(sequence, at, User, DecisionKind.PlanCreated, "s", "{}", null, previousHash);
        return new DecisionEntry(sequence, at, User, DecisionKind.PlanCreated, "s", "{}", null, previousHash, hash);
    }

    [Fact]
    public void A_rewritten_chain_whose_time_goes_backwards_is_rejected()
    {
        // Every hash is consistent, but no writer going through Append could have produced this order.
        var first = ConsistentlyHashed(1, T0.AddMinutes(5), DecisionLog.GenesisHash);
        var second = ConsistentlyHashed(2, T0, first.Hash);

        var result = VerifyCopy([first, second]);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstInvalidSequence);
    }

    [Fact]
    public void An_empty_log_has_the_genesis_hash_as_head_and_is_valid()
    {
        var log = new DecisionLog();

        Assert.Equal(DecisionLog.GenesisHash, log.HeadHash);
        Assert.Equal(new string('0', 64), DecisionLog.GenesisHash);
        Assert.True(log.Verify().IsValid);
    }

    [Fact]
    public void Entries_are_numbered_from_one_and_chained_by_hash()
    {
        var log = ThreeEntries();

        Assert.Equal([1L, 2L, 3L], log.Entries.Select(e => e.Sequence));
        Assert.Equal(DecisionLog.GenesisHash, log.Entries[0].PreviousHash);
        Assert.Equal(log.Entries[0].Hash, log.Entries[1].PreviousHash);
        Assert.Equal(log.Entries[1].Hash, log.Entries[2].PreviousHash);
        Assert.Equal(log.Entries[2].Hash, log.HeadHash);
        Assert.All(log.Entries, e => Assert.Matches("^[0-9a-f]{64}$", e.Hash));
        Assert.True(log.Verify().IsValid);
    }

    [Fact]
    public void A_faithful_copy_loaded_from_storage_verifies()
    {
        var log = ThreeEntries();

        var loaded = DecisionLog.Load(log.Entries.ToList());

        Assert.True(loaded.Verify().IsValid);
        Assert.Equal(log.HeadHash, loaded.HeadHash);
    }

    [Fact]
    public void Appending_after_loading_continues_the_chain()
    {
        var loaded = DecisionLog.Load(ThreeEntries().Entries.ToList());

        var next = loaded.Append(T0.AddMinutes(3), User, DecisionKind.ApprovalGranted, "ok");

        Assert.Equal(4, next.Sequence);
        Assert.True(loaded.Verify().IsValid);
    }

    [Fact]
    public void Modifying_a_summary_is_detected_at_that_entry()
    {
        var entries = ThreeEntries().Entries.ToList();
        entries[1] = entries[1] with { Summary = "task two" };

        var result = VerifyCopy(entries);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstInvalidSequence);
    }

    [Fact]
    public void Modifying_a_payload_is_detected_at_that_entry()
    {
        var entries = ThreeEntries().Entries.ToList();
        entries[0] = entries[0] with { PayloadJson = "{\"a\":2}" };

        var result = VerifyCopy(entries);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.FirstInvalidSequence);
    }

    [Fact]
    public void Modifying_the_actor_the_kind_or_the_time_is_detected()
    {
        var original = ThreeEntries().Entries.ToList();

        foreach (var tamper in new Func<DecisionEntry, DecisionEntry>[]
                 {
                     e => e with { Actor = new Actor(ActorKind.User, "mallory") },
                     e => e with { Kind = DecisionKind.ApprovalGranted },
                     e => e with { RecordedAt = e.RecordedAt.AddSeconds(1) },
                 })
        {
            var entries = original.ToList();
            entries[1] = tamper(entries[1]);
            Assert.False(VerifyCopy(entries).IsValid);
        }
    }

    [Fact]
    public void Deleting_a_middle_entry_is_detected()
    {
        var entries = ThreeEntries().Entries.ToList();
        entries.RemoveAt(1);

        Assert.False(VerifyCopy(entries).IsValid);
    }

    [Fact]
    public void Deleting_the_first_entry_is_detected()
    {
        var entries = ThreeEntries().Entries.ToList();
        entries.RemoveAt(0);

        Assert.False(VerifyCopy(entries).IsValid);
    }

    [Fact]
    public void Reordering_entries_is_detected()
    {
        // Swap the sequence numbers so that, once the storage returns them in sequence order, the content is reordered.
        var entries = ThreeEntries().Entries.ToList();
        var swapped = new List<DecisionEntry>
        {
            entries[0],
            entries[2] with { Sequence = 2 },
            entries[1] with { Sequence = 3 },
        };

        Assert.False(VerifyCopy(swapped).IsValid);
    }

    [Fact]
    public void Altering_a_stored_hash_is_detected()
    {
        var entries = ThreeEntries().Entries.ToList();
        entries[1] = entries[1] with { Hash = new string('f', 64) };

        var result = VerifyCopy(entries);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstInvalidSequence);
    }

    [Fact]
    public void Altering_a_previous_hash_is_detected()
    {
        var entries = ThreeEntries().Entries.ToList();
        entries[2] = entries[2] with { PreviousHash = DecisionLog.GenesisHash };

        Assert.False(VerifyCopy(entries).IsValid);
    }

    [Fact]
    public void Rewriting_an_entry_and_recomputing_only_its_own_hash_still_breaks_the_chain()
    {
        var entries = ThreeEntries().Entries.ToList();
        var forged = entries[1] with { Summary = "forged" };
        forged = forged with
        {
            Hash = DecisionLog.ComputeHash(forged.Sequence, forged.RecordedAt, forged.Actor, forged.Kind, forged.Summary, forged.PayloadJson, forged.Corrects, forged.PreviousHash),
        };
        entries[1] = forged;

        var result = VerifyCopy(entries);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstInvalidSequence); // the next entry no longer chains to the forged one
    }

    [Fact]
    public void A_correction_must_reference_an_earlier_entry()
    {
        var log = ThreeEntries();

        Violation(() => log.Append(T0.AddMinutes(3), User, DecisionKind.Correction, "fix", corrects: null));
        Violation(() => log.Append(T0.AddMinutes(3), User, DecisionKind.Correction, "fix", corrects: 4)); // itself
        Violation(() => log.Append(T0.AddMinutes(3), User, DecisionKind.Correction, "fix", corrects: 5)); // the future
        Violation(() => log.Append(T0.AddMinutes(3), User, DecisionKind.Correction, "fix", corrects: 0));
        Assert.Equal(3, log.Entries.Count);

        var correction = log.Append(T0.AddMinutes(3), User, DecisionKind.Correction, "fix", corrects: 2);
        Assert.Equal(2, correction.Corrects);
        Assert.Equal(4, log.Entries.Count); // the corrected entry stays
        Assert.True(log.Verify().IsValid);
    }

    [Fact]
    public void Only_correction_entries_may_reference_another_entry()
    {
        var log = ThreeEntries();

        Violation(() => log.Append(T0.AddMinutes(3), User, DecisionKind.TaskCreated, "x", corrects: 1));
        Assert.Equal(3, log.Entries.Count);
    }

    [Fact]
    public void Verify_rejects_a_stored_correction_that_points_forward_even_with_valid_hashes()
    {
        var entries = ThreeEntries().Entries.ToList();
        var previous = entries[2];
        var sequence = previous.Sequence + 1;
        var hash = DecisionLog.ComputeHash(sequence, T0.AddMinutes(3), User, DecisionKind.Correction, "bad", "{}", sequence + 1, previous.Hash);
        entries.Add(new DecisionEntry(sequence, T0.AddMinutes(3), User, DecisionKind.Correction, "bad", "{}", sequence + 1, previous.Hash, hash));

        Assert.False(VerifyCopy(entries).IsValid);
    }

    [Fact]
    public void Entries_dated_before_the_last_one_are_rejected_but_equal_dates_are_fine()
    {
        var log = ThreeEntries();

        Violation(() => log.Append(T0.AddMinutes(1), User, DecisionKind.ApprovalGranted, "late"));
        Assert.Equal(3, log.Entries.Count);

        log.Append(T0.AddMinutes(2), User, DecisionKind.ApprovalGranted, "same time");
        Assert.Equal(4, log.Entries.Count);
    }

    [Fact]
    public void Summary_is_required()
    {
        var log = new DecisionLog();

        Assert.ThrowsAny<ArgumentException>(() => log.Append(T0, User, DecisionKind.PlanCreated, " "));
    }

    [Fact]
    public async Task Concurrent_appends_keep_the_chain_valid_and_the_sequence_contiguous()
    {
        var log = new DecisionLog();
        var next = 0;
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(
            () =>
            {
                gate.Wait(TestContext.Current.CancellationToken);
                while (Interlocked.Increment(ref next) <= 200)
                {
                    log.Append(T0, User, DecisionKind.PlanCreated, "concurrent");
                }
            },
            TestContext.Current.CancellationToken)).ToArray();

        gate.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(200, log.Entries.Count);
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), log.Entries.Select(e => e.Sequence));
        Assert.True(log.Verify().IsValid);
    }

    [Fact]
    public void Entries_is_a_snapshot_not_the_live_list()
    {
        var log = ThreeEntries();
        var snapshot = log.Entries;

        log.Append(T0.AddMinutes(3), User, DecisionKind.PlanRevised, "later");

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(4, log.Entries.Count);
    }

    [Fact]
    public void Identical_content_gives_identical_hashes_and_any_field_changes_them()
    {
        var a = new DecisionLog();
        var b = new DecisionLog();
        var ea = a.Append(T0, User, DecisionKind.PlanCreated, "plan", "{}");
        var eb = b.Append(T0, User, DecisionKind.PlanCreated, "plan", "{}");
        var ec = new DecisionLog().Append(T0, User, DecisionKind.PlanCreated, "plan!", "{}");

        Assert.Equal(ea.Hash, eb.Hash);
        Assert.NotEqual(ea.Hash, ec.Hash);
    }

    // ---- against the head anchored outside the project (ADR-0010, addendum) ----

    private static DecisionLogAnchor AnchorOf(DecisionLog log) => new(log.Entries.Count, log.HeadHash);

    [Fact]
    public void A_log_that_matches_its_anchor_verifies()
    {
        var log = ThreeEntries();

        Assert.True(log.Verify(AnchorOf(log)).IsValid);
        Assert.True(new DecisionLog().Verify(new DecisionLogAnchor(0, DecisionLog.GenesisHash)).IsValid);
    }

    [Fact]
    public void Without_an_anchor_only_the_chain_is_checked()
    {
        Assert.True(ThreeEntries().Verify(null).IsValid);
    }

    [Fact]
    public void A_log_whose_end_was_cut_off_after_its_head_was_anchored_does_not_verify()
    {
        var log = ThreeEntries();
        var anchor = AnchorOf(log);

        var cut = DecisionLog.Load(log.Entries.Take(2));
        var result = cut.Verify(anchor);

        Assert.True(cut.Verify().IsValid); // the chain alone cannot tell
        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstInvalidSequence);
        Assert.Contains("removed", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_rewritten_with_recomputed_hashes_does_not_verify_against_its_anchor()
    {
        var anchor = AnchorOf(ThreeEntries());
        var rewritten = new DecisionLog();
        rewritten.Append(T0, User, DecisionKind.PlanCreated, "plan", "{\"a\":1}");
        rewritten.Append(T0.AddMinutes(1), Orchestrator, DecisionKind.TaskCreated, "task one", "{\"id\":\"t1\"}");
        rewritten.Append(T0.AddMinutes(2), Orchestrator, DecisionKind.TaskAssigned, "assign", "{\"id\":\"FORGED\"}");

        var result = rewritten.Verify(anchor);

        Assert.True(rewritten.Verify().IsValid);
        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstInvalidSequence);
        Assert.Contains("rewritten", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_added_after_the_anchored_head_without_being_announced_do_not_verify()
    {
        var log = ThreeEntries();
        var anchor = new DecisionLogAnchor(2, log.Entries[1].Hash);

        var result = log.Verify(anchor);

        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstInvalidSequence);
        Assert.Contains("added", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_entry_announced_before_it_was_stored_is_trusted_although_the_anchor_was_not_updated_after_it()
    {
        var log = ThreeEntries();

        Assert.True(log.Verify(new DecisionLogAnchor(2, log.Entries[1].Hash, log.Entries[2].Hash)).IsValid);
        Assert.False(log.Verify(new DecisionLogAnchor(2, log.Entries[1].Hash, HashA)).IsValid);
    }

    [Fact]
    public void An_announced_entry_that_never_reached_the_store_leaves_the_log_trusted()
    {
        var log = ThreeEntries();

        Assert.True(log.Verify(new DecisionLogAnchor(3, log.HeadHash, HashA)).IsValid);
    }

    [Fact]
    public void A_broken_chain_is_reported_as_such_even_with_an_anchor()
    {
        var log = ThreeEntries();
        var entries = log.Entries.ToList();
        entries[1] = entries[1] with { Summary = "forged" };

        var result = DecisionLog.Load(entries).Verify(AnchorOf(log));

        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstInvalidSequence);
    }

    [Theory]
    [InlineData(-1, "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(2, "not-a-hash")]
    public void An_anchor_that_cannot_be_a_head_makes_the_log_untrusted(long count, string head)
    {
        var result = ThreeEntries().Verify(new DecisionLogAnchor(count, head));

        Assert.False(result.IsValid);
        Assert.Contains("anchor", result.Problem, StringComparison.Ordinal);
    }
}
