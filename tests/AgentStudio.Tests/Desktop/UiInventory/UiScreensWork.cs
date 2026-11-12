using AgentStudio.Application.Projects;
using AgentStudio.Application.Work;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Work;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work;
using AgentStudio.Domain.Work.Backlog;
using AgentStudio.Domain.Work.Milestones;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The «Trabajo» document (Phase 9): one screen per section, over fakes of the services with synthetic data, the way the document is shown
/// when a project has a milestone open, a closed one with its report, a backlog, a board with a card in review, a chain, the participants, a link
/// and a suggestion, and a budget. A section is a view that is only shown while it is the one chosen, so each has its own screen.
/// </summary>
internal static class UiScreensWork
{
    private static readonly ProjectContext Project = new("p-work", @"C:\work\calc");

    private static readonly MilestoneId Open = new("mil-20261110-a1b2c3");

    private static readonly MilestoneId Closed = new("mil-20261101-d4e5f6");

    private static readonly string Commit = new('a', 40);

    private static MilestoneView Milestone(MilestoneId id, string name, MilestoneStatus status, WorkBudget? budget, params MilestoneReportReference[] reports) =>
        new(
            new MilestoneHeader(id, name, "Que Calc multiplique y divida", new string('c', 64), budget, T0),
            status,
            new string('c', 64),
            [new string('c', 64)],
            [new MilestoneTarget(MilestoneTargetKind.Run, "20261111-a1b2c3"), new MilestoneTarget(MilestoneTargetKind.Debate, "deb-20261111-a1b2c3")],
            reports,
            []);

    private sealed class FakeMilestones : IMilestoneService
    {
        private readonly List<MilestoneView> _views =
        [
            Milestone(Open, "Primera entrega", MilestoneStatus.Open, new WorkBudget(250_000, 3)),
            Milestone(Closed, "Cimientos", MilestoneStatus.Closed, null, new MilestoneReportReference(1, "aud-20261102-aaaaaa", new EvidenceId("ev-informe"), new string('d', 64), Commit, T0)),
        ];

        public Task<IReadOnlyList<MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MilestoneView>>(_views);

        public Task<MilestoneView?> GetAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken) =>
            Task.FromResult(_views.FirstOrDefault(v => v.Header.Id.Value == milestone.Value));

        public Task<MilestoneView> OpenAsync(OpenMilestoneRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView> ReviseRequirementsAsync(ProjectContext project, MilestoneId milestone, FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView> LinkAsync(ProjectContext project, MilestoneId milestone, MilestoneTarget target, string linkedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MilestoneView> ReopenAsync(ProjectContext project, MilestoneId milestone, MilestoneReopenReason reason, EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeBacklog : IBacklogService
    {
        private static readonly IReadOnlyList<BacklogEntry> Entries =
        [
            new(new BacklogItem(new BacklogItemId("bk-20261108-aaaaaa"), 2, "Calc multiplica dos números", RequirementPriority.Mandatory, [new AcceptanceCriterion("C1", "Multiply(2,3) da 6", VerificationMethod.AutomatedTest)], T0), 2),
            new(new BacklogItem(new BacklogItemId("bk-20261108-bbbbbb"), 1, "Calc está documentada", RequirementPriority.Desirable, [], T0), 1),
        ];

        public Task<IReadOnlyList<BacklogEntry>> ListAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult(Entries);

        public Task<BacklogItem> AddAsync(ProjectContext project, BacklogDraft draft, string addedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BacklogItem> ReviseAsync(ProjectContext project, BacklogItemId item, BacklogDraft draft, string revisedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RetireAsync(ProjectContext project, BacklogItemId item, string retiredBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReorderAsync(ProjectContext project, IReadOnlyList<BacklogItemId> items, string decidedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BacklogItem>> HistoryAsync(ProjectContext project, BacklogItemId item, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeBoard : IBoardProjector
    {
        public Task<MilestoneBoard> ProjectAsync(ProjectContext project, MilestoneId milestone, CancellationToken cancellationToken) =>
            Task.FromResult(new MilestoneBoard(
                milestone,
                120,
                new string('e', 64),
                [
                    new(BoardCardKind.Requirement, "R1", "Calc multiplica dos números", BoardColumn.Done, "20261111-a1b2c3", null),
                    new(BoardCardKind.Requirement, "R2", "Calc divide", BoardColumn.InReview, "20261111-a1b2c3", BoardCardReasons.Challenged),
                    new(BoardCardKind.Requirement, "R3", "Calc está documentada", BoardColumn.Planned, "20261111-a1b2c3", null),
                ],
                [
                    new(BoardCardKind.Task, "T1", "Tarea T1", BoardColumn.Done, "20261111-a1b2c3", null),
                    new(BoardCardKind.Task, "T2", "Tarea T2", BoardColumn.InProgress, "20261111-a1b2c3", BoardCardReasons.WaitingForQuota),
                ]));

        public Task<IReadOnlyList<BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BoardCard>>([new(BoardCardKind.Requirement, "bk-20261108-bbbbbb", "Calc está documentada", BoardColumn.Backlog, null, null)]);
    }

    private sealed class FakeChain : ITraceChainReader
    {
        public Task<TraceChain> ChainAsync(ProjectContext project, TraceRef start, CancellationToken cancellationToken) =>
            Task.FromResult(new TraceChain(start,
            [
                new(TraceChainStage.Requirement, TraceRef.Requirement("R1"), "Calc multiplica dos números", false),
                new(TraceChainStage.Milestone, TraceRef.Milestone(Open.Value), "Primera entrega", false),
                new(TraceChainStage.Task, TraceRef.Task("20261111-a1b2c3", "T1"), "Tarea T1", false),
                new(TraceChainStage.Attempt, TraceRef.Attempt("20261111-a1b2c3", "T1", 1), null, false),
                new(TraceChainStage.Change, TraceRef.Evidence("ev-diff"), "Diff del intento 1", false),
                new(TraceChainStage.Verification, TraceRef.Commit(Commit), null, true),
                new(TraceChainStage.Verdict, TraceRef.Verdict("v1"), null, false),
                new(TraceChainStage.Delivery, TraceRef.Commit(new string('b', 40)), null, false),
            ]));
    }

    private sealed class FakeParticipation : IParticipationProjector
    {
        public Task<ParticipationView> ProjectAsync(ProjectContext project, TraceRef subject, CancellationToken cancellationToken) =>
            Task.FromResult(new ParticipationView(subject,
            [
                new(ParticipationRole.ProducingAgent, "ejecutor", "2 intento(s)", ParticipationSources.Attempt),
                ParticipationEntry.NotRecorded(ParticipationRole.GitAuthorAndCommitter),
                ParticipationEntry.NotRecorded(ParticipationRole.PublishingAccount),
                new(ParticipationRole.Reviewer, "juez", "Approved", ParticipationSources.Verdict),
                new(ParticipationRole.Approver, "user", "integrate.deliver", ParticipationSources.Approval),
                ParticipationEntry.NotRecorded(ParticipationRole.Integrator),
            ]));
    }

    private sealed class FakeLinks : ITraceLinkService, ITraceSuggester
    {
        public Task<IReadOnlyList<TraceLink>> LinksAroundAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TraceLink>>([new TraceLink(TraceRef.Requirement("R1"), TraceRef.Commit(Commit), TraceAssertedBy.User, "Lo escribí a mano", T0)]);

        public Task<IReadOnlyList<TraceSuggestion>> SuggestAsync(ProjectContext project, TraceRef node, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TraceSuggestion>>([new TraceSuggestion(TraceRef.Commit(new string('b', 40)), TraceRef.Requirement("R1"), TraceSuggestionSource.CommitMessageMention, "Refs R1")]);

        public Task<TraceLink> RecordAsync(ProjectContext project, TraceRef from, TraceRef to, TraceAssertedBy assertedBy, string reason, string decidedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TraceLink> ConfirmAsync(ProjectContext project, TraceSuggestion suggestion, string confirmedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RetractAsync(ProjectContext project, string linkId, string retractedBy, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeBudgets : IProjectBudgetStore
    {
        public Task<WorkBudget?> LoadAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<WorkBudget?>(new WorkBudget(1_000_000, null, BudgetPeriod.Week));

        public Task SaveAsync(ProjectContext project, WorkBudget? budget, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWorkspace : IProjectWorkspace
    {
        public Task<ProjectContext> OpenAsync(string projectRoot, CancellationToken cancellationToken) => Task.FromResult(Project);
    }

    private static async Task<WorkDocumentViewModel> DocumentAsync()
    {
        var links = new FakeLinks();
        var backend = new WorkBackend(new FakeWorkspace(), new FakeMilestones(), null, new FakeBacklog(), new FakeBoard(), links, links, new FakeChain(), new FakeParticipation(), new FakeBudgets());
        var time = new FakeTimeProvider(T0);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var document = new WorkDocumentViewModel(backend, time);
        await document.SetProjectAsync(Project.RootPath);
        return document;
    }

    private static UiScreen SectionScreen(string name, Func<WorkDocumentViewModel, WorkSection> choose, Func<WorkDocumentViewModel, Task>? prepare = null) =>
        UiScreens.ScreenAsync(name, async () =>
        {
            var document = await DocumentAsync();
            document.Milestones.SelectedItem = document.Milestones.Items.First(m => m.Name == "Primera entrega");
            if (prepare is not null)
            {
                await prepare(document);
            }

            document.SelectedSection = choose(document);
            return UiScreens.Document(document);
        }, 1180, 1500);

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        SectionScreen("Work · milestones", d => d.Milestones, d =>
        {
            d.Milestones.OpenedReport = new EvidenceViewer("ev-informe", "Informe del hito", "Informe · generado por el sistema", "Misma huella: 0123456789ab", Tone.Success, "{ \"formato\": \"informe\" }");
            d.Milestones.NewName = "Segunda entrega";
            return Task.CompletedTask;
        }),
        SectionScreen("Work · closed milestone", d => d.Milestones, d =>
        {
            d.Milestones.SelectedItem = d.Milestones.Items.First(m => m.Name == "Cimientos");
            return Task.CompletedTask;
        }),
        SectionScreen("Work · backlog", d => d.Backlog, d =>
        {
            d.Backlog.SelectedItem = d.Backlog.Items.First();
            return Task.CompletedTask;
        }),
        SectionScreen("Work · board", d => d.Board),
        SectionScreen("Work · chain", d => d.Chain, async d =>
        {
            d.Context.LookAt(TraceRef.Requirement("R1"));
            await d.Chain.ReloadAsync();
            d.Chain.OpenedProof = new EvidenceViewer("ev-diff", "Diff del intento 1", "Diff · capturada por el sistema", "Misma huella: 0123456789ab", Tone.Success, "--- a/Calc.cs\n+++ b/Calc.cs");
            d.Chain.OpenedVerdict = new VerdictGlance("Veredicto v1", "Aprobado", "Juez: juez (Google)", "Es válido.", "Cumple lo pedido.");
        }),
        SectionScreen("Work · participation", d => d.Participation, async d =>
        {
            d.Context.LookAt(TraceRef.Task("20261111-a1b2c3", "T1"));
            await d.Participation.ReloadAsync();
        }),
        SectionScreen("Work · links", d => d.Links, async d =>
        {
            d.Context.LookAt(TraceRef.Requirement("R1"));
            await d.Links.ReloadAsync();
        }),
        SectionScreen("Work · budgets", d => d.Budgets),
    ];
}
