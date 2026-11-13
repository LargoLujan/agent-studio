using System.Collections.ObjectModel;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.Policies;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Repository.PullRequests;

/// <summary>
/// «Repositorio y cambios › Peticiones de fusión (PR)» (docs/21 section 7.1 and 7.2). It reads the pull requests the log follows (<see cref="IPullRequestProjector"/>), says what is pending
/// (<see cref="IRemoteSync"/>), and offers each action only when the capability is declared and the state admits it (F10-1). It never decides or checks a permission: every action goes to
/// <see cref="IPublicationService"/> and what the service or the gate answers is what the screen says.
/// </summary>
/// <remarks>
/// Without the remote, without <c>gh</c> or without a session the section says so with its guide and does not show an empty list as if there were no pull requests. Nothing here blocks the
/// interface: every call is awaited, a newer read drops an older one, the reads are cancelled when the project changes or the section is disposed, and
/// <see cref="IRemoteSync.Changed"/> (which can come from another thread) is posted to the context the section was built in.
/// </remarks>
public sealed partial class PullRequestsViewModel : ObservableObject, IDisposable
{
    private const string RemoteName = "origin";

    // The settings of the repository and the protections of a branch change rarely: a refresh of the list does not ask the platform again for a few minutes.
    private static readonly TimeSpan ReadCacheLifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTimeOffset At, RemoteRepositoryFacts Facts, BranchRules Rules)> _readCache = [];

    private readonly IHostingProviders? _providers;
    private readonly IPullRequestProjector? _projector;
    private readonly IRemoteSync? _sync;
    private readonly IPublicationService? _publication;
    private readonly IHostingPolicyGate? _gate;
    private readonly IRemoteSyncPolicy? _syncPolicy;
    private readonly TimeProvider _time;
    private readonly IAddressOpener? _opener;
    private readonly IRemoteSyncPermission? _syncPermission;

    // The context of the interface, taken when the project is set (the shell does it from the interface thread), never where the section was built.
    private SynchronizationContext? _context;
    private ProjectContext? _project;
    private CancellationTokenSource _actions = new();
    private Func<PullRequestDetail, bool>? _cardNeeds;
    private bool _selectingInPlace;
    private bool _rowsUpdating;
    private bool _permissionSubscribed;
    private bool _syncSwitchUpdating;
    private int _otherRepositories;
    private RemoteRepository? _repository;
    private IHostingProvider? _provider;
    private HostingCapabilities _capabilities = HostingCapabilities.None;
    private CancellationTokenSource _cancellation = new();
    private int _generation;
    private int _detailGeneration;
    private IReadOnlyList<string> _runBranches = [];
    private IReadOnlyList<PullRequestView> _views = [];
    private bool _subscribed;
    private bool _disposed;
    private EventHandler<TraceRef>? _chainRequested;

    /// <param name="providers">Identifies the repository and gives its provider; null: the section says it is not available.</param>
    /// <param name="projector">The pull requests the log follows; null: not available.</param>
    /// <param name="sync">The mailbox: pending work, the health of the connection, «Sincronizar ahora» and «Dejar de seguir».</param>
    /// <param name="publication">The service of the actions; null leaves the section read only (no action has a button).</param>
    /// <param name="gate">The permissions; null hides «Permisos de publicación».</param>
    /// <param name="syncPolicy">Whether the project may sync in the background, to say it in the header.</param>
    /// <param name="time">The clock of the dates and the «hace 3 min».</param>
    /// <param name="opener">Opens a page of a pull request in the browser; null: no such button.</param>
    /// <param name="syncPermission">The switch «Sincronizar con GitHub» of the header (docs/21 §7.1); only a click on it gives the permission. Null: the header only says the state.</param>
    public PullRequestsViewModel(
        IHostingProviders? providers,
        IPullRequestProjector? projector,
        IRemoteSync? sync,
        IPublicationService? publication,
        IHostingPolicyGate? gate,
        IRemoteSyncPolicy? syncPolicy,
        TimeProvider time,
        IAddressOpener? opener = null,
        IRemoteSyncPermission? syncPermission = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _syncPermission = syncPermission;
        _providers = providers;
        _projector = projector;
        _sync = sync;
        _publication = publication;
        _gate = gate;
        _syncPolicy = syncPolicy;
        _time = time;
        _opener = opener;
        Permissions = new PublishingPermissionsViewModel(gate, time);
        _guideText = Texts.Get("Hosting_Pr_NoProject");
    }

    /// <summary>A section for a shell composed without the ports of the remote: it says it is not available.</summary>
    public static PullRequestsViewModel Unavailable(TimeProvider time) => new(null, null, null, null, null, null, time);

    /// <summary>The permissions of publication of this project («Permisos de publicación»).</summary>
    public PublishingPermissionsViewModel Permissions { get; }

    public PullRequestsStatus Status { get; private set; } = PullRequestsStatus.NoProject;

    /// <summary>The guide of the state the section is in (no remote, no <c>gh</c>, no session, wait, no pull requests); null when the list says it all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuide))]
    private string? _guideText;

    public bool HasGuide => GuideText is not null;

    /// <summary>«GitHub · propietario/repositorio»; null while no remote is recognised.</summary>
    [ObservableProperty]
    private string? _repositoryText;

    /// <summary>The session as it was last seen: signed in, in need of a session, offline, or not checked.</summary>
    [ObservableProperty]
    private string? _sessionText;

    [ObservableProperty]
    private string? _lastSyncText;

    /// <summary>Whether the project syncs in the background; null when the shell has no way to say it. It is set in «Ajustes › Git remoto».</summary>
    [ObservableProperty]
    private string? _syncPolicyText;

    /// <summary>Whether the header offers the switch «Sincronizar con GitHub»: only when the shell composed the port that gives the permission.</summary>
    public bool HasSyncSwitch => _syncPermission is not null;

    /// <summary>The switch of the header. Only a click on it gives the permission (the setter is the click); a change that comes from Settings only moves the box.</summary>
    public bool SyncEnabled
    {
        get => _syncPermission is not null && _project is { } project && _syncPermission.IsAllowed(project.ProjectId);
        set
        {
            if (_syncSwitchUpdating || _syncPermission is null || _project is not { } project || value == SyncEnabled)
            {
                return;
            }

            _ = ApplySyncSwitchAsync(project, value);
        }
    }

    private async Task ApplySyncSwitchAsync(ProjectContext project, bool allowed)
    {
        Error = null;
        try
        {
            await _syncPermission!.SetAsync(project.ProjectId, allowed, _actions.Token).ConfigureAwait(true);
            Message = Texts.Get(allowed ? "Hosting_Sync_TurnedOn" : "Hosting_Sync_TurnedOff");
        }
        catch (OperationCanceledException) when (_actions.IsCancellationRequested)
        {
            // Closed or changed project.
        }
#pragma warning disable CA1031 // The port is behind an interface: a failure is said in the section.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Texts.Get("Hosting_Card_Failed");
        }

        RefreshSyncSwitch(project);
    }

    private void RefreshSyncSwitch(ProjectContext project)
    {
        _ = project;
        _syncSwitchUpdating = true;
        try
        {
            OnPropertyChanged(nameof(SyncEnabled));
            OnPropertyChanged(nameof(HasSyncSwitch));
        }
        finally
        {
            _syncSwitchUpdating = false;
        }
    }

    private void OnSyncPermissionChanged(object? sender, string projectId)
    {
        if (_disposed || _project is not { } project || !string.Equals(project.ProjectId, projectId, StringComparison.Ordinal))
        {
            return;
        }

        PostToInterface(() => RefreshSyncSwitch(project));
    }

    /// <summary>The log follows pull requests of another repository (the remote changed): said apart, since they are not in the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherRepository))]
    private string? _otherRepositoryText;

    public bool HasOtherRepository => OtherRepositoryText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<PullRequestRow> Rows { get; } = [];

    /// <summary>What waits to be synchronised, with its reason, above the list.</summary>
    public ObservableCollection<PendingLine> Pending { get; } = [];

    /// <summary>Runs with a branch and no pull request the log knows: each can be published.</summary>
    public ObservableCollection<PublishableRunRow> PublishableRuns { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasPending => Pending.Count > 0;

    public bool HasPublishableRuns => PublishableRuns.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PullRequestRow? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private PullRequestDetail? _detail;

    /// <summary>The card that is open (publish, merge, close, delete, reviewers, verdict); null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCard))]
    private HostingCardViewModel? _card;

    public bool HasCard => Card is not null;

    /// <summary>Someone connected a way to open the chain of a pull request (the shell): without it the button is not offered.</summary>
    public bool CanOpenChain => _chainRequested is not null;

    /// <summary>Raised with the node of the pull request when the person asks for its chain; the shell walks it in «Trabajo».</summary>
    public event EventHandler<TraceRef>? ChainRequested
    {
        add
        {
            _chainRequested += value;
            OnPropertyChanged(nameof(CanOpenChain));
        }

        remove
        {
            _chainRequested -= value;
            OnPropertyChanged(nameof(CanOpenChain));
        }
    }

    /// <summary>Raised on the context of the section when a read ended and the list, the pending work or the state changed: «Ramas» and «Actividad» read it again.</summary>
    public event EventHandler? Reloaded;

    /// <summary>
    /// What the log says of the remote branch of a run (docs/21 section 7.3): published and at which version, not observed yet, or deleted on the remote; null when no pull request of
    /// the log names the run, since then nothing is known of its remote branch.
    /// </summary>
    public string? RemoteBranchText(string run)
    {
        // Of several pull requests on the same branch the one in force speaks (open or draft, or not observed yet); without one, the latest.
        var ofRun = _views.Where(v => string.Equals(v.Run?.Value, run, StringComparison.Ordinal)).OrderByDescending(v => v.PullRequest.Number).ToList();
        var view = ofRun.FirstOrDefault(IsLive) ?? ofRun.FirstOrDefault();
        if (view is null)
        {
            return null;
        }

        return view.Snapshot switch
        {
            null => Texts.Format("Hosting_Branch_NotObserved", view.PullRequest.Number),
            { HeadBranchExists: false } => Texts.Format("Hosting_Branch_RemoteDeleted", view.PullRequest.Number),
            var snapshot => Texts.Format("Hosting_Branch_Published", HostingTexts.Short(snapshot.HeadCommit), view.PullRequest.Number),
        };
    }

    /// <summary>A pull request that can still use its branch: open or draft, or not observed yet (the careful reading: an unobserved one may be open).</summary>
    private static bool IsLive(PullRequestView view) => view.Snapshot is null || view.Snapshot.State is PullRequestState.Open or PullRequestState.Draft;

    /// <summary>Whether the pieces to read and to act are composed.</summary>
    public bool IsAvailable => _providers is not null && _projector is not null;

    /// <summary>The branches of the repository (local names): those of a run without a pull request are offered to publish. Called by the screen that reads the branches.</summary>
    public void SetBranches(IEnumerable<string> localBranchNames)
    {
        _runBranches = [.. localBranchNames.Select(n => RemoteBranch.TryParse(n)?.RunIdentifier).OfType<string>().Distinct(StringComparer.Ordinal)];
        RefreshPublishable();
    }

    /// <summary>The project changed (null: none): the reads of the one before are cancelled and everything is read for the new one.</summary>
    public async Task SetProjectAsync(ProjectContext? project)
    {
        // What belongs to the project before goes at once: no row, card, permission or switch of it stays to be confirmed over the new one.
        Reset();
        _context = SynchronizationContext.Current;
        _project = project;
        if (project is not null && !_disposed)
        {
            if (_sync is not null)
            {
                _sync.Changed += OnSyncChanged;
                _subscribed = true;
            }

            if (_syncPermission is not null)
            {
                _syncPermission.Changed += OnSyncPermissionChanged;
                _permissionSubscribed = true;
            }
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Leaves the project: cancels what was being read or asked, empties the rows, the card, the pending work and the permissions, and says nothing is read yet. The screen that changes
    /// project calls it before it starts reading, so the old project is never on screen with the new one.
    /// </summary>
    public void Reset()
    {
        CancelReads();
        RestartActions();
        Unsubscribe();
        _project = null;
        _repository = null;
        _provider = null;
        _capabilities = HostingCapabilities.None;
        _views = [];
        _otherRepositories = 0;
        Clear();
        Message = null;
        Error = null;
        OtherRepositoryText = null;
        Card = null;
        _cardNeeds = null;
        Permissions.Reset();
        IsBusy = false;
        SetStatus(PullRequestsStatus.NotRead, Texts.Get("Hosting_Pr_Loading"));
        OnPropertyChanged(nameof(SyncEnabled));
    }

    /// <summary>Reads again: the remote, the program, the pull requests and what is pending. A read that is not the last one asked is dropped when it ends.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        var token = RestartReads();
        if (_project is not { } project)
        {
            IsBusy = false;
            Clear();
            Permissions.Reset();
            SetStatus(PullRequestsStatus.NoProject, Texts.Get("Hosting_Pr_NoProject"));
            return;
        }

        if (_providers is null || _projector is null)
        {
            IsBusy = false;
            Clear();
            Permissions.Reset();
            SetStatus(PullRequestsStatus.Unavailable, Texts.Get("Hosting_Pr_Unavailable"));
            return;
        }

        IsBusy = true;
        try
        {
            var repository = await _providers.IdentifyAsync(project, RemoteName, token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            _repository = repository;
            _provider = repository is null ? null : _providers.For(repository);
            if (repository is null || _provider is null)
            {
                Clear();
                RepositoryText = null;
                Permissions.Reset();
                SetStatus(PullRequestsStatus.NoRemote, Texts.Get("Hosting_Pr_NoRemote"));
                return;
            }

            RepositoryText = Texts.Format("Hosting_Header_Repository", $"{repository.Owner}/{repository.Name}");
            var availability = await _provider.ProbeAsync(token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            _capabilities = availability.Capabilities;
            var views = await _projector.ListAsync(project, token).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            _views = [.. views.Where(v => v.PullRequest.Repository == repository)];
            _otherRepositories = views.Count - _views.Count;
            OtherRepositoryText = _otherRepositories > 0 ? Texts.Format("Hosting_Pr_OtherRepository", _otherRepositories) : null;
            ShowHealth(project);
            ShowRows();
            ResolveStatus(availability);
            RefreshPublishable();
            await Permissions.LoadAsync(project, repository).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer read or a closed section: nothing to say.
        }
#pragma warning disable CA1031 // The ports are behind interfaces: a failure (a timeout inside one too) is said in the section, never thrown into the UI.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                Clear();
                Permissions.Reset();
                SetStatus(PullRequestsStatus.Unavailable, Texts.Get("Hosting_Pr_Unreadable"));
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsBusy = false;
                Reloaded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ResolveStatus(HostingAvailability availability)
    {
        var health = _sync?.Health(_project!.ProjectId);
        if (!availability.Installed)
        {
            SetStatus(PullRequestsStatus.NoGh, Texts.Get("Hosting_Pr_NoGh"));
        }
        else if (health?.RateLimitedUntil is { } until && until > _time.GetUtcNow())
        {
            SetStatus(PullRequestsStatus.RateLimited, Texts.Format("Hosting_RateLimited", HostingTexts.Clock(until, _time)));
        }
        else if (health?.LastOutcome == HostingOutcome.SignInNeeded)
        {
            SetStatus(PullRequestsStatus.SignInNeeded, Texts.Get("Hosting_SignIn"));
        }
        else
        {
            // Nothing in the list is «none followed» only when the log has none of any repository: pull requests of another one are said as that.
            SetStatus(PullRequestsStatus.Ready, Rows.Count == 0 && _otherRepositories == 0 ? Texts.Get("Hosting_Pr_None") : null);
        }
    }

    private void SetStatus(PullRequestsStatus status, string? guide)
    {
        Status = status;
        OnPropertyChanged(nameof(Status));
        GuideText = guide;
        OnPropertyChanged(nameof(IsAvailable));
    }

    private void ShowHealth(ProjectContext project)
    {
        // With the switch the header shows the box; without it, only the state, which is changed in Settings.
        SyncPolicyText = _syncPolicy is null || _syncPermission is not null
            ? null
            : Texts.Get(_syncPolicy.PeriodicSyncAllowed(project.ProjectId) ? "Hosting_Header_SyncOn" : "Hosting_Header_SyncOff");
        RefreshSyncSwitch(project);
        if (_sync is null)
        {
            SessionText = null;
            LastSyncText = null;
            return;
        }

        var health = _sync.Health(project.ProjectId);
        LastSyncText = health.LastGoodSync is { } good
            ? Texts.Format("Hosting_Header_LastSync", HostingTexts.When(good, _time))
            : Texts.Get("Hosting_Header_NeverSynced");
        SessionText = health.LastOutcome switch
        {
            null => Texts.Get("Hosting_Header_SessionUnchecked"),
            HostingOutcome.Ok or HostingOutcome.NotModified => Texts.Format("Hosting_Header_SessionOk", health.LastOutcomeAt is { } at ? HostingTexts.When(at, _time) : "-"),
            HostingOutcome.SignInNeeded => Texts.Get("Hosting_SignIn"),
            var other => HostingTexts.Outcome(other.Value),
        };

        Pending.Clear();
        foreach (var work in _sync.Pending(project.ProjectId))
        {
            Pending.Add(new PendingLine(PendingText(work)));
        }

        OnPropertyChanged(nameof(HasPending));
    }

    private void ShowRows()
    {
        // In place: a row that did not change stays the very same object (its selection, its detail and the card that is open stay too); one that changed is replaced where it is.
        var fresh = _views.OrderByDescending(v => v.PullRequest.Number).Select(BuildRow).ToList();
        var selected = Selected?.Reference.TraceId;
        _rowsUpdating = true;
        try
        {
            UpdateRows(fresh, selected);
        }
        finally
        {
            _rowsUpdating = false;
        }
    }

    private void UpdateRows(List<PullRequestRow> fresh, string? selected)
    {
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i < Rows.Count && Rows[i].Reference.TraceId == fresh[i].Reference.TraceId)
            {
                if (Rows[i].Signature != fresh[i].Signature)
                {
                    var wasSelected = ReferenceEquals(Selected, Rows[i]);
                    Rows[i] = fresh[i];
                    if (wasSelected)
                    {
                        SelectInPlace(fresh[i]);
                    }
                }
            }
            else
            {
                Rows.Insert(i, fresh[i]);
            }
        }

        while (Rows.Count > fresh.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        OnPropertyChanged(nameof(HasRows));
        if (selected is not null && Rows.All(r => r.Reference.TraceId != selected))
        {
            // The pull request is no longer followed: its detail and its card go with it.
            _rowsUpdating = false;
            Selected = null;
            Detail = null;
            Card = null;
            _cardNeeds = null;
        }
        else if (selected is not null && !ReferenceEquals(Selected, Rows.First(r => r.Reference.TraceId == selected)))
        {
            SelectInPlace(Rows.First(r => r.Reference.TraceId == selected));
        }
    }

    /// <summary>Selects the new row of the pull request that was already selected: its detail is read again, but the card that is open and what was typed in it stay.</summary>
    private void SelectInPlace(PullRequestRow row)
    {
        _selectingInPlace = true;
        try
        {
            Selected = row;
        }
        finally
        {
            _selectingInPlace = false;
        }

        _ = LoadDetailAsync(row);
    }

    private void RefreshPublishable()
    {
        var followed = _views.Select(v => v.Run?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        PublishableRuns.Clear();
        if (Status == PullRequestsStatus.Ready && _publication is not null && _capabilities.Has(HostingCapability.OpenPullRequest))
        {
            foreach (var run in _runBranches.Where(r => !followed.Contains(r)))
            {
                PublishableRuns.Add(new PublishableRunRow(run));
            }
        }

        OnPropertyChanged(nameof(HasPublishableRuns));
    }

    private PullRequestRow BuildRow(PullRequestView view)
    {
        var caps = _capabilities;
        var snapshot = view.Snapshot;
        var display = view.Display;
        var checks = string.Empty;
        var reviews = string.Empty;
        var deployment = string.Empty;
        var environment = string.Empty;
        var judged = string.Empty;
        var headMoved = false;
        var published = string.Empty;
        var publishedStale = false;

        // What the pull request says of the verdict (docs/21 D-22): only for one that is still open and has a verdict to compare with.
        if (view.Verdict is { } verdictNow && snapshot is { State: PullRequestState.Open or PullRequestState.Draft })
        {
            if (view.PublishedVerdict is not { } last)
            {
                published = Texts.Get("Hosting_Published_None");
            }
            else if (last.Standing == verdictNow && (last.Commit is null || string.Equals(last.Commit, view.JudgedCommit, StringComparison.Ordinal)))
            {
                published = Texts.Format("Hosting_Published_Same", Texts.Get("Hosting_Verdict_" + last.Standing));
            }
            else
            {
                published = Texts.Get("Hosting_Published_Stale");
                publishedStale = true;
            }
        }

        if (snapshot is not null)
        {
            if (caps.Has(HostingCapability.ReadChecks))
            {
                checks = snapshot.Checks.Rollup == ChecksRollup.None
                    ? Texts.Get("Hosting_Checks_None")
                    : Texts.Format("Hosting_Checks", snapshot.Checks.Passing, snapshot.Checks.Failing, snapshot.Checks.Pending)
                        + (snapshot.Checks.Truncated ? " " + Texts.Get("Hosting_Truncated") : string.Empty);
            }

            if (caps.Has(HostingCapability.ReadReviews))
            {
                reviews = snapshot.Reviews.ChangesRequested > 0
                    ? Texts.Get("Hosting_Reviews_Changes")
                    : snapshot.Reviews.Approved > 0
                        ? Texts.Format("Hosting_Reviews_Approved", snapshot.Reviews.Approved)
                        : Texts.Get("Hosting_Reviews_None");
            }

            if (caps.Has(HostingCapability.ReadDeployments) && snapshot.Deployment is { } d)
            {
                deployment = IntegrationByPullRequest.IsDeployed(snapshot) ? Texts.Get("Hosting_Deployed") : Texts.Get("Hosting_Deployment_" + d.State);
                environment = HostingTexts.Data(display.DeploymentEnvironment) ?? string.Empty;
            }

            if (view.JudgedCommit is null)
            {
                judged = Texts.Get("Hosting_Judged_Unknown");
            }
            else if (view.HeadIsJudgedCommit)
            {
                judged = Texts.Get("Hosting_Judged_Same");
            }
            else
            {
                judged = Texts.Get("Hosting_Head_Moved");
                headMoved = true;
            }
        }

        return new PullRequestRow(view)
        {
            NumberText = Texts.Format("Hosting_Pr_Number", view.PullRequest.Number),
            Title = HostingTexts.Data(display.Title) ?? string.Empty,
            StateText = StateText(view),
            OriginText = view.Run is { } run ? Texts.Format("Hosting_Origin_Run", run.Value) : string.Empty,
            MilestoneText = view.Milestone is { } milestone ? Texts.Format("Hosting_Milestone_Of", HostingTexts.Data(milestone) ?? "?") : string.Empty,
            PublishedText = published,
            PublishedIsStale = publishedStale,
            VerdictText = view.Verdict is { } standing ? Texts.Get("Hosting_Verdict_" + standing) : Texts.Get("Hosting_Verdict_None"),
            JudgedText = judged,
            HeadMoved = headMoved,
            ChecksText = checks,
            ReviewsText = reviews,
            DeploymentText = deployment,
            EnvironmentText = environment,
            SyncText = view.Pending.Count > 0
                ? PendingText(view.Pending[0])
                : view.ObservedAt is { } at ? HostingTexts.Ago(at, _time) : Texts.Get("Hosting_Sync_Never"),
            IsPending = view.Pending.Count > 0,
        };
    }

    private static string StateText(PullRequestView view) => view.Snapshot is not { } snapshot
        ? Texts.Get("Hosting_State_NotObserved")
        : snapshot.State switch
        {
            PullRequestState.Open => Texts.Get("Hosting_State_Open"),
            PullRequestState.Draft => Texts.Get("Hosting_State_Draft"),
            PullRequestState.Closed => Texts.Get("Hosting_State_Closed"),

            // «(deducido)» only when the mode is a deduction: a merge Agent Studio did with that method is a fact (docs/21 D-27).
            _ => snapshot.MergeMode is { } mode
                ? view.ModeIsDeduced ? Texts.Format("Hosting_State_MergedDeduced", HostingTexts.MergeMode(mode)) : HostingTexts.MergeMode(mode)
                : Texts.Get("Hosting_State_Merged"),
        };

    /// <summary>The words of a piece of work that waits: its reason and, when there is one, the time of the next try.</summary>
    private string PendingText(RemoteWorkStatus status)
    {
        if (status.Message is { } message)
        {
            return HostingTexts.Say(message);
        }

        return status.Reason switch
        {
            PendingReason.Offline => status.RetryAt is { } at ? Texts.Format("Hosting_Pending_Offline", HostingTexts.Clock(at, _time)) : Texts.Get("Hosting_Pending_OfflineNoTime"),
            PendingReason.SignInNeeded => Texts.Get("Hosting_SignIn"),
            PendingReason.CredentialsNeeded => Texts.Get("Hosting_Credentials"),
            PendingReason.RateLimited => status.RetryAt is { } until ? Texts.Format("Hosting_RateLimited", HostingTexts.Clock(until, _time)) : Texts.Get("Hosting_RateLimitedNoTime"),
            PendingReason.ProjectBusy => Texts.Get("Hosting_Pending_Busy"),
            _ => Texts.Get("Hosting_Work_" + status.State),
        };
    }

    // ---- Selection and detail ----

    partial void OnSelectedChanged(PullRequestRow? value)
    {
        // The control drops its selection while the rows are replaced in place, and the same pull request is selected again at once: neither is the person choosing another one.
        if ((_rowsUpdating && value is null) || _selectingInPlace)
        {
            return;
        }

        Detail = null;
        Card = null;
        _cardNeeds = null;
        if (value is not null)
        {
            _ = LoadDetailAsync(value);
        }
    }

    private async Task LoadDetailAsync(PullRequestRow row)
    {
        var generation = ++_detailGeneration;
        var token = _cancellation.Token;
        var view = row.View;
        var snapshot = view.Snapshot;
        var notes = new List<CardLine>();
        var rules = new List<CardLine>();
        var capabilities = _capabilities;
        RemoteRepositoryFacts? facts = null;
        BranchRules? branchRules = null;
        try
        {
            if (_provider is { } provider && snapshot is not null)
            {
                var cacheKey = view.PullRequest.Repository.Id + "|" + snapshot.BaseBranch.Name;
                if (_readCache.TryGetValue(cacheKey, out var cached) && _time.GetUtcNow() - cached.At < ReadCacheLifetime)
                {
                    facts = cached.Facts;
                    branchRules = cached.Rules;
                }
                else if (capabilities.Has(HostingCapability.ReadRepository))
                {
                    var read = await provider.ReadRepositoryAsync(view.PullRequest.Repository, null, token).ConfigureAwait(true);
                    if (read.Outcome == HostingOutcome.Ok && read.Value is not null)
                    {
                        facts = read.Value;
                    }
                    else
                    {
                        notes.Add(new CardLine(Texts.Format("Hosting_Facts_Unreadable", HostingTexts.Outcome(read.Outcome)), true));
                    }
                }
                else
                {
                    notes.Add(new CardLine(Texts.Get("Hosting_Facts_NotDeclared"), true));
                }

                if (branchRules is null && capabilities.Has(HostingCapability.ReadBranchRules))
                {
                    var read = await provider.ReadBranchRulesAsync(view.PullRequest.Repository, snapshot.BaseBranch, token).ConfigureAwait(true);
                    branchRules = read.Outcome == HostingOutcome.Ok && read.Value is not null ? read.Value : BranchRules.Unreadable;
                    if (facts is not null && branchRules.Readable)
                    {
                        _readCache[cacheKey] = (_time.GetUtcNow(), facts, branchRules);
                    }
                }

                if (branchRules is not null)
                {
                    rules.AddRange(PublishCardViewModel.RulesLines(branchRules, snapshot.BaseBranch.Name));
                }
                else
                {
                    notes.Add(new CardLine(Texts.Format("Hosting_Rules_NotDeclared", snapshot.BaseBranch.Name)));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // The provider is behind a port: a failure (a timeout inside it too) is said in the detail, never thrown into the UI.
        catch (Exception)
#pragma warning restore CA1031
        {
            notes.Add(new CardLine(Texts.Get("Hosting_Pr_Unreadable"), true));
        }

        if (generation != _detailGeneration || !ReferenceEquals(Selected, row))
        {
            return;
        }

        Detail = BuildDetail(row, capabilities, facts, branchRules, rules, notes);

        // A card that was open for an action the pull request no longer admits (it changed under it) is closed, and it is said.
        if (Card is not null && _cardNeeds is { } needs && !needs(Detail))
        {
            Card = null;
            _cardNeeds = null;
            Message = Texts.Get("Hosting_Card_Stale");
        }
    }

    private PullRequestDetail BuildDetail(PullRequestRow row, HostingCapabilities declared, RemoteRepositoryFacts? facts, BranchRules? rules, List<CardLine> ruleLines, List<CardLine> notes)
    {
        var view = row.View;
        var snapshot = view.Snapshot;
        var caps = facts is null ? declared : declared.NarrowedBy(facts, rules);
        var open = snapshot is { State: PullRequestState.Open or PullRequestState.Draft };
        var hasRun = view.Run is not null;
        var canWrite = _publication is not null;
        IReadOnlyList<MergeMethod> methods = facts is null || !caps.Has(HostingCapability.Merge) ? [] : caps.MergeMethods;

        // The service merges only the commit the judge saw (docs/21 D-20): with another head, or without knowing what the judge saw, «Fusionar…» is not offered and the detail says why.
        var judgedHead = view.JudgedCommit is not null && view.HeadIsJudgedCommit;
        if (snapshot is { State: PullRequestState.Open } && caps.Has(HostingCapability.Merge) && methods.Count > 0 && !judgedHead)
        {
            notes.Add(new CardLine(Texts.Get(view.JudgedCommit is null ? "Hosting_Merge_NoJudged" : "Hosting_Merge_HeadMoved"), true));
        }

        // Another pull request of the same run that is still alive uses the branch: deleting it would close that one (docs/21 D-16).
        var branchInUse = view.Run is { } ownRun
            && _views.Any(v => v.PullRequest != view.PullRequest && v.Run == ownRun && IsLive(v));
        var originals = snapshot is null || snapshot.OriginalCommits.Count == 0 ? string.Empty
            : Texts.Format(snapshot.CommitsTruncated ? "Hosting_Detail_OriginalsTruncated" : "Hosting_Detail_Originals", snapshot.OriginalCommits.Count, string.Join(", ", snapshot.OriginalCommits.Select(HostingTexts.Short)));
        var results = snapshot is null ? string.Empty : IntegrationByPullRequest.ResultCommitsOf(snapshot) is { Count: > 0 } r
            ? Texts.Format("Hosting_Detail_Results", r.Count, string.Join(", ", r.Select(HostingTexts.Short)))
            : string.Empty;
        return new PullRequestDetail
        {
            Heading = Texts.Format("Hosting_Detail_Heading", row.Number),
            BaseText = snapshot is null ? string.Empty : Texts.Format("Hosting_Detail_Base", snapshot.BaseBranch.Name),
            Rules = ruleLines,
            Notes = notes,
            FailingChecks = [.. view.Display.FailingCheckNames.Select(HostingTexts.Data).OfType<string>()],
            OriginalsText = originals,
            ResultsText = results,
            CanPublishVerdict = canWrite && open && hasRun && (caps.Has(HostingCapability.CommitStatus) || caps.Has(HostingCapability.Comment)),
            VerdictOnlyAsComment = !caps.Has(HostingCapability.CommitStatus) && caps.Has(HostingCapability.Comment),
            CanUpdate = canWrite && open && hasRun && caps.Has(HostingCapability.UpdatePullRequest),
            CanRequestReviewers = canWrite && open && hasRun && caps.Has(HostingCapability.RequestReviewers),
            CanMerge = canWrite && snapshot is { State: PullRequestState.Open } && caps.Has(HostingCapability.Merge) && methods.Count > 0 && judgedHead,
            CanClose = canWrite && open && caps.Has(HostingCapability.ClosePullRequest),
            CanDeleteBranch = canWrite && hasRun && !branchInUse && snapshot is { HeadBranchExists: true, State: PullRequestState.Merged or PullRequestState.Closed } && caps.Has(HostingCapability.DeleteBranch),
            CanOpenInBrowser = _opener is not null && PullRequestAddress.Of(view.PullRequest) is not null,
            CanUnfollow = _sync is not null,
            MergeMethods = methods,
        };
    }

    // ---- Actions ----

    /// <summary>«Sincronizar ahora»: the click of the person, allowed with the periodic poll off.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (_sync is null || _project is not { } project)
        {
            return;
        }

        var token = _actions.Token;
        await Run(() => _sync.SyncNowAsync(project, token), "Hosting_Sync_Asked", token).ConfigureAwait(true);
    }

    /// <summary>«Comprobar la conexión»: asks the platform who the account is; the answer says if there is a session, no limit or no connection.</summary>
    [RelayCommand]
    private async Task CheckConnectionAsync()
    {
        if (_provider is null || _repository is not { } repository)
        {
            return;
        }

        Error = null;
        Message = null;
        IsBusy = true;
        var token = _actions.Token;
        try
        {
            var result = await _provider.ReadAccountAsync(repository, token).ConfigureAwait(true);
            if (result.Outcome == HostingOutcome.Ok && result.Value is { } login)
            {
                Message = Texts.Format("Hosting_Connection_Ok", login.Value);
            }
            else if (result.Outcome == HostingOutcome.Ok)
            {
                // Said Ok and gave no account: that is not a connection to be proud of, and «GitHub lo aceptó» is no error either.
                Error = HostingTexts.Outcome(HostingOutcome.InvalidAnswer);
            }
            else if (result.Outcome == HostingOutcome.RateLimited && result.RetryAt is { } until)
            {
                Error = Texts.Format("Hosting_RateLimited", HostingTexts.Clock(until, _time));
            }
            else if (result.Outcome == HostingOutcome.SignInNeeded)
            {
                Error = Texts.Get("Hosting_SignIn");
            }
            else
            {
                Error = result.RefusalCode is { } code ? HostingTexts.Code(code) : HostingTexts.Outcome(result.Outcome);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closed or changed project: nothing to say.
        }
#pragma warning disable CA1031 // The provider is behind a port: a failure (a timeout inside it too) is said in the section.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Texts.Get("Hosting_Pr_Unreadable");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Dejar de seguir»: the mailbox stops observing the selected pull request. Nothing changes on the platform.</summary>
    [RelayCommand]
    private async Task UnfollowAsync()
    {
        if (_sync is null || _project is not { } project || Selected is not { } row)
        {
            return;
        }

        var token = _actions.Token;
        await Run(() => _sync.UnfollowAsync(project, row.Reference, token), "Hosting_Unfollowed", token).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>«Abrir en el navegador»: opens only the address the code builds from the validated identifiers of the pull request.</summary>
    [RelayCommand]
    private void OpenInBrowser()
    {
        if (_opener is null || Selected is not { } row || PullRequestAddress.Of(row.Reference) is not { } address)
        {
            return;
        }

        try
        {
            _opener.Open(address);
        }
#pragma warning disable CA1031 // The shell is behind a port: a failure is said in the section.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Texts.Get("Hosting_Browser_Failed");
        }
    }

    /// <summary>«Ver su cadena»: raises the node of the pull request for the shell to walk in «Trabajo».</summary>
    [RelayCommand]
    private void ShowChain()
    {
        if (Selected is { } row)
        {
            _chainRequested?.Invoke(this, TraceRef.PullRequest(row.Reference));
        }
    }

    [RelayCommand]
    private async Task OpenVerdictCardAsync()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanPublishVerdict: true } detail || row.View.Snapshot is not { } snapshot)
        {
            return;
        }

        var coverage = await CoverageAsync(
            project, row.Reference.Repository, snapshot.BaseBranch, [HostingOperationKind.PublishVerdictStatus, HostingOperationKind.PublishVerdictComment], _actions.Token).ConfigureAwait(true);
        if (!ReferenceEquals(Selected, row))
        {
            return;
        }

        var lines = new List<CardLine> { new(Texts.Get("Hosting_Verdict_NotApproval")), new(coverage) };
        if (detail.VerdictOnlyAsComment)
        {
            lines.Add(new CardLine(Texts.Get("Hosting_Verdict_OnlyComment")));
        }

        _cardNeeds = d => d.CanPublishVerdict;
        Card = new ActionCardViewModel(
            Texts.Format("Hosting_Verdict_Heading", row.Number),
            Texts.Get("Hosting_Verdict_Confirm"),
            null,
            lines,
            ct => _publication.UpdateVerdictAsync(project, row.Reference, ct),
            CloseCard,
            Sent);
    }

    [RelayCommand]
    private void OpenReviewersCard()
    {
        if (_publication is not null && _project is { } project && Selected is { } row && Detail is { CanRequestReviewers: true })
        {
            _cardNeeds = d => d.CanRequestReviewers;
            Card = new ReviewersCardViewModel(project, row.Reference, _publication, CloseCard, Sent);
        }
    }

    [RelayCommand]
    private void OpenMergeCard()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanMerge: true } detail || row.View.Snapshot is not { } snapshot)
        {
            return;
        }

        // «Fusionar…» is offered only when the head is the commit the judge saw, and the service merges exactly that one (docs/21 D-20): the card says both.
        var lines = new List<CardLine>
        {
            new(Texts.Format("Hosting_Merge_Into", snapshot.BaseBranch.Name)),
            new(Texts.Format("Hosting_Merge_Commit", HostingTexts.Short(snapshot.HeadCommit))),
            new(Texts.Get("Hosting_Merge_Exact")),
        };
        lines.AddRange(detail.Rules);
        if (row.ChecksText.Length > 0)
        {
            lines.Add(new CardLine(row.ChecksText));
        }

        if (row.ReviewsText.Length > 0)
        {
            lines.Add(new CardLine(row.ReviewsText));
        }

        _cardNeeds = d => d.CanMerge;
        Card = new MergeCardViewModel(project, row.Reference, snapshot.BaseBranch.Name, detail.MergeMethods, lines, _publication, CloseCard, Sent);
    }

    [RelayCommand]
    private void OpenCloseCard()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanClose: true } || row.View.Snapshot is not { } snapshot)
        {
            return;
        }

        _cardNeeds = d => d.CanClose;
        Card = new ActionCardViewModel(
            Texts.Format("Hosting_Close_Heading", row.Number),
            Texts.Get("Hosting_Close_Confirm"),
            Texts.Format("Hosting_Close_Hello", snapshot.BaseBranch.Name),
            [
                new CardLine(Texts.Format("Hosting_Card_Where", $"{row.Reference.Repository.Owner}/{row.Reference.Repository.Name}", HostingTexts.Short(snapshot.HeadCommit))),
                new CardLine(Texts.Get("Hosting_Close_Explain")),
            ],
            ct => _publication.CloseAsync(project, row.Reference, ct),
            CloseCard,
            Sent);
    }

    [RelayCommand]
    private void OpenDeleteBranchCard()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanDeleteBranch: true } || row.View.Snapshot is not { } snapshot)
        {
            return;
        }

        if (row.View.Run is not { } run)
        {
            return;
        }

        _cardNeeds = d => d.CanDeleteBranch;
        Card = new ActionCardViewModel(
            Texts.Format("Hosting_Delete_Heading", row.Number),
            Texts.Get("Hosting_Delete_Confirm"),
            Texts.Get("Hosting_Delete_Hello"),
            [
                new CardLine(Texts.Format("Hosting_Card_Where", $"{row.Reference.Repository.Owner}/{row.Reference.Repository.Name}", HostingTexts.Short(snapshot.HeadCommit))),
                new CardLine(Texts.Format("Hosting_Delete_Explain", RemoteBranch.ForRun(run).Name)),
            ],
            ct => _publication.DeleteRunBranchAsync(project, run, ct),
            CloseCard,
            Sent);
    }

    /// <summary>«Publicar y abrir PR…» for a run: the card shows what the service says it would do, and what covers it.</summary>
    [RelayCommand]
    private async Task OpenPublishCardAsync(PublishableRunRow? row)
    {
        if (row is null)
        {
            return;
        }

        RunId run;
        try
        {
            run = new RunId(row.Run);
        }
        catch (ArgumentException)
        {
            return;
        }

        await ShowPublishCardAsync(run).ConfigureAwait(true);
    }

    /// <summary>
    /// «Actualizar el PR…»: publishing the same run again opens or updates the pull request (docs/21 D-18), so the card is the one of publication, over a pull request that
    /// already exists, with the text to edit.
    /// </summary>
    [RelayCommand]
    private async Task OpenUpdateCardAsync()
    {
        if (Selected is { } row && row.View.Run is { } run && Detail is { CanUpdate: true })
        {
            await ShowPublishCardAsync(run).ConfigureAwait(true);
        }
    }

    /// <summary>Opens the card of publication of a run, wherever the person asked for it (the list of runs here, or the button beside «Entregar» of the monitor).</summary>
    public async Task ShowPublishCardAsync(RunId run)
    {
        if (_publication is null || _project is not { } project)
        {
            Error = Texts.Get("Hosting_Pr_Unavailable");
            return;
        }

        Error = null;
        Message = null;
        var token = _actions.Token;
        IsBusy = true;
        try
        {
            var preview = await _publication.PreviewAsync(project, run, null, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || _project != project)
            {
                return;
            }

            HostingOperationKind[] kinds = preview.Existing is null
                ? [HostingOperationKind.PushRunBranch, HostingOperationKind.OpenPullRequest]
                : [HostingOperationKind.PushRunBranch, HostingOperationKind.UpdatePullRequest];
            var coverage = await CoverageAsync(project, preview.Repository, preview.BaseBranch, kinds, token).ConfigureAwait(true);
            _cardNeeds = null;
            Card = new PublishCardViewModel(project, run, preview, coverage, _publication, CloseCard, Sent);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closed or changed project.
        }
        catch (UserTextException refusal)
        {
            Error = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The service is behind a port: a failure (a timeout inside it too) is said in the section.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Texts.Get("Hosting_Card_Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// What covers an action: a standing permission the gate holds for every operation it needs, or the confirmation with Windows Hello. The gate is not asked to authorise: it is asked
    /// what it holds. The push has no base branch; the others go towards <paramref name="baseBranch"/>.
    /// </summary>
    private async Task<string> CoverageAsync(ProjectContext project, RemoteRepository? repository, RemoteBranch? baseBranch, IReadOnlyList<HostingOperationKind> kinds, CancellationToken token)
    {
        var hello = Texts.Get("Hosting_Publish_CoverHello");
        if (_gate is null || repository is null || baseBranch is null)
        {
            return hello;
        }

        try
        {
            var grants = await _gate.GrantsAsync(project, token).ConfigureAwait(true);
            var covering = new List<HostingPolicyGrant>();
            foreach (var kind in kinds)
            {
                var found = grants.Where(g => g.Covers(kind, repository, kind == HostingOperationKind.PushRunBranch ? null : baseBranch)).OrderByDescending(g => g.At).FirstOrDefault();
                if (found is null)
                {
                    return hello;
                }

                covering.Add(found);
            }

            return Texts.Format("Hosting_Publish_CoverGrant", HostingTexts.When(covering.Max(g => g.At), _time));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The gate is behind a port: without its answer the card says the careful thing.
        catch (Exception)
#pragma warning restore CA1031
        {
            return hello;
        }
    }

    private void CloseCard()
    {
        Card = null;
        _cardNeeds = null;
    }

    private void Sent(string message)
    {
        Message = message;
        Error = null;
    }

    private async Task Run(Func<Task> action, string doneKey, CancellationToken token)
    {
        Error = null;
        Message = null;
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
            Message = Texts.Get(doneKey);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closed or changed project.
        }
        catch (UserTextException refusal)
        {
            Error = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The mailbox is behind a port: a failure (a timeout inside it too) is said in the section.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Texts.Get("Hosting_Card_Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Lifetime ----

    private void OnSyncChanged(object? sender, string projectId)
    {
        if (_disposed || _project is not { } project || !string.Equals(project.ProjectId, projectId, StringComparison.Ordinal))
        {
            return;
        }

        PostToInterface(() => _ = ReloadAsync());
    }

    /// <summary>The mailbox and the settings raise their events from their own thread: what touches the lists runs on the context of the interface the project was set from.</summary>
    private void PostToInterface(Action action)
    {
        if (_context is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private CancellationToken RestartReads()
    {
        var previous = _cancellation;
        _cancellation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
        return _cancellation.Token;
    }

    /// <summary>The actions have a cancellation of their own: a refresh of the list never aborts «Publicar» or «Sincronizar ahora»; changing the project does.</summary>
    private void RestartActions()
    {
        var previous = _actions;
        _actions = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();
    }

    private void CancelReads()
    {
        _generation++;
        _detailGeneration++;
        RestartReads();
    }

    private void Unsubscribe()
    {
        if (_subscribed && _sync is not null)
        {
            _sync.Changed -= OnSyncChanged;
        }

        _subscribed = false;
        if (_permissionSubscribed && _syncPermission is not null)
        {
            _syncPermission.Changed -= OnSyncPermissionChanged;
        }

        _permissionSubscribed = false;
    }

    private void Clear()
    {
        Rows.Clear();
        Pending.Clear();
        PublishableRuns.Clear();
        Selected = null;
        Detail = null;
        RepositoryText = null;
        SessionText = null;
        LastSyncText = null;
        SyncPolicyText = null;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasPublishableRuns));
    }

    /// <summary>Stops listening to the mailbox and cancels what is being read.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unsubscribe();
        _cancellation.Cancel();
        _cancellation.Dispose();
        _actions.Cancel();
        _actions.Dispose();
    }
}
