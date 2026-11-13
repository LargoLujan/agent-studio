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
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private ProjectContext? _project;
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
    public PullRequestsViewModel(
        IHostingProviders? providers,
        IPullRequestProjector? projector,
        IRemoteSync? sync,
        IPublicationService? publication,
        IHostingPolicyGate? gate,
        IRemoteSyncPolicy? syncPolicy,
        TimeProvider time,
        IAddressOpener? opener = null)
    {
        ArgumentNullException.ThrowIfNull(time);
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
        CancelReads();
        Unsubscribe();
        _project = project;
        _repository = null;
        _provider = null;
        _capabilities = HostingCapabilities.None;
        _views = [];
        Clear();
        Message = null;
        Error = null;
        Card = null;
        if (project is not null && _sync is not null && !_disposed)
        {
            _sync.Changed += OnSyncChanged;
            _subscribed = true;
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Reads again: the remote, the program, the pull requests and what is pending. A read that is not the last one asked is dropped when it ends.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        var token = RestartReads();
        if (_project is not { } project)
        {
            SetStatus(PullRequestsStatus.NoProject, Texts.Get("Hosting_Pr_NoProject"));
            return;
        }

        if (_providers is null || _projector is null)
        {
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
                SetStatus(PullRequestsStatus.NoRemote, Texts.Get("Hosting_Pr_NoRemote"));
                await Permissions.LoadAsync(project, null).ConfigureAwait(true);
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
            ShowHealth(project);
            ShowRows();
            ResolveStatus(availability);
            RefreshPublishable();
            await Permissions.LoadAsync(project, repository).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer read or a closed section: nothing to say.
        }
#pragma warning disable CA1031 // The ports are behind interfaces: a failure is said in the section, never thrown into the UI.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (generation == _generation)
            {
                Clear();
                SetStatus(PullRequestsStatus.Unavailable, Texts.Get("Hosting_Pr_Unreadable"));
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
            SetStatus(PullRequestsStatus.Ready, Rows.Count == 0 ? Texts.Get("Hosting_Pr_None") : null);
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
        SyncPolicyText = _syncPolicy is null
            ? null
            : Texts.Get(_syncPolicy.PeriodicSyncAllowed(project.ProjectId) ? "Hosting_Header_SyncOn" : "Hosting_Header_SyncOff");
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
        var selected = Selected?.Reference.TraceId;
        Rows.Clear();
        foreach (var view in _views.OrderByDescending(v => v.PullRequest.Number))
        {
            Rows.Add(BuildRow(view));
        }

        OnPropertyChanged(nameof(HasRows));
        Selected = Rows.FirstOrDefault(r => r.Reference.TraceId == selected);
    }

    private void RefreshPublishable()
    {
        var followed = _views.Select(v => v.Run).OfType<string>().ToHashSet(StringComparer.Ordinal);
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
            StateText = StateText(snapshot),
            OriginText = view.Run is { } run ? Texts.Format("Hosting_Origin_Run", HostingTexts.Data(run) ?? "?") : string.Empty,
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

    private static string StateText(RemoteSnapshot? snapshot) => snapshot is null
        ? Texts.Get("Hosting_State_NotObserved")
        : snapshot.State switch
        {
            PullRequestState.Open => Texts.Get("Hosting_State_Open"),
            PullRequestState.Draft => Texts.Get("Hosting_State_Draft"),
            PullRequestState.Closed => Texts.Get("Hosting_State_Closed"),

            // The mode is a deduction: the view does not say whether Agent Studio merged it (docs/21 D-27), so it is said as one.
            _ => snapshot.MergeMode is { } mode ? Texts.Format("Hosting_State_MergedDeduced", HostingTexts.MergeMode(mode)) : Texts.Get("Hosting_State_Merged"),
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
        Detail = null;
        Card = null;
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
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // The provider is behind a port: a failure is said in the detail, never thrown into the UI.
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
    }

    private PullRequestDetail BuildDetail(PullRequestRow row, HostingCapabilities declared, RemoteRepositoryFacts? facts, BranchRules? rules, List<CardLine> ruleLines, List<CardLine> notes)
    {
        var view = row.View;
        var snapshot = view.Snapshot;
        var caps = facts is null ? declared : declared.NarrowedBy(facts);
        var open = snapshot is { State: PullRequestState.Open or PullRequestState.Draft };
        var hasRun = view.Run is not null;
        var canWrite = _publication is not null;
        var methods = facts is null ? [] : ModesFor(caps, rules);
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
            CanRequestReviewers = canWrite && open && hasRun && caps.Has(HostingCapability.RequestReviewers),
            CanMerge = canWrite && snapshot is { State: PullRequestState.Open } && caps.Has(HostingCapability.Merge) && methods.Count > 0,
            CanClose = canWrite && open && caps.Has(HostingCapability.ClosePullRequest),
            CanDeleteBranch = canWrite && hasRun && snapshot is { HeadBranchExists: true, State: PullRequestState.Merged or PullRequestState.Closed } && caps.Has(HostingCapability.DeleteBranch),
            CanOpenInBrowser = _opener is not null && PullRequestAddress.Of(view.PullRequest) is not null,
            CanUnfollow = _sync is not null,
            MergeMethods = methods,
        };
    }

    /// <summary>
    /// The methods to offer: those the provider declares and the repository allows (already in <paramref name="caps"/>), minus the commit of merge when the branch asks for a linear
    /// history, which GitHub then refuses. It only narrows: it never adds a method.
    /// </summary>
    internal static IReadOnlyList<MergeMethod> ModesFor(HostingCapabilities caps, BranchRules? rules) =>
        !caps.Has(HostingCapability.Merge)
            ? []
            : [.. caps.MergeMethods.Where(m => !(m == MergeMethod.MergeCommit && rules is { Readable: true } && rules.Has(BranchRuleKind.LinearHistory)))];

    // ---- Actions ----

    /// <summary>«Sincronizar ahora»: the click of the person, allowed with the periodic poll off.</summary>
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (_sync is null || _project is not { } project)
        {
            return;
        }

        await Run(() => _sync.SyncNowAsync(project, _cancellation.Token), "Hosting_Sync_Asked").ConfigureAwait(true);
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
        try
        {
            var result = await _provider.ReadAccountAsync(repository, _cancellation.Token).ConfigureAwait(true);
            if (result.Outcome == HostingOutcome.Ok && result.Value is { } login)
            {
                Message = Texts.Format("Hosting_Connection_Ok", login.Value);
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
        catch (OperationCanceledException)
        {
            // Closed or changed project: nothing to say.
        }
#pragma warning disable CA1031 // The provider is behind a port: a failure is said in the section.
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

        await Run(() => _sync.UnfollowAsync(project, row.Reference, _cancellation.Token), "Hosting_Unfollowed").ConfigureAwait(true);
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
    private void OpenVerdictCard()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanPublishVerdict: true } detail)
        {
            return;
        }

        var lines = new List<CardLine> { new(Texts.Get("Hosting_Verdict_NotApproval")) };
        if (detail.VerdictOnlyAsComment)
        {
            lines.Add(new CardLine(Texts.Get("Hosting_Verdict_OnlyComment")));
        }

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

        var lines = new List<CardLine>
        {
            new(Texts.Format("Hosting_Merge_Into", snapshot.BaseBranch.Name)),
            row.View.JudgedCommit is not null && row.View.HeadIsJudgedCommit
                ? new CardLine(Texts.Format("Hosting_Merge_Commit", HostingTexts.Short(snapshot.HeadCommit)))
                : new CardLine(Texts.Get("Hosting_Head_Moved"), true),
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

        Card = new MergeCardViewModel(project, row.Reference, snapshot.BaseBranch.Name, detail.MergeMethods, lines, _publication, CloseCard, Sent);
    }

    [RelayCommand]
    private void OpenCloseCard()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanClose: true } || row.View.Snapshot is not { } snapshot)
        {
            return;
        }

        Card = new ActionCardViewModel(
            Texts.Format("Hosting_Close_Heading", row.Number),
            Texts.Get("Hosting_Close_Confirm"),
            Texts.Format("Hosting_Close_Hello", snapshot.BaseBranch.Name),
            [new CardLine(Texts.Get("Hosting_Close_Explain"))],
            ct => _publication.CloseAsync(project, row.Reference, ct),
            CloseCard,
            Sent);
    }

    [RelayCommand]
    private void OpenDeleteBranchCard()
    {
        if (_publication is null || _project is not { } project || Selected is not { } row || Detail is not { CanDeleteBranch: true })
        {
            return;
        }

        RunId run;
        try
        {
            run = new RunId(row.View.Run!);
        }
        catch (ArgumentException)
        {
            return;
        }

        Card = new ActionCardViewModel(
            Texts.Format("Hosting_Delete_Heading", row.Number),
            Texts.Get("Hosting_Delete_Confirm"),
            Texts.Get("Hosting_Delete_Hello"),
            [new CardLine(Texts.Format("Hosting_Delete_Explain", RemoteBranch.ForRun(run).Name))],
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
        var token = _cancellation.Token;
        IsBusy = true;
        try
        {
            var preview = await _publication.PreviewAsync(project, run, null, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || _project != project)
            {
                return;
            }

            var coverage = await CoverageAsync(project, preview, token).ConfigureAwait(true);
            Card = new PublishCardViewModel(project, run, preview, coverage, _publication, CloseCard, Sent);
        }
        catch (OperationCanceledException)
        {
            // Closed or changed project.
        }
        catch (UserTextException refusal)
        {
            Error = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The service is behind a port: a failure is said in the section.
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

    /// <summary>What covers a publication: a standing permission the gate holds for the push and for opening the pull request, or the confirmation with Windows Hello. The gate is not asked to authorise: it is asked what it holds.</summary>
    private async Task<string> CoverageAsync(ProjectContext project, PublicationPreview preview, CancellationToken token)
    {
        var hello = Texts.Get("Hosting_Publish_CoverHello");
        if (_gate is null || preview.Repository is not { } repository || preview.BaseBranch is not { } baseBranch)
        {
            return hello;
        }

        try
        {
            var grants = await _gate.GrantsAsync(project, token).ConfigureAwait(true);
            var push = grants.Where(g => g.Covers(HostingOperationKind.PushRunBranch, repository, null)).OrderByDescending(g => g.At).FirstOrDefault();
            var open = grants.Where(g => g.Covers(HostingOperationKind.OpenPullRequest, repository, baseBranch)).OrderByDescending(g => g.At).FirstOrDefault();
            return push is not null && open is not null
                ? Texts.Format("Hosting_Publish_CoverGrant", HostingTexts.When(push.At > open.At ? push.At : open.At, _time))
                : hello;
        }
        catch (OperationCanceledException)
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

    private void CloseCard() => Card = null;

    private void Sent(string message)
    {
        Message = message;
        Error = null;
    }

    private async Task Run(Func<Task> action, string doneKey)
    {
        Error = null;
        Message = null;
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
            Message = Texts.Get(doneKey);
        }
        catch (OperationCanceledException)
        {
            // Closed or changed project.
        }
        catch (UserTextException refusal)
        {
            Error = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The mailbox is behind a port: a failure is said in the section.
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

        // The mailbox raises it from its own thread: the lists belong to the one that built the section.
        if (_context is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => _ = ReloadAsync(), null);
        }
        else
        {
            _ = ReloadAsync();
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
    }
}
