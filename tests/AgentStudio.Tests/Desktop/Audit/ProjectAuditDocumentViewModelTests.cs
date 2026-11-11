using AgentStudio.Application.Judging;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Audit;

/// <summary>
/// «Auditar proyecto completo» (docs/15 D-28 to D-31 and D-43): the preparation sends the latest frozen requirements, the
/// judge and the authors you declare and writes nothing; the audit asks its approval through the service; and the report
/// says whether it can be trusted and why, with the verification of HEAD and the problems the system lists without a judge.
/// </summary>
public sealed class ProjectAuditDocumentViewModelTests
{
    private static async Task<(ProjectAuditDocumentViewModel Document, FakeProjectAuditService Audits, FakeReviewNavigation Navigation)> DocumentAsync(
        FakeProjectAuditService? audits = null,
        bool withRequirements = true)
    {
        var service = audits ?? new FakeProjectAuditService();
        var navigation = new FakeReviewNavigation();
        var backend = new DebateScreenBackend(
            Challenges: new FakeVerdictChallenges(),
            Audits: service,
            Workspace: new FakeWorkspace(),
            Roster: new FakeRoster(() => Roster),
            Requirements: withRequirements ? new FakeRequirementStore(Requirements) : new FakeRequirementStore(),
            Verdicts: new FakeVerdictStore());
        var screens = new DebateScreens(backend, new FakeClock(Now), () => navigation);
        var document = new ProjectAuditDocumentViewModel(Root, backend, new FakeClock(Now), screens, new InlineSynchronizationContext());
        await document.OpenAsync();
        return (document, service, navigation);
    }

    [Fact]
    public async Task The_preparation_sends_the_latest_requirements_the_judge_and_your_declared_authors()
    {
        var (document, audits, _) = await DocumentAsync();
        Assert.Equal(["juez-claude", "juez-revision"], document.Judges.Select(j => j.Agent.Id.Value));
        document.SelectedJudge = document.Judges.Single(j => j.Agent.Id.Value == "juez-revision");
        document.Authors.Single(a => a.Vendor == VendorId.Anthropic).IsDeclared = true;

        await document.PrepareCommand.ExecuteAsync(null);

        var request = Assert.Single(audits.Prepared);
        Assert.Equal(Requirements.ContentHash, request.RequirementSetHash);
        Assert.Equal("juez-revision", request.Judge.Value);
        Assert.Equal([VendorId.Anthropic], request.DeclaredAuthors);
        Assert.StartsWith("Se audita contra los últimos requisitos congelados: 2 requisitos", document.RequirementsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_proposal_shows_the_commit_the_changes_left_out_the_skipped_steps_and_the_estimate()
    {
        var (document, _, _) = await DocumentAsync();

        await document.PrepareCommand.ExecuteAsync(null);

        Assert.Equal("Se audita la última versión guardada (commit HEAD 777777777777).", document.HeadText);
        Assert.Equal("Hay cambios sin guardar como versión (commit): no entran en la auditoría, y el informe lo dirá.", document.UncommittedText);
        Assert.Equal("Juez: juez-revision · Codex CLI · OpenAI · modelo predeterminado del programa", document.JudgeText);
        Assert.Equal("Estas empresas produjeron el código: Anthropic. El juez no puede ser de ninguna de ellas.", document.ProducersText);
        Assert.Equal(["e2e: no se ejecuta en una auditoría, porque lo haría con tus permisos."], document.SkippedSteps);
        Assert.Equal(["test · Pruebas · dotnet test"], document.ProfileSteps);
        Assert.Equal("Estimación: ~320.000 tokens (estimado por Agent Studio), más como mucho una corrección del juez. Verificar no gasta el límite de uso de tu plan.", document.EstimateText);
        Assert.True(document.AuditCommand.CanExecute(null));
    }

    [Fact]
    public async Task With_nobody_to_judge_the_audit_still_runs_and_says_it_only_reports_what_the_system_checks()
    {
        var audits = new FakeProjectAuditService();
        audits.Prepare = request => new ProjectAuditProposal(request, Commit, false, null, [VendorId.Anthropic, VendorId.OpenAI], null, [], MetricValueNone, ["audit.no-judge: los dos vendors produjeron código"], new string('e', 64), "Auditar sin juez.");
        var (document, _, _) = await DocumentAsync(audits);

        await document.PrepareCommand.ExecuteAsync(null);

        Assert.Equal("Nadie puede juzgar: quien produjo el código no puede ser juez, y esa regla no admite excepciones.", document.JudgeText);
        Assert.Equal(["audit.no-judge: los dos vendors produjeron código"], document.ProposalProblems);
        Assert.StartsWith("«Auditar» no lanzará a ningún juez", document.AuditMode, StringComparison.Ordinal);
        Assert.Equal("Sin juez no se gasta el límite de uso de tu plan. Solo se verifica la última versión guardada y se listan los problemas pendientes.", document.EstimateText);
        Assert.True(document.AuditCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_report_says_whether_it_can_be_trusted_with_the_verification_the_pending_problems_and_the_card()
    {
        var (document, audits, navigation) = await DocumentAsync();
        await document.PrepareCommand.ExecuteAsync(null);

        await document.AuditCommand.ExecuteAsync(null);

        Assert.Single(audits.Audited);
        var report = Assert.IsType<AuditReportViewModel>(document.Report);
        Assert.True(document.ShowsReport);
        Assert.Equal("Fiable: lo emitió un juez, pasó la validación y el historial de decisiones está íntegro.", report.TrustText);
        Assert.Equal(Tone.Success, report.TrustTone);
        Assert.Equal("Versión guardada (commit) auditada: 777777777777", report.CommitText);
        Assert.Equal("Hay cambios sin guardar como versión (commit): no entran en la auditoría.", report.UncommittedText);
        Assert.Equal("Conclusión: Cambios solicitados", report.DispositionText);
        Assert.Equal(new VerificationRow("dotnet test (HEAD)", "Pruebas · capturada por el sistema · versión guardada (commit) 777777777777", "5354 correctas, 0 fallidas, 12 omitidas"), Assert.Single(report.Verification));
        Assert.Equal(["La ejecución 20261018-c4a1f2 espera a una persona. · 20261018-c4a1f2 (run.awaiting-human)", "Una propuesta de memoria sin revisar. · m-12 (memory.proposal-unreviewed)"], report.Pending);
        var card = Assert.IsType<VerdictCardViewModel>(report.Card);
        Assert.Equal("Cambios solicitados · veredicto válido", card.OutcomeText);
        Assert.True(card.Verdicts[0].CanBeChallenged);
        Assert.True(card.CanCreateCorrectiveTasks);
        Assert.StartsWith("Tareas correctivas de «Veredicto de la auditoría» (veredictos ", card.CorrectiveRequest, StringComparison.Ordinal);

        await card.CreateCorrectiveTasksCommand.ExecuteAsync(null);
        Assert.Equal(Root, Assert.Single(navigation.CorrectiveTasks).ProjectRoot);
    }

    [Fact]
    public async Task A_broken_log_makes_the_report_untrustworthy_and_without_a_judge_there_is_no_card()
    {
        var audits = new FakeProjectAuditService { Audit = (_, _) => AuditReport(verdictId: null, intact: false) };
        var (document, _, _) = await DocumentAsync(audits);
        await document.PrepareCommand.ExecuteAsync(null);

        await document.AuditCommand.ExecuteAsync(null);

        var report = document.Report!;
        Assert.Equal("No fiable: el historial de decisiones no está íntegro, ningún juez emitió un veredicto.", report.TrustText);
        Assert.Equal(Tone.Error, report.TrustTone);
        Assert.StartsWith("El historial de decisiones está roto. No se lanzó al juez", report.IntegrityText, StringComparison.Ordinal);
        Assert.StartsWith("Lo escaló el sistema, no un juez:", report.EscalationText, StringComparison.Ordinal);
        Assert.Null(report.Card);
    }

    [Fact]
    public async Task The_judges_events_while_auditing_are_listed_and_auditing_again_asks_the_preparation_again()
    {
        var audits = new FakeProjectAuditService
        {
            Audit = (_, progress) =>
            {
                progress?.Report(new SessionStarted(Now, "s-1", null));
                return AuditReport();
            },
        };
        var (document, _, _) = await DocumentAsync(audits);
        await document.PrepareCommand.ExecuteAsync(null);
        await document.AuditCommand.ExecuteAsync(null);
        Assert.Equal(["Sesión iniciada"], document.Activity.Select(a => a.Text));

        await document.AuditAgainCommand.ExecuteAsync(null);

        Assert.Null(document.Report);
        Assert.True(document.ShowsPreparation);
        Assert.Equal(2, audits.Prepared.Count);
        Assert.True(document.HasProposal);
    }

    [Fact]
    public async Task Without_frozen_requirements_nothing_is_audited_and_the_document_says_where_to_freeze_them()
    {
        var (document, _, _) = await DocumentAsync(withRequirements: false);

        Assert.Contains("Sin requisitos congelados no se audita. Congélalos en «Requisitos», en el orquestador.", document.Problems);
        Assert.False(document.PrepareCommand.CanExecute(null));
    }

    private static MetricValue<long> MetricValueNone => MetricValue<long>.NotExposed("no judge runs");
}
