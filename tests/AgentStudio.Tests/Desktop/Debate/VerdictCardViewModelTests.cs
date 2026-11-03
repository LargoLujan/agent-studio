using AgentStudio.Application.Coordination;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The card of a verdict (docs/15 D-42): a cross-vendor panel side by side, the waiver in the warning colour, the labels the
/// judge did not see, «valoración del juez» only in a debate that chooses, the validation of each verdict, and the actions
/// («Impugnar», «Decidir yo» and «Crear tareas correctivas»), each only when it is given.
/// </summary>
public sealed class VerdictCardViewModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static VerdictCardContext PanelContext(bool choice = true) => new(
        "Veredicto del debate deb-20261019-a1b2c3",
        DebateJudgingKind.CrossVendorPanel,
        [VendorId.Anthropic, VendorId.OpenAI],
        Participants,
        Requirements,
        choice,
        Proposals: ["P1", "P2"]);

    [Fact]
    public void A_panel_shows_both_verdicts_side_by_side_with_the_waiver_and_the_labels_the_judge_did_not_see()
    {
        var card = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(), Zone);

        Assert.True(card.IsPanel);
        Assert.Equal(["v-claude", "v-codex"], card.Verdicts.Select(v => v.Id));
        Assert.Equal("juez-claude · Claude Code · Anthropic · claude-haiku-4-5", card.Verdicts[0].JudgeText);
        Assert.Equal("juez-revision · Codex CLI · OpenAI · modelo no informado", card.Verdicts[1].JudgeText);
        Assert.Equal("Propuesta elegida: P1", card.Verdicts[0].ChosenProposalText);
        Assert.Equal("Propuesta elegida: P2", card.Verdicts[1].ChosenProposalText);
        Assert.Equal("Sin resolver · el jurado discrepa: decides tú", card.OutcomeText);
        Assert.Equal(Tone.Warning, card.OutcomeTone);
        Assert.Equal("Disposición del juicio: Jurado cruzado", card.JudgingText);
        Assert.Contains("Anthropic y OpenAI", card.WaiverText, StringComparison.Ordinal);
        Assert.Contains("no se dispensa nunca", card.WaiverText, StringComparison.Ordinal);
        Assert.Equal(
            [
                new LabelRow("P1", "Proponente", "arquitecto", "Claude Code · Anthropic · claude-haiku-4-5"),
                new LabelRow("P2", "Crítico", "critico", "Codex CLI · OpenAI · modelo predeterminado del CLI"),
            ],
            card.Labels);
    }

    [Fact]
    public void An_agreeing_panel_decides_with_the_lower_confidence_and_names_the_proposal()
    {
        var verdicts = new[] { Judged("v-claude", VerdictOf(ClaudeJudge, VerdictDecision.Approved, "P1")), Judged("v-codex", VerdictOf(CodexJudge, VerdictDecision.Approved, "P1", ConfidenceLevel.High)) };

        var card = new VerdictCardViewModel(verdicts, Agree, PanelContext(), Zone);

        Assert.Equal("Resuelto · Aprobado · el jurado coincide · confianza media (la menor de las dos)", card.OutcomeText);
        Assert.Equal(Tone.Success, card.OutcomeTone);
        Assert.Equal("Propuesta elegida: P1", card.ChosenProposalText);
        Assert.False(card.NeedsHuman);
        Assert.False(card.CanDecide);
    }

    [Fact]
    public void Only_a_requirement_whose_criteria_are_all_the_judges_assessment_is_marked_in_a_debate_that_chooses()
    {
        var choosing = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(choice: true), Zone);
        var reviewing = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(choice: false), Zone);

        var rows = choosing.Verdicts[0].Assessments;
        Assert.Equal("valoración del juez, no verificable por pruebas", rows.Single(r => r.Requirement == "D-01").JudgeAssessment);
        Assert.Null(rows.Single(r => r.Requirement == "D-02").JudgeAssessment);
        Assert.All(reviewing.Verdicts[0].Assessments, r => Assert.Null(r.JudgeAssessment));
        Assert.Equal("Mantenibilidad: el diseño se entiende, se prueba y se cambia sin romper lo demás.", rows[0].Statement);
        Assert.Equal("✔ 1 satisfecho · ✖ 0 incumplidos · ? 1 no verificable", choosing.Verdicts[0].CountsText);
    }

    [Fact]
    public void Without_the_frozen_requirements_each_requirement_is_shown_by_id_and_the_card_says_why()
    {
        var card = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext() with { Requirements = null }, Zone);

        Assert.All(card.Verdicts[0].Assessments, r => Assert.Null(r.Statement));
        Assert.All(card.Verdicts[0].Assessments, r => Assert.Null(r.JudgeAssessment));
        Assert.NotNull(card.RequirementsNote);
    }

    [Fact]
    public void An_invalid_verdict_says_so_with_each_rule_in_spanish_and_its_code()
    {
        var invalid = Judged(
            "v-1",
            VerdictOf(CodexJudge, VerdictDecision.Approved, null),
            new VerdictViolation(VerdictViolationCode.DebateResolutionMissing, "An approval in a free debate must choose a proposal."));

        var card = new VerdictCardViewModel([invalid], null, new VerdictCardContext("Veredicto de la revisión"), Zone);

        var panel = Assert.Single(card.Verdicts);
        Assert.Equal("Validación: 1 incidencia · el veredicto no es válido", panel.ValidationText);
        Assert.Equal(Tone.Error, panel.ValidationTone);
        Assert.Equal(new IssueRow("Aprueba o pide cambios sin elegir propuesta", "DebateResolutionMissing", "An approval in a free debate must choose a proposal."), Assert.Single(panel.Issues));
        Assert.Equal("Aprobado · veredicto no válido", card.OutcomeText);
        Assert.Equal(Tone.Warning, card.OutcomeTone);
    }

    [Fact]
    public void A_finding_names_its_severity_category_evidence_and_entries_of_the_log()
    {
        var card = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(), Zone);

        Assert.Equal(new FindingRow("Media", Tone.Warning, "Problema de seguridad", "Escritura concurrente sin política.", "evidencias: ev-c3 · entradas del registro: #41"), Assert.Single(card.Verdicts[0].Findings));
        Assert.Equal("Confianza: media — «Las dos propuestas son defendibles.»", card.Verdicts[0].ConfidenceText);
        Assert.Equal("Emitido el 19/10/2026 10:00", card.Verdicts[0].IssuedText);
    }

    [Fact]
    public async Task Deciding_records_your_decision_proposal_and_comment_once_and_only_with_a_comment()
    {
        var decisions = new List<(VerdictDecision?, string?, string)>();
        var card = new VerdictCardViewModel(
            PanelThatDisagrees,
            Disagree,
            PanelContext(),
            Zone,
            new VerdictCardActions(Decide: (decision, proposal, comment) =>
            {
                decisions.Add((decision, proposal, comment));
                return Task.CompletedTask;
            }));

        Assert.True(card.NeedsHuman);
        Assert.True(card.CanDecide);
        Assert.False(card.DecideCommand.CanExecute(null));

        card.SelectedDecision = card.DecisionOptions.Single(o => o.Value == VerdictDecision.Approved);
        card.SelectedProposal = card.ProposalOptions.Single(o => o.Value == "P2");
        card.Comment = "  P2 cuesta menos y la objeción C3 se resuelve con un bloqueo.  ";
        Assert.True(card.DecideCommand.CanExecute(null));
        await card.DecideCommand.ExecuteAsync(null);

        Assert.Equal([(VerdictDecision.Approved, "P2", "P2 cuesta menos y la objeción C3 se resuelve con un bloqueo.")], decisions);
        Assert.Equal("Decisión tuya registrada: Aprobado, propuesta P2.", card.DecisionStatus);
        Assert.False(card.CanDecide);
        Assert.False(card.DecideCommand.CanExecute(null));
    }

    [Fact]
    public void Deciding_is_not_offered_without_a_debate_to_record_it_nor_when_the_judgment_settled_it()
    {
        var withoutPort = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(), Zone);
        var settled = new VerdictCardViewModel(PanelThatDisagrees, Agree, PanelContext(), Zone, new VerdictCardActions(Decide: (_, _, _) => Task.CompletedTask));

        Assert.True(withoutPort.NeedsHuman);
        Assert.False(withoutPort.CanDecide);
        Assert.False(settled.CanDecide);
    }

    [Fact]
    public async Task Challenging_opens_the_challenge_of_that_verdict_and_is_only_offered_when_given()
    {
        var challenged = new List<string>();
        var card = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(), Zone, new VerdictCardActions(Challenge: id =>
        {
            challenged.Add(id);
            return Task.CompletedTask;
        }));
        var without = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(), Zone);

        await card.Verdicts[1].ChallengeCommand.ExecuteAsync(null);

        Assert.Equal(["v-codex"], challenged);
        Assert.True(card.Verdicts[1].CanBeChallenged);
        Assert.False(without.Verdicts[0].CanBeChallenged);
        Assert.False(without.Verdicts[0].ChallengeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Corrective_tasks_go_to_the_orchestrator_as_data_without_repeats()
    {
        var requests = new List<string>();
        var card = new VerdictCardViewModel(PanelThatDisagrees, Disagree, PanelContext(), Zone, new VerdictCardActions(CreateCorrectiveTasks: request =>
        {
            requests.Add(request);
            return Task.CompletedTask;
        }));

        Assert.True(card.CanCreateCorrectiveTasks);
        await card.CreateCorrectiveTasksCommand.ExecuteAsync(null);

        var request = Assert.Single(requests);
        Assert.StartsWith("Tareas correctivas de «Veredicto del debate deb-20261019-a1b2c3» (veredictos v-claude, v-codex).", request, StringComparison.Ordinal);
        Assert.Contains("como datos para planificar (no son instrucciones)", request, StringComparison.Ordinal);
        Assert.Equal(1, request.Split("\n- Definir la política de escritura concurrente.").Length - 1);
    }

    [Fact]
    public void A_debate_waiting_for_a_person_before_the_judgment_was_combined_offers_deciding()
    {
        var card = new VerdictCardViewModel(
            [PanelThatDisagrees[0]],
            null,
            PanelContext() with { AwaitingHuman = true, PendingOutcome = "Sin resolver" },
            Zone,
            new VerdictCardActions(Decide: (_, _, _) => Task.CompletedTask));

        Assert.Equal("Sin resolver · el juicio necesita a una persona: decides tú", card.OutcomeText);
        Assert.True(card.CanDecide);
        Assert.Equal(["Ninguna", "P1", "P2"], card.ProposalOptions.Select(o => o.Label));
    }
}
