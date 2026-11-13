using System.Collections.ObjectModel;
using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Tracing;
using AgentStudio.Memory.Consistency;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Docs;

/// <summary>A discrepancy of the list: what it says in words, where it is and what the person can do about it.</summary>
public sealed class FindingRowItem
{
    public FindingRowItem(ConsistencyFinding finding, IUiTextProvider texts, string? groupTitle)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(texts);
        Finding = finding;
        GroupTitle = groupTitle;
        Text = LineOf(finding, texts);
        Location = finding.Path is null
            ? null
            : finding.Line is { } line ? texts.Format("Docs_Disc_Where", finding.Path, line) : finding.Path;
        DiagramKind = finding.Code == ConsistencyCodes.DiagramStale && finding.Facts.FirstOrDefault(f => f.Name == "diagram") is { } word
            ? DocsTexts.KindOfWord(word.Value)
            : null;
        AutomationName = Location is null ? Text : texts.Format("Docs_Disc_Name", Text, Location);
        OpenFileName = Finding.Path is null ? string.Empty : texts.Format("Docs_Disc_OpenFile_Name", Finding.Path);
        RegenerateName = texts.Format("Docs_Disc_Regenerate_Name", Text);
        BoardName = texts.Format("Docs_Disc_Board_Name", Text);
    }

    public ConsistencyFinding Finding { get; }

    /// <summary>The title of its group, only on the first finding of each code, so the list reads as groups without nesting.</summary>
    public string? GroupTitle { get; }

    public bool HasGroupTitle => GroupTitle is not null;

    public string Text { get; }

    public string? Location { get; }

    public bool HasLocation => Location is not null;

    public string AutomationName { get; }

    public string OpenFileName { get; }

    public string RegenerateName { get; }

    public string BoardName { get; }

    /// <summary>The kind of diagram a <c>doc.diagram-stale</c> finding names; null for the others.</summary>
    public DiagramKind? DiagramKind { get; }

    public bool CanOpenFile => Finding.Path is not null;

    public bool CanRegenerate => DiagramKind is not null;

    public bool CanGoToBoard => Finding.Code == ConsistencyCodes.RequirementUncovered;

    /// <summary>The sentence of a finding, from its code and its facts, in the language of the screen (docs/20 D-27, ADR-0054).</summary>
    public static string LineOf(ConsistencyFinding finding, IUiTextProvider texts)
    {
        string Fact(string name) => finding.Facts.FirstOrDefault(f => f.Name == name)?.Value ?? "—";

        return finding.Code switch
        {
            ConsistencyCodes.LinkBroken => texts.Format(
                texts.Has(UserText.KeyOf(finding.Code) + "_" + Fact("reason")) ? UserText.KeyOf(finding.Code) + "_" + Fact("reason") : UserText.KeyOf(finding.Code) + "_missing",
                Fact("target"),
                Fact("found")),
            ConsistencyCodes.AdrCitesMissing => texts.Format(UserText.KeyOf(finding.Code) + "_Line", Fact("cited")),
            ConsistencyCodes.DiagramStale => texts.Format(
                UserText.KeyOf(finding.Code) + "_Line",
                DocsTexts.KindOfWord(Fact("diagram")) is { } kind
                    ? DocsTexts.Name(texts, new DiagramRef(kind, kind == AgentStudio.Application.Diagrams.DiagramKind.Solution ? string.Empty : Fact("subject")))
                    : Fact("diagram")),
            ConsistencyCodes.RequirementUncovered => texts.Format(UserText.KeyOf(finding.Code) + "_Line", Fact("requirement"), Fact("title"), Fact("milestone")),
            ConsistencyCodes.CheckFailed => texts.Format(UserText.KeyOf(finding.Code) + "_Line", texts.Has(UserText.KeyOf(Fact("check"))) ? texts.Get(UserText.KeyOf(Fact("check"))) : Fact("check")),
            _ => texts.Has(UserText.KeyOf(finding.Code)) ? texts.Get(UserText.KeyOf(finding.Code)) : finding.Code,
        };
    }
}

/// <summary>
/// «Discrepancias» (docs/20 D-27 to D-30, F6-11): what does not agree in the project, found without an AI over the saved version the analysis describes: links to files that
/// do not exist, documents of decisions that name files that are gone, diagrams that are out of date and requirements no task covers. They come grouped by kind, each with
/// where it is and what to do: open the file, regenerate the diagram or go to «Trabajo». It shows what is saved for the version the project is at and never starts the copy or
/// the analysis by itself: «Calcular» does, when the person presses it. The diagrams that are out of date are read from the cache each time, because regenerating one changes that
/// without a new analysis.
/// </summary>
public sealed partial class DocsDiscrepanciesSection : GraphSection
{
    private readonly DocsBackend _backend;
    private readonly IUiTextProvider? _texts;
    private int _generation;
    private CancellationTokenSource? _cts;

    public DocsDiscrepanciesSection(DocsBackend backend, IUiTextProvider? texts = null)
        : base("Info_Docs_Discrepancies")
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _texts = texts;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsClean));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                CalculateCommand.NotifyCanExecuteChanged();
                RefreshCommand.NotifyCanExecuteChanged();
            }
        };
    }

    private IUiTextProvider T => _texts ?? Texts.Current;

    public ObservableCollection<FindingRowItem> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClean))]
    [NotifyPropertyChangedFor(nameof(NeedsCalculation))]
    private bool _hasResult;

    [ObservableProperty]
    private string _describesText = string.Empty;

    [ObservableProperty]
    private string? _notice;

    public bool IsAvailable => _backend.Analysis is not null && _backend.Git is not null;

    public string UnavailableText => T.Get("Docs_Disc_Unavailable");

    /// <summary>There is a result for this version and it has nothing in it.</summary>
    public bool IsClean => HasResult && Rows.Count == 0;

    /// <summary>There is nothing saved for the version the project is at: the screen says so and offers to calculate it.</summary>
    public bool NeedsCalculation => IsAvailable && HasProject && !HasResult;

    /// <summary>The person asks for a file in the editor: the full path, already checked.</summary>
    public event EventHandler<string>? FileRequested;

    /// <summary>The person asks to go to the board of the milestones («Trabajo»).</summary>
    public event EventHandler<string?>? BoardRequested;

    public override void CancelPending()
    {
        var pending = _cts;
        _cts = null;
        if (pending is not null)
        {
            try
            {
                pending.Cancel();
            }
            finally
            {
                pending.Dispose();
            }
        }
    }

    protected override void ProjectChanged()
    {
        CancelPending();
        _generation++;
        IsBusy = false;
        Rows.Clear();
        HasResult = false;
        DescribesText = string.Empty;
        Notice = null;
        Message = null;
    }

    /// <summary>Reads what is saved for the version the project is at. It never calculates.</summary>
    public override Task ReloadAsync(CancellationToken ct) => LoadAsync(calculate: false, ct);

    private async Task LoadAsync(bool calculate, CancellationToken ct)
    {
        if (Project is not { } project || !IsAvailable)
        {
            OnPropertyChanged(nameof(NeedsCalculation));
            return;
        }

        var generation = ++_generation;
        CancelPending();
        _cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var caller = ct;
        var token = linked.Token;
        IsBusy = true;
        Error = null;
        try
        {
            var summary = await _backend.Git!.SummaryAsync(project, token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            if (!summary.Accepted || summary.HeadCommit is not { } head || !TraceRef.IsFullCommitId(head))
            {
                HasResult = false;
                Rows.Clear();
                Error = T.Get("Docs_Disc_NoVersion");
                return;
            }

            // «Calcular» is the person's order to look again (the checks of the requirements and of a check that failed do not depend on the version), so it does not take
            // what is saved; opening the section and «Actualizar» only read it.
            var analysis = calculate ? null : await _backend.Analysis!.FindAsync(project, head, token).ConfigureAwait(true);
            if (analysis is null && calculate)
            {
                Notice = T.Get("Docs_Disc_Calculating");
                analysis = await _backend.Analysis!.AnalyzeAsync(project, head, token).ConfigureAwait(true);
            }

            if (generation != _generation)
            {
                return;
            }

            Notice = null;
            if (analysis is null)
            {
                HasResult = false;
                Rows.Clear();
                DescribesText = string.Empty;
                return;
            }

            await ShowAsync(project, analysis, summary.HasUncommittedChanges, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
            {
                Notice = null;
                if (caller.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
#pragma warning disable CA1031 // The analysis and the history are behind ports: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                Notice = null;
                Error = T.Format("Docs_Disc_Error", DocsTexts.Failure(T, ex));
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(NeedsCalculation));
            }
        }
    }

    private async Task ShowAsync(ProjectContext project, CommitAnalysis analysis, bool hasChanges, CancellationToken ct)
    {
        // The diagrams out of date and the requirements with no task come from the cache and the log, not from the files of the commit: regenerating a diagram, or a new entry of
        // a run, changes them without a new analysis, so they are worked out now (the line of Auto Doctor does the same: the two give the same count).
        var report = _backend.Live is { } live ? await live.RefreshAsync(project, analysis, ct).ConfigureAwait(true) : analysis.Consistency;
        var findings = report.Findings.ToList();
        if (_backend.Live is null && _backend.Diagrams is { } catalog && report.NotApplicable.All(c => c != ConsistencyCodes.DiagramStale))
        {
            findings.RemoveAll(f => f.Code == ConsistencyCodes.DiagramStale);
            foreach (var status in (await catalog.ListAsync(project, ct).ConfigureAwait(true)).Where(s => s.State == DiagramState.Stale))
            {
                findings.Add(DiagramStaleCheck.Finding(status.Diagram, status.Describes));
            }
        }

        Rows.Clear();
        string? previous = null;
        foreach (var finding in findings
            .OrderBy(f => OrderOf(f.Code))
            .ThenBy(f => f.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0))
        {
            var count = findings.Count(f => f.Code == finding.Code);
            var title = finding.Code == previous ? null : GroupTitle(finding.Code, count);
            previous = finding.Code;
            Rows.Add(new FindingRowItem(finding, T, title));
        }

        HasResult = true;
        DescribesText = T.Format(hasChanges ? "Docs_Disc_DescribesChanges" : "Docs_Disc_Describes", analysis.Commit[..7]);
        Notice = report.Truncated ? T.Get("Docs_Disc_Truncated") : null;
    }

    /// <summary>The order of the groups: the codes of Phase 6 as the contract lists them, and the checks that could not run last.</summary>
    private static int OrderOf(string code)
    {
        var index = ((IList<string>)[.. ConsistencyCodes.All, ConsistencyCodes.CheckFailed]).IndexOf(code);
        return index < 0 ? int.MaxValue : index;
    }

    private string GroupTitle(string code, int count) =>
        T.Format("Docs_Disc_Group", T.Has(UserText.KeyOf(code)) ? T.Get(UserText.KeyOf(code)) : code, count);

    private bool CanCalculate() => IsAvailable && Project is not null && !IsBusy;

    /// <summary>«Calcular»: opens a copy of the saved version and runs the analysis, which can take a while and waits for a verification that is running.</summary>
    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private Task CalculateAsync(CancellationToken ct) => LoadAsync(calculate: true, ct);

    /// <summary>«Actualizar»: reads again what is saved. It never calculates.</summary>
    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private Task RefreshAsync(CancellationToken ct) => LoadAsync(calculate: false, ct);

    /// <summary>Opens the file of a discrepancy in the editor, if it is still in the folder.</summary>
    [RelayCommand]
    private void OpenFile(FindingRowItem? row)
    {
        if (Project is not { } project || row?.Finding.Path is not { } path)
        {
            return;
        }

        if (GraphFileTarget.Resolve(project.RootPath, path) is not { } full)
        {
            Message = T.Format("Docs_Message_FileGone", path);
            return;
        }

        Message = null;
        FileRequested?.Invoke(this, full);
    }

    /// <summary>«Regenerar»: the diagram that is out of date, in the cache; the row goes when it is up to date.</summary>
    [RelayCommand]
    private async Task RegenerateAsync(FindingRowItem? row, CancellationToken ct)
    {
        if (Project is not { } project || _backend.Diagrams is not { } catalog || row is not { DiagramKind: { } kind } || IsBusy)
        {
            return;
        }

        var subject = row.Finding.Facts.FirstOrDefault(f => f.Name == "subject")?.Value ?? string.Empty;
        IsBusy = true;
        Error = null;
        try
        {
            await catalog.GenerateAsync(project, kind, kind == DiagramKind.Solution ? string.Empty : subject, ct).ConfigureAwait(true);
            Message = T.Get("Docs_Message_Generated");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The catalog is behind a port: why it failed is said, never thrown into the UI.
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or AgentStudio.Application.Decisions.DecisionLogIntegrityException)
#pragma warning restore CA1031
        {
            Error = T.Format("Docs_Error_Action", DocsTexts.Failure(T, ex));
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(calculate: false, ct).ConfigureAwait(true);
    }

    /// <summary>Goes to the board of the milestone the requirement belongs to (its id is a fact of the finding), or to the milestones when the finding names none.</summary>
    [RelayCommand]
    private void GoToBoard(FindingRowItem? row) =>
        BoardRequested?.Invoke(this, row?.Finding.Facts.FirstOrDefault(f => f.Name == "milestone-id")?.Value);
}
