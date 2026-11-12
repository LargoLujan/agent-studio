using System.Collections.ObjectModel;
using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Desktop.ViewModels;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Tracing;
using AgentStudio.Domain.Work.Milestones;
using AgentStudio.Orchestration.Work;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Repository;

/// <summary>The period of the filter of the activity, in the user's local days.</summary>
public enum RepositoryPeriod
{
    All,
    Today,
    Last7Days,
    Last30Days,
}

/// <summary>
/// The «Repositorio y cambios» document (docs/16 F9-9, D-44): what the agents and the person did to the repository, annotated from the
/// verified log. It is not a git client: it reads and never writes.
/// <list type="bullet">
/// <item><b>Resumen</b>: the current branch, whether there are unsaved changes, and the remotes (<see cref="IRepositoryRemoteReader"/>).</item>
/// <item><b>Ramas</b>: the local branches, those of the runs of the system marked as such.</item>
/// <item><b>Versiones guardadas</b>: the bounded graph of commits, newest first, annotated by <see cref="IRepositoryAnnotator"/>. A commit of the
/// system says its run, task, attempt, agent and model, and only when the log backs it; a commit the log does not back is at most a
/// suggestion, marked as one. Each version can ask for its chain (<see cref="ChainRequested"/>).</item>
/// <item><b>Actividad</b>: the timeline of the log (<see cref="IRepositoryActivityReader"/>) with the filters it gives: run, task,
/// milestone, agent, provider, model and time.</item>
/// </list>
/// The history is read only through the hardened git layer (<see cref="IRepositoryHistoryReader"/>, ADR-0012): Desktop never starts git.
/// When the reader refuses the repository, the screen says why, in words, and shows neither «sin ramas» nor «sin versiones» (it did not read
/// them); the activity, which comes from the log and not from git, is still shown. A log that does not verify leaves the history of git
/// without annotations, and says so.
/// </summary>
/// <remarks>
/// Opening the chain of a version is the job of another screen: this one raises <see cref="ChainRequested"/> with the node of the trace, and
/// the shell connects it (<c>ITraceChainReader.ChainAsync</c>). Without a subscriber the button is not offered.
/// </remarks>
public sealed partial class RepositoryViewModel : DocumentViewModel
{
    public const string DocumentId = "repository:project";

    /// <summary>The graph asks for at most this many commits (the reader bounds it again).</summary>
    public const int MaxCommits = 200;

    private const int ShortIdLength = 8;

    private readonly IProjectWorkspace? _workspace;
    private readonly IRepositoryHistoryReader? _history;
    private readonly IRepositoryAnnotator? _annotator;
    private readonly IRepositoryActivityReader? _activity;
    private readonly IRepositoryRemoteReader? _remotes;
    private readonly TimeProvider _time;
    private ProjectContext? _project;
    private EventHandler<RepositoryChainRequest>? _chainRequested;

    // A read that was started before another one (a project change, «Actualizar») is dropped when it ends: the screen shows the last asked.
    private int _generation;

    // The activity can be asked again (the filters) while the rest is still being read: its own count drops the older answers.
    private int _activityReads;

    /// <param name="workspace">Opens the project to know its id.</param>
    /// <param name="history">The history through the hardened git layer; null when the shell was composed without it (the screen says so).</param>
    /// <param name="annotator">Annotates the commits from the log; null shows them without annotation.</param>
    /// <param name="activity">The timeline of the log; null leaves the section empty and says so.</param>
    /// <param name="remotes">The remotes of the repository; null shows none.</param>
    /// <param name="time">The local time of the dates shown and the periods of the filter.</param>
    public RepositoryViewModel(
        IProjectWorkspace? workspace,
        IRepositoryHistoryReader? history,
        IRepositoryAnnotator? annotator,
        IRepositoryActivityReader? activity,
        IRepositoryRemoteReader? remotes,
        TimeProvider time)
        : base(DocumentId, Texts.Get("Repository_Title"))
    {
        ArgumentNullException.ThrowIfNull(time);
        _workspace = workspace;
        _history = history;
        _annotator = annotator;
        _activity = activity;
        _remotes = remotes;
        _time = time;
        _selectedSection = Sections[0];
        _selectedProvider = Providers[0];
        _selectedPeriod = Periods[0];
    }

    /// <summary>
    /// Someone asks to open the chain of a saved version (requirement, task, attempt, verification, verdict and delivery). The shell subscribes
    /// and walks it with <c>ITraceChainReader</c>; while nobody does, <see cref="CanOpenChain"/> is false and no version offers the button.
    /// </summary>
    public event EventHandler<RepositoryChainRequest>? ChainRequested
    {
        add
        {
            _chainRequested += value;
            ChainAvailabilityChanged();
        }

        remove
        {
            _chainRequested -= value;
            ChainAvailabilityChanged();
        }
    }

    /// <summary>Whether a way to open the chain of a version is connected.</summary>
    public bool CanOpenChain => _chainRequested is not null;

    /// <summary>The four sections, in the order they are offered.</summary>
    public IReadOnlyList<Option<RepositorySection>> Sections { get; } =
    [
        new(RepositorySection.Summary, Texts.Get("Repository_Section_Summary")),
        new(RepositorySection.Branches, Texts.Get("Repository_Section_Branches")),
        new(RepositorySection.Commits, Texts.Get("Repository_Section_Commits")),
        new(RepositorySection.Activity, Texts.Get("Repository_Section_Activity")),
    ];

    /// <summary>The section that is shown.</summary>
    [ObservableProperty]
    private Option<RepositorySection> _selectedSection;

    /// <summary>The project that is shown; null without one.</summary>
    public ProjectContext? Project => _project;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Why the hardened git layer does not read this repository, in words; null when it does (or before it was asked).</summary>
    [ObservableProperty]
    private string? _refusalText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    // ---- Resumen ----

    [ObservableProperty]
    private string? _branchText;

    [ObservableProperty]
    private string? _headText;

    [ObservableProperty]
    private string? _changesText;

    /// <summary>The remotes with their addresses (without credentials).</summary>
    public ObservableCollection<RemoteRow> Remotes { get; } = [];

    /// <summary>Said under the remotes: none, or why they could not be read; null when there are some.</summary>
    [ObservableProperty]
    private string? _remotesNote;

    // ---- Ramas ----

    public ObservableCollection<BranchRow> Branches { get; } = [];

    [ObservableProperty]
    private string? _branchesNote;

    // ---- Versiones guardadas ----

    public ObservableCollection<CommitRow> Commits { get; } = [];

    /// <summary>Said above the versions: how many are shown, none, or that they are shown without annotations; null before they were read.</summary>
    [ObservableProperty]
    private string? _commitsNote;

    // ---- Actividad ----

    public ObservableCollection<ActivityRow> Activities { get; } = [];

    [ObservableProperty]
    private string? _activityNote;

    /// <summary>The filters of the activity that cannot be applied (an id that is not valid), in words; null when they can.</summary>
    [ObservableProperty]
    private string? _filterError;

    [ObservableProperty]
    private string _runFilter = string.Empty;

    [ObservableProperty]
    private string _taskFilter = string.Empty;

    [ObservableProperty]
    private string _milestoneFilter = string.Empty;

    [ObservableProperty]
    private string _agentFilter = string.Empty;

    [ObservableProperty]
    private string _modelFilter = string.Empty;

    /// <summary>«Todos» and the providers Agent Studio knows.</summary>
    public IReadOnlyList<Option<ProviderId?>> Providers { get; } =
    [
        new(null, Texts.Get("Repository_Filter_Provider_All")),
        new(ProviderId.ClaudeCode, Texts.Get("Repository_Filter_Provider_ClaudeCode")),
        new(ProviderId.CodexCli, Texts.Get("Repository_Filter_Provider_CodexCli")),
        new(ProviderId.GeminiCli, Texts.Get("Repository_Filter_Provider_GeminiCli")),
    ];

    public IReadOnlyList<Option<RepositoryPeriod>> Periods { get; } =
    [
        new(RepositoryPeriod.All, Texts.Get("Repository_Filter_Period_All")),
        new(RepositoryPeriod.Today, Texts.Get("Repository_Filter_Period_Today")),
        new(RepositoryPeriod.Last7Days, Texts.Get("Repository_Filter_Period_Last7Days")),
        new(RepositoryPeriod.Last30Days, Texts.Get("Repository_Filter_Period_Last30Days")),
    ];

    [ObservableProperty]
    private Option<ProviderId?> _selectedProvider;

    [ObservableProperty]
    private Option<RepositoryPeriod> _selectedPeriod;

    /// <summary>The project open in the shell changed (null: none): everything is read for it.</summary>
    public async Task SetProjectAsync(string? projectRoot)
    {
        var generation = ++_generation;

        // Every read started before this call is dropped when it ends, and none of them lowers the flag: this call does.
        IsBusy = false;
        _project = null;
        OnPropertyChanged(nameof(Project));
        Clear();
        if (projectRoot is null)
        {
            StatusMessage = Texts.Get("Repository_Status_OpenFolder");
            return;
        }

        if (_workspace is null || _history is null)
        {
            StatusMessage = Texts.Get("Repository_Status_Unavailable");
            return;
        }

        IsBusy = true;
        StatusMessage = Texts.Get("Repository_Status_Loading");
        try
        {
            var project = await _workspace.OpenAsync(projectRoot, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            _project = project;
            OnPropertyChanged(nameof(Project));
            await ReadAllAsync(project, generation).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The workspace is behind a port: a failure is shown in the screen, never thrown into the UI.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                RefusalText = Texts.Format("Repository_Refused", RepositoryTexts.Refusal("unreadable"));
                StatusMessage = null;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>«Actualizar»: reads the repository and the activity again, with the filters on screen.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        if (_project is null)
        {
            return;
        }

        var generation = ++_generation;
        IsBusy = true;
        StatusMessage = Texts.Get("Repository_Status_Loading");
        try
        {
            await ReadAllAsync(_project, generation).ConfigureAwait(true);
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
            }
        }
    }

    private bool IsIdle() => !IsBusy;

    /// <summary>«Filtrar»: reads the activity again with the filters on screen; an id that is not valid is said and nothing is read.</summary>
    [RelayCommand]
    private async Task ApplyFiltersAsync()
    {
        if (_project is null)
        {
            return;
        }

        await ReadActivityAsync(_project, _generation).ConfigureAwait(true);
    }

    /// <summary>«Quitar filtros»: leaves every filter empty and reads the activity again.</summary>
    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        ResetFilters();
        await ApplyFiltersAsync().ConfigureAwait(true);
    }

    private void ResetFilters()
    {
        RunFilter = string.Empty;
        TaskFilter = string.Empty;
        MilestoneFilter = string.Empty;
        AgentFilter = string.Empty;
        ModelFilter = string.Empty;
        SelectedProvider = Providers[0];
        SelectedPeriod = Periods[0];
    }

    private async Task ReadAllAsync(ProjectContext project, int generation)
    {
        try
        {
            // The summary comes first: it is the only call that says why the layer refused. Branches and commits return empty without a reason.
            var summary = await _history!.SummaryAsync(project, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            StatusMessage = null;
            if (!summary.Accepted)
            {
                Refuse(RepositoryTexts.Refusal(summary.Warnings));
            }
            else
            {
                RefusalText = null;
                ShowSummary(summary);
                await ReadBranchesAsync(project, generation).ConfigureAwait(true);
                await ReadCommitsAsync(project, generation).ConfigureAwait(true);
                await ReadRemotesAsync(project, generation).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // The readers are behind ports: a failure is said in the screen, never thrown out of the command.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                // What was read before the failure would look complete next to what was not: nothing partial stays.
                StatusMessage = null;
                Refuse(RepositoryTexts.Refusal("unreadable"));
            }
        }

        if (generation == _generation)
        {
            await ReadActivityAsync(project, generation).ConfigureAwait(true);
        }
    }

    private void Refuse(string why)
    {
        RefusalText = Texts.Format("Repository_Refused", why);
        BranchText = null;
        HeadText = null;
        ChangesText = null;
        Remotes.Clear();
        RemotesNote = null;
        Branches.Clear();
        BranchesNote = null;
        Commits.Clear();
        CommitsNote = null;
    }

    private void ShowSummary(RepositorySummary summary)
    {
        BranchText = summary.HeadBranch ?? Texts.Get("Repository_Summary_NoBranch");
        HeadText = summary.HeadCommit is { } head ? ShortId(head) : Texts.Get("Repository_Summary_NoHead");
        ChangesText = Texts.Get(summary.HasUncommittedChanges ? "Repository_Summary_ChangesYes" : "Repository_Summary_ChangesNo");
    }

    private async Task ReadBranchesAsync(ProjectContext project, int generation)
    {
        var branches = await _history!.BranchesAsync(project, CancellationToken.None).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        Replace(Branches, branches.Select(b => new BranchRow(
            b.Name,
            Texts.Format("Repository_Branch_Points", ShortId(b.Commit)),
            string.Join(" · ", new[] { b.IsCurrent ? Texts.Get("Repository_Branch_Current") : null, b.Kind == BranchKind.Run ? Texts.Get("Repository_Branch_Run") : null }.OfType<string>()),
            b.IsCurrent,
            b.Kind == BranchKind.Run)));
        BranchesNote = branches.Count == 0 ? Texts.Get("Repository_Branches_None") : null;
    }

    private async Task ReadCommitsAsync(ProjectContext project, int generation)
    {
        var nodes = await _history!.CommitsAsync(project, new CommitGraphQuery(MaxCommits), CancellationToken.None).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        IReadOnlyList<AnnotatedCommit> annotated = [.. nodes.Select(n => new AnnotatedCommit(n, null))];
        var note = nodes.Count == 0 ? Texts.Get("Repository_Commits_None") : Texts.Format("Repository_Commits_Showing", nodes.Count.ToString("N0", Texts.Current.Culture));

        // Without an annotator, or when the log could not be checked, nothing is known: that is not «the log knows nothing of this version».
        var checkedAgainstLog = _annotator is not null;
        if (_annotator is not null && nodes.Count > 0)
        {
            try
            {
                annotated = await _annotator.AnnotateAsync(project, nodes, CancellationToken.None).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // The annotator is behind a port: the history of git is still worth seeing, and whatever it fails with is said.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // What the log would add is not trusted, so it is not drawn; the count stays.
                checkedAgainstLog = false;
            }

            if (generation != _generation)
            {
                return;
            }
        }

        if (!checkedAgainstLog && nodes.Count > 0)
        {
            note = Texts.Format("Repository_Commits_ShowingNotAnnotated", note, Texts.Get("Repository_Commits_NotAnnotated"));
        }

        Replace(Commits, annotated.Select(a => CommitRowOf(a, checkedAgainstLog)));
        CommitsNote = note;
    }

    private async Task ReadRemotesAsync(ProjectContext project, int generation)
    {
        if (_remotes is null)
        {
            Remotes.Clear();
            RemotesNote = Texts.Get("Repository_Summary_NoRemotes");
            return;
        }

        var listing = await _remotes.ListRemotesAsync(project.RootPath, CancellationToken.None).ConfigureAwait(true);
        if (generation != _generation)
        {
            return;
        }

        Replace(Remotes, listing.Remotes.Select(r => new RemoteRow(r.Name, r.Url)));
        RemotesNote = listing.RefusalCode is { } code
            ? Texts.Format("Repository_Summary_RemotesRefused", RepositoryTexts.Refusal(code))
            : listing.Remotes.Count == 0 ? Texts.Get("Repository_Summary_NoRemotes") : null;
    }

    private async Task ReadActivityAsync(ProjectContext project, int generation)
    {
        FilterError = null;

        // Counted before the filter is checked: a read that was still running must not paint its list over a filter that is refused.
        var read = ++_activityReads;
        if (_activity is null)
        {
            Activities.Clear();
            ActivityNote = Texts.Get("Repository_Status_Unavailable");
            return;
        }

        if (!TryFilter(out var filter))
        {
            return;
        }

        try
        {
            var events = await _activity.ListAsync(project, filter, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation || read != _activityReads)
            {
                return;
            }

            // The reader gives the oldest first; the screen starts with the newest.
            Replace(Activities, events.OrderByDescending(e => e.At).ThenByDescending(e => e.Sequence).Select(ActivityRowOf));
            ActivityNote = events.Count == 0 ? Texts.Get("Repository_Activity_None") : null;
        }
        catch (DecisionLogIntegrityException)
        {
            if (generation == _generation && read == _activityReads)
            {
                Activities.Clear();
                ActivityNote = Texts.Get("Repository_Activity_Failed");
            }
        }
#pragma warning disable CA1031 // The reader is behind a port: a failure is said in the screen, never thrown out of the command.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            if (generation == _generation && read == _activityReads)
            {
                Activities.Clear();
                ActivityNote = Texts.Get("Repository_Activity_Unreadable");
            }
        }
    }

    /// <summary>The filter the boxes say; an empty box lets everything through, an id that is not valid is told and nothing is read.</summary>
    private bool TryFilter(out RepositoryActivityFilter filter)
    {
        filter = new RepositoryActivityFilter();
        MilestoneId? milestone = null;
        if (Clean(MilestoneFilter) is { } milestoneText)
        {
            try
            {
                milestone = new MilestoneId(milestoneText);
            }
            catch (ArgumentException)
            {
                FilterError = Texts.Format("Repository_Filter_InvalidMilestone", milestoneText);
                return false;
            }
        }

        AgentId? agent = null;
        if (Clean(AgentFilter) is { } agentText)
        {
            try
            {
                agent = new AgentId(agentText);
            }
            catch (ArgumentException)
            {
                FilterError = Texts.Format("Repository_Filter_InvalidAgent", agentText);
                return false;
            }
        }

        filter = new RepositoryActivityFilter(
            Clean(RunFilter),
            Clean(TaskFilter),
            milestone,
            agent,
            SelectedProvider.Value,
            Clean(ModelFilter),
            From(SelectedPeriod.Value));
        return true;
    }

    private static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>The first local midnight of the period; null for every time.</summary>
    private DateTimeOffset? From(RepositoryPeriod period)
    {
        var days = period switch
        {
            RepositoryPeriod.Today => 0,
            RepositoryPeriod.Last7Days => 6,
            RepositoryPeriod.Last30Days => 29,
            _ => (int?)null,
        };
        if (days is not { } back)
        {
            return null;
        }

        var local = _time.GetLocalNow().DateTime.Date.AddDays(-back);
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
    }

    private CommitRow CommitRowOf(AnnotatedCommit annotated, bool checkedAgainstLog)
    {
        var commit = annotated.Commit;
        bool isSystem;
        List<OriginLine> origin;
        if (checkedAgainstLog)
        {
            origin = OriginOf(annotated.Annotation, out isSystem);
        }
        else
        {
            origin = [new OriginLine(Texts.Get("Repository_Origin_Unchecked"), false)];
            isSystem = false;
        }

        return new CommitRow(
            commit.Id,
            ShortId(commit.Id),
            commit.Subject,
            commit.AuthorName,
            TimeZoneInfo.ConvertTime(commit.At, _time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            string.Join(", ", commit.Refs),
            origin,
            isSystem,
            CanOpenChain && TraceRef.IsFullCommitId(commit.Id),
            OpenChain);
    }

    /// <summary>
    /// What the log says of a commit (D-44): that Agent Studio made it, with the run, task, attempt, agent and model the log backs, the
    /// links a person or the system confirmed, and, last and marked, the suggestions nobody confirmed. Nothing is read from the message.
    /// </summary>
    private static List<OriginLine> OriginOf(CommitAnnotation? annotation, out bool isSystem)
    {
        isSystem = false;
        var lines = new List<OriginLine>();
        if (annotation is null)
        {
            lines.Add(new OriginLine(Texts.Get("Repository_Origin_None"), false));
            return lines;
        }

        if (annotation.Run is { } run)
        {
            isSystem = true;
            var parts = new List<string> { Texts.Get("Repository_Origin_System"), Texts.Format("Repository_Origin_Run", run) };
            if (annotation.Task is { } task)
            {
                parts.Add(Texts.Format("Repository_Origin_Task", task));
            }

            if (annotation.Attempt is { } attempt)
            {
                parts.Add(Texts.Format("Repository_Origin_Attempt", attempt));
            }

            if (annotation.Agent is { } agent)
            {
                parts.Add(Texts.Format("Repository_Origin_Agent", agent.Value));
            }

            if (annotation.Model is { Length: > 0 } model)
            {
                parts.Add(Texts.Format("Repository_Origin_Model", model));
            }

            lines.Add(new OriginLine(string.Join(" · ", parts), false));
        }

        foreach (var link in annotation.Links)
        {
            // The link is read from the commit's end: the other end is what it is linked to.
            var other = link.From.Kind == TraceNodeKind.Commit ? link.To : link.From;
            lines.Add(new OriginLine(Texts.Format("Repository_Origin_Link", RepositoryTexts.Who(link.AssertedBy), RepositoryTexts.Node(other)), false));
        }

        foreach (var suggestion in annotation.Suggestions)
        {
            var other = suggestion.From.Kind == TraceNodeKind.Commit ? suggestion.To : suggestion.From;
            lines.Add(new OriginLine(Texts.Format("Repository_Origin_Suggestion", RepositoryTexts.Node(other), RepositoryTexts.Source(suggestion.Source)), true));
        }

        if (lines.Count == 0)
        {
            lines.Add(new OriginLine(Texts.Get("Repository_Origin_None"), false));
        }

        return lines;
    }

    private ActivityRow ActivityRowOf(RepositoryActivity activity)
    {
        var where = activity.Run is { } run
            ? activity.Task is { } task ? Texts.Format("Repository_Activity_Of", run, task) : Texts.Format("Repository_Activity_OfRun", run)
            : string.Empty;
        return new ActivityRow(
            TimeZoneInfo.ConvertTime(activity.At, _time.LocalTimeZone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            RepositoryTexts.Kind(activity.Kind),
            where,
            activity.Commit is { } commit ? ShortId(commit) : string.Empty,
            activity.Succeeded ? Texts.Get("Repository_Activity_Succeeded") : Texts.Format("Repository_Activity_FailedWith", RepositoryTexts.Error(activity.ErrorCode ?? RepositoryActivityReader.FailedCode)),
            activity.Succeeded);
    }

    private void OpenChain(CommitRow row)
    {
        if (_project is { } project && _chainRequested is { } handler && TraceRef.IsFullCommitId(row.Id))
        {
            handler(this, new RepositoryChainRequest(project, TraceRef.Commit(row.Id)));
        }
    }

    private void ChainAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanOpenChain));
        foreach (var row in Commits)
        {
            row.CanOpenChain = CanOpenChain && TraceRef.IsFullCommitId(row.Id);
        }
    }

    private void Clear()
    {
        RefusalText = null;
        BranchText = null;
        HeadText = null;
        ChangesText = null;
        Remotes.Clear();
        RemotesNote = null;
        Branches.Clear();
        BranchesNote = null;
        Commits.Clear();
        CommitsNote = null;
        Activities.Clear();
        ActivityNote = null;
        FilterError = null;

        // The filters belong to the project they were typed for.
        ResetFilters();
    }

    private static string ShortId(string id) => id.Length <= ShortIdLength ? id : id[..ShortIdLength];

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
