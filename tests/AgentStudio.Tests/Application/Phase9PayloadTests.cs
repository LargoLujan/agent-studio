using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Work;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Domain.Work.Milestones;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

/// <summary>
/// docs/16 section 4.6 (P0 of Phase 9): the entries of milestones (D-17), of the backlog (D-18) and of the links of the trace (D-29).
/// The log hashes the text of every payload, so each is pinned here as text and read back byte for byte, and the hash of a milestone
/// header is computed again by an independent implementation of the canonical form.
/// </summary>
public sealed class Phase9PayloadTests
{
    private static readonly MilestoneId Mil = new("mil-20261115-a1b2c3");

    private static readonly BacklogItem Item = new(
        new BacklogItemId("bk-20261115-000001"), 2, "Sumar", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Pasa", VerificationMethod.AutomatedTest)], T0);

    private static readonly TraceLink Link = new(TraceRef.Commit(CommitA), TraceRef.Requirement("RQ-01"), TraceAssertedBy.User, "cumple", T0);

    internal static readonly DecisionPayload[] Samples =
    [
        MilestoneOpenedPayload.Opening(Mil, HashA, "Calculadora", HashB, new WorkBudget(500000, null), "manuel"),
        MilestoneRequirementsRevisedPayload.Revising(Mil, HashB, HashA, "manuel", null),
        MilestoneLinkedPayload.Linking(Mil, MilestoneTargetKind.Run, "20261015-a1b2c3", "agent-studio"),
        MilestoneClosedPayload.Closing(Mil, 1, "aud-20261120-a1b2c3", Eid("ev-report"), HashA, CommitA, "manuel"),
        MilestoneReopenedPayload.Reopening(Mil, MilestoneReopenReason.Revert, Eid("ev-revert"), HashB, "manuel", "Se revirtió el merge"),
        BacklogItemRecordedPayload.Announcing(Item),
        BacklogItemRetiredPayload.Retiring(Item.Id, 2, "manuel", null),
        BacklogOrderChangedPayload.Ordering([Item.Id, new BacklogItemId("bk-20261115-000002")], "manuel"),
        TraceLinkRecordedPayload.Recording(Link),
        TraceLinkRetractedPayload.Retracting(Link.Id, "manuel", "era otro commit"),
    ];

    private static string Json(DecisionPayload payload) => DecisionPayloadSerializer.Serialize(payload);

    private static void RoundTrips(DecisionPayload payload, string expected, DecisionKind kind)
    {
        var json = Json(payload);
        Assert.Equal(expected, json);
        var read = DecisionPayloadSerializer.Deserialize(payload.GetType(), json);
        Assert.Equal(json, Json(read));
        Assert.Equal(payload.GetType(), DecisionPayloadSerializer.PayloadTypeFor(kind));
    }

    [Fact]
    public void A_milestone_opening_is_written_in_one_fixed_text_with_the_id_of_the_milestone_as_its_run()
    {
        RoundTrips(
            Samples[0],
            $$"""{"headerHash":"{{HashA}}","name":"Calculadora","requirementSetHash":"{{HashB}}","budget":{"maxTokens":500000,"period":"Total"},"openedBy":"manuel","v":1,"runId":"mil-20261115-a1b2c3"}""",
            DecisionKind.MilestoneOpened);
        Assert.Equal("mil-20261115-a1b2c3", Samples[0].RunId);
        Assert.DoesNotContain("budget", Json(MilestoneOpenedPayload.Opening(Mil, HashA, "x", HashB, null, "manuel")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_revision_the_link_the_close_and_the_reopening_are_written_in_one_fixed_text()
    {
        RoundTrips(Samples[1], $$"""{"requirementSetHash":"{{HashB}}","previousRequirementSetHash":"{{HashA}}","revisedBy":"manuel","v":1,"runId":"mil-20261115-a1b2c3"}""", DecisionKind.MilestoneRequirementsRevised);
        RoundTrips(Samples[2], """{"kind":"Run","target":"20261015-a1b2c3","linkedBy":"agent-studio","v":1,"runId":"mil-20261115-a1b2c3"}""", DecisionKind.MilestoneLinked);
        RoundTrips(Samples[3], $$"""{"revision":1,"audit":"aud-20261120-a1b2c3","report":"ev-report","reportSha256":"{{HashA}}","integratedCommit":"{{CommitA}}","closedBy":"manuel","v":1,"runId":"mil-20261115-a1b2c3"}""", DecisionKind.MilestoneClosed);
        RoundTrips(Samples[4], $$"""{"reason":"Revert","evidence":"ev-revert","evidenceSha256":"{{HashB}}","reopenedBy":"manuel","note":"Se revirtió el merge","v":1,"runId":"mil-20261115-a1b2c3"}""", DecisionKind.MilestoneReopened);
    }

    [Fact]
    public void The_entries_of_the_backlog_and_the_links_belong_to_no_run_and_carry_no_text_of_the_item()
    {
        RoundTrips(Samples[5], $$"""{"item":"bk-20261115-000001","version":2,"priority":"Mandatory","sha256":"{{Item.Sha256()}}","v":1,"runId":""}""", DecisionKind.BacklogItemRecorded);
        RoundTrips(Samples[6], """{"item":"bk-20261115-000001","version":2,"decidedBy":"manuel","v":1,"runId":""}""", DecisionKind.BacklogItemRetired);
        RoundTrips(Samples[7], """{"items":["bk-20261115-000001","bk-20261115-000002"],"decidedBy":"manuel","v":1,"runId":""}""", DecisionKind.BacklogOrderChanged);
        RoundTrips(
            Samples[8],
            $$"""{"link":"{{Link.Id}}","fromKind":"Commit","from":"{{CommitA}}","toKind":"Requirement","to":"RQ-01","assertedBy":"User","reason":"cumple","v":1,"runId":""}""",
            DecisionKind.TraceLinkRecorded);
        RoundTrips(Samples[9], $$"""{"link":"{{Link.Id}}","retractedBy":"manuel","reason":"era otro commit","v":1,"runId":""}""", DecisionKind.TraceLinkRetracted);
        Assert.All(Samples[5..], s => Assert.Equal(DecisionPayload.NoRun, s.RunId));
        Assert.DoesNotContain("Sumar", Json(Samples[5]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_confirmed_suggestion_names_its_source_and_only_a_person_confirms()
    {
        var link = new TraceSuggestion(Link.From, Link.To, TraceSuggestionSource.CommitTrailer, "Agent-Studio-Run").Confirm(T0);

        var payload = TraceLinkRecordedPayload.Recording(link, TraceSuggestionSource.CommitTrailer);

        Assert.Contains("\"suggestion\":\"CommitTrailer\"", Json(payload), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => TraceLinkRecordedPayload.Recording(link with { AssertedBy = TraceAssertedBy.System }, TraceSuggestionSource.CommitTrailer));
    }

    [Fact]
    public void A_link_is_read_back_from_its_entry_and_an_edited_entry_is_refused()
    {
        var read = DecisionPayloadSerializer.Deserialize<TraceLinkRecordedPayload>(Json(Samples[8])).ToLink(T0);

        Assert.Equal(Link, read);
        Assert.Throws<ArgumentException>(() => (DecisionPayloadSerializer.Deserialize<TraceLinkRecordedPayload>(Json(Samples[8])) with { To = "RQ-02" }).ToLink(T0));
        Assert.Throws<ArgumentException>(() => (DecisionPayloadSerializer.Deserialize<TraceLinkRecordedPayload>(Json(Samples[8])) with { From = "abc" }).ToLink(T0));
    }

    [Fact]
    public void The_payloads_refuse_what_the_domain_refuses()
    {
        Assert.Throws<ArgumentException>(() => MilestoneOpenedPayload.Opening(Mil, "x", "n", HashB, null, "m"));
        Assert.Throws<ArgumentException>(() => MilestoneOpenedPayload.Opening(Mil, HashA, " ", HashB, null, "m"));
        Assert.Throws<AgentStudio.Domain.Common.DomainRuleViolationException>(() => MilestoneOpenedPayload.Opening(Mil, HashA, "n", HashB, new WorkBudget(null, null), "m"));
        Assert.Throws<ArgumentException>(() => MilestoneRequirementsRevisedPayload.Revising(Mil, HashA, HashA, "m", null));
        Assert.Throws<ArgumentException>(() => MilestoneReopenedPayload.Reopening(Mil, MilestoneReopenReason.CheckFailed, null, null, "m", null));
        Assert.Throws<ArgumentException>(() => MilestoneReopenedPayload.Reopening(Mil, MilestoneReopenReason.ByPerson, Eid("e"), null, "m", null));
        Assert.Equal(MilestoneReopenReason.ByPerson, MilestoneReopenedPayload.Reopening(Mil, MilestoneReopenReason.ByPerson, null, null, "m", null).Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() => MilestoneClosedPayload.Closing(Mil, 0, "aud", Eid("e"), HashA, null, "m"));
        Assert.Throws<ArgumentException>(() => BacklogItemRecordedPayload.Announcing(Item with { Statement = "" }));
        Assert.Throws<ArgumentException>(() => BacklogOrderChangedPayload.Ordering([Item.Id, Item.Id], "m"));
        Assert.Throws<ArgumentException>(() => TraceLinkRetractedPayload.Retracting("abc", "m", "r"));
    }

    [Fact]
    public void The_hash_of_a_milestone_header_is_the_canonical_form_of_its_fields()
    {
        var header = new MilestoneHeader(Mil, "Calculadora", "Una calculadora", HashA, new WorkBudget(1000, null), T0);
        var without = header with { Budget = null };

        Assert.Equal(Independent(header, "budget", "=1000", "-", "Total"), MilestoneHeaderHash.Of(header));
        Assert.Equal(Independent(without, "-"), MilestoneHeaderHash.Of(without));
        Assert.NotEqual(MilestoneHeaderHash.Of(header), MilestoneHeaderHash.Of(header with { Objective = "Otra" }));
        Assert.NotEqual(MilestoneHeaderHash.Of(header), MilestoneHeaderHash.Of(header with { Budget = new WorkBudget(2000, null) }));
        Assert.NotEqual(MilestoneHeaderHash.Of(header with { Budget = new WorkBudget(null, 5) }), MilestoneHeaderHash.Of(header with { Budget = new WorkBudget(5, null) }));
        Assert.Equal(MilestoneHeaderHash.Of(header), MilestoneHeaderHash.Of(header with { Id = new MilestoneId("mil-otro") }));
    }

    private static string Independent(MilestoneHeader header, params string[] budget)
    {
        var fields = new List<string> { "milestone-header/1", header.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), header.Name, header.Objective, header.RequirementSetHash };
        fields.AddRange(budget);
        var text = string.Concat(fields.Select(f => f.Length.ToString(CultureInfo.InvariantCulture) + ":" + f + ";"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The entry of a milestone opening hashes as the canonical form of the log says, and a chain with it verifies.</summary>
    [Fact]
    public void An_entry_of_a_milestone_hashes_as_the_log_says_and_the_chain_verifies()
    {
        var log = new DecisionLog();
        var actor = new Actor(ActorKind.User, "manuel");

        var opened = log.Append(T0, actor, DecisionKind.MilestoneOpened, "Abre el hito Calculadora", Json(Samples[0]));
        var linked = log.Append(T0.AddMinutes(1), new Actor(ActorKind.System, "agent-studio"), DecisionKind.MilestoneLinked, "Vincula la ejecución", Json(Samples[2]));

        var canonical = string.Concat(new[] { "1", T0.ToString("O", CultureInfo.InvariantCulture), "User", "manuel", "MilestoneOpened", "Abre el hito Calculadora", Json(Samples[0]), string.Empty, DecisionLog.GenesisHash }
            .Select(f => f.Length.ToString(CultureInfo.InvariantCulture) + ":" + f + ";"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), opened.Hash);
        Assert.Equal(opened.Hash, linked.PreviousHash);
        Assert.True(DecisionLog.Load(log.Entries).Verify().IsValid);
    }

    [Fact]
    public void The_kinds_of_phase_9_have_their_payload_type()
    {
        string[] kinds = ["MilestoneOpened", "MilestoneRequirementsRevised", "MilestoneLinked", "MilestoneClosed", "MilestoneReopened", "BacklogItemRecorded", "BacklogItemRetired", "BacklogOrderChanged", "TraceLinkRecorded", "TraceLinkRetracted"];
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = Enum.Parse<DecisionKind>(kinds[i]);
            Assert.Equal(Samples[i].GetType(), DecisionPayloadSerializer.PayloadTypeFor(kind));
        }
    }
}
