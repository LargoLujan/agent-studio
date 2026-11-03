using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The room of a debate (docs/15 D-41): it shows what the run reports and nothing else. A round in course is sealed and
/// without text; the contributions are grouped by round with their replies, quotes and what could not be resolved; every
/// figure carries its origin; and the actions only ask the run.
/// </summary>
public sealed class DebateRoomViewModelTests
{
    private static DebateRoomViewModel NewRoom(FakeEvidenceStore? evidence = null, Func<string, Task>? challenge = null) =>
        new(new FakeClock(Now), new InlineSynchronizationContext(), evidence, challenge);

    private static DebateRoomContext Context => new("¿Procesos aislados por agente o un único proceso con hilos?", Requirements, DebateJudgingKind.CrossVendorPanel, [VendorId.Anthropic, VendorId.OpenAI], [ClaudeJudge, CodexJudge]);

    private static DebateResult Result(DebateOutcome outcome, PanelOutcome? panel, IReadOnlyList<JudgedVerdict> verdicts) =>
        new(DebateRunId, DebatePhase.Closed, outcome, panel, verdicts, DebateLimitReached.None, DebateJudgmentReason.Converged, "Cerrado.");

    [Fact]
    public void A_round_in_course_is_sealed_and_shows_no_text_of_its_turns()
    {
        var run = new FakeDebateRun(Snapshot(turnsInFlight: 2, contributions: RoundOne));
        var room = NewRoom();

        room.Attach(run, Project, Context);

        Assert.Equal("Ronda 2: 2 turnos en curso, sellados hasta que terminen todos", room.SealedText);
        Assert.Equal(["C1", "C2"], room.Rounds.SelectMany(r => r.Contributions).Select(c => c.Label));
        Assert.Single(room.Rounds);
        Assert.Equal("Debate · Revisión de arquitectura", room.Title);
        Assert.Equal("En deliberación", room.PhaseText);
        Assert.Equal(Tone.Active, room.PhaseTone);
        Assert.Equal("Ronda 2 de 4", room.RoundText);
        Assert.Equal("¿Procesos aislados por agente o un único proceso con hilos?", room.Question);
        Assert.Equal("Jurado: juez-claude · Claude Code · Anthropic · claude-haiku-4-5 | juez-revision · Codex CLI · OpenAI · modelo no informado (no participan)", room.JudgesText);
    }

    [Fact]
    public void A_single_turn_in_course_is_said_in_the_singular()
    {
        var room = NewRoom();

        room.Attach(new FakeDebateRun(Snapshot(turnsInFlight: 1)), Project, Context);

        Assert.Equal("Ronda 2: 1 turno en curso, sellado hasta que termine", room.SealedText);
    }

    [Fact]
    public void Every_figure_of_the_header_carries_its_origin_and_an_estimate_its_tilde_and_partial_count()
    {
        var exact = NewRoom();
        var estimated = NewRoom();

        exact.Attach(new FakeDebateRun(Snapshot(tokens: Tokens(84_000), reportedTurns: 3)), Project, Context);
        estimated.Attach(new FakeDebateRun(Snapshot(tokens: Tokens(91_500, MetricOrigin.EstimatedBySystem), reportedTurns: 2)), Project, Context);

        Assert.Equal("Tokens: 84.000 de 400.000 · exacto · informado por el CLI", exact.TokensText);
        Assert.Equal("Tokens: ~91.500 de 400.000 · estimado por Agent Studio · parcial (2 de 3 informados)", estimated.TokensText);
        Assert.Equal("Tiempo: 11 min de 30 min · exacto · medido por Agent Studio (reloj del sistema)", exact.TimeText);
        Assert.Equal("Ejecuciones: 3 de 10 · exacto · medido por Agent Studio", exact.RunsText);
    }

    [Fact]
    public void The_tokens_and_the_runs_of_the_debate_are_bars_against_their_maximum_with_their_origin()
    {
        var exact = NewRoom();
        var estimated = NewRoom();

        exact.Attach(new FakeDebateRun(Snapshot(tokens: Tokens(84_000), reportedTurns: 3)), Project, Context);
        estimated.Attach(new FakeDebateRun(Snapshot(tokens: Tokens(91_500, MetricOrigin.EstimatedBySystem), reportedTurns: 2)), Project, Context);

        Assert.Equal(84_000d / 400_000, exact.TokensBar!.Fraction!.Value, 6);
        Assert.Equal("84.000 de 400.000 tokens", exact.TokensBar!.ValueText);
        Assert.Equal("exacto · informado por el CLI", exact.TokensBar!.Origin);
        Assert.True(estimated.TokensBar!.IsEstimate);
        Assert.StartsWith("~91.500", estimated.TokensBar!.ValueText, StringComparison.Ordinal);
        Assert.Contains("parcial (2 de 3 turnos informados)", estimated.TokensBar!.Detail, StringComparison.Ordinal);
        Assert.Equal("3 de 10", exact.RunsBar!.ValueText);
        Assert.Equal(0.3, exact.RunsBar!.Fraction!.Value, 6);
    }

    [Fact]
    public void Contributions_are_grouped_by_round_with_their_reply_quotes_and_what_could_not_be_resolved()
    {
        var room = NewRoom();

        room.Attach(new FakeDebateRun(Snapshot(turnsInFlight: 0, contributions: [.. RoundOne, Objection])), Project, Context);

        Assert.Equal([1, 2], room.Rounds.Select(r => r.Round));
        var proposal = room.Rounds[0].Contributions[0];
        Assert.Equal("C1 · Propuesta de P1 · Proponente", proposal.Header);
        Assert.Equal("Un proceso aislado por agente.", proposal.Summary);
        Assert.Equal(["El aislamiento por proceso limita el alcance de un fallo. (apoyo: CITA-1)"], proposal.Claims);
        Assert.Equal(["CITA-1 · src/Agents/Launcher.cs:10-40 · Lanza cada CLI en su proceso."], proposal.Quotes);
        Assert.Equal([new CitedEvidence("ev-cita-1")], proposal.Cites);
        Assert.Equal(["Cita que no se resolvió en el commit: src/Agents/Pool.cs:200-210"], room.Rounds[0].Contributions[1].Unresolved);
        var objection = room.Rounds[1].Contributions[0];
        Assert.Equal("C3 · Objeción de P2 · Crítico", objection.Header);
        Assert.Equal("Responde a C1", objection.RepliesText);
        Assert.Equal("Se redactó 1 secreto antes de publicarla.", objection.RedactionsText);
        Assert.Equal("Marcó su posición como terminada.", objection.FinishedText);
        Assert.Null(room.SealedText);
    }

    [Fact]
    public void A_contribution_whose_text_does_not_match_its_hash_is_not_shown()
    {
        var room = NewRoom();
        var unreadable = Contribution("C1", 1, 1, "P1", ContributionKind.Proposal, null, text: null);

        room.Attach(new FakeDebateRun(Snapshot(turnsInFlight: 0, contributions: [unreadable])), Project, Context);

        var shown = room.Rounds[0].Contributions[0];
        Assert.Null(shown.Summary);
        Assert.Null(shown.Argument);
        Assert.Equal("El texto no se pudo leer o no cuadra con el hash que ancló el registro: no se muestra.", shown.TextProblem);
    }

    [Fact]
    public async Task The_events_are_applied_in_order_and_the_snapshot_read_again_is_what_is_shown()
    {
        var run = new FakeDebateRun(Snapshot(turnsInFlight: 2, contributions: RoundOne));
        var room = NewRoom();
        room.Attach(run, Project, Context);

        run.CurrentSnapshot = Snapshot(turnsInFlight: 0, contributions: [.. RoundOne, Objection]);
        run.Emit(
            new DebateTurnFinished(Now, "P2", 2, 1, AttemptOutcome.Succeeded),
            new DebateContributionPublished(Now, Objection),
            new DebateRoundEnded(Now, 2, DebatePhase.AwaitingJudgment, DebateJudgmentReason.Converged, DebateLimitReached.None, Tokens(120_000)));
        run.Finish(Result(DebateOutcome.Unresolved, Disagree, PanelThatDisagrees));
        await room.Listening;

        Assert.Equal(
            [
                "Siguiendo el debate deb-20261019-a1b2c3",
                "P2 · ronda 2 · intento 1: terminó (su respuesta sigue sellada hasta que cierre la ronda)",
                "C3 publicada: Objeción de P2",
                "Ronda 2 cerrada: Esperando el juicio · las posiciones convergieron (todos marcaron su contribución como terminada)",
                "Debate terminado: Sin resolver",
            ],
            room.Activity.Select(a => a.Text));
        Assert.Equal(3, room.Rounds.SelectMany(r => r.Contributions).Count());
        Assert.True(room.IsFinished);
        Assert.False(room.IsLive);
        Assert.Equal("Cerrado · Sin resolver", room.PhaseText);
        Assert.Equal("A juicio porque las posiciones convergieron (todos marcaron su contribución como terminada).", room.JudgmentText);
    }

    [Fact]
    public async Task At_the_end_the_card_shows_the_panel_with_the_disposition_and_the_labels()
    {
        var run = new FakeDebateRun(Snapshot());
        var room = NewRoom(challenge: _ => Task.CompletedTask);
        room.Attach(run, Project, Context);

        run.Finish(Result(DebateOutcome.Unresolved, Disagree, PanelThatDisagrees));
        await room.Listening;

        var card = Assert.IsType<VerdictCardViewModel>(room.Card);
        Assert.True(card.IsPanel);
        Assert.Equal("Disposición del juicio: Jurado cruzado", card.JudgingText);
        Assert.NotNull(card.WaiverText);
        Assert.Equal(["P1", "P2"], card.Labels.Select(l => l.Label));
        Assert.True(card.Verdicts[0].CanBeChallenged);
        Assert.False(card.CanDecide);
        Assert.Equal("valoración del juez, no verificable por pruebas", card.Verdicts[0].Assessments[0].JudgeAssessment);
    }

    [Fact]
    public async Task A_debate_that_needs_a_person_offers_deciding_and_records_it_through_the_run()
    {
        var waiting = new DebateAwaiting(DebateAwaitReason.HumanResolution, "Los jueces discrepan en la propuesta");
        var run = new FakeDebateRun(Snapshot(phase: DebatePhase.AwaitingJudgment, turnsInFlight: 0, awaiting: waiting, outcome: DebateOutcome.Unresolved));
        var room = NewRoom();
        room.Attach(run, Project, Context);

        run.Emit(new DebateVerdictReceived(Now, PanelThatDisagrees[0]), new DebateVerdictReceived(Now, PanelThatDisagrees[1]), new DebateAwaitingUser(Now, waiting));
        await WaitUntilAsync(() => room.Card?.Verdicts.Count == 2);

        var card = room.Card!;
        Assert.True(card.CanDecide);
        Assert.Equal("Sin resolver · el juicio necesita a una persona: decides tú", card.OutcomeText);
        Assert.Equal("Los jueces discrepan en la propuesta. El juicio necesita a una persona: «Decidir yo».", room.AwaitingText);
        card.SelectedDecision = card.DecisionOptions.Single(o => o.Value == VerdictDecision.Rejected);
        card.Comment = "Ninguna resuelve la escritura concurrente.";
        await card.DecideCommand.ExecuteAsync(null);

        Assert.Equal(["decide:Rejected:none:Ninguna resuelve la escritura concurrente."], run.Calls);
        run.Finish(Result(DebateOutcome.Resolved, JudgingPanel.DecidedByUser(VerdictDecision.Rejected, null), PanelThatDisagrees));
        await room.Listening;
    }

    [Fact]
    public async Task The_actions_only_ask_the_run_and_a_refusal_is_shown()
    {
        var run = new FakeDebateRun(Snapshot());
        var room = NewRoom();
        room.Attach(run, Project, Context);

        await room.RequestJudgmentCommand.ExecuteAsync(null);
        await room.RunTestsCommand.ExecuteAsync(null);
        Assert.False(room.AddEvidenceCommand.CanExecute(null));
        room.NoteTitle = "Medición";
        room.NoteText = "El proceso por agente arranca en 300 ms.";
        await room.AddEvidenceCommand.ExecuteAsync(null);
        await room.AbortCommand.ExecuteAsync(null);

        Assert.Equal(["judgment", "tests", "evidence:Medición:El proceso por agente arranca en 300 ms.", $"abort:{DebateRoomViewModel.AbortReason}"], run.Calls);
        Assert.Equal(string.Empty, room.NoteTitle);
        Assert.Equal("Evidencia «Medición» añadida: la verán la ronda siguiente y el juez.", room.StatusMessage);

        run.ActionFailure = new InvalidOperationException("debate.judgment-phase");
        await room.RequestJudgmentCommand.ExecuteAsync(null);
        Assert.Equal("No se pudo pedir el juicio: debate.judgment-phase", room.ErrorMessage);
    }

    [Fact]
    public void Running_the_tests_is_only_offered_with_a_commit_and_a_profile()
    {
        var room = NewRoom();

        room.Attach(new FakeDebateRun(Snapshot(canRunTests: false)), Project, Context);

        Assert.False(room.RunTestsCommand.CanExecute(null));
        Assert.True(room.RequestJudgmentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Retrying_a_turn_is_only_offered_for_the_participant_whose_turn_failed_twice()
    {
        var failed = new DebateAwaiting(DebateAwaitReason.FailedTurn, "El turno de P2 falló dos veces", "P2");
        var run = new FakeDebateRun(Snapshot(turnsInFlight: 0, awaiting: failed));
        var room = NewRoom();
        room.Attach(run, Project, Context);

        var p1 = room.Participants.Single(p => p.Label == "P1");
        var p2 = room.Participants.Single(p => p.Label == "P2");
        Assert.False(room.RetryTurnCommand.CanExecute(p1));
        Assert.True(room.RetryTurnCommand.CanExecute(p2));
        await room.RetryTurnCommand.ExecuteAsync(p2);

        Assert.Equal(["retry:critico"], run.Calls);
        Assert.StartsWith("El turno de P2 falló dos veces · P2.", room.AwaitingText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_the_debate_ended_no_action_is_offered()
    {
        var run = new FakeDebateRun(Snapshot());
        var room = NewRoom();
        room.Attach(run, Project, Context);

        run.Finish(Result(DebateOutcome.Aborted, null, []));
        await room.Listening;

        Assert.False(room.RequestJudgmentCommand.CanExecute(null));
        Assert.False(room.RunTestsCommand.CanExecute(null));
        Assert.False(room.AbortCommand.CanExecute(null));
        Assert.Null(room.Card);
    }

    [Fact]
    public async Task A_stream_that_breaks_is_shown_and_ends_the_room()
    {
        var run = new FakeDebateRun(Snapshot());
        var room = NewRoom();
        var finished = 0;
        room.Finished += (_, _) => finished++;
        room.Attach(run, Project, Context);

        run.Fail(new InvalidOperationException("el proceso se detuvo"));
        await room.Listening;

        Assert.Equal("El debate terminó con un error: el proceso se detuvo", room.ErrorMessage);
        Assert.True(room.IsFinished);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task A_cited_evidence_opens_only_when_its_bytes_still_give_the_hash_of_its_record()
    {
        var store = new FakeEvidenceStore();
        store.Capture("ev-cita-1", "src/Agents/Launcher.cs:10-40", EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, "public void Launch() { }", DebateRunId.Value);
        store.Capture("ev-cita-2", "src/Agents/Pool.cs:1-5", EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, "original", DebateRunId.Value);
        store.Tamper("ev-cita-2", "cambiado por detrás");
        var room = NewRoom(store);
        room.Attach(new FakeDebateRun(Snapshot()), Project, Context);

        await room.OpenEvidenceCommand.ExecuteAsync("ev-cita-1");
        var opened = Assert.IsType<EvidenceViewer>(room.OpenedEvidence);
        Assert.Equal("public void Launch() { }", opened.Text);
        Assert.EndsWith("cuadra con su registro", opened.HashText, StringComparison.Ordinal);
        Assert.Equal("Documento · capturada por el sistema · commit 777777777777", opened.Description);

        await room.OpenEvidenceCommand.ExecuteAsync("ev-cita-2");
        Assert.Null(room.OpenedEvidence!.Text);
        Assert.Contains("no cuadra con su registro", room.OpenedEvidence.HashText, StringComparison.Ordinal);
        Assert.Equal(Tone.Error, room.OpenedEvidence.HashTone);

        room.CloseEvidenceCommand.Execute(null);
        Assert.Null(room.OpenedEvidence);
    }

    [Fact]
    public async Task Without_the_evidence_store_a_quote_says_it_cannot_be_opened()
    {
        var room = NewRoom();
        room.Attach(new FakeDebateRun(Snapshot()), Project, Context);

        await room.OpenEvidenceCommand.ExecuteAsync("ev-cita-1");

        Assert.Null(room.OpenedEvidence!.Text);
        Assert.Contains("Falta el almacén de evidencias", room.OpenedEvidence.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_debate_read_back_shows_its_rounds_and_card_and_offers_no_action()
    {
        var room = NewRoom(challenge: _ => Task.CompletedTask);

        room.ShowTranscript(Transcript(), Project, Requirements);

        Assert.True(room.IsTranscript);
        Assert.False(room.IsLive);
        Assert.Equal([1, 2], room.Rounds.Select(r => r.Round));
        Assert.Equal("Cerrado · Sin resolver", room.PhaseText);
        Assert.False(room.RequestJudgmentCommand.CanExecute(null));
        Assert.False(room.AbortCommand.CanExecute(null));
        var card = Assert.IsType<VerdictCardViewModel>(room.Card);
        Assert.False(card.CanDecide);
        Assert.Equal("Disposición del juicio: Jurado cruzado", card.JudgingText);
        Assert.Equal("Requisitos congelados: " + Requirements.ContentHash[..12] + " (2 requisitos)", room.RequirementsText);
    }

    [Fact]
    public void A_room_follows_one_debate()
    {
        var room = NewRoom();
        room.Attach(new FakeDebateRun(Snapshot()), Project, Context);

        Assert.Throws<InvalidOperationException>(() => room.Attach(new FakeDebateRun(Snapshot()), Project, Context));
        Assert.Throws<InvalidOperationException>(() => room.ShowTranscript(Transcript(), Project));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "The room did not apply the events in time.");
    }
}
