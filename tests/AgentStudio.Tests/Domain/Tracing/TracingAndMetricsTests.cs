using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Domain.Tracing;

/// <summary>docs/16 D-29, D-30 and D-34 (P0 of Phase 9): the nodes and links of the trace, the trailers of the commits of the system and the classification of the activity.</summary>
public sealed class TracingAndMetricsTests
{
    private static readonly TraceRef Req = TraceRef.Requirement("RQ-01");
    private static readonly TraceRef Commit = TraceRef.Commit(CommitA);
    private static readonly TraceRef Milestone = TraceRef.Milestone("mil-20261115-a1b2c3");

    private static TraceLink Link(TraceRef from, TraceRef to, TraceAssertedBy by = TraceAssertedBy.User, DateTimeOffset? at = null) => new(from, to, by, "cumple", at ?? T0);

    // ---- nodes ----

    [Fact]
    public void A_commit_is_always_the_full_id()
    {
        Assert.Equal(CommitA, TraceRef.Commit(CommitA).Id);
        Assert.Equal(64, TraceRef.Commit(new string('a', 64)).Id.Length);
        Assert.Throws<ArgumentException>(() => TraceRef.Commit("abc1234"));
        Assert.Throws<ArgumentException>(() => TraceRef.Commit(CommitA.ToUpperInvariant().Replace('1', 'A')));
    }

    [Fact]
    public void Tasks_attempts_and_documents_carry_what_they_are_unique_in()
    {
        Assert.Equal("20261015-a1b2c3/T3", TraceRef.Task("20261015-a1b2c3", "T3").Id);
        Assert.Equal("20261015-a1b2c3/T3#2", TraceRef.Attempt("20261015-a1b2c3", "T3", 2).Id);
        Assert.Equal($"docs/adr/0038.md@{CommitA}", TraceRef.Document("docs\\adr\\0038.md", CommitA).Id);
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceRef.Attempt("r", "T", 0));
        Assert.Throws<ArgumentException>(() => TraceRef.Document("../secret.md", CommitA));
        Assert.Throws<ArgumentException>(() => TraceRef.Document("/abs.md", CommitA));
        Assert.Throws<ArgumentException>(() => TraceRef.Document("docs/a.md", "abc"));
        Assert.Throws<ArgumentException>(() => new TraceRef(TraceNodeKind.Document, "sin-arroba"));
    }

    [Fact]
    public void A_node_has_an_id_on_one_line_without_spaces_at_its_ends()
    {
        Assert.Throws<ArgumentException>(() => TraceRef.Requirement(" "));
        Assert.Throws<ArgumentException>(() => TraceRef.Requirement("a\nb"));
        Assert.Throws<ArgumentException>(() => TraceRef.Requirement(" a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceRef((TraceNodeKind)99, "x"));
    }

    [Fact]
    public void The_kinds_of_node_are_the_documented_ones_in_order() =>
        Assert.Equal(
            ["Requirement", "BacklogItem", "Milestone", "Run", "Task", "Attempt", "Commit", "Evidence", "Verdict", "Debate", "Audit", "Document", "PullRequest"],
            Enum.GetNames<TraceNodeKind>());

    // ---- links ----

    [Fact]
    public void A_link_has_two_different_ends_and_a_reason_and_its_id_follows_its_content()
    {
        Assert.Contains("trace.self-link", Link(Req, Req).Validate());
        Assert.Contains("trace.reason", (Link(Req, Commit) with { Reason = " " }).Validate());
        var link = Link(Commit, Req);

        Assert.Empty(link.Validate());
        Assert.Equal(link.Id, (link with { At = T0.AddDays(1), Reason = "otra" }).Id);
        Assert.NotEqual(link.Id, Link(Req, Commit).Id);
        Assert.NotEqual(link.Id, Link(Commit, Req, TraceAssertedBy.System).Id);
        Assert.True(ContentHash.IsSha256Hex(link.Id));
    }

    [Fact]
    public void Links_are_read_in_both_directions_many_to_many()
    {
        var set = new TraceLinkSet();
        var other = TraceRef.Requirement("RQ-02");
        set.Record(Link(Commit, Req));
        set.Record(Link(Commit, other, at: T0.AddMinutes(1)));
        set.Record(Link(Milestone, Req, at: T0.AddMinutes(2)));

        Assert.Equal([Req, other], set.Neighbours(Commit));
        Assert.Equal([Commit, Milestone], set.Neighbours(Req));
        Assert.Equal(2, set.From(Commit).Count);
        Assert.Equal(2, set.To(Req).Count);
        Assert.Equal(3, set.Active.Count);
    }

    [Fact]
    public void Recording_the_same_link_twice_is_one_and_a_retracted_link_stays_in_the_history()
    {
        var set = new TraceLinkSet();
        var link = Link(Commit, Req);

        Assert.True(set.Record(link));
        Assert.False(set.Record(link with { At = T0.AddMinutes(1) }));
        set.Retract(link.Id, T0.AddMinutes(2), "manuel", "era otro");

        Assert.Empty(set.Active);
        Assert.Equal("era otro", Assert.Single(set.Retracted).Reason);
        Assert.Equal("trace.link-not-active", Assert.Throws<DomainRuleViolationException>(() => set.Retract(link.Id, T0.AddMinutes(3), "manuel", "x")).Rule);
        Assert.True(set.Record(link with { At = T0.AddMinutes(4) }));
        Assert.Single(set.Active);
        Assert.Single(set.Retracted);
    }

    [Fact]
    public void Replaying_the_trace_reports_a_retraction_of_a_link_that_was_never_in_force()
    {
        var link = Link(Commit, Req);

        var good = TraceLinkReplayer.Replay([new LinkRecorded(link), new LinkRetracted(T0.AddMinutes(1), link.Id, "manuel", "x")]);
        var bad = TraceLinkReplayer.Replay([new LinkRetracted(T0.AddMinutes(1), link.Id, "manuel", "x")]);

        Assert.True(good.IsConsistent);
        Assert.Empty(good.Links.Active);
        Assert.Contains("trace.link-not-active", Assert.Single(bad.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_suggestion_counts_for_nothing_until_a_person_confirms_it_and_then_it_is_theirs()
    {
        var suggestion = new TraceSuggestion(Commit, Req, TraceSuggestionSource.CommitMessageMention, "Cumple RQ-01\n(T3)");

        var link = suggestion.Confirm(T0);

        Assert.Equal(TraceAssertedBy.User, link.AssertedBy);
        Assert.Equal("confirmed: CommitMessageMention: Cumple RQ-01(T3)", link.Reason);
        Assert.Empty(link.Validate());
        Assert.Empty(new TraceLinkSet().Active);
        Assert.True((suggestion with { InferredFrom = new string('x', 900) }).Confirm(T0).Validate().Count == 0);
    }

    // ---- trailers ----

    private static readonly RunId Run = new("20261015-a1b2c3");

    [Fact]
    public void The_trailers_of_a_commit_of_the_system_are_written_in_one_fixed_form_and_read_back()
    {
        var block = CommitTrailers.Format(Run, Tid("T3"), 2, Aid("ejecutor-1"), "claude-sonnet-5-5");

        Assert.Equal(
            "Agent-Studio-Run: 20261015-a1b2c3\nAgent-Studio-Task: T3\nAgent-Studio-Attempt: 2\nAgent-Studio-Agent: ejecutor-1; model=claude-sonnet-5-5",
            block);
        var read = CommitTrailers.Read(CommitTrailers.Append("T3: añade Multiply\n\nCuerpo.", Run, Tid("T3"), 2, Aid("ejecutor-1"), "claude-sonnet-5-5"));
        Assert.Equal(new SystemCommitTrailers(Run, Tid("T3"), 2, Aid("ejecutor-1"), "claude-sonnet-5-5"), read);
        Assert.Null(CommitTrailers.Read(CommitTrailers.Append("x", Run, Tid("T3"), 1, Aid("a"), null))!.Model);
    }

    [Fact]
    public void A_value_with_a_line_break_cannot_add_a_trailer()
    {
        Assert.Throws<ArgumentException>(() => CommitTrailers.Format(Run, Tid("T3"), 1, Aid("a\nAgent-Studio-Run: x"), null));
        Assert.Throws<ArgumentException>(() => CommitTrailers.Format(Run, Tid("T3"), 1, Aid("a"), "m\r\nx: y"));
        Assert.Throws<ArgumentException>(() => CommitTrailers.Format(Run, Tid("T3"), 1, Aid("a; model=b"), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => CommitTrailers.Format(Run, Tid("T3"), 0, Aid("a"), null));
    }

    [Fact]
    public void A_message_that_does_not_end_in_a_full_block_of_ours_reads_as_none()
    {
        const string Full = "Agent-Studio-Run: r1\nAgent-Studio-Task: T1\nAgent-Studio-Attempt: 1\nAgent-Studio-Agent: a";

        Assert.NotNull(CommitTrailers.Read("asunto\n\n" + Full));
        Assert.NotNull(CommitTrailers.Read("asunto\r\n\r\n" + Full.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n"));
        Assert.Null(CommitTrailers.Read(null));
        Assert.Null(CommitTrailers.Read(Full));
        Assert.Null(CommitTrailers.Read("asunto\n\n" + Full + "\nAgent-Studio-Run: r2"));
        Assert.Null(CommitTrailers.Read("asunto\n\n" + Full.Replace("Attempt: 1", "Attempt: uno", StringComparison.Ordinal)));
        Assert.Null(CommitTrailers.Read("asunto\n\n" + Full.Replace("Attempt: 1", "Attempt: 0", StringComparison.Ordinal)));
        Assert.Null(CommitTrailers.Read("asunto\n\n" + Full.Replace("Run: r1", "Run: r 1", StringComparison.Ordinal)));
        Assert.Null(CommitTrailers.Read("asunto\n\n" + Full.Replace("Agent-Studio-Task: T1\n", string.Empty, StringComparison.Ordinal)));
        Assert.Null(CommitTrailers.Read("asunto\n\n" + Full + "\n\ntexto después"));
        Assert.Null(CommitTrailers.Read("asunto\n\nuna frase sin trailer\n" + Full));
        Assert.NotNull(CommitTrailers.Read("asunto\n\n" + Full + "\nSigned-off-by: yo"));
    }

    // ---- activity ----

    [Fact]
    public void An_executor_is_classified_by_the_paths_of_its_diff()
    {
        Assert.Equal(ActivityKind.Implementation, ActivityPaths.OfDiff(["src/Calc/Calc.cs", "tests/CalcTests.cs"]));
        Assert.Equal(ActivityKind.Implementation, ActivityPaths.OfDiff(["src/Calc/Calc.cs", "docs/a.md"]));
        Assert.Equal(ActivityKind.Tests, ActivityPaths.OfDiff(["tests/CalcTests.cs", "tests/Util/Fake.cs"]));
        Assert.Equal(ActivityKind.Documentation, ActivityPaths.OfDiff(["docs/a.cs", "README.md", "src/notas.MD"]));
        Assert.Equal(ActivityKind.Tests, ActivityPaths.OfDiff(["tests/a.cs", "docs/a.md"]));
        Assert.Equal(ActivityKind.Implementation, ActivityPaths.OfDiff([]));
        Assert.Equal(ActivityKind.Implementation, ActivityPaths.OfDiff([".agentstudio/x.cs"]));
    }

    [Fact]
    public void The_paths_of_the_profile_replace_the_default_ones_for_the_tests()
    {
        Assert.Equal(ActivityKind.Tests, ActivityPaths.OfDiff(["spec/a.rb"], ["spec/"]));
        Assert.Equal(ActivityKind.Implementation, ActivityPaths.OfDiff(["tests/a.cs"], ["spec/"]));
        Assert.Equal(ActivityKind.Tests, ActivityPaths.OfDiff(["tests/a.cs"], []));
    }

    [Fact]
    public void The_three_axes_have_their_documented_values()
    {
        Assert.Equal(["Coordination", "Analysis", "Implementation", "Tests", "Documentation", "Review", "Conflicts"], Enum.GetNames<ActivityKind>());
        Assert.Equal(["First", "Retry"], Enum.GetNames<AttemptOrder>());
        Assert.Equal(["Kept", "Discarded", "Pending"], Enum.GetNames<AttemptFate>());
    }

    [Fact]
    public void An_actual_figure_is_compared_with_the_interval_of_the_estimate_and_never_with_a_missing_one()
    {
        Assert.Equal(EstimateDeviation.Within, EstimateComparison.Compare<long>(10, 20, 10));
        Assert.Equal(EstimateDeviation.Within, EstimateComparison.Compare<long>(10, 20, 20));
        Assert.Equal(EstimateDeviation.Below, EstimateComparison.Compare<long>(10, 20, 9));
        Assert.Equal(EstimateDeviation.Above, EstimateComparison.Compare<TimeSpan>(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));
        Assert.Equal(EstimateDeviation.Unknown, EstimateComparison.Compare<long>(null, 20, 10));
        Assert.Equal(EstimateDeviation.Unknown, EstimateComparison.Compare<long>(10, 20, null));
        Assert.Equal(EstimateDeviation.Unknown, EstimateComparison.Compare<long>(20, 10, 15));
    }
}
