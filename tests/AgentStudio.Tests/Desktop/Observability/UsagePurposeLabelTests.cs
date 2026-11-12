using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Common;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.Observability;

/// <summary>
/// The labels of the purposes Phase 5 appends (docs/15 D-44): the projection groups the turns of a debate and the judge of
/// an audit by their run id (<c>deb-…</c>, <c>aud-…</c>) without change, and the panel only gains their Spanish names.
/// </summary>
public sealed class UsagePurposeLabelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 10, 0, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(AgentRunPurpose.DebateParticipant, "deb-20261019-a1b2c3", "r1-arquitecto", "Participante de debate")]
    [InlineData(AgentRunPurpose.DebateJudge, "deb-20261019-a1b2c3", "juez-revision", "Juez de debate")]
    [InlineData(AgentRunPurpose.ProjectAudit, "aud-20261019-0f9e8d", "juez-revision", "Auditoría del proyecto")]
    public async Task The_purposes_of_phase_5_are_named_in_spanish_in_each_attempt(AgentRunPurpose purpose, string run, string task, string label)
    {
        var attempt = UsageSamples.ClaudeAttempt with { RunId = run, TaskId = task, Purpose = purpose, Agent = new AgentId("arquitecto") };
        var dashboard = Dashboard(UsageSamples.Empty with { Attempts = [attempt] });

        await dashboard.SetProjectAsync(@"C:\proyectos\calc");

        Assert.Equal($"{run} · {task} · intento 1 · {label} · arquitecto", Assert.Single(dashboard.Attempts).Who);
    }

    /// <summary>The purposes Phase 14 appends (docs/17 §8.2): the help and the draft of a document for clients, named from the resources, in each language.</summary>
    [Theory]
    [InlineData(AgentRunPurpose.Help, "aux-20261111-a1b2c3", "ayuda", "Ayuda", "Help")]
    [InlineData(AgentRunPurpose.ClientDocumentDraft, "aux-20261111-d4e5f6", "borrador", "Borrador del documento para clientes", "Draft of the document for clients")]
    [InlineData(AgentRunPurpose.GraphRelations, "aux-20261112-a1b2c3", "relaciones-grafo", "Relaciones propuestas del grafo", "Relations proposed for the graph")]
    public async Task The_purposes_of_phase_14_are_named_from_the_resources_in_each_attempt(AgentRunPurpose purpose, string run, string agent, string spanish, string english)
    {
        var attempt = UsageSamples.ClaudeAttempt with { RunId = run, TaskId = null, Purpose = purpose, Agent = new AgentId(agent) };
        var dashboard = Dashboard(UsageSamples.Empty with { Attempts = [attempt] });

        await dashboard.SetProjectAsync(@"C:\proyectos\calc");

        Assert.Equal($"{run} · intento 1 · {spanish} · {agent}", Assert.Single(dashboard.Attempts).Who);
        Assert.Equal(spanish, Texts.Get($"Usage_Purpose_{purpose}"));
        Assert.Equal(english, new ResourceTextProvider(CultureInfo.GetCultureInfo("en")).Get($"Usage_Purpose_{purpose}"));
    }

    private static UsageDashboardViewModel Dashboard(UsageReport report)
    {
        var store = new RecordingSettingsStore();
        var clock = new FakeClock(Now);
        return new UsageDashboardViewModel(
            new FakeUsageReport(() => report),
            new FakeUsageExporter(),
            new FakeSaveFilePicker(null),
            store,
            clock,
            new QuotaViewModel(new FakeQuotaObservations(), null, clock),
            new ObservabilitySettingsViewModel(store));
    }
}
