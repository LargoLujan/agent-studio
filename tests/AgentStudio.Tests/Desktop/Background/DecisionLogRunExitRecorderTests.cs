using AgentStudio.Application.Decisions;
using AgentStudio.Desktop.Background;
using AgentStudio.Domain.Decisions;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Background.BackgroundSamples;

namespace AgentStudio.Tests.Desktop.Background;

/// <summary>
/// F8-9 and docs/04 §18.2: the choice of the managed exit goes to the log of each piece of work as one
/// <c>RunExitChosen</c>, version 1, through the only writer of the log (the real <see cref="DecisionRecorder"/> here, over
/// a store in memory): signed by the person, or by the system when Windows ended the session.
/// </summary>
public sealed class DecisionLogRunExitRecorderTests
{
    private const string Run = "20261021-abc123";

    private readonly InMemoryDecisionLogStore _store = new();
    private readonly DecisionRecorder _decisions;

    public DecisionLogRunExitRecorderTests() => _decisions = new DecisionRecorder(_store, new FakeTimeProvider(T0));

    [Fact]
    public async Task A_choice_of_the_person_is_one_entry_of_its_run_signed_by_the_user()
    {
        await new DecisionLogRunExitRecorder(_decisions).RecordAsync(ProjectA, Run, RunExitChoice.PauseAndExitWhenIdle, CancellationToken.None);

        var log = await _decisions.LoadAsync(ProjectA.ProjectId, CancellationToken.None);
        Assert.True(log.Verification.IsValid);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(DecisionKind.RunExitChosen, entry.Kind);
        Assert.Equal(new Actor(ActorKind.User, "user"), entry.Actor);
        Assert.Equal("Pausar y salir cuando terminen los intentos en curso", entry.Summary);
        var payload = DecisionPayloadSerializer.Deserialize<RunExitChosenPayload>(entry.PayloadJson);
        Assert.Equal((RunExitChoice.PauseAndExitWhenIdle, "user", Run, 1), (payload.Choice, payload.DecidedBy, payload.RunId, payload.V));
        Assert.False(payload.AllowsAutomaticResume());
    }

    [Fact]
    public async Task The_end_of_the_session_is_signed_by_the_system_and_says_windows_decided()
    {
        await new DecisionLogRunExitRecorder(_decisions).RecordAsync(ProjectA, Run, RunExitChoice.SessionEnded, CancellationToken.None);

        var entry = Assert.Single((await _decisions.LoadAsync(ProjectA.ProjectId, CancellationToken.None)).Entries);
        Assert.Equal(new Actor(ActorKind.System, "agent-studio"), entry.Actor);
        var payload = DecisionPayloadSerializer.Deserialize<RunExitChosenPayload>(entry.PayloadJson);
        Assert.Equal((RunExitChoice.SessionEnded, DecisionLogRunExitRecorder.WindowsDecided), (payload.Choice, payload.DecidedBy));
        Assert.True(payload.AllowsAutomaticResume());
    }

    [Theory]
    [InlineData(RunExitChoice.PauseAndExitWhenIdle, "Pausar y salir cuando terminen los intentos en curso")]
    [InlineData(RunExitChoice.ExitNow, "Salir ahora y dejarla interrumpida")]
    [InlineData(RunExitChoice.SessionEnded, "Windows cerró la sesión: cuenta como salir ahora")]
    public void Each_choice_has_its_summary_in_spanish(RunExitChoice choice, string summary) =>
        Assert.Equal(summary, DecisionLogRunExitRecorder.Summary(choice));

    [Fact]
    public async Task Each_piece_of_work_gets_its_own_entry_in_the_log_of_its_project()
    {
        var recorder = new DecisionLogRunExitRecorder(_decisions);

        await recorder.RecordAsync(ProjectA, Run, RunExitChoice.ExitNow, CancellationToken.None);
        await recorder.RecordAsync(ProjectA, "deb-20261021-def456", RunExitChoice.ExitNow, CancellationToken.None);
        await recorder.RecordAsync(ProjectB, "20261021-fed987", RunExitChoice.ExitNow, CancellationToken.None);

        var a = await _decisions.LoadAsync(ProjectA.ProjectId, CancellationToken.None);
        var b = await _decisions.LoadAsync(ProjectB.ProjectId, CancellationToken.None);
        Assert.Equal([Run, "deb-20261021-def456"], a.Entries.Select(e => DecisionPayloadSerializer.Deserialize<RunExitChosenPayload>(e.PayloadJson).RunId));
        Assert.Equal(["20261021-fed987"], b.Entries.Select(e => DecisionPayloadSerializer.Deserialize<RunExitChosenPayload>(e.PayloadJson).RunId));
        Assert.True(a.Verification.IsValid && b.Verification.IsValid);
    }

    [Fact]
    public async Task A_choice_that_is_not_one_of_the_three_is_refused_before_anything_is_written()
    {
        var recorder = new DecisionLogRunExitRecorder(_decisions);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => recorder.RecordAsync(ProjectA, Run, (RunExitChoice)7, CancellationToken.None));

        Assert.Empty(_store.Entries(ProjectA.ProjectId));
    }
}
