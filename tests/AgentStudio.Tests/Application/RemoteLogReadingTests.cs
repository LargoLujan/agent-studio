using AgentStudio.Application.Decisions;
using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Hosting;
using static AgentStudio.Tests.Domain.Hosting.HostingP0Data;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/21 D-23, D-28 and D-29: the one reading of the remote entries of the log (<see cref="RemoteLogReading"/>) validates them as data, leaves out what does not validate with its
/// sequence and run, and gives the same final observation, run and moment of every pull request for every order and every repetition of the entries.
/// </summary>
public sealed class RemoteLogReadingTests
{
    private const string RunA = "20261111-a1b2c3";
    private const string RunB = "20261112-aaaaaa";
    private const string RunC = "20261113-bbbbbb";

    private static readonly Actor SystemActor = new(ActorKind.System, "agent-studio");

    private static DecisionEntry EntryOf(long sequence, DecisionKind kind, DecisionPayload payload, DateTimeOffset? at = null) =>
        new(sequence, at ?? At.AddSeconds(sequence), SystemActor, kind, "test", DecisionPayloadSerializer.Serialize(payload), null, string.Empty, string.Empty);

    private static (DecisionEntry Entry, DecisionPayload? Payload) Observed(long sequence, RemoteSnapshot snapshot, string? run) =>
        Parsed(EntryOf(sequence, DecisionKind.RemoteStateObserved, RemoteStateObservedPayload.Observing(snapshot, run, null, [])));

    private static (DecisionEntry Entry, DecisionPayload? Payload) Parsed(DecisionEntry entry) => (entry, DecisionPayloadSerializer.Deserialize(entry.Kind, entry.PayloadJson));

    private static (DecisionEntry Entry, DecisionPayload? Payload) Operation(long sequence, HostingOperationKind operation, MergeMethod? method = null, HostingOutcome outcome = HostingOutcome.Ok, string run = RunA) =>
        Parsed(EntryOf(
            sequence,
            DecisionKind.HostingOperationPerformed,
            HostingOperationPerformedPayload.Of(operation, run, Repository, 12, null, Head, method, null, 1, outcome, null, null, null, null, null, null)));

    private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            foreach (var tail in Permutations(items.Where((_, index) => index != i).ToList()))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    [Fact]
    public void Two_observations_that_are_the_final_one_and_name_another_run_give_the_same_run_whatever_the_order()
    {
        var merged = Merged(RemoteMergeMode.Squash);
        var older = Open() with { UpdatedAt = At.AddMinutes(-3) };
        var entries = new[] { Observed(1, merged, RunB), Observed(2, merged, RunA), Observed(3, older, RunC) };

        foreach (var order in Permutations(entries))
        {
            var pullRequest = Assert.Single(RemoteLogReading.Read(order).PullRequests);

            Assert.Equal(RunA, pullRequest.Run);
            Assert.Equal(merged.Fingerprint, pullRequest.Latest.Fingerprint);
            Assert.Equal(3, pullRequest.Observations);
            Assert.Equal(At.AddSeconds(2), pullRequest.ObservedAt);
        }
    }

    [Fact]
    public void A_final_observation_with_no_run_takes_the_smallest_run_any_observation_names()
    {
        var entries = new[] { Observed(1, Open(), RunB), Observed(2, Merged(RemoteMergeMode.Squash), null), Observed(3, Open() with { UpdatedAt = At.AddMinutes(-1) }, RunA) };

        foreach (var order in Permutations(entries))
        {
            Assert.Equal(RunA, Assert.Single(RemoteLogReading.Read(order).PullRequests).Run);
        }
    }

    [Fact]
    public void A_pull_request_reopened_after_it_was_closed_is_read_as_open_in_every_order_and_with_repetitions()
    {
        var opened = Open();
        var closed = Open() with { State = PullRequestState.Closed, UpdatedAt = At.AddMinutes(1) };
        var reopened = Open() with { UpdatedAt = At.AddMinutes(2), Checks = new ChecksSummary(ChecksRollup.Pending, 0, 0, 1, false) };
        var entries = new[] { Observed(1, opened, RunA), Observed(2, closed, RunA), Observed(3, reopened, RunA), Observed(4, closed, RunA) };

        foreach (var order in Permutations(entries))
        {
            var pullRequest = Assert.Single(RemoteLogReading.Read(order).PullRequests);
            Assert.Equal(reopened.Fingerprint, pullRequest.Latest.Fingerprint);
        }
    }

    [Fact]
    public void The_same_repository_in_another_case_is_another_pull_request()
    {
        var elsewhere = Open() with { PullRequest = new PullRequestRef(RemoteRepository.TryCreateGitHub("largolujan", "AGENT-STUDIO")!, 12) };

        var reading = RemoteLogReading.Read([Observed(1, Open(), RunA), Observed(2, elsewhere, RunA)]);

        Assert.Equal(2, reading.PullRequests.Count);
        Assert.NotNull(reading.Find(PullRequest));
        Assert.NotNull(reading.Find(elsewhere.PullRequest));
        Assert.Equal(2, reading.PullRequests.Select(p => p.PullRequest.TraceId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void What_does_not_validate_is_left_out_with_its_sequence_and_the_run_it_names_and_what_cannot_be_read_names_none()
    {
        var wrongFingerprint = EntryOf(5, DecisionKind.RemoteStateObserved, RemoteStateObservedPayload.Observing(Open(), RunB, null, []) with { Fingerprint = new string('0', 64) });
        var observed = RemoteStateObservedPayload.Observing(Open(), RunC, null, []);
        var badHead = EntryOf(6, DecisionKind.RemoteStateObserved, observed with { Snapshot = observed.Snapshot with { HeadCommit = "ABC" } });
        var badOperation = EntryOf(7, DecisionKind.HostingOperationPerformed, HostingOperationPerformedPayload.Of(HostingOperationKind.OpenPullRequest, RunA, Repository, 12, null, Head, null, null, 1, HostingOutcome.Ok, null, null, null, null, null, null) with { Class = HostingOperationClass.Destructive });
        var unreadable = new DecisionEntry(8, At, SystemActor, DecisionKind.RemoteStateObserved, "x", "{ not json", null, string.Empty, string.Empty);

        var reading = RemoteLogReading.ReadEntries([Parsed(Observed(1, Open(), RunA).Entry).Entry, wrongFingerprint, badHead, badOperation, unreadable]);

        Assert.Single(reading.PullRequests);
        Assert.Equal(
            [
                (5L, RemoteLogReading.InvalidCode, (string?)RunB),
                (6L, RemoteLogReading.InvalidCode, RunC),
                (7L, RemoteLogReading.InvalidCode, RunA),
                (8L, RemoteLogReading.UnreadableCode, null),
            ],
            reading.Ignored.Select(i => (i.Sequence, i.Code, i.Run)));
        Assert.Empty(reading.Operations);
    }

    [Fact]
    public void The_operations_are_by_sequence_and_the_method_the_app_merged_with_is_the_last_of_those_that_worked()
    {
        var entries = new[]
        {
            Operation(3, HostingOperationKind.MergePullRequest, MergeMethod.Squash),
            Operation(1, HostingOperationKind.MergePullRequest, MergeMethod.Rebase, HostingOutcome.Refused),
            Operation(2, HostingOperationKind.OpenPullRequest),
        };

        foreach (var order in Permutations(entries))
        {
            var reading = RemoteLogReading.Read(order);

            Assert.Equal([1L, 2L, 3L], reading.Operations.Select(o => o.Sequence));
            Assert.Equal(MergeMethod.Squash, reading.MergedByAppWith(PullRequest));
            Assert.Null(reading.MergedByAppWith(new PullRequestRef(Repository, 99)));
        }
    }

    [Fact]
    public void The_succession_says_a_mode_the_app_merged_with_is_a_fact_and_one_it_did_not_is_deduced()
    {
        var merged = Observed(1, Merged(RemoteMergeMode.Squash), RunA);

        var byApp = RemoteLogReading.Read([merged, Operation(2, HostingOperationKind.MergePullRequest, MergeMethod.Squash)]).Succession(_ => Head);
        var outside = RemoteLogReading.Read([merged]).Succession(_ => Head);

        Assert.False(byApp.OfPullRequest(PullRequest)!.ModeIsDeduced);
        Assert.True(outside.OfPullRequest(PullRequest)!.ModeIsDeduced);
        Assert.True(outside.OfPullRequest(PullRequest)!.HeadWasJudgedCommit);
    }

    [Fact]
    public void The_lists_it_gives_are_read_only_for_real()
    {
        var reading = RemoteLogReading.Read([Observed(1, Open(), RunA), Operation(2, HostingOperationKind.OpenPullRequest)]);

        foreach (var list in new System.Collections.IList[] { (System.Collections.IList)reading.PullRequests, (System.Collections.IList)reading.Operations, (System.Collections.IList)reading.Ignored, (System.Collections.IList)RemoteLogReading.Empty.PullRequests })
        {
            Assert.True(list.IsReadOnly);
        }
    }

    [Fact]
    public void A_log_with_no_remote_entry_reads_as_empty()
    {
        var reading = RemoteLogReading.ReadEntries([]);

        Assert.Empty(reading.PullRequests);
        Assert.Empty(reading.Operations);
        Assert.Empty(reading.Ignored);
        Assert.Empty(reading.OfRun(RunA));
    }
}
