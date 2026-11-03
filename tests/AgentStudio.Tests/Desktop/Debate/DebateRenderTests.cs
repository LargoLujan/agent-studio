using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Views.Audit;
using AgentStudio.Desktop.Views.Debate;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The Phase 5 screens drawn without ever being shown (<see cref="OffscreenRender"/>): measured, arranged and rendered into
/// a bitmap on the WPF test thread, with no window and no HWND. They prove the templates, the theme and the bindings make
/// a picture, and what has to be in the warning colour is; with <c>AGENTSTUDIO_RENDER_EVIDENCE</c> set the pictures are
/// saved for docs/evidencias (synthetic data only).
/// </summary>
public sealed class DebateRenderTests
{
    private static readonly Color Surface = Color.FromRgb(0x10, 0x1A, 0x30);
    private static readonly Color Raised = Color.FromRgb(0x16, 0x22, 0x3D);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xB5, 0x44);
    private static readonly Color Success = Color.FromRgb(0x2F, 0xD0, 0x8A);
    private static readonly Color Accent = Color.FromRgb(0x2E, 0x68, 0xF9);
    private static readonly Color Error = Color.FromRgb(0xFF, 0x5C, 0x6C);

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private static DebateRoomContext Context => new("¿Procesos aislados por agente o un único proceso con hilos?", Requirements, DebateJudgingKind.CrossVendorPanel, [VendorId.Anthropic, VendorId.OpenAI], [ClaudeJudge, CodexJudge]);

    [Fact]
    public void The_room_renders_offscreen_with_round_two_sealed_and_the_contributions_of_round_one()
    {
        var (drawn, accent, warning, room) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = new DebateRoomViewModel(new FakeClock(Now), new InlineSynchronizationContext());
            model.Attach(new FakeDebateRun(Snapshot(turnsInFlight: 2, contributions: RoundOne, tokens: Tokens(84_000))), Project, Context);
            var view = new DebateRoomView { DataContext = model };

            var bitmap = OffscreenRender.Render(view, 1180, 1180);
            OffscreenRender.SaveIfRequested(bitmap, "fase5-sala.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Accent), OffscreenRender.Count(bitmap, Warning), model);
        });

        Assert.Equal("Ronda 2: 2 turnos en curso, sellados hasta que terminen todos", room.SealedText);
        Assert.Equal([1], room.Rounds.Select(r => r.Round));
        Assert.True(drawn > 40_000, $"Only {drawn} pixels differ from the panel: the room was not drawn.");
        Assert.True(accent > 40, $"Only {accent} pixels have the accent colour (the sealed round and the active phase).");
        Assert.True(warning > 40, $"Only {warning} pixels have the warning colour (the quote that did not resolve).");
    }

    [Fact]
    public void The_card_renders_offscreen_with_the_panel_side_by_side_the_waiver_and_deciding()
    {
        var (drawn, warning, raised, panels) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var context = new VerdictCardContext("Veredicto del debate deb-20261019-a1b2c3", DebateJudgingKind.CrossVendorPanel, [VendorId.Anthropic, VendorId.OpenAI], Participants, Requirements, Choice: true, Proposals: ["P1", "P2"]);
            var actions = new VerdictCardActions(_ => Task.CompletedTask, (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask);
            var model = new VerdictCardViewModel(PanelThatDisagrees, Disagree, context, Zone, actions);
            var view = new VerdictCardView { DataContext = model };
            var panel = new Border { Background = new SolidColorBrush(Surface), Padding = new Thickness(12), Child = view };

            var bitmap = OffscreenRender.Render(panel, 1320, 1180);
            OffscreenRender.SaveIfRequested(bitmap, "fase5-tarjeta-jurado.png");
            // The item containers of the verdicts: one cell each of the grid that lays them out side by side.
            var positions = Descendants<UniformGrid>(view).Single().Children.Cast<UIElement>()
                .Select(cell => cell.TranslatePoint(new Point(0, 0), view))
                .ToList();
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Warning), OffscreenRender.Count(bitmap, Raised), positions);
        });

        Assert.Equal(2, panels.Count);
        Assert.Equal(panels[0].Y, panels[1].Y, 1);
        Assert.True(panels[1].X - panels[0].X > 400, $"The two verdicts are not side by side: {panels[0]} and {panels[1]}.");
        Assert.True(drawn > 60_000, $"Only {drawn} pixels differ from the panel: the card was not drawn.");
        Assert.True(warning > 200, $"Only {warning} pixels have the warning colour (the waiver, the outcome and «Decidir yo»).");
        Assert.True(raised > 20_000, $"Only {raised} pixels have the card colour.");
    }

    [Fact]
    public async Task The_audit_report_renders_offscreen_with_its_trust_verification_pending_problems_and_card()
    {
        var document = await WpfHost.RunAsync(async () =>
        {
            var backend = new DebateScreenBackend(
                Challenges: new FakeVerdictChallenges(),
                Audits: new FakeProjectAuditService(),
                Workspace: new FakeWorkspace(),
                Roster: new FakeRoster(() => Roster),
                Requirements: new FakeRequirementStore(Requirements),
                Verdicts: new FakeVerdictStore());
            var navigation = new FakeReviewNavigation();
            var model = new ProjectAuditDocumentViewModel(Root, backend, new FakeClock(Now), new DebateScreens(backend, new FakeClock(Now), () => navigation), new InlineSynchronizationContext());
            await model.OpenAsync();
            model.SelectedJudge = model.Judges.Single(j => j.Agent.Id.Value == "juez-revision");
            model.Authors.Single(a => a.Vendor == VendorId.Anthropic).IsDeclared = true;
            await model.PrepareCommand.ExecuteAsync(null);
            await model.AuditCommand.ExecuteAsync(null);
            return model;
        });

        var (drawn, success, warning) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new ProjectAuditView { DataContext = document };
            var bitmap = OffscreenRender.Render(view, 1180, 1500);
            OffscreenRender.SaveIfRequested(bitmap, "fase5-informe-auditoria.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Success), OffscreenRender.Count(bitmap, Warning));
        });

        Assert.True(document.ShowsReport);
        Assert.True(drawn > 60_000, $"Only {drawn} pixels differ from the panel: the report was not drawn.");
        Assert.True(success > 30, $"Only {success} pixels have the success colour (the report can be trusted).");
        Assert.True(warning > 100, $"Only {warning} pixels have the warning colour (the changes left out and the pending problems).");
    }

    [Fact]
    public async Task The_monitor_renders_offscreen_with_the_auditor_the_request_to_replan_and_the_card_of_the_review()
    {
        var monitor = await WpfHost.RunAsync(async () =>
        {
            var auditor = new FakeOrchestratorAuditService
            {
                Report = run => new OrchestratorAuditReport(
                    run,
                    DecisionLogVerification.Valid,
                    [
                        new OrchestratorFinding("orchestrator.review-omitted", Severity.High, [new TaskId("T3")], [12], "La tarea T3 exigía revisión independiente y no la tuvo."),
                        new OrchestratorFinding("orchestrator.instructions", Severity.Medium, [new TaskId("T2")], [], "Las instrucciones de T2 no citan el requisito R2."),
                    ],
                    [40, 41],
                    true),
            };
            var challenges = new FakeVerdictChallenges();
            challenges.Statuses["veredicto-1"] = ChallengeStatus.None;
            var verdicts = new FakeVerdictStore(Stored("veredicto-1", ReviewVerdict(CodexJudge, VerdictDecision.Approved), run: "20261018-c4a1f2"));
            var delivery = new FakeDeliveryService { Readiness = new(false, null, "El auditor pidió replanificar: prepara una ejecución de seguimiento o descarta la petición antes de entregar.") };
            var model = new RunMonitorViewModel(new FakeClock(Now), new InlineSynchronizationContext(), delivery)
            {
                Review = new RunReviewServices(auditor, verdicts, challenges, new FakeApprovalGate(), new FakeRequirementStore(Requirements), (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
                PrepareFollowUp = (_, _) => Task.CompletedTask,
            };
            var run = new FakeRun(new RunSnapshot(
                new RunId("20261018-c4a1f2"),
                RunPhase.Running,
                [
                    new TaskView(new TaskId("T1"), "Implementar Calc.Multiply", WorkTaskKind.AgentWork, WorkTaskStatus.Completed, new AgentId("ejecutor"), 1, 0, null, null, null, []),
                    new TaskView(new TaskId("T3"), "Revisión independiente", WorkTaskKind.Review, WorkTaskStatus.Completed, new AgentId("juez-revision"), 1, 0, null, null, null, []),
                ],
                FakeRun.Healthy,
                AgentRunsUsed: 3));
            model.Attach(run, Project);
            run.Finish(new RunResult(new RunId("20261018-c4a1f2"), RunPhase.Succeeded, FakeRun.Healthy, Commit, "veredicto-1", "La ejecución terminó."));
            await model.Listening;
            await model.AuditCheck;
            await model.DeliveryCheck;
            await model.ReviewCheck;
            return model;
        });

        var (drawn, warning, error) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new RunMonitorView { DataContext = monitor };
            var bitmap = OffscreenRender.Render(view, 1440, 980);
            OffscreenRender.SaveIfRequested(bitmap, "fase5-monitor-auditor.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Warning), OffscreenRender.Count(bitmap, Error));
        });

        Assert.True(monitor.HasReviewSection);
        Assert.NotNull(monitor.ReviewCard);
        Assert.True(drawn > 60_000, $"Only {drawn} pixels differ from the panel: the review was not drawn.");
        Assert.True(warning > 100, $"Only {warning} pixels have the warning colour (the request to replan).");
        Assert.True(error > 20, $"Only {error} pixels have the error colour (the finding of high severity).");
    }

    [Fact]
    public async Task The_new_debate_form_renders_offscreen_with_the_disposition_the_waiver_and_the_estimate()
    {
        var form = await WpfHost.RunAsync(async () =>
        {
            var model = new NewDebateViewModel(new DebateScreenBackend(new FakeDebateCoordinator(), Roster: new FakeRoster(() => Roster)), new FakeClock(Now));
            await model.LoadAsync(Project);
            model.SelectedMode = model.Modes.Single(m => m.Value == DebateMode.ArchitectureReview);
            model.Question = "¿Procesos aislados por agente o un único proceso con hilos?";
            foreach (var participant in model.Participants.Where(p => p.Agent.Id.Value is "arquitecto" or "critico"))
            {
                participant.IsIncluded = true;
            }

            model.SelectedJudging = model.JudgingKinds.Single(k => k.Value == DebateJudgingKind.CrossVendorPanel);
            await model.PrepareCommand.ExecuteAsync(null);
            return model;
        });

        var (drawn, warning) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new NewDebateView { DataContext = form };
            var bitmap = OffscreenRender.Render(view, 1100, 1500);
            OffscreenRender.SaveIfRequested(bitmap, "fase5-nuevo-debate.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Warning));
        });

        Assert.True(form.HasProposal);
        Assert.True(drawn > 50_000, $"Only {drawn} pixels differ from the panel: the form was not drawn.");
        Assert.True(warning > 50, $"Only {warning} pixels have the warning colour (the waiver of the panel).");
    }

    [Fact]
    public async Task The_debates_document_renders_offscreen_with_its_history_and_a_debate_read_back()
    {
        var document = await WpfHost.RunAsync(async () =>
        {
            var coordinator = new FakeDebateCoordinator { Read = _ => Transcript() };
            coordinator.Debates.Add(Summary("deb-20261019-a1b2c3", DebateMode.ArchitectureReview, DebatePhase.Closed, DebateOutcome.Unresolved, minutesAgo: 20, question: "¿Procesos aislados por agente o un único proceso con hilos?"));
            coordinator.Debates.Add(Summary("deb-20261019-0d0e0f", DebateMode.Adversarial, DebatePhase.Deliberation, DebateOutcome.Open, interrupted: true, minutesAgo: 90, question: "¿Basta la caché en memoria para el catálogo de skills?"));
            coordinator.Debates.Add(Summary("deb-20261018-ab12cd", DebateMode.Free, DebatePhase.Closed, DebateOutcome.Resolved, decision: VerdictDecision.Approved, chosen: "P2", minutesAgo: 1500, question: "¿Cómo nombramos los ficheros de evidencia?"));
            var backend = new DebateScreenBackend(coordinator, new FakeVerdictChallenges(), Workspace: new FakeWorkspace(), Roster: new FakeRoster(() => Roster), Requirements: new FakeRequirementStore(Requirements), Evidence: new FakeEvidenceStore());
            var model = new DebatesDocumentViewModel(Root, backend, new FakeClock(Now), null, new InlineSynchronizationContext());
            await model.OpenAsync();
            await model.OpenItemCommand.ExecuteAsync(model.History[0]);
            return model;
        });

        var (drawn, warning) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new DebatesView { DataContext = document };
            var bitmap = OffscreenRender.Render(view, 1500, 1100);
            OffscreenRender.SaveIfRequested(bitmap, "fase5-debates.png");
            return (OffscreenRender.CountUnlike(bitmap, Surface), OffscreenRender.Count(bitmap, Warning));
        });

        Assert.Equal(DebatesPart.Room, document.Part);
        Assert.True(drawn > 60_000, $"Only {drawn} pixels differ from the panel: the document was not drawn.");
        Assert.True(warning > 40, $"Only {warning} pixels have the warning colour (the interrupted debate and the unresolved quote).");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
