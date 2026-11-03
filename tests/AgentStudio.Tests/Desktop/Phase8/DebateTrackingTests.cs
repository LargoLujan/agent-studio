using AgentStudio.Application.Coordination;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Desktop.Background;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Debate;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Memory;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Phase8;

/// <summary>
/// The debates in the registry of the work (ADR-0036, decision 3): the shell tracks every debate a «Debates» document follows
/// (opened, challenged or resumed), so the tray lists it, the managed exit waits for it and writes its choice, and «mantener
/// despierto» holds while it goes on. The coordinator is a fake.
/// </summary>
public sealed class DebateTrackingTests
{
    [Fact]
    public async Task A_debate_resumed_in_its_document_is_tracked_until_another_one_takes_its_place()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("calc");
        var coordinator = new FakeDebateCoordinator();
        coordinator.Debates.Add(Summary("deb-20261019-000002", DebateMode.ArchitectureReview, DebatePhase.Deliberation, DebateOutcome.Open, interrupted: true, minutesAgo: 30));
        var first = new FakeDebateRun(Snapshot());
        coordinator.Resume = _ => first;
        coordinator.Read = _ => Transcript();

        var (whileGoing, afterSecond) = await WpfHost.RunAsync(async () =>
        {
            var work = new BackgroundWorkRegistry(new FakeTimeProvider(Now), pollInterval: Timeout.InfiniteTimeSpan);
            MainViewModel? main = null;
            var screens = new DebateScreens(Backend(coordinator), new FakeClock(Now), () => main);
            main = new MainViewModel(
                new InMemorySettingsStore(),
                new FakeFolderPicker(),
                new FakeUnsavedChangesPrompt(),
                new ProviderDiagnosticsViewModel(new ProviderDiagnostics([]), TimeProvider.System),
                new ExplorerViewModel(),
                new JournalViewModel(TimeProvider.System),
                new QuickOpenViewModel(),
                debates: screens,
                work: work);
            await main.OpenProjectAsync(root);
            await main.OpenDebatesCommand.ExecuteAsync(null);
            var document = Assert.IsType<DebatesDocumentViewModel>(main.ActiveContent);

            await document.ResumeCommand.ExecuteAsync(document.History.Single(h => h.CanResume));
            var going = work.Active.Select(w => (w.Kind, w.WorkId)).ToList();

            // The debate ends and another one is opened in the same document: the registry follows the new one.
            first.Finish(DebateResultOf(first.Id));
            await first.Completion;
            var second = new FakeDebateRun(Snapshot());
            coordinator.Resume = _ => second;
            await document.ResumeCommand.ExecuteAsync(document.History.Single(h => h.CanResume));
            return (going, work.All.OfType<DebateWork>().Select(w => w.WorkId).ToList());
        });

        Assert.Equal([(BackgroundWorkKind.Debate, first.Id.Value)], whileGoing);
        Assert.Single(afterSecond);
    }

    private static DebateScreenBackend Backend(FakeDebateCoordinator coordinator) => new(
        coordinator,
        new FakeVerdictChallenges(),
        Workspace: new FakeWorkspace(),
        Roster: new FakeRoster(() => Roster),
        Requirements: new FakeRequirementStore(Requirements),
        Verdicts: new FakeVerdictStore(Stored("v-1", VerdictOf(CodexJudge, VerdictDecision.Approved, null))));

    private static DebateResult DebateResultOf(RunId id) =>
        new(id, DebatePhase.Closed, DebateOutcome.Resolved, null, [], DebateLimitReached.None, null, "Cerrado.");
}
