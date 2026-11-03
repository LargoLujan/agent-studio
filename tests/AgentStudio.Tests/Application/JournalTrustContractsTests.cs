using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The contracts of the journal as memory (docs/17 D-02): the source of a journal entry, and the decision «Confiar en la
/// bitácora de este proyecto», by project or by day, which is an entry of the log outside every run.
/// </summary>
public sealed class JournalTrustContractsTests
{
    private static readonly string Hash = ContentHash.Sha256Hex("- **11:42:07** · P-0007 · Backend");

    [Fact]
    public void A_journal_entry_cites_its_day_and_the_hash_of_its_text()
    {
        Assert.Equal($"bitacora:2026-10-30#sha256:{Hash}", MemorySources.Journal(new DateOnly(2026, 10, 30), Hash));
        Assert.StartsWith(MemorySources.JournalPrefix, MemorySources.Journal(new DateOnly(2026, 1, 2), Hash), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => MemorySources.Journal(new DateOnly(2026, 10, 30), "abc"));
        Assert.Throws<ArgumentException>(() => MemorySources.Journal(new DateOnly(2026, 10, 30), Hash.ToUpperInvariant()));
    }

    [Fact]
    public void Trusting_the_journal_is_a_decision_outside_every_run_by_project_or_by_day()
    {
        var project = JournalTrustChangedPayload.Deciding(JournalTrustScope.Project, null, trusted: true, "manuel", "Son mis prompts.");
        var day = JournalTrustChangedPayload.Deciding(JournalTrustScope.Day, new DateOnly(2026, 10, 29), trusted: false, "manuel", null);

        Assert.Equal(DecisionPayload.NoRun, project.RunId);
        Assert.Equal(DecisionPayload.NoRun, day.RunId);
        Assert.Equal(typeof(JournalTrustChangedPayload), DecisionPayloadSerializer.PayloadTypeFor(DecisionKind.JournalTrustChanged));

        var json = DecisionPayloadSerializer.Serialize(day);
        Assert.Equal("{\"scope\":\"Day\",\"day\":\"2026-10-29\",\"trusted\":false,\"decidedBy\":\"manuel\",\"v\":1,\"runId\":\"\"}", json);
        Assert.Equal(day, DecisionPayloadSerializer.Deserialize(DecisionKind.JournalTrustChanged, json));
        Assert.Equal(project, DecisionPayloadSerializer.Deserialize<JournalTrustChangedPayload>(DecisionPayloadSerializer.Serialize(project)));
    }

    [Fact]
    public void A_decision_about_a_day_names_it_and_one_about_the_project_does_not()
    {
        Assert.Throws<ArgumentException>(() => JournalTrustChangedPayload.Deciding(JournalTrustScope.Day, null, true, "manuel", null));
        Assert.Throws<ArgumentException>(() => JournalTrustChangedPayload.Deciding(JournalTrustScope.Project, new DateOnly(2026, 10, 29), true, "manuel", null));
        Assert.Throws<ArgumentException>(() => JournalTrustChangedPayload.Deciding(JournalTrustScope.Project, null, true, " ", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => JournalTrustChangedPayload.Deciding((JournalTrustScope)5, null, true, "manuel", null));
    }
}
