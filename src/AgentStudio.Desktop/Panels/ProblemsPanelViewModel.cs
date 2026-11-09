using System.Collections.ObjectModel;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Work;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.Desktop.Panels;

/// <summary>Where a problem comes from (docs/17 D-45): the analyzer or the validator of the montage, or the run itself.</summary>
public enum ProblemSource
{
    Montage,
    Task,
    Health,
}

/// <summary>How serious a problem is: it is said in words, and the colour only repeats it.</summary>
public enum ProblemSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>One line of the panel «Problemas»: what it is, how serious, where, and the detail.</summary>
/// <param name="Where">The task or the nodes it is about.</param>
/// <param name="Detail">The main line of the error of a verification step, or why a task failed; null when there is none.</param>
public sealed record ProblemRow(ProblemSource Source, ProblemSeverity Severity, string SeverityText, string Text, string Where, string? Detail)
{
    /// <summary>What a screen reader says of a problem: its severity, where, and what it is.</summary>
    public string AccessibleName => string.Join(" · ", new[] { SeverityText, Where, Text, Detail }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The panel «Problemas» (docs/17 D-45, F13-18): the findings of the analyzer and the validator of the montage, and why each task
/// of the run failed or is blocked, with the class of its end and its reason. Nothing is made up: it is what the analyzer, the
/// validator and the run say. It follows the run through the grouped state (<see cref="IRunLiveProjection"/>), so it repaints at most
/// four times a second.
/// </summary>
public sealed partial class ProblemsPanelViewModel : ObservableObject
{
    private readonly IUiTextProvider _texts;
    private RunMonitorViewModel? _monitor;
    private IReadOnlyList<Finding> _findings = [];

    public ProblemsPanelViewModel(IUiTextProvider? texts = null) => _texts = texts ?? Texts.Current;

    /// <summary>The problems, the most serious first, and in a stable order inside each level.</summary>
    public ObservableCollection<ProblemRow> Rows { get; } = [];

    /// <summary>«2 problemas: 1 error y 1 aviso», or «Sin problemas».</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Follows the run the monitor follows: it reads the tasks again at each grouped change.</summary>
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

    /// <summary>The findings of the analyzer and the validator of the montage as they are now (the canvas gives them); empty for none.</summary>
    public void SetMontage(MontageAnalysis? analysis)
    {
        _findings = analysis?.Findings ?? [];
        Refresh();
    }

    private void OnChanged(object? sender, RunLiveChangedEventArgs e) => Refresh();

    /// <summary>Builds the list again from the findings and the tasks.</summary>
    public void Refresh()
    {
        var rows = new List<ProblemRow>();
        foreach (var finding in _findings)
        {
            rows.Add(MontageRow(finding));
        }

        if (_monitor is { } monitor)
        {
            foreach (var task in monitor.Tasks)
            {
                if (TaskRow(task) is { } row)
                {
                    rows.Add(row);
                }
            }

            foreach (var line in monitor.HealthLines)
            {
                rows.Add(new ProblemRow(ProblemSource.Health, ProblemSeverity.Warning, SeverityText(ProblemSeverity.Warning), line, _texts.Get("Panels_Problems_Graph"), null));
            }
        }

        var ordered = rows.OrderBy(r => r.Severity).ThenBy(r => r.Source).ToList();
        Rows.Clear();
        foreach (var row in ordered)
        {
            Rows.Add(row);
        }

        IsEmpty = Rows.Count == 0;
        Summary = Summarize();
    }

    private ProblemRow MontageRow(Finding finding)
    {
        var sentence = MontageTexts.SentenceOf(finding);
        var key = UserText.KeyOf(sentence.Code);

        // A finding without its sentence in the resources (a code of the validator that has none yet) says its code, never an empty line.
        var text = _texts.Has(key) ? _texts.Render(new UserText(sentence.Code, sentence.Arguments)) : _texts.Format("Panels_Problems_Code", sentence.Code);
        var severity = finding.Severity switch
        {
            FindingSeverity.Error => ProblemSeverity.Error,
            FindingSeverity.Warning => ProblemSeverity.Warning,
            _ => ProblemSeverity.Info,
        };
        return new ProblemRow(ProblemSource.Montage, severity, SeverityText(severity), text, string.Join(", ", finding.Nodes), null);
    }

    private ProblemRow? TaskRow(MonitorTaskViewModel task)
    {
        if (task.Status == WorkTaskStatus.Failed)
        {
            var end = task.EndClass is { } cls ? _texts.Format("Panels_Problems_EndClass", _texts.Get("Panels_EndClass_" + cls)) : null;
            var text = _texts.Format("Panels_Problems_TaskFailed", task.Title);
            return new ProblemRow(
                ProblemSource.Task,
                ProblemSeverity.Error,
                SeverityText(ProblemSeverity.Error),
                end is null ? text : text + " " + end,
                task.Id.Value,
                string.Join(" · ", new[] { task.ReasonText, task.ShowsFailureLine ? task.FailureLine : null }.Where(p => !string.IsNullOrWhiteSpace(p))));
        }

        // A task that waits for the answer to a question is not a problem; a block to retry is.
        if (task.Status == WorkTaskStatus.Blocked && !task.AwaitingAnswer)
        {
            return new ProblemRow(ProblemSource.Task, ProblemSeverity.Warning, SeverityText(ProblemSeverity.Warning), _texts.Format("Panels_Problems_TaskBlocked", task.Title), task.Id.Value, task.StatusLabel);
        }

        return null;
    }

    private string SeverityText(ProblemSeverity severity) => _texts.Get("Panels_Severity_" + severity);

    private string Summarize()
    {
        if (Rows.Count == 0)
        {
            return _texts.Get("Panels_Problems_None");
        }

        var errors = Rows.Count(r => r.Severity == ProblemSeverity.Error);
        var warnings = Rows.Count(r => r.Severity == ProblemSeverity.Warning);
        var infos = Rows.Count - errors - warnings;
        return _texts.Format("Panels_Problems_Summary", Rows.Count.ToString(_texts.Culture), errors.ToString(_texts.Culture), warnings.ToString(_texts.Culture), infos.ToString(_texts.Culture));
    }
}
