using System.Reflection;
using System.Windows.Controls;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Desktop.UiInventory;

namespace AgentStudio.Tests.Desktop;

public sealed class BackgroundRunsDocumentTests
{
    [Fact]
    public void Adding_a_monitor_notifies_the_list_keeps_its_identity_and_does_not_replace_the_selection()
    {
        var document = new BackgroundRunsDocumentViewModel();
        var first = new RunMonitorViewModel(TimeProvider.System);
        var second = new RunMonitorViewModel(TimeProvider.System);
        var changes = 0;
        document.Runs.CollectionChanged += (_, _) => changes++;

        Add(document, @"C:\projects\one", first);
        var firstRow = Assert.Single(document.Runs);
        Add(document, @"C:\projects\two", second);
        Add(document, @"C:\projects\one", first);

        Assert.Equal(2, document.Runs.Count);
        Assert.Equal(2, changes);
        Assert.Same(firstRow, document.SelectedRun);
        Assert.Same(first, document.SelectedMonitor);
        document.SelectedRun = document.Runs[1];
        Assert.Same(second, document.SelectedMonitor);
        Assert.Equal(@"C:\projects\one", firstRow.ProjectRoot);
        Assert.Equal("one", firstRow.ProjectName);
        Assert.Contains(firstRow.ProjectRoot, firstRow.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_monitor_stays_reachable_with_the_state_from_its_original_reader()
    {
        var document = new BackgroundRunsDocumentViewModel();
        var monitor = new RunMonitorViewModel(TimeProvider.System);
        var run = new FakeRun(FakeRun.SnapshotOf(RunPhase.Running));
        monitor.Attach(run);
        Add(document, @"C:\projects\one", monitor);
        run.Finish(new RunResult(run.Id, RunPhase.Cancelled, FakeRun.Healthy, null, null, "Stopped"));
        await monitor.Listening;

        Assert.Same(monitor, Assert.Single(document.Runs).Monitor);
        Assert.Same(monitor, document.SelectedMonitor);
        Assert.True(monitor.IsFinished);
        Assert.Contains(run.Id.ToString(), document.Runs[0].AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_a_list_row_renders_its_original_monitor()
    {
        WpfHost.Run(() =>
        {
            var document = new BackgroundRunsDocumentViewModel();
            var first = new RunMonitorViewModel(TimeProvider.System);
            var second = new RunMonitorViewModel(TimeProvider.System);
            Add(document, @"C:\projects\one", first);
            Add(document, @"C:\projects\two", second);
            var view = new BackgroundRunsView { DataContext = document };
            UiTree.Lay(view, 1100, 800);
            var list = Assert.Single(UiTree.All(view).OfType<ListBox>(), control => ReferenceEquals(control.ItemsSource, document.Runs));

            list.SelectedIndex = 1;
            UiTree.Lay(view, 1100, 800);

            Assert.Same(second, document.SelectedMonitor);
            Assert.Same(second, Assert.Single(UiTree.All(view).OfType<RunMonitorView>()).DataContext);
        });
    }

    // The shell owns registration; keep its internal entry point out of the public desktop API.
    private static void Add(BackgroundRunsDocumentViewModel document, string root, RunMonitorViewModel monitor) =>
        typeof(BackgroundRunsDocumentViewModel).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(document, [root, monitor]);
}
