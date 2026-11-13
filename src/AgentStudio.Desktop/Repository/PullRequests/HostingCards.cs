using System.Collections.ObjectModel;
using AgentStudio.Application.Hosting;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Hosting;
using AgentStudio.Domain.Orchestration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Repository.PullRequests;

/// <summary>
/// A card that shows what an action on the remote does before its button (docs/21 section 7.2 and D-17, D-18, D-20). The card asks the service and shows what it answers: it never decides
/// or checks a permission itself. A refusal of the service (a <see cref="UserTextException"/>) is said with its coded text; a ticket is said as sent, and what happens next shows in the pending
/// list and in the activity.
/// </summary>
public abstract partial class HostingCardViewModel : ObservableObject
{
    private readonly Action _close;
    private readonly Action<string> _sent;
    private CancellationTokenSource? _cancellation;

    protected HostingCardViewModel(string heading, string confirmText, Action close, Action<string> sent)
    {
        Heading = heading;
        ConfirmText = confirmText;
        _close = close;
        _sent = sent;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !IsBusy && CanConfirm);
        CancelCommand = new RelayCommand(Cancel);
    }

    public string Heading { get; }

    /// <summary>The button: a verb and its object.</summary>
    public string ConfirmText { get; }

    /// <summary>What the card says, one sentence each: where to, what, the protections, the checks.</summary>
    public ObservableCollection<CardLine> Lines { get; } = [];

    /// <summary>The fixed notice of an action that asks for Windows Hello every time; null for one that a permission can cover.</summary>
    public string? HelloNotice { get; protected set; }

    public bool HasHelloNotice => HelloNotice is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    private bool _isBusy;

    public IAsyncRelayCommand ConfirmCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>Whether the button can go on: what the card asks must be there.</summary>
    protected virtual bool CanConfirm => true;

    /// <summary>Whether the card has nothing to offer (the service said it cannot be done): the button is not drawn.</summary>
    public virtual bool ShowsConfirm => true;

    /// <summary>What the card asks the service for; the ticket it returns is what was enqueued.</summary>
    protected abstract Task<RemoteWorkTicket> RunAsync(CancellationToken ct);

    protected void RaiseCanConfirm() => ConfirmCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => ConfirmCommand.NotifyCanExecuteChanged();

    private async Task ConfirmAsync()
    {
        Error = null;
        var source = _cancellation = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await RunAsync(source.Token).ConfigureAwait(true);
            _sent(Texts.Get("Hosting_Card_Sent"));
            _close();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // The person closed the card: nothing to say. A timeout inside a port is not that: it falls to the general answer below.
        }
        catch (UserTextException refusal)
        {
            Error = HostingTexts.Say(refusal.Text);
        }
#pragma warning disable CA1031 // The service is behind a port: whatever it fails with is said in the card, never thrown into the UI.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Texts.Get("Hosting_Card_Failed");
        }
        finally
        {
            IsBusy = false;
            _cancellation = null;
            source.Dispose();
        }
    }

    private void Cancel()
    {
        _cancellation?.Cancel();
        _close();
    }
}

/// <summary>An action that needs no input: ask for the verdict to be published, close the pull request, delete the branch.</summary>
public sealed class ActionCardViewModel : HostingCardViewModel
{
    private readonly Func<CancellationToken, Task<RemoteWorkTicket>> _run;

    public ActionCardViewModel(string heading, string confirmText, string? helloNotice, IEnumerable<CardLine> lines, Func<CancellationToken, Task<RemoteWorkTicket>> run, Action close, Action<string> sent)
        : base(heading, confirmText, close, sent)
    {
        _run = run;
        HelloNotice = helloNotice;
        foreach (var line in lines)
        {
            Lines.Add(line);
        }
    }

    protected override Task<RemoteWorkTicket> RunAsync(CancellationToken ct) => _run(ct);

    public override string ToString() => Heading;
}

/// <summary>«Pedir revisión…»: the accounts to ask, by the rule of the platform; anything else is refused before the service is called.</summary>
public sealed partial class ReviewersCardViewModel : HostingCardViewModel
{
    private readonly ProjectContext _project;
    private readonly PullRequestRef _pullRequest;
    private readonly IPublicationService _service;

    public ReviewersCardViewModel(ProjectContext project, PullRequestRef pullRequest, IPublicationService service, Action close, Action<string> sent)
        : base(Texts.Format("Hosting_Reviewers_Heading", pullRequest.Number), Texts.Get("Hosting_Reviewers_Confirm"), close, sent)
    {
        _project = project;
        _pullRequest = pullRequest;
        _service = service;
        HelloNotice = Texts.Get("Hosting_Reviewers_Hello");
        Lines.Add(new CardLine(Texts.Get("Hosting_Reviewers_Explain")));
    }

    /// <summary>The accounts, separated by spaces or commas.</summary>
    [ObservableProperty]
    private string _loginsText = string.Empty;

    partial void OnLoginsTextChanged(string value) => RaiseCanConfirm();

    protected override bool CanConfirm => !string.IsNullOrWhiteSpace(LoginsText);

    protected override Task<RemoteWorkTicket> RunAsync(CancellationToken ct)
    {
        var logins = new List<RemoteLogin>();
        foreach (var part in LoginsText.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (RemoteLogin.TryParse(part) is not { } login)
            {
                throw new UserTextException(new UserText("account.login-invalid"), "A reviewer is not a valid account name.");
            }

            if (!logins.Contains(login))
            {
                logins.Add(login);
            }
        }

        if (logins.Count == 0)
        {
            throw new UserTextException(new UserText("reviewers.invalid"), "No reviewers were given.");
        }

        return _service.RequestReviewersAsync(_project, _pullRequest, logins, ct);
    }

    public override string ToString() => Heading;
}

/// <summary>
/// «Fusionar…» (docs/21 section 7.2 and D-20): the mode (only those the repository and the branch admit), the version that is merged, the protections, the checks and the
/// reviews as they stand, and the fixed notice that Windows Hello is asked every time.
/// </summary>
public sealed partial class MergeCardViewModel : HostingCardViewModel
{
    private readonly ProjectContext _project;
    private readonly PullRequestRef _pullRequest;
    private readonly IPublicationService _service;

    public MergeCardViewModel(
        ProjectContext project,
        PullRequestRef pullRequest,
        string baseBranch,
        IReadOnlyList<MergeMethod> methods,
        IEnumerable<CardLine> lines,
        IPublicationService service,
        Action close,
        Action<string> sent)
        : base(Texts.Format("Hosting_Merge_Heading", pullRequest.Number), Texts.Get("Hosting_Merge_Confirm"), close, sent)
    {
        _project = project;
        _pullRequest = pullRequest;
        _service = service;
        HelloNotice = Texts.Format("Hosting_Merge_Hello", baseBranch);
        Methods = [.. methods.Select(m => new MethodOption(m, HostingTexts.Method(m)))];
        _selectedMethod = Methods.Count == 1 ? Methods[0] : null;
        foreach (var line in lines)
        {
            Lines.Add(line);
        }
    }

    public IReadOnlyList<MethodOption> Methods { get; }

    [ObservableProperty]
    private MethodOption? _selectedMethod;

    partial void OnSelectedMethodChanged(MethodOption? value) => RaiseCanConfirm();

    protected override bool CanConfirm => SelectedMethod is not null;

    protected override Task<RemoteWorkTicket> RunAsync(CancellationToken ct) =>
        _service.MergeAsync(_project, _pullRequest, SelectedMethod!.Method, ct);

    public override string ToString() => Heading;
}

/// <summary>
/// «Publicar y abrir PR» (docs/21 section 7.2 and D-17, D-18): where it goes, what goes up, the protections of the base branch, the text (editable), whether it opens as a draft and what
/// covers it. It is built from <see cref="PublicationPreview"/>: when the service says the run cannot be published, the card says why and has no button.
/// </summary>
public sealed partial class PublishCardViewModel : HostingCardViewModel
{
    public const int MaxTitleLength = 256;
    public const int MaxBodyLength = 60000;

    private readonly ProjectContext _project;
    private readonly RunId _run;
    private readonly PublicationPreview _preview;
    private readonly IPublicationService _service;

    public PublishCardViewModel(
        ProjectContext project,
        RunId run,
        PublicationPreview preview,
        string coverageText,
        IPublicationService service,
        Action close,
        Action<string> sent)
        : base(
            preview.Existing is { } existing ? Texts.Format("Hosting_Update_Heading", existing.Number) : Texts.Get("Hosting_Publish_Button"),
            Texts.Get(preview.Existing is null ? "Hosting_Publish_Confirm" : "Hosting_Update_Confirm"),
            close,
            sent)
    {
        _project = project;
        _run = run;
        _preview = preview;
        _service = service;
        CoverageText = coverageText;
        _title = preview.ProposedText.Title;
        _body = preview.ProposedText.Body;
        CanOpenAsDraft = preview.Capabilities.Has(HostingCapability.DraftPullRequest);
        ShowsConfirm = Offered;
        BuildLines();
    }

    /// <summary>What covers the publication: «Con tu permiso del 12/11» or «Te pedirá confirmación con Windows Hello».</summary>
    public string CoverageText { get; }

    public bool CanOpenAsDraft { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _body;

    [ObservableProperty]
    private bool _openAsDraft;

    public override bool ShowsConfirm { get; }

    private bool Offered =>
        _preview.Ready && _preview.Repository is not null && _preview.BaseBranch is not null
        && _preview.Capabilities.Has(_preview.Existing is null ? HostingCapability.OpenPullRequest : HostingCapability.UpdatePullRequest);

    protected override bool CanConfirm => Offered && !string.IsNullOrWhiteSpace(Title);

    partial void OnTitleChanged(string value) => RaiseCanConfirm();

    private void BuildLines()
    {
        var preview = _preview;
        if (!preview.Ready)
        {
            var busy = preview.NotReadyReason is { Code: "project.busy-remote" };
            Lines.Add(new CardLine(busy ? Texts.Get("Hosting_Publish_Busy") : Texts.Format("Hosting_Publish_NotReady", HostingTexts.Say(preview.NotReadyReason)), true));
        }

        if (preview.Repository is { } repository && preview.BaseBranch is { } baseBranch)
        {
            Lines.Add(new CardLine(Texts.Format("Hosting_Publish_Target", $"{repository.Owner}/{repository.Name}", RemoteBranch.ForRun(_run).Name, baseBranch.Name)));
        }

        if (preview.Commit is { } commit)
        {
            Lines.Add(new CardLine(Texts.Format("Hosting_Publish_Commit", HostingTexts.Short(commit))));
        }

        if (preview.Ready)
        {
            Lines.Add(new CardLine(preview.OwnUnpublishedCommits is { } own
                ? Texts.Format("Hosting_Publish_Uploads", preview.SystemCommits + own, preview.SystemCommits, own)
                : Texts.Format("Hosting_Publish_UploadsUnknown", preview.SystemCommits)));
        }

        if (preview.Existing is { } existing)
        {
            Lines.Add(new CardLine(Texts.Format("Hosting_Publish_Existing", existing.Number)));
            Lines.Add(new CardLine(Texts.Get("Hosting_Update_Replaces")));
        }

        if (preview.Rules is { } rules && preview.BaseBranch is { } protectedBranch)
        {
            foreach (var line in RulesLines(rules, protectedBranch.Name))
            {
                Lines.Add(line);
            }
        }

        if (preview.Inspection is { } inspection)
        {
            foreach (var refusal in inspection.Refusals)
            {
                Lines.Add(new CardLine(Texts.Format("Hosting_Config_Refused", HostingTexts.Data(refusal.Key) ?? "?", HostingTexts.Data(refusal.OriginFile) ?? "?"), true));
            }

            if (inspection.UsesLfs)
            {
                Lines.Add(new CardLine(Texts.Get("Hosting_Lfs_Refused"), true));
            }

            if (inspection.FailureCode is { } failure)
            {
                Lines.Add(new CardLine(HostingTexts.Code(failure), true));
            }
        }

        if (!preview.Capabilities.Has(preview.Existing is null ? HostingCapability.OpenPullRequest : HostingCapability.UpdatePullRequest))
        {
            Lines.Add(new CardLine(Texts.Get(preview.Existing is null ? "Hosting_Publish_NoOpen" : "Hosting_Update_NoUpdate"), true));
        }

        if (preview.Capabilities.UntestedCliVersion)
        {
            Lines.Add(new CardLine(Texts.Get("Hosting_Cli_Untested"), true));
        }

        Lines.Add(new CardLine(Texts.Get("Hosting_Verdict_NotApproval")));
    }

    /// <summary>The protections of a branch as sentences: «No se pudieron leer…» when the platform did not show them, never «sin protecciones».</summary>
    internal static IEnumerable<CardLine> RulesLines(BranchRules rules, string branch)
    {
        if (!rules.Readable)
        {
            yield return new CardLine(Texts.Format("Hosting_Rules_Unreadable", branch), true);
            yield break;
        }

        if (rules.Rules.Count == 0)
        {
            yield return new CardLine(Texts.Format("Hosting_Rules_None", branch));
            yield break;
        }

        foreach (var rule in rules.Rules)
        {
            var counted = "Hosting_Rule_" + rule.Kind + "_Count";
            yield return new CardLine(rule.Count is { } count && Texts.Current.Has(counted)
                ? Texts.Format(counted, count)
                : Texts.Get("Hosting_Rule_" + rule.Kind));
        }
    }

    protected override Task<RemoteWorkTicket> RunAsync(CancellationToken ct)
    {
        var title = Title.Trim();
        if (title.Length == 0 || title.Length > MaxTitleLength)
        {
            throw new UserTextException(new UserText("text.title-invalid"), "The title is empty or too long.");
        }

        if (Body.Length > MaxBodyLength)
        {
            throw new UserTextException(new UserText("text.body-invalid"), "The body is too long.");
        }

        var request = new PublicationRequest(_project, _run, _preview.BaseBranch!, new PullRequestText(title, Body), OpenAsDraft && CanOpenAsDraft);
        return _service.PublishAsync(request, ct);
    }

    public override string ToString() => Heading;
}
