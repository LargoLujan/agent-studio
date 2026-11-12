using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Desktop.Audit;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Views.Audit;
using AgentStudio.Desktop.Views.Debate;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Memory;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>The screens of the debates and of the audit of the project (Phase 5), over the synthetic debates of <see cref="DebateSamples"/>.</summary>
internal static class UiScreensDebate
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    /// <summary>The synthetic debates are written in Spanish (a question, a contribution): data, not interface.</summary>
    private static readonly string[] SpanishData =
    [
        "Procesos aislados", "caché en memoria", "nombramos los ficheros", "Instrucciones de prueba", "Cada turno", "Cita ", "cita ",
        "La tarea", "Las instrucciones", "Implementar", "Revisión independiente", "ejecución", "Ejecución", "El auditor", "requisito",
    ];

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Debates", async () =>
        {
            var coordinator = new FakeDebateCoordinator { Read = _ => Transcript() };
            coordinator.Debates.Add(Summary("deb-20261019-a1b2c3", DebateMode.ArchitectureReview, DebatePhase.Closed, DebateOutcome.Unresolved, minutesAgo: 20, question: "¿Procesos aislados por agente o un único proceso con hilos?"));
            coordinator.Debates.Add(Summary("deb-20261019-0d0e0f", DebateMode.Adversarial, DebatePhase.Deliberation, DebateOutcome.Open, interrupted: true, minutesAgo: 90, question: "¿Basta la caché en memoria para el catálogo de skills?"));
            coordinator.Debates.Add(Summary("deb-20261018-ab12cd", DebateMode.Free, DebatePhase.Closed, DebateOutcome.Resolved, decision: VerdictDecision.Approved, chosen: "P2", minutesAgo: 1500, question: "¿Cómo nombramos los ficheros de evidencia?"));
            var backend = new DebateScreenBackend(coordinator, new FakeVerdictChallenges(), Workspace: new AgentStudio.Tests.Desktop.Skills.FakeWorkspace(), Roster: new AgentStudio.Tests.Desktop.Orchestrator.FakeRoster(() => Roster), Requirements: new AgentStudio.Tests.Desktop.Orchestrator.FakeRequirementStore(Requirements), Evidence: new FakeEvidenceStore());
            var model = new DebatesDocumentViewModel(Root, backend, new FakeClock(Now), null, new AgentStudio.Tests.Desktop.Orchestrator.InlineSynchronizationContext());
            await model.OpenAsync();
            await model.OpenItemCommand.ExecuteAsync(model.History[0]);
            return UiScreens.Document(model);
        }, 1500, 1100, SpanishData),
        UiScreens.ScreenAsync("New debate", async () =>
        {
            var model = new NewDebateViewModel(new DebateScreenBackend(new FakeDebateCoordinator(), Roster: new AgentStudio.Tests.Desktop.Orchestrator.FakeRoster(() => Roster)), new FakeClock(Now));
            await model.LoadAsync(Project);
            model.SelectedMode = model.Modes.Single(m => m.Value == DebateMode.ArchitectureReview);
            model.Question = "¿Procesos aislados por agente o un único proceso con hilos?";
            foreach (var participant in model.Participants.Where(p => p.Agent.Id.Value is "arquitecto" or "critico"))
            {
                participant.IsIncluded = true;
            }

            model.SelectedJudging = model.JudgingKinds.Single(k => k.Value == DebateJudgingKind.CrossVendorPanel);
            await model.PrepareCommand.ExecuteAsync(null);
            return UiScreens.Of(new NewDebateView(), model);
        }, 1100, 1500, SpanishData),
        UiScreens.Screen("Debate room", () =>
        {
            var model = new DebateRoomViewModel(new FakeClock(Now), new AgentStudio.Tests.Desktop.Orchestrator.InlineSynchronizationContext());
            var context = new DebateRoomContext("¿Procesos aislados por agente o un único proceso con hilos?", Requirements, DebateJudgingKind.CrossVendorPanel, [VendorId.Anthropic, VendorId.OpenAI], [ClaudeJudge, CodexJudge]);
            model.Attach(new FakeDebateRun(Snapshot(turnsInFlight: 2, contributions: RoundOne, tokens: Tokens(84_000))), Project, context);
            return UiScreens.Of(new DebateRoomView(), model);
        }, 1180, 1180, SpanishData),
        UiScreens.Screen("Verdict card", () =>
        {
            var context = new VerdictCardContext("Veredicto del debate deb-20261019-a1b2c3", DebateJudgingKind.CrossVendorPanel, [VendorId.Anthropic, VendorId.OpenAI], Participants, Requirements, Choice: true, Proposals: ["P1", "P2"]);
            var actions = new VerdictCardActions(_ => Task.CompletedTask, (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask);
            return UiScreens.Of(new VerdictCardView(), new VerdictCardViewModel(PanelThatDisagrees, Disagree, context, Zone, actions));
        }, 1320, 1180, SpanishData),
        UiScreens.ScreenAsync("Challenge form", async () =>
        {
            var challenges = new FakeVerdictChallenges();
            challenges.Statuses["v-1"] = ChallengeStatus.None;
            var backend = new DebateScreenBackend(
                Challenges: challenges,
                Roster: new AgentStudio.Tests.Desktop.Orchestrator.FakeRoster(() => Roster),
                Requirements: new AgentStudio.Tests.Desktop.Orchestrator.FakeRequirementStore(Requirements),
                Verdicts: new FakeVerdictStore(Stored("v-1", VerdictOf(CodexJudge, VerdictDecision.Approved, null) with { Resolution = null })));
            var model = new ChallengeFormViewModel(backend, new FakeClock(Now));
            await model.LoadAsync(Project, "v-1");
            return UiScreens.Of(new ChallengeFormView(), model);
        }, 1100, 900, SpanishData),
        UiScreens.ScreenAsync("Project audit", async () =>
        {
            var backend = new DebateScreenBackend(
                Challenges: new FakeVerdictChallenges(),
                Audits: new FakeProjectAuditService(),
                Workspace: new AgentStudio.Tests.Desktop.Skills.FakeWorkspace(),
                Roster: new AgentStudio.Tests.Desktop.Orchestrator.FakeRoster(() => Roster),
                Requirements: new AgentStudio.Tests.Desktop.Orchestrator.FakeRequirementStore(Requirements),
                Verdicts: new FakeVerdictStore());
            var navigation = new FakeReviewNavigation();
            var model = new ProjectAuditDocumentViewModel(Root, backend, new FakeClock(Now), new DebateScreens(backend, new FakeClock(Now), () => navigation), new AgentStudio.Tests.Desktop.Orchestrator.InlineSynchronizationContext());
            await model.OpenAsync();
            model.SelectedJudge = model.Judges.Single(j => j.Agent.Id.Value == "juez-revision");
            model.Authors.Single(a => a.Vendor == VendorId.Anthropic).IsDeclared = true;
            await model.PrepareCommand.ExecuteAsync(null);
            await model.AuditCommand.ExecuteAsync(null);
            return UiScreens.Of(new ProjectAuditView(), model);
        }, 1180, 1500, SpanishData),
    ];
}
