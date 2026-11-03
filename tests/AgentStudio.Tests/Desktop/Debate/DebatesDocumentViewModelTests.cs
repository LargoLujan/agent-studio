using AgentStudio.Application.Coordination;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The «Debates» document (docs/15 D-41): the history comes from the coordinator's replay, filtered by mode; only an
/// interrupted debate without problems offers «Reanudar»; a debate read back is shown in a room of its own; and the debate
/// in course is followed in the room, one per project.
/// </summary>
public sealed class DebatesDocumentViewModelTests
{
    private static DebatesDocumentViewModel Document(FakeDebateCoordinator coordinator, DebateScreens? screens = null, FakeVerdictChallenges? challenges = null) =>
        new(Root, Backend(coordinator, challenges), new FakeClock(Now), screens, new InlineSynchronizationContext());

    private static DebateScreenBackend Backend(FakeDebateCoordinator coordinator, FakeVerdictChallenges? challenges = null) => new(
        coordinator,
        challenges ?? new FakeVerdictChallenges(),
        Workspace: new FakeWorkspace(),
        Roster: new FakeRoster(() => Roster),
        Requirements: new FakeRequirementStore(Requirements),
        Verdicts: new FakeVerdictStore(Stored("v-1", VerdictOf(CodexJudge, VerdictDecision.Approved, null))));

    private static FakeDebateCoordinator WithHistory()
    {
        var coordinator = new FakeDebateCoordinator();
        coordinator.Debates.Add(Summary("deb-20261019-000001", DebateMode.Free, DebatePhase.Closed, DebateOutcome.Resolved, decision: VerdictDecision.Approved, chosen: "P2", minutesAgo: 120));
        coordinator.Debates.Add(Summary("deb-20261019-000002", DebateMode.ArchitectureReview, DebatePhase.Deliberation, DebateOutcome.Open, interrupted: true, minutesAgo: 30));
        coordinator.Debates.Add(Summary("deb-20261019-000003", DebateMode.Adversarial, DebatePhase.Proposals, DebateOutcome.Open, interrupted: true, minutesAgo: 10, problems: ["debate.replay: una ronda que no es la actual"], challenges: "v-1"));
        return coordinator;
    }

    [Fact]
    public async Task The_history_is_the_coordinators_replay_newest_first_and_filters_by_mode()
    {
        var document = Document(WithHistory());

        await document.OpenAsync();

        Assert.Equal(["deb-20261019-000003", "deb-20261019-000002", "deb-20261019-000001"], document.History.Select(h => h.Debate.Value));
        var closed = document.History[2];
        Assert.Equal("Resuelto", closed.StateText);
        Assert.Equal("Aprobado · propuesta P2", closed.DecisionText);
        Assert.Equal(Tone.Success, closed.Tone);
        Assert.Equal("Revisión del veredicto v-1", document.History[0].ChallengeText);
        Assert.Equal("19/10/2026 09:50", document.History[0].When);

        document.SelectedFilter = document.ModeFilters.Single(f => f.Value == DebateMode.ArchitectureReview);
        Assert.Equal(["deb-20261019-000002"], document.History.Select(h => h.Debate.Value));
        Assert.Equal("Interrumpido · En deliberación", document.History[0].StateText);
    }

    [Fact]
    public async Task Only_an_interrupted_debate_without_problems_offers_resuming()
    {
        var document = Document(WithHistory());

        await document.OpenAsync();

        Assert.False(document.History.Single(h => h.Debate.Value == "deb-20261019-000003").CanResume);
        Assert.True(document.History.Single(h => h.Debate.Value == "deb-20261019-000002").CanResume);
        Assert.False(document.History.Single(h => h.Debate.Value == "deb-20261019-000001").CanResume);
    }

    [Fact]
    public async Task Resuming_follows_the_debate_in_the_room_with_what_its_transcript_says()
    {
        var coordinator = WithHistory();
        var run = new FakeDebateRun(Snapshot());
        coordinator.Resume = _ => run;
        coordinator.Read = _ => Transcript();
        var document = Document(coordinator);
        await document.OpenAsync();

        await document.ResumeCommand.ExecuteAsync(document.History.Single(h => h.CanResume));

        Assert.Equal([new RunId("deb-20261019-000002")], coordinator.Resumed);
        Assert.Equal(DebatesPart.Room, document.Part);
        Assert.True(document.Room!.IsLive);
        Assert.Contains("Jurado cruzado", document.Room.JudgesText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_debate_read_back_opens_in_a_room_of_its_own_and_the_live_one_stays()
    {
        var coordinator = WithHistory();
        var run = new FakeDebateRun(Snapshot());
        coordinator.Open = _ => run;
        coordinator.Read = _ => Transcript();
        var document = Document(coordinator);
        await document.OpenAsync();
        document.ShowNewDebateCommand.Execute(null);
        document.NewDebate.Question = "¿Procesos o hilos?";
        foreach (var participant in document.NewDebate.Participants.Take(2))
        {
            participant.IsIncluded = true;
        }

        document.NewDebate.Criteria[0].Text = "Mantenibilidad.";
        await document.NewDebate.PrepareCommand.ExecuteAsync(null);
        await document.NewDebate.OpenCommand.ExecuteAsync(null);
        var live = document.Room!;

        await document.OpenItemCommand.ExecuteAsync(document.History[2]);

        Assert.NotSame(live, document.Room);
        Assert.True(document.Room!.IsTranscript);
        Assert.True(document.ShowsAnotherThanLive);
        document.BackToLiveCommand.Execute(null);
        Assert.Same(live, document.Room);
        Assert.False(document.ShowsAnotherThanLive);
    }

    [Fact]
    public async Task Challenging_a_verdict_shows_the_challenge_form_on_it()
    {
        var challenges = new FakeVerdictChallenges();
        var document = Document(WithHistory(), challenges: challenges);
        await document.OpenAsync();

        await document.StartChallengeAsync("v-1");

        Assert.Equal(DebatesPart.Challenge, document.Part);
        Assert.Equal("v-1", document.Challenge.VerdictId);
        Assert.Equal(["v-1"], challenges.StatusAsked);
    }

    [Fact]
    public async Task Without_the_coordinator_the_document_says_what_is_missing_and_lists_nothing()
    {
        var document = new DebatesDocumentViewModel(Root, new DebateScreenBackend(Workspace: new FakeWorkspace()), new FakeClock(Now), null, new InlineSynchronizationContext());

        await document.OpenAsync();

        Assert.Contains("Falta el coordinador de debates en esta composición: no se puede listar, abrir ni reanudar ningún debate.", document.Notices);
        Assert.Contains("Falta el servicio de impugnación: no se puede impugnar un veredicto.", document.Notices);
        Assert.Contains("Falta la plantilla de agentes: no hay participantes ni jueces que elegir.", document.Notices);
        Assert.Contains("Falta el registro de ejecuciones: no se puede debatir sobre una ejecución.", document.Notices);
        Assert.Empty(document.History);
    }

    [Fact]
    public void The_document_of_a_project_is_one_and_the_screens_keep_it()
    {
        var navigation = new FakeReviewNavigation();
        var screens = new DebateScreens(Backend(WithHistory()), new FakeClock(Now), () => navigation);

        Assert.Same(screens.Debates(Root), screens.Debates(Root.ToUpperInvariant()));
        Assert.Same(screens.Audit(Root), screens.Audit(Root));
        Assert.StartsWith(DebatesDocumentViewModel.IdPrefix, screens.Debates(Root).ContentId, StringComparison.Ordinal);
        Assert.Equal("Debates · calc", screens.Debates(Root).Title);
        Assert.True(screens.CanNavigate);
        Assert.False(new DebateScreens(DebateScreenBackend.None, new FakeClock(Now)).CanNavigate);
    }

    [Fact]
    public async Task Challenging_from_another_screen_shows_the_debates_of_the_project_on_the_challenge()
    {
        var navigation = new FakeReviewNavigation();
        var screens = new DebateScreens(Backend(WithHistory()), new FakeClock(Now), () => navigation);

        await screens.ChallengeAsync(Root, "v-1");

        var shown = Assert.IsType<DebatesDocumentViewModel>(Assert.Single(navigation.Shown));
        Assert.Same(screens.Debates(Root), shown);
        Assert.Equal(DebatesPart.Challenge, shown.Part);
    }
}
