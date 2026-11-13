using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Graph;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/20 §4.3 and §5 (P0 of Phase 6, ADR-0061 and ADR-0062): <c>DoctorFixApplied</c> and <c>GraphRelationsProposed</c>. The log hashes the text of every
/// payload, so each is pinned here as text and read back byte for byte. Neither carries text of a person or of an agent, a path of the machine or
/// anything of a token: the reason an agent gave for a relation stays in the evidence of the proposal.
/// </summary>
public sealed class Phase6PayloadTests
{
    private const string Commit = "9c4da5d1f2e3a4b5c6d7e8f9a0b1c2d3e4f5a6b7";

    private static readonly ProposedRelation UsesDomain = new("src/App/App.csproj", "src/Domain/Domain.csproj", GraphEdgeKind.ProjectReference);

    private static readonly ProposedRelation SharesNamespace = new("src/App/App.csproj::A.App", "src/Domain/Domain.csproj::A.Domain", GraphEdgeKind.NamespaceUse);

    private static readonly DoctorFixAppliedPayload Applied = DoctorFixAppliedPayload.Applying("doctor.fix.regenerate-diagrams", "doc.diagram-stale");

    private static readonly GraphRelationsProposedPayload Proposed = GraphRelationsProposedPayload.Proposing(
        Commit, HashA, Eid("ev-proposal"), [UsesDomain, SharesNamespace], 3, "analista", "claude-sonnet-5");

    /// <summary>One sample of every payload type Phase 6 adds; <see cref="DecisionPayloadTests"/> adds them to the samples of every payload.</summary>
    internal static readonly DecisionPayload[] Samples = [Applied, Proposed];

    // ---- DoctorFixApplied ----

    [Fact]
    public void A_fix_applied_is_written_in_one_fixed_text_outside_every_run_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(Applied);

        Assert.Equal("{\"fixCode\":\"doctor.fix.regenerate-diagrams\",\"checkCode\":\"doc.diagram-stale\",\"v\":1,\"runId\":\"\"}", json);
        var read = DecisionPayloadSerializer.Deserialize<DoctorFixAppliedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(Applied, read);
        Assert.Null(read.Undoes);
        Assert.Equal(DecisionPayload.NoRun, Applied.RunId);
        Assert.Equal(typeof(DoctorFixAppliedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.DoctorFixApplied));
    }

    [Fact]
    public void An_undo_is_another_entry_that_points_to_the_first_by_its_sequence()
    {
        var undo = DoctorFixAppliedPayload.Undoing("doctor.fix.regenerate-diagrams", "doc.diagram-stale", 41);

        var json = DecisionPayloadSerializer.Serialize(undo);

        Assert.Equal("{\"fixCode\":\"doctor.fix.regenerate-diagrams\",\"checkCode\":\"doc.diagram-stale\",\"undoes\":41,\"v\":1,\"runId\":\"\"}", json);
        Assert.Equal(41, DecisionPayloadSerializer.Deserialize<DoctorFixAppliedPayload>(json).Undoes);
        Assert.Equal(DecisionPayload.NoRun, undo.RunId);
    }

    [Theory]
    [InlineData("", "doctor.x")]
    [InlineData("doctor.x", "")]
    [InlineData("Doctor.X", "doctor.x")]
    [InlineData("doctor.x", "doctor x")]
    [InlineData("doctor.x\n", "doctor.x")]
    [InlineData("C:\\tools\\fix", "doctor.x")]
    [InlineData("doctor.x", "doctor..x")]
    public void A_fix_and_a_check_are_named_by_their_codes_and_by_nothing_else(string fixCode, string checkCode)
    {
        Assert.Throws<ArgumentException>(() => DoctorFixAppliedPayload.Applying(fixCode, checkCode));
        Assert.Throws<ArgumentException>(() => DoctorFixAppliedPayload.Undoing(fixCode, checkCode, 1));
    }

    [Fact]
    public void An_undo_points_to_a_sequence_from_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DoctorFixAppliedPayload.Undoing("doctor.x", "doctor.y", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DoctorFixAppliedPayload.Undoing("doctor.x", "doctor.y", -3));
    }

    // ---- GraphRelationsProposed ----

    [Fact]
    public void A_proposal_is_written_in_one_fixed_text_outside_every_run_and_read_back_byte_for_byte()
    {
        var json = DecisionPayloadSerializer.Serialize(Proposed);

        Assert.Equal(
            $$"""{"commit":"{{Commit}}","modelHash":"{{HashA}}","proposal":"ev-proposal","accepted":[{"from":"src/App/App.csproj","to":"src/Domain/Domain.csproj","kind":"ProjectReference"},{"from":"src/App/App.csproj::A.App","to":"src/Domain/Domain.csproj::A.Domain","kind":"NamespaceUse"}],"discarded":3,"agent":"analista","model":"claude-sonnet-5","v":1,"runId":""}""",
            json);
        var read = DecisionPayloadSerializer.Deserialize<GraphRelationsProposedPayload>(json);
        Assert.Equal(json, DecisionPayloadSerializer.Serialize(read));
        Assert.Equal(2, read.Accepted.Count);
        Assert.Equal(UsesDomain, read.Accepted[0]);
        Assert.Equal(Eid("ev-proposal"), read.Proposal);
        Assert.Equal(DecisionPayload.NoRun, Proposed.RunId);
        Assert.Equal(typeof(GraphRelationsProposedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.GraphRelationsProposed));
    }

    [Fact]
    public void A_relation_carries_its_ends_and_its_kind_and_no_text_an_agent_wrote()
    {
        Assert.Equal(["From", "To", "Kind"], typeof(ProposedRelation).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"));
        Assert.DoesNotContain("reason", DecisionPayloadSerializer.Serialize(Proposed), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_relation_is_between_two_different_nodes_of_one_line()
    {
        Assert.Throws<ArgumentException>(() => new ProposedRelation("a", "a", GraphEdgeKind.NamespaceUse));
        Assert.Throws<ArgumentException>(() => new ProposedRelation("a\nb", "c", GraphEdgeKind.NamespaceUse));
        Assert.Throws<ArgumentException>(() => new ProposedRelation("a", " ", GraphEdgeKind.NamespaceUse));
        Assert.Throws<ArgumentException>(() => new ProposedRelation(new string('a', GraphNode.MaxIdLength + 1), "b", GraphEdgeKind.NamespaceUse));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProposedRelation("a", "b", (GraphEdgeKind)99));
    }

    [Fact]
    public void A_proposal_names_a_full_commit_and_the_fingerprint_of_the_model_it_refers_to()
    {
        ProposedRelation[] one = [UsesDomain];

        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing("abc1234", HashA, Eid("e"), one, 0, "a", "m"));
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit.ToUpperInvariant(), HashA, Eid("e"), one, 0, "a", "m"));
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, "abc", Eid("e"), one, 0, "a", "m"));
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA.ToUpperInvariant(), Eid("e"), one, 0, "a", "m"));
        Assert.Equal(Commit, GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), one, 0, "a", "m").Commit);
    }

    [Fact]
    public void A_proposal_holds_one_to_fifty_relations_between_those_accepted_and_those_discarded()
    {
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), [], 0, "a", "m"));
        Assert.Equal(5, GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), [], 5, "a", "m").Discarded);
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), [UsesDomain], -1, "a", "m"));

        var fifty = Enumerable.Range(0, GraphRelationsProposedPayload.MaxRelations).Select(i => new ProposedRelation($"n{i}", $"n{i + 1}", GraphEdgeKind.NamespaceUse)).ToList();
        Assert.Equal(50, GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), fifty, 0, "a", "m").Accepted.Count);
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), fifty, 1, "a", "m"));
    }

    [Fact]
    public void A_relation_is_accepted_once_and_never_null()
    {
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), [UsesDomain, new ProposedRelation(UsesDomain.From, UsesDomain.To, UsesDomain.Kind)], 0, "a", "m"));
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), [null!], 0, "a", "m"));
        Assert.Throws<ArgumentNullException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), null!, 0, "a", "m"));
    }

    [Theory]
    [InlineData("", "m")]
    [InlineData("a", " ")]
    [InlineData(" a", "m")]
    [InlineData("a\nb", "m")]
    [InlineData("a", "m\tx")]
    public void The_agent_and_the_model_are_names_of_one_line(string agent, string model)
    {
        Assert.Throws<ArgumentException>(() => GraphRelationsProposedPayload.Proposing(Commit, HashA, Eid("e"), [UsesDomain], 0, agent, model));
    }

    [Fact]
    public void Reading_a_proposal_with_a_relation_that_breaks_a_rule_fails()
    {
        var json = DecisionPayloadSerializer.Serialize(Proposed).Replace("\"from\":\"src/App/App.csproj\",\"to\":\"src/Domain/Domain.csproj\"", "\"from\":\"x\",\"to\":\"x\"", StringComparison.Ordinal);

        Assert.ThrowsAny<Exception>(() => DecisionPayloadSerializer.Deserialize<GraphRelationsProposedPayload>(json));
    }

    // ---- the whole set ----

    [Fact]
    public void Both_payloads_are_in_the_samples_and_both_round_trip_and_serialize_the_same_text_every_time()
    {
        Assert.Equal(2, Samples.Length);
        foreach (var sample in Samples)
        {
            var json = DecisionPayloadSerializer.Serialize(sample);
            Assert.Equal(json, DecisionPayloadSerializer.Serialize(sample));
            Assert.Equal(json, DecisionPayloadSerializer.Serialize(DecisionPayloadSerializer.Deserialize(sample.GetType(), json)));
        }
    }

    [Fact]
    public void A_log_accepts_both_payloads_and_the_hash_of_the_entry_covers_their_text()
    {
        var actor = new Actor(ActorKind.User, "user");
        var log = new DecisionLog();

        var applied = log.Append(T0, actor, DecisionKind.DoctorFixApplied, "fix", DecisionPayloadSerializer.Serialize(Applied));
        var proposed = log.Append(T0.AddMinutes(1), actor, DecisionKind.GraphRelationsProposed, "relations", DecisionPayloadSerializer.Serialize(Proposed));
        var other = new DecisionLog().Append(T0, actor, DecisionKind.DoctorFixApplied, "fix", DecisionPayloadSerializer.Serialize(DoctorFixAppliedPayload.Applying("doctor.fix.other", "doc.diagram-stale")));

        Assert.NotEqual(applied.Hash, other.Hash);
        Assert.NotEqual(applied.Hash, proposed.Hash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }
}
