using System.Collections.ObjectModel;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Evidence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Panels;

/// <summary>One verification of the panel «Pruebas»: the step the system ran, its counters, and the main line of the error when it failed.</summary>
/// <param name="Title">What the system called the step: its name, its id and whether it ran isolated, as the engine wrote it.</param>
/// <param name="Counters">«10 correctas · 2 fallidas · 0 omitidas · código de salida 1».</param>
/// <param name="Failed">The step did not succeed: a run that executed no test does not either.</param>
/// <param name="Error">The main line of the error of the failed step; null when there is none or it passed.</param>
public sealed record TestRunRow(string? Task, string Title, string Counters, bool Failed, string StateText, string? Error, string ShortHash)
{
    /// <summary>What a screen reader says of a verification: its task, its step, whether it passed and its counters.</summary>
    public string AccessibleName => string.Join(" · ", new[] { Task, Title, StateText, Counters, Error }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public override string ToString() => AccessibleName;
}

/// <summary>What the tests of HEAD said when they were run on demand, as the panel shows it.</summary>
/// <param name="Lines">One line per step: its title, whether it passed and its counters.</param>
public sealed record HeadTestsResult(IReadOnlyList<TestRunRow> Lines);

/// <summary>
/// Runs the profile of verification on the HEAD of the project in an isolated copy, without quota (docs/17 D-45): the shell composes
/// it with the runner of the verification and the git layer, as the action of the debates does. Without it the button is not shown:
/// nothing here pretends to run tests.
/// </summary>
public interface IHeadTestsService
{
    Task<HeadTestsResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The panel «Pruebas» (docs/17 D-45, F13-18): the verifications the system captured in the run (their step, their counters and the
/// ones that failed with the main line of their error) and, when the shell composes it, «Ejecutar las pruebas de HEAD», which passes
/// the profile in the isolated copy without spending quota. It follows the run through the grouped state.
/// </summary>
public sealed partial class TestsPanelViewModel : ObservableObject
{
    private readonly IUiTextProvider _texts;
    private RunMonitorViewModel? _monitor;

    public TestsPanelViewModel(IUiTextProvider? texts = null) => _texts = texts ?? Texts.Current;

    /// <summary>Runs the tests of HEAD on demand; without it «Ejecutar las pruebas de HEAD» is not shown.</summary>
    public IHeadTestsService? HeadTests { get; set; }

    /// <summary>The verifications of the run, in the order they were captured.</summary>
    public ObservableCollection<TestRunRow> Rows { get; } = [];

    /// <summary>
    /// The main line of the error of each task with failed steps, said once for the task: the log keeps one line per task and not one per
    /// test, so it is not repeated on every failed row as if each had its own.
    /// </summary>
    public ObservableCollection<string> Notes { get; } = [];

    private CancellationTokenSource? _running;

    /// <summary>What the tests of HEAD said the last time the person ran them.</summary>
    public ObservableCollection<TestRunRow> HeadRows { get; } = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunHeadTestsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelHeadTestsCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _headSummary;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>The button of the tests of HEAD is offered: the shell composed the service.</summary>
    public bool CanRunHead => HeadTests is not null;

    /// <summary>Follows the run the monitor follows.</summary>
    public void Attach(RunMonitorViewModel monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        if (_monitor is not null)
        {
            _monitor.Changed -= OnChanged;
        }

        _monitor = monitor;
        monitor.Changed += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, RunLiveChangedEventArgs e) => Refresh();

    /// <summary>Builds the list again from the evidence the system captured in the run.</summary>
    public void Refresh()
    {
        Rows.Clear();
        Notes.Clear();
        if (_monitor is { } monitor)
        {
            foreach (var evidence in monitor.Evidence.Where(IsVerification))
            {
                Rows.Add(RowOf(evidence));
            }

            foreach (var task in Rows.Where(r => r.Failed && r.Task is not null).Select(r => r.Task!).Distinct(StringComparer.Ordinal))
            {
                if (monitor.Tasks.FirstOrDefault(t => t.Id.Value == task) is { ShowsFailureLine: true, FailureLine: { } line })
                {
                    Notes.Add(_texts.Format("Panels_Tests_TaskError", task, line));
                }
            }
        }

        IsEmpty = Rows.Count == 0;
        var failed = Rows.Count(r => r.Failed);
        Summary = Rows.Count == 0
            ? _texts.Get("Panels_Tests_None")
            : failed == 0
                ? _texts.Format("Panels_Tests_AllPassed", Rows.Count.ToString(_texts.Culture))
                : _texts.Format("Panels_Tests_SomeFailed", Rows.Count.ToString(_texts.Culture), failed.ToString(_texts.Culture));
        OnPropertyChanged(nameof(CanRunHead));
    }

    /// <summary>A verification step the system captured: a run of tests, a build or the output of a command, not what an agent says.</summary>
    private static bool IsVerification(EvidenceEntry evidence) =>
        evidence.RawOrigin == EvidenceOrigin.CapturedBySystem
        && evidence.RawKind is EvidenceKind.TestRun or EvidenceKind.BuildLog or EvidenceKind.CommandOutput;

    private TestRunRow RowOf(EvidenceEntry evidence)
    {
        var summary = evidence.Summary;
        var failed = summary is not null && !summary.Succeeded;
        var counters = summary is null
            ? _texts.Get("Panels_Tests_NoCounters")
            : _texts.Format("Panels_Tests_Counters", summary.Passed.ToString(_texts.Culture), summary.Failed.ToString(_texts.Culture), summary.Skipped.ToString(_texts.Culture), summary.ExitCode.ToString(_texts.Culture));
        var state = summary is null ? _texts.Get("Panels_Tests_Captured") : _texts.Get(failed ? "Panels_Tests_Failed" : "Panels_Tests_Passed");
        return new TestRunRow(evidence.Task, evidence.Title, counters, failed, state, null, evidence.ShortHash);
    }

    /// <summary>«Ejecutar las pruebas de HEAD»: passes the profile in the isolated copy, without quota. Only when the shell composed the service.</summary>
    [RelayCommand(CanExecute = nameof(CanRunHeadNow))]
    private async Task RunHeadTestsAsync()
    {
        if (HeadTests is not { } service)
        {
            return;
        }

        IsRunning = true;
        ErrorMessage = null;
        HeadSummary = null;
        HeadRows.Clear();
        _running = new CancellationTokenSource();
        try
        {
            var result = await service.RunAsync(_running.Token).ConfigureAwait(true);
            foreach (var line in result.Lines)
            {
                HeadRows.Add(line);
            }

            var failed = result.Lines.Count(l => l.Failed);
            HeadSummary = result.Lines.Count == 0
                ? _texts.Get("Panels_Tests_HeadNone")
                : failed == 0 ? _texts.Get("Panels_Tests_HeadPassed") : _texts.Format("Panels_Tests_HeadFailed", failed.ToString(_texts.Culture));
        }
        catch (OperationCanceledException)
        {
            HeadSummary = _texts.Get("Panels_Tests_HeadCancelled");
        }
#pragma warning disable CA1031 // The service is behind a port: whatever it refuses is said in the panel, never thrown into the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorMessage = _texts.Format("Panels_Tests_HeadError", ex.Message);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsRunning = false;
        }
    }

    /// <summary>«Cancelar las pruebas»: stops the tests of HEAD that run; the service gets the cancellation.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelHeadTests() => _running?.Cancel();

    private bool CanRunHeadNow() => HeadTests is not null && !IsRunning;
}
