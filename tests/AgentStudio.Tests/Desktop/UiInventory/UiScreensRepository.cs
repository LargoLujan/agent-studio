using AgentStudio.Application.Git;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.Repository;
using AgentStudio.Desktop.Views.Observability;
using AgentStudio.Desktop.Views.Repository;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Tracing;
using AgentStudio.Tests.Desktop.Observability;
using AgentStudio.Tests.Desktop.RepositoryScreen;
using AgentStudio.Tests.Memory;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The screens of Phase 9 that show «Repositorio y cambios» and the usage by milestone and activity, over synthetic data: its four
/// sections, each in its tab, and the screen when the hardened layer refuses the repository. The data carries no words (identifiers
/// and figures), so the check of the language reads only the interface.
/// </summary>
internal static class UiScreensRepository
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(2));

    private static readonly string Sha1 = FakeHistory.Sha('1');
    private static readonly string Sha2 = FakeHistory.Sha('2');
    private static readonly string Sha3 = FakeHistory.Sha('3');

    private static CommitNode Node(string id, string subject) =>
        new(id, [], new DateTimeOffset(2026, 11, 12, 8, 30, 0, TimeSpan.Zero), subject, "ci-bot", id == Sha1 ? ["main"] : [], null);

    /// <summary>A repository with a commit of the system, one with a suggestion, one with a link and one the log knows nothing of.</summary>
    private static async Task<RepositoryViewModel> LoadedAsync(RepositorySection section, bool refused = false)
    {
        var workspace = new FakeWorkspace();
        var history = new FakeHistory();
        history.Branches.AddRange([new BranchSummary("main", Sha1, true, BranchKind.Local), new BranchSummary("wip-4", Sha2, false, BranchKind.Local), new BranchSummary("agentstudio/run-20261112-a1b2c3", Sha3, false, BranchKind.Run)]);
        history.Summary = refused
            ? new RepositorySummary(@"C:\work\calc", false, null, null, false, ["unsafe-configuration"])
            : new RepositorySummary(@"C:\work\calc", true, "main", Sha1, true, []);
        history.Commits.AddRange([Node(Sha1, "T-101 calc-multiply"), Node(Sha2, "wip-4f2a"), Node(Sha3, "T-102 notes"), Node(FakeHistory.Sha('4'), "wip-9c1d")]);

        var annotator = new FakeAnnotator();
        annotator.Annotations[Sha1] = new CommitAnnotation("20261112-a1b2c3", "T2", 1, new AgentId("agent-1"), "model-1", [], []);
        annotator.Annotations[Sha2] = new CommitAnnotation(
            null, null, null, null, null, [],
            [new TraceSuggestion(TraceRef.Commit(Sha2), TraceRef.Task("20261112-a1b2c3", "T2"), TraceSuggestionSource.CommitTrailer, "trailer")]);
        annotator.Annotations[Sha3] = new CommitAnnotation(
            null, null, null, null, null,
            [new TraceLink(TraceRef.Commit(Sha3), TraceRef.Requirement("R-4"), TraceAssertedBy.User, "link", Now)],
            []);

        var activity = new FakeActivity();
        activity.Events.AddRange(
        [
            new RepositoryActivity(Now.AddMinutes(-30), RepositoryActivityKind.Integration, "20261112-a1b2c3", "T1", Sha1, true, null, 1),
            new RepositoryActivity(Now.AddMinutes(-20), RepositoryActivityKind.Integration, "20261112-a1b2c3", "T2", null, false, AgentStudio.Orchestration.Work.RepositoryActivityReader.ConflictCode, 2),
            new RepositoryActivity(Now.AddMinutes(-10), RepositoryActivityKind.Delivery, "20261112-a1b2c3", null, Sha1, true, null, 3),
            new RepositoryActivity(Now.AddMinutes(-5), RepositoryActivityKind.WorktreeRemoved, "20261112-a1b2c3", "T1", null, true, null, 4),
        ]);

        var remotes = new FakeRemotes { Listing = new RemoteListing([FakeRemotes.Origin], null) };
        var model = new RepositoryViewModel(workspace, history, annotator, activity, remotes, new FakeClock(Now));

        // A way to open the chain is connected, so the button of each version is on screen.
        model.ChainRequested += (_, _) => { };
        await model.SetProjectAsync(@"C:\work\calc");
        model.SelectedSection = model.Sections.Single(s => s.Value == section);
        return model;
    }

    public static IReadOnlyList<UiScreen> All { get; } =
    [
        UiScreens.ScreenAsync("Repository summary", async () => UiScreens.Of(new RepositoryView(), await LoadedAsync(RepositorySection.Summary)), 1100, 600),
        UiScreens.ScreenAsync("Repository branches", async () => UiScreens.Of(new RepositoryView(), await LoadedAsync(RepositorySection.Branches)), 1100, 600),
        UiScreens.ScreenAsync("Repository commits", async () => UiScreens.Of(new RepositoryView(), await LoadedAsync(RepositorySection.Commits)), 1100, 760),
        UiScreens.ScreenAsync("Repository activity", async () => UiScreens.Of(new RepositoryView(), await LoadedAsync(RepositorySection.Activity)), 1100, 760),
        UiScreens.ScreenAsync("Repository refused", async () => UiScreens.Of(new RepositoryView(), await LoadedAsync(RepositorySection.Summary, refused: true)), 1100, 500),
        UiScreens.ScreenAsync("Usage by milestone and activity", async () =>
        {
            var store = new RecordingSettingsStore();
            var clock = new FakeClock(Now);
            var report = UsageSamples.Empty with { Attempts = UsageDashboardPhase9Tests.Attempts };
            var model = new UsageDashboardViewModel(
                new FakeUsageReport(() => report),
                new FakeUsageExporter(),
                new FakeSaveFilePicker(null),
                store,
                clock,
                new QuotaViewModel(new FakeQuotaObservations(), null, clock),
                new ObservabilitySettingsViewModel(store));
            await model.SetProjectAsync(@"C:\work\calc");
            return UiScreens.Of(new UsageDashboardView(), model);
        }, 1500, 2000),
    ];
}
