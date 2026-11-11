using AgentStudio.Application.Coordination;
using AgentStudio.Desktop.Debate;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// «Nuevo debate» (docs/15 D-41 and D-06): the template gives the participants and the judges; each mode asks for its roles
/// and brings its requirements; the disposition of the judgment is explained; «Preparar» writes nothing; and what you
/// approve with «Abrir debate» is exactly what is on screen.
/// </summary>
public sealed class NewDebateViewModelTests
{
    private static async Task<(NewDebateViewModel Form, FakeDebateCoordinator Coordinator)> FormAsync(
        DebateScreenBackend? backend = null,
        FakeDebateCoordinator? coordinator = null)
    {
        var fake = coordinator ?? new FakeDebateCoordinator();
        var form = new NewDebateViewModel(backend ?? new DebateScreenBackend(fake, Roster: new FakeRoster(() => Roster)), new FakeClock(Now));
        await form.LoadAsync(Project);
        return (form, fake);
    }

    private static void Fill(NewDebateViewModel form, DebateMode mode = DebateMode.ArchitectureReview)
    {
        form.SelectedMode = form.Modes.Single(m => m.Value == mode);
        form.Question = "¿Procesos aislados por agente o un único proceso con hilos?";
        foreach (var participant in form.Participants.Where(p => p.Agent.Id.Value is "arquitecto" or "critico"))
        {
            participant.IsIncluded = true;
        }
    }

    [Fact]
    public async Task The_template_gives_participants_and_judges_and_never_seats_the_orchestrator_or_the_auditor()
    {
        var (form, _) = await FormAsync();

        Assert.Equal(["arquitecto", "critico", "ejecutor"], form.Participants.Select(p => p.Agent.Id.Value));
        Assert.Equal(["juez-claude", "juez-revision"], form.Judges.Select(j => j.Agent.Id.Value));
        Assert.Equal("Crítico", form.Participants.Single(p => p.Agent.Id.Value == "critico").SelectedRole.Label);
        Assert.Equal("Codex CLI · OpenAI · modelo predeterminado del programa", form.Participants.Single(p => p.Agent.Id.Value == "critico").Where);
        Assert.All(form.Judges, j => Assert.True(j.IsIncluded));
    }

    [Fact]
    public async Task An_architecture_review_starts_with_its_five_criteria_as_the_judges_assessment()
    {
        var (form, _) = await FormAsync();

        form.SelectedMode = form.Modes.Single(m => m.Value == DebateMode.ArchitectureReview);

        Assert.Equal(NewDebateViewModel.ArchitectureCriteria, form.Criteria.Select(c => c.Text));
        Assert.All(form.Criteria, c => Assert.Equal(VerificationMethod.JudgeAssessment, c.Method));
        Assert.Equal(["Valoración del juez", "Revisión de una persona"], DebateCriterionViewModel.Methods.Select(m => m.Label));
        Assert.True(form.WritesCriteria);
    }

    [Fact]
    public async Task What_is_missing_is_said_before_anything_can_be_prepared()
    {
        var (form, _) = await FormAsync();

        Assert.Contains("Escribe la pregunta del debate.", form.Problems);
        Assert.Contains("Hacen falta dos participantes como mínimo.", form.Problems);
        Assert.Contains("Escribe al menos un criterio: es lo que evalúa el juez.", form.Problems);
        Assert.False(form.PrepareCommand.CanExecute(null));
    }

    [Fact]
    public async Task Each_mode_asks_for_its_roles()
    {
        var (form, _) = await FormAsync();
        Fill(form, DebateMode.Adversarial);
        form.Participants.Single(p => p.Agent.Id.Value == "critico").SelectedRole = ParticipantChoiceViewModel.Roles.Single(r => r.Value == DebateRole.Proponent);

        Assert.Contains("Este modo necesita un proponente y un crítico como mínimo.", form.Problems);

        form.SelectedMode = form.Modes.Single(m => m.Value == DebateMode.Tribunal);
        Assert.All(form.Participants, p => Assert.Equal(DebateRole.Panelist, p.Role));
        Assert.DoesNotContain(form.Problems, p => p.Contains("panelistas", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_disposition_is_explained_and_mixing_vendors_offers_the_panel()
    {
        var (form, _) = await FormAsync();
        Fill(form);

        Assert.Contains("Caben: Jurado cruzado (juez-claude + juez-revision)", form.JudgingHint, StringComparison.Ordinal);
        Assert.True(form.JudgingHintIsWarning);
        Assert.Contains("no cabe con esta configuración", form.JudgingHint, StringComparison.Ordinal);

        form.SelectedJudging = form.JudgingKinds.Single(k => k.Value == DebateJudgingKind.CrossVendorPanel);
        Assert.False(form.JudgingHintIsWarning);
        Assert.Contains("excepción", form.JudgingHelp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_run_as_subject_the_coordinator_computes_the_disposition()
    {
        var store = new FakeRequirementStore(Requirements);
        var runs = new FakeRunRegistry(RunRecord("20261018-c4a1f2", Requirements.ContentHash));
        var coordinator = new FakeDebateCoordinator();
        var (form, _) = await FormAsync(new DebateScreenBackend(coordinator, Roster: new FakeRoster(() => Roster), Requirements: store, Runs: runs), coordinator);

        Fill(form, DebateMode.ImplementationAudit);
        form.SelectedRun = form.Runs.Single();

        Assert.Equal(DebateSubjectKind.Run, form.SelectedSubject.Value);
        Assert.Contains("el coordinador calcula quién puede juzgar", form.JudgingHint, StringComparison.Ordinal);
        Assert.False(form.WritesCriteria);
        Assert.Equal($"Los requisitos congelados de la ejecución (2 requisitos, huella {Requirements.ContentHash[..12]}).", form.RequirementsText);
        Assert.Empty(form.Problems);
    }

    [Fact]
    public async Task The_review_of_the_orchestrator_uses_its_fixed_requirements_or_says_they_are_missing()
    {
        var runs = new FakeRunRegistry(RunRecord("20261018-c4a1f2", Requirements.ContentHash));
        var coordinator = new FakeDebateCoordinator();
        var (without, _) = await FormAsync(new DebateScreenBackend(coordinator, Roster: new FakeRoster(() => Roster), Runs: runs), coordinator);
        var orq = FrozenRequirementSet.Freeze([new Requirement(new RequirementId("orq-1"), "Tareas asignadas a agentes con el rol adecuado.", RequirementPriority.Mandatory, [new AcceptanceCriterion("c1", "Asignación", VerificationMethod.JudgeAssessment)])], Now);
        var (with, _) = await FormAsync(new DebateScreenBackend(coordinator, Roster: new FakeRoster(() => Roster), Runs: runs, FixedRequirements: mode => mode == DebateMode.OrchestratorReview ? orq : null), coordinator);

        foreach (var form in new[] { without, with })
        {
            form.SelectedMode = form.Modes.Single(m => m.Value == DebateMode.OrchestratorReview);
            form.Question = "¿Respetó el orquestador el protocolo?";
            foreach (var participant in form.Participants.Take(2))
            {
                participant.IsIncluded = true;
            }

            form.SelectedRun = form.Runs.Single();
        }

        Assert.Contains("Los requisitos fijos de este modo (orq-1 a orq-7) no están disponibles.", without.Problems);
        Assert.Empty(with.Problems);
        Assert.StartsWith("Requisitos fijos del modo (1 requisito", with.RequirementsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preparing_sends_exactly_what_is_on_screen_and_shows_the_disposition_waiver_and_estimate()
    {
        var (form, coordinator) = await FormAsync();
        Fill(form);
        form.SelectedJudging = form.JudgingKinds.Single(k => k.Value == DebateJudgingKind.CrossVendorPanel);
        form.MaxRounds = "2";

        await form.PrepareCommand.ExecuteAsync(null);

        var request = Assert.Single(coordinator.Prepared);
        Assert.Equal(DebateMode.ArchitectureReview, request.Mode);
        Assert.Equal("¿Procesos aislados por agente o un único proceso con hilos?", request.Question);
        Assert.Equal(new DebateSubject(DebateSubjectKind.None, null), request.Subject);
        Assert.Equal([new DebateParticipantRequest(new AgentId("arquitecto"), DebateRole.Proponent), new DebateParticipantRequest(new AgentId("critico"), DebateRole.Critic)], request.Participants);
        Assert.Equal([new AgentId("juez-claude"), new AgentId("juez-revision")], request.Judges);
        Assert.Equal(DebateJudgingKind.CrossVendorPanel, request.Judging);
        Assert.Equal(new DebateLimits(2, TimeSpan.FromMinutes(30), 400_000), request.Limits);
        Assert.Equal(5, request.Requirements.Requirements.Count);
        Assert.All(request.Requirements.Requirements, r => Assert.Equal(VerificationMethod.JudgeAssessment, Assert.Single(r.Criteria).Method));
        Assert.Equal("Jurado cruzado: juez-claude · Claude Code · Anthropic · claude-haiku-4-5 | juez-revision · Codex CLI · OpenAI · modelo no informado", form.ArrangementText);
        Assert.Contains("Anthropic y OpenAI", form.WaiverText, StringComparison.Ordinal);
        Assert.Equal("Como mucho 10 ejecuciones de agente y ~240.000 tokens (estimado por Agent Studio).", form.EstimateText);
        Assert.True(form.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changing_anything_after_preparing_withdraws_the_proposal()
    {
        var (form, _) = await FormAsync();
        Fill(form);
        await form.PrepareCommand.ExecuteAsync(null);
        Assert.True(form.HasProposal);

        form.Criteria[0].Text = "Otro criterio.";

        Assert.False(form.HasProposal);
        Assert.False(form.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_proposal_with_problems_cannot_be_opened()
    {
        var coordinator = new FakeDebateCoordinator { Prepare = request => Proposal(request, "debate.judge-participant-not-waived") };
        var (form, _) = await FormAsync(coordinator: coordinator);
        Fill(form);

        await form.PrepareCommand.ExecuteAsync(null);

        Assert.Equal(["debate.judge-participant-not-waived"], form.ProposalProblems);
        Assert.False(form.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_raises_the_run_with_what_the_room_needs_and_a_refusal_is_shown()
    {
        var run = new FakeDebateRun(Snapshot());
        var coordinator = new FakeDebateCoordinator { Open = _ => run };
        var (form, _) = await FormAsync(coordinator: coordinator);
        Fill(form);
        OpenedDebate? opened = null;
        form.Opened += (_, debate) => opened = debate;
        await form.PrepareCommand.ExecuteAsync(null);

        await form.OpenCommand.ExecuteAsync(null);

        Assert.Same(run, opened!.Run);
        Assert.Equal(DebateJudgingKind.CrossVendorPanel, opened.Context.Judging);
        Assert.Equal(form.Proposal, opened.Proposal);
        Assert.Equal(["¿Procesos aislados por agente o un único proceso con hilos?"], [opened.Context.Question]);

        coordinator.Open = _ => throw new InvalidOperationException("Aprobación denegada.");
        await form.OpenCommand.ExecuteAsync(null);
        Assert.Equal("No se abrió el debate: Aprobación denegada.", form.ErrorMessage);
    }

    [Fact]
    public async Task Without_the_coordinator_nothing_can_be_prepared_and_the_form_says_why()
    {
        var (form, _) = await FormAsync(new DebateScreenBackend(Roster: new FakeRoster(() => Roster)));
        Fill(form);

        Assert.Contains("Falta el coordinador de debates: no se puede preparar ni abrir ningún debate.", form.Problems);
        Assert.False(form.PrepareCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_the_template_the_form_says_it_is_missing_instead_of_asking_for_participants()
    {
        var (form, _) = await FormAsync(new DebateScreenBackend(new FakeDebateCoordinator()));
        form.Question = "¿Procesos o hilos?";
        form.SelectedSubject = form.Subjects.Single(s => s.Value == DebateSubjectKind.None);

        Assert.Contains("Falta el equipo de agentes: no hay participantes ni jueces que elegir.", form.Problems);
        Assert.DoesNotContain("Hacen falta dos participantes como mínimo.", form.Problems);
        Assert.DoesNotContain(form.Problems, p => p.Contains("ningún juez", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_the_run_registry_a_run_cannot_be_the_subject_and_the_form_says_why()
    {
        var (form, _) = await FormAsync();

        form.SelectedMode = form.Modes.Single(m => m.Value == DebateMode.ImplementationAudit);

        Assert.Contains("Falta el registro de ejecuciones: no se puede debatir una ejecución.", form.Problems);
        Assert.Empty(form.Runs);
    }

    [Fact]
    public async Task Limits_that_are_not_positive_integers_are_refused_and_no_token_limit_is_allowed()
    {
        var (form, coordinator) = await FormAsync();
        Fill(form);

        form.MaxRounds = "0";
        Assert.Contains(form.Problems, p => p.StartsWith("Los límites tienen que ser números enteros", StringComparison.Ordinal));

        form.MaxRounds = "3";
        form.MaxTokens = string.Empty;
        await form.PrepareCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(coordinator.Prepared).Limits.MaxTotalTokens);
    }
}
