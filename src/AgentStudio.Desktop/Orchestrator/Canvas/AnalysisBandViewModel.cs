using System.Collections.ObjectModel;
using AgentStudio.Application.Orchestration;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// What the analysis band needs of the document it analyzes (docs/17 D-16 and D-18): an immutable draft to analyze off the UI
/// thread, the state a fix edits, and the way to write a new state back (the plan on the canvas, the template through the
/// editor of the team, the budget of the run). The document implements it; a test answers it with a fake.
/// </summary>
public interface IDraftHost
{
    /// <summary>An immutable copy of the draft, built on the UI thread; null when there is not enough to analyze yet (no frozen requirements or profile).</summary>
    MontageDraft? BuildDraft(long generation);

    /// <summary>The part of the draft a fix edits, as it is drawn now.</summary>
    MontageEditState ReadState();

    /// <summary>What applying a fix needs besides the state: the title of an Integration task, the steps of the profile.</summary>
    FixEnvironment Environment { get; }

    /// <summary>
    /// Writes <paramref name="after"/> in place of <paramref name="before"/>: the plan on the canvas, the agents that changed
    /// through the editor of the team (which withdraws the approval and asks Windows Hello for a capability of high risk) and the
    /// budget of the run. False when something was refused: the band then goes back.
    /// </summary>
    Task<bool> ApplyAsync(MontageEditState before, MontageEditState after, Fix? fix);

    /// <summary>The text of a finding of the validator (<c>validator.*</c>), which has no text of its own in the resources: the same one as the list of problems of the plan; null when there is none.</summary>
    string? DescribeValidator(Finding finding);
}

/// <summary>Where the analysis of the band is: approving is allowed only when it is <see cref="Current"/> without errors, or when there is nothing to analyze yet.</summary>
public enum AnalysisState
{
    /// <summary>Never asked for yet.</summary>
    Idle,

    /// <summary>An edit came and the analysis has not answered for it yet (the pause of 200 ms, or running).</summary>
    Pending,

    /// <summary>The result on screen is the one of the draft as it is.</summary>
    Current,

    /// <summary>There is not enough to analyze (no frozen requirements or profile): the other checks of approving say so.</summary>
    Unavailable,

    /// <summary>The analysis failed: it says why and can be asked again.</summary>
    Failed,
}

/// <summary>One finding of the band: its sentence, its severity, the nodes it names and the button of its fix, if it has one.</summary>
public sealed partial class FindingItemViewModel : ObservableObject
{
    private readonly AnalysisBandViewModel _band;

    internal FindingItemViewModel(Finding finding, string text, string? buttonText, AnalysisBandViewModel band, CanvasText canvasText)
    {
        Finding = finding;
        Text = text;
        ButtonText = buttonText;
        _band = band;
        SeverityText = canvasText.Get("Canvas_Severity_" + finding.Severity);
        SeverityIcon = canvasText.Get("Canvas_SeverityIcon_" + finding.Severity);
        Consequence = finding.Fix switch
        {
            { GrantsHighRiskCapability: true } => canvasText.Get("Canvas_Fix_AsksHello"),
            { ChangesRoster: true } => canvasText.Get("Canvas_Fix_WithdrawsApproval"),
            _ => null,
        };
        AutomationName = string.Join(" · ", new[] { SeverityText, text, buttonText, Consequence }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public Finding Finding { get; }

    public string Text { get; }

    public string SeverityText { get; }

    /// <summary>An icon for the severity: the colour is never the only cue.</summary>
    public string SeverityIcon { get; }

    public FindingSeverity Severity => Finding.Severity;

    /// <summary>The button of the fix of one click («Añadir Integración al final»); null when the finding only informs.</summary>
    public string? ButtonText { get; }

    public bool HasFix => ButtonText is not null && Finding.Fix is not null;

    /// <summary>What the fix does besides its edit, said before it is applied: it withdraws the approval of the plan, or it asks Windows Hello.</summary>
    public string? Consequence { get; }

    public IReadOnlyList<string> Nodes => Finding.Nodes;

    public string AutomationName { get; }

    [RelayCommand(CanExecute = nameof(HasFix))]
    private Task ApplyFixAsync() => _band.ApplyFixAsync(Finding.Fix!);

    public override string ToString() => AutomationName;
}

/// <summary>
/// The band of the analysis of the assembly (docs/17 D-16, D-18 and D-21, F13-9): the findings of <see cref="MontageAnalyzer"/>
/// on the draft, seen while the person assembles, with the fix of one click each and Ctrl+Z and Ctrl+Y over every edit. It runs
/// off the UI thread on an immutable copy of the draft, 200 ms after the last edit, and its result is applied only if no edit
/// came meanwhile (the generation, as <c>RunMonitorViewModel</c> does). It never spends quota and never decides whether a plan
/// is approved: the errors it shows are those of the validator, which the document still enforces.
/// </summary>
public sealed partial class AnalysisBandViewModel : ObservableObject, IDisposable
{
    /// <summary>The time after the last edit before the analysis runs (D-16).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private readonly IDraftHost _host;
    private readonly CanvasText _text;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private ITimer? _timer;
    private long _generation;
    private bool _applying;
    private bool _disposed;
    private EditHistory<MontageEditState>? _history;

    public AnalysisBandViewModel(IDraftHost host, CanvasText? text = null, TimeProvider? time = null, SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _text = text ?? new CanvasText();
        _time = time ?? TimeProvider.System;
        _ui = uiContext ?? SynchronizationContext.Current;
        Summary = _text.Get("Canvas_Analysis_Unavailable");
    }

    public ObservableCollection<FindingItemViewModel> Findings { get; } = [];

    /// <summary>The findings shown: errors and warnings always, suggestions only while they are not folded.</summary>
    public IEnumerable<FindingItemViewModel> Visible => Findings.Where(f => ShowSuggestions || f.Severity != FindingSeverity.Suggestion);

    /// <summary>The suggestions are folded by default (D-18): a click opens them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Visible))]
    private bool _showSuggestions;

    /// <summary>The analysis has errors: they prevent approving the plan, as the validator does.</summary>
    [ObservableProperty]
    private bool _blocksApproval;

    [ObservableProperty]
    private string _summary;

    /// <summary>What happened to the last fix, or why it was not applied.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>The last analysis finished and its result is on screen.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>Where the analysis is (docs/17 D-18): approving fails closed while it is pending or failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalRefusal), nameof(IsFailed))]
    private AnalysisState _state = AnalysisState.Idle;

    /// <summary>The analysis failed: «Volver a analizar» is shown.</summary>
    public bool IsFailed => State == AnalysisState.Failed;

    /// <summary>Why the plan cannot be approved because of the analysis, in words; null when the analysis lets it be approved.</summary>
    public string? ApprovalRefusal => State switch
    {
        AnalysisState.Current when BlocksApproval => _text.Get("Canvas_Analysis_RefusesErrors"),
        AnalysisState.Pending or AnalysisState.Idle => _text.Get("Canvas_Analysis_RefusesPending"),
        AnalysisState.Failed => _text.Get("Canvas_Analysis_RefusesFailed"),
        _ => null,
    };

    /// <summary>«Volver a analizar»: asks for the analysis again after it failed.</summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        Schedule();
        Running = RefreshAsync();
        await Running.ConfigureAwait(true);
    }

    /// <summary>The last analysis, for a test or a caller that has to wait for it.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>The generation of the last edit: a result is applied only if it is still this one.</summary>
    public long Generation => Volatile.Read(ref _generation);

    public int Errors => Findings.Count(f => f.Severity == FindingSeverity.Error);

    public bool CanUndo => _history?.CanUndo == true;

    public bool CanRedo => _history?.CanRedo == true;

    /// <summary>Starts the history of the edits from the draft as it is now: a proposal loaded or a project opened is not an edit to undo.</summary>
    public void Reset()
    {
        // The band is writing a state itself (a fix, Ctrl+Z, Ctrl+Y) and the document read its context again because of it: that
        // is not a new starting point, and the history that knows the state before has to survive it (P55).
        if (_applying)
        {
            return;
        }

        _history = new EditHistory<MontageEditState>(_host.ReadState());
        NotifyHistory();
    }

    /// <summary>The draft changed (a node, a cable, a field, the template or the budget): the analysis runs 200 ms after the last of these.</summary>
    public void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        IsCurrent = false;
        State = AnalysisState.Pending;
        _timer?.Dispose();
        _timer = _time.CreateTimer(_ => Post(() => Running = RefreshAsync()), null, Debounce, Timeout.InfiniteTimeSpan);
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    /// <summary>
    /// Analyzes the draft as it is now, off the UI thread, and shows the result if no edit came meanwhile. An edit that is not
    /// a fix is kept in the history here, once per pause of the edits, so typing is one step to undo and not one per letter.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = Generation;
        MontageDraft? draft;
        try
        {
            draft = _host.BuildDraft(generation);
        }
#pragma warning disable CA1031 // The host reads the document: a failure is shown in the band, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Show(null, _text.Format("Canvas_Analysis_Failed", OrchestratorErrorText.Detail(ex, _text.Provider)), failed: true);
            return;
        }

        RecordManualEdit();
        if (draft is null)
        {
            Show(null, _text.Get("Canvas_Analysis_Unavailable"));
            return;
        }

        MontageAnalysis analysis;
        try
        {
            analysis = await Task.Run(() => MontageAnalyzer.Analyze(draft)).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The analyzer is pure; whatever it throws is shown, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Show(null, _text.Format("Canvas_Analysis_Failed", OrchestratorErrorText.Detail(ex, _text.Provider)), failed: true);
            return;
        }

        // An edit came while it ran: this result is old, and the next one is on its way.
        if (analysis.Generation != Generation)
        {
            return;
        }

        Show(analysis, null);
    }

    /// <summary>The analysis of the montage as it was last shown; null while there is none or it failed. The panel «Problemas» lists it.</summary>
    public MontageAnalysis? Analysis { get; private set; }

    /// <summary>The analysis shown changed (<see cref="Analysis"/>).</summary>
    public event EventHandler? AnalysisChanged;

    private void Show(MontageAnalysis? analysis, string? summary, bool failed = false)
    {
        Analysis = analysis;
        Findings.Clear();
        if (analysis is not null)
        {
            foreach (var finding in analysis.Findings)
            {
                Findings.Add(Item(finding));
            }
        }

        BlocksApproval = analysis?.BlocksApproval == true;
        OnPropertyChanged(nameof(ApprovalRefusal));
        Summary = summary ?? SummaryOf(analysis!);
        IsCurrent = analysis is not null;
        State = analysis is not null ? AnalysisState.Current : failed ? AnalysisState.Failed : AnalysisState.Unavailable;
        OnPropertyChanged(nameof(Visible));
        OnPropertyChanged(nameof(Errors));
        NotifyHistory();
        AnalysisChanged?.Invoke(this, EventArgs.Empty);
    }

    private string SummaryOf(MontageAnalysis analysis)
    {
        var errors = analysis.Findings.Count(f => f.Severity == FindingSeverity.Error);
        var warnings = analysis.Findings.Count(f => f.Severity == FindingSeverity.Warning);
        var suggestions = analysis.Findings.Count(f => f.Severity == FindingSeverity.Suggestion);
        return analysis.Findings.Count == 0
            ? _text.Get("Canvas_Analysis_Clean")
            : _text.Format("Canvas_Analysis_Summary", errors, warnings, suggestions);
    }

    private FindingItemViewModel Item(Finding finding)
    {
        var text = finding.Code.StartsWith(FindingCodes.PlanViolationPrefix, StringComparison.Ordinal)
            ? _host.DescribeValidator(finding) ?? finding.Code
            : _text.Render(MontageTexts.SentenceOf(finding).Code, MontageTexts.SentenceOf(finding).Arguments);
        var button = finding.Fix is { } fix ? _text.Render(MontageTexts.ButtonOf(fix).Code, MontageTexts.ButtonOf(fix).Arguments) : null;
        return new FindingItemViewModel(finding, text, button, this, _text);
    }

    // ---------- The fix of one click, and undo ----------

    /// <summary>
    /// Applies a fix as an edit of the draft that can be undone. It is said before whether it withdraws the approval or asks Windows
    /// Hello (<see cref="FindingItemViewModel.Consequence"/>); a fix the applier refuses changes nothing and says why.
    /// </summary>
    public async Task ApplyFixAsync(Fix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        Notice = null;
        var before = _host.ReadState();
        var outcome = FixApplier.Apply(before, fix, _host.Environment);
        if (!outcome.Applied)
        {
            Notice = _text.Get("Text_" + (outcome.Refusal ?? FixRefusals.NothingToChange).Replace('.', '_').Replace('-', '_'));
            return;
        }

        _history ??= new EditHistory<MontageEditState>(before);
        if (!Same(_history.Current, before))
        {
            _history.Do(before);
        }

        if (await WriteAsync(before, outcome.State, fix).ConfigureAwait(true))
        {
            _history.Do(outcome.State);
            Notice = _text.Format("Canvas_Analysis_Fixed", _text.Render(MontageTexts.ButtonOf(fix).Code, MontageTexts.ButtonOf(fix).Arguments));
        }

        NotifyHistory();
        Schedule();
    }

    /// <summary>Ctrl+Z: goes back to the draft before the last edit, a fix or a change of the canvas.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync() => await StepAsync(undo: true).ConfigureAwait(true);

    /// <summary>Ctrl+Y: goes forward again to what was undone.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync() => await StepAsync(undo: false).ConfigureAwait(true);

    private async Task StepAsync(bool undo)
    {
        if (_history is null)
        {
            return;
        }

        Notice = null;
        if (!(undo ? _history.Undo() : _history.Redo()))
        {
            return;
        }

        var target = _history.Current;
        if (!await WriteAsync(_host.ReadState(), target, null).ConfigureAwait(true))
        {
            // Not written (Windows Hello refused, say): the history goes back to what is on screen.
            _ = undo ? _history.Redo() : _history.Undo();
            Notice = _text.Get("Canvas_Analysis_NotUndone");
        }

        NotifyHistory();
        Schedule();
    }

    private async Task<bool> WriteAsync(MontageEditState before, MontageEditState after, Fix? fix)
    {
        _applying = true;
        try
        {
            return await _host.ApplyAsync(before, after, fix).ConfigureAwait(true);
        }
        finally
        {
            _applying = false;
        }
    }

    private void RecordManualEdit()
    {
        if (_applying || _history is null)
        {
            return;
        }

        var now = _host.ReadState();
        if (!Same(_history.Current, now))
        {
            _history.Do(now);
        }
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Two states of the draft are the same when their plan, their template and their budget are.</summary>
    private static bool Same(MontageEditState a, MontageEditState b) =>
        a.MaxAgentRuns == b.MaxAgentRuns
        && string.Equals(a.Plan?.ContentHash, b.Plan?.ContentHash, StringComparison.Ordinal)
        && string.Equals(RosterHash.Of(a.Roster), RosterHash.Of(b.Roster), StringComparison.Ordinal);

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
