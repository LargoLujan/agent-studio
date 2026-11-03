using AgentStudio.Application.Decisions;
using AgentStudio.Application.Usage;
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
