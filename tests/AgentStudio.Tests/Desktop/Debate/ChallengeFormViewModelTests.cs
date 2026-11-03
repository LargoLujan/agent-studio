using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Desktop.Debate;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// «Impugnar» (docs/15 D-38 and D-39): the reasons are yours and required; the revision is adversarial, about the verdict,
/// on its requirements; the service picks who judges it; and nothing is recorded until «Impugnar», after your approval.
/// </summary>
public sealed class ChallengeFormViewModelTests
{
    private static readonly Verdict Challenged = VerdictOf(CodexJudge, VerdictDecision.Approved, null) with { Resolution = null };

    private static async Task<(ChallengeFormViewModel Form, FakeVerdictChallenges Challenges)> FormAsync(ChallengeStatus status = ChallengeStatus.None, bool withRequirements = true)
    {
        var challenges = new FakeVerdictChallenges();
        challenges.Statuses["v-1"] = status;
        var backend = new DebateScreenBackend(
            Challenges: challenges,
            Roster: new FakeRoster(() => Roster),
            Requirements: withRequirements ? new FakeRequirementStore(Requirements) : new FakeRequirementStore(),
            Verdicts: new FakeVerdictStore(Stored("v-1", Challenged)));
        var form = new ChallengeFormViewModel(backend, new FakeClock(Now));
        await form.LoadAsync(Project, "v-1");
        return (form, challenges);
    }

    [Fact]
    public async Task The_verdict_its_requirements_and_where_its_challenge_stands_are_read_first()
    {
        var (form, challenges) = await FormAsync();

        Assert.Equal("Veredicto v-1 · 20261018-c4a1f2 · Aprobado · válido · juez-revision · Codex CLI · OpenAI · modelo no informado", form.VerdictText);
        Assert.Equal($"Los requisitos del veredicto impugnado (2 requisitos, hash {Requirements.ContentHash[..12]}).", form.RequirementsText);
        Assert.Equal(["v-1"], challenges.StatusAsked);
        Assert.Equal("Sin impugnar.", form.ChallengeText);
        Assert.Equal(["arquitecto", "critico", "ejecutor"], form.Candidates.Select(c => c.Agent.Id.Value));
        Assert.Equal("arquitecto", form.Proponent!.Agent.Id.Value);
        Assert.Equal("critico", form.Critic!.Agent.Id.Value);
    }

    [Fact]
    public async Task Your_reasons_are_required_before_anything_is_prepared()
    {
        var (form, _) = await FormAsync();

        Assert.Contains("Escribe por qué impugnas el veredicto: se guarda como nota tuya y abre la ronda 1 del crítico.", form.Problems);
        Assert.False(form.PrepareCommand.CanExecute(null));
    }

    [Fact]
    public async Task Preparing_asks_for_an_adversarial_revision_of_the_verdict_on_its_requirements()
    {
        var (form, challenges) = await FormAsync();
        form.Reasons = "  Aprobó sin pruebas del commit candidato.  ";

        await form.PrepareCommand.ExecuteAsync(null);

        var request = Assert.Single(challenges.Prepared);
        Assert.Equal("v-1", request.Verdict);
        Assert.Equal("Aprobó sin pruebas del commit candidato.", request.Reasons);
        Assert.Equal(DebateMode.Adversarial, request.Revision.Mode);
        Assert.Equal(new DebateSubject(DebateSubjectKind.Verdict, "v-1"), request.Revision.Subject);
        Assert.Equal(Requirements.ContentHash, request.Revision.Requirements.ContentHash);
        Assert.Equal([new DebateParticipantRequest(new AgentId("arquitecto"), DebateRole.Proponent), new DebateParticipantRequest(new AgentId("critico"), DebateRole.Critic)], request.Revision.Participants);
        Assert.Equal([new AgentId("juez-claude"), new AgentId("juez-revision")], request.Revision.Judges);
        Assert.True(form.HasProposal);
        Assert.Contains("Es el mismo agente que emitió el veredicto, en una sesión nueva", form.ArrangementText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_agent_cannot_defend_both_sides()
    {
        var (form, _) = await FormAsync();
        form.Reasons = "Razones.";

        form.Critic = form.Candidates[0];

        Assert.Contains("El proponente y el crítico tienen que ser agentes distintos.", form.Problems);
    }

    [Fact]
    public async Task A_verdict_whose_challenge_is_in_course_cannot_be_challenged_again()
    {
        var (form, _) = await FormAsync(ChallengeStatus.Pending);
        form.Reasons = "Razones.";

        Assert.Equal("Impugnado: la revisión no se ha cerrado y la entrega espera.", form.ChallengeText);
        Assert.Contains("Este veredicto ya tiene una impugnación en curso: espera a que se cierre su revisión.", form.Problems);
    }

    [Fact]
    public async Task Without_the_requirements_of_the_verdict_the_revision_cannot_be_judged()
    {
        var (form, _) = await FormAsync(withRequirements: false);
        form.Reasons = "Razones.";

        Assert.Contains("No se pudieron leer los requisitos del veredicto impugnado: la revisión se juzga contra ellos.", form.Problems);
    }

    [Fact]
    public async Task Without_the_template_the_form_says_it_is_missing()
    {
        var backend = new DebateScreenBackend(
            Challenges: new FakeVerdictChallenges(),
            Requirements: new FakeRequirementStore(Requirements),
            Verdicts: new FakeVerdictStore(Stored("v-1", Challenged)));
        var form = new ChallengeFormViewModel(backend, new FakeClock(Now));
        await form.LoadAsync(Project, "v-1", TestContext.Current.CancellationToken);
        form.Reasons = "Razones.";

        Assert.Contains("Falta la plantilla de agentes en esta composición: no hay quien defienda el veredicto ni la impugnación, ni jueces.", form.Problems);
        Assert.DoesNotContain(form.Problems, p => p.StartsWith("Elige quién defiende", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Challenging_opens_the_revision_debate_and_hands_it_to_the_room()
    {
        var run = new FakeDebateRun(Snapshot());
        var (form, challenges) = await FormAsync();
        challenges.Challenge = _ => run;
        form.Reasons = "Razones.";
        OpenedDebate? opened = null;
        form.Opened += (_, debate) => opened = debate;
        await form.PrepareCommand.ExecuteAsync(null);

        await form.ChallengeCommand.ExecuteAsync(null);

        Assert.Single(challenges.Challenged);
        Assert.Same(run, opened!.Run);
        Assert.Equal($"Impugnación registrada: la revisión es el debate {run.Id}.", form.StatusMessage);
    }
}
