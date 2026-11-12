using AgentStudio.Application.Security;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Lock;
using AgentStudio.Domain.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator.Approvals;

/// <summary>The person's answer. A dialog closed without one is not an answer: the prompt treats it as a denial.</summary>
public sealed record ApprovalAnswer(bool Approved, string? Comment);

/// <summary>
/// The question of an approval dialog: what is asked, how risky it is and what it touches. Approving a critical action
/// also needs an explicit confirmation, so a stray Enter or click cannot approve it; and, with Windows Hello on, the
/// question says that Hello is asked after «Aprobar», or warns that it is not there and the dialog decides alone (docs/16
/// D-13, F8-11).
/// </summary>
public sealed partial class ApprovalDialogViewModel : ObservableObject
{
    private readonly UserPresenceAvailability? _availability;
    private readonly string? _projectName;

    /// <param name="request">What is asked.</param>
    /// <param name="confirmation">
    /// How a yes is confirmed: by the dialog alone (<see cref="ApprovalVerification.Dialog"/>), by Windows Hello after
    /// «Aprobar» (<see cref="ApprovalVerification.WindowsHello"/>), or by the dialog alone because Windows Hello is not there
    /// (<see cref="ApprovalVerification.WindowsHelloUnavailable"/>, with <paramref name="availability"/> saying why).
    /// </param>
    /// <param name="availability">What Windows said of Windows Hello, when it was asked.</param>
    /// <param name="projectName">
    /// The name the screens give the project of the request (its folder); null when it is not known, and then the question
    /// names no project: its id means nothing to a person.
    /// </param>
    /// <param name="requirement">
    /// What the action asks (docs/17 D-37): with <see cref="PresenceRequirement.Always"/> the question says that Windows Hello
    /// is asked whatever the setting says, or, on a computer without it (<see cref="ApprovalVerification.WindowsLock"/>), that
    /// Windows locks after «Aprobar».
    /// </param>
    public ApprovalDialogViewModel(
        ApprovalRequest request,
        ApprovalVerification confirmation = ApprovalVerification.Dialog,
        UserPresenceAvailability? availability = null,
        string? projectName = null,
        PresenceRequirement requirement = PresenceRequirement.Configurable)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        Confirmation = confirmation;
        Requirement = requirement;
        _availability = availability;
        _projectName = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim();
    }

    public ApprovalRequest Request { get; }

    /// <summary>How a yes to this question is confirmed (docs/16 D-13, docs/17 D-40 (a)).</summary>
    public ApprovalVerification Confirmation { get; }

    /// <summary>What the action asks of the person's presence (docs/17 D-37).</summary>
    public PresenceRequirement Requirement { get; }

    /// <summary>What happens after «Aprobar», or why the dialog decides alone; null when Windows Hello plays no part.</summary>
    public string? HelloNotice => Confirmation switch
    {
        ApprovalVerification.WindowsHello when Requirement == PresenceRequirement.Always => HelloTexts.AlwaysAsksHello,
        ApprovalVerification.WindowsHello => Texts.Get("Orchestrator_Approval_HelloNotice"),
        ApprovalVerification.WindowsLock => HelloTexts.LocksWindowsAfterYes,
        ApprovalVerification.WindowsHelloUnavailable =>
            Texts.Format("Orchestrator_Approval_HelloUnavailable", HelloAnswers.WhyAbsent(_availability ?? UserPresenceAvailability.NotConfiguredForUser)),
        _ => null,
    };

    public bool HasHelloNotice => HelloNotice is not null;

    /// <summary>A warning when Windows Hello is not there (the dialog decides alone, or Windows locks); neutral when it is asked.</summary>
    public Tone HelloNoticeTone => Confirmation is ApprovalVerification.WindowsHelloUnavailable or ApprovalVerification.WindowsLock ? Tone.Warning : Tone.Neutral;

    /// <summary>The identifier of the action, as the log records it.</summary>
    public string Action => Request.Action;

    /// <summary>What is asked, as a plain sentence (<see cref="ApprovalTitles"/>): the title of the question, never the identifier.</summary>
    public string Title => ApprovalTitles.For(Request.Action);

    /// <summary>The accessible name of the title: «Acción: …», so a screen reader says what the sentence is.</summary>
    public string AccessibleTitle => Texts.Format("Orchestrator_Approval_ActionName", Title);

    /// <summary>
    /// The identifier the log records the action with, as a technical detail under the question; null when the action is
    /// already a sentence (then the title is that sentence).
    /// </summary>
    public string? ActionDetail => ApprovalTitles.IsIdentifier(Request.Action) ? Texts.Format("Orchestrator_Approval_ActionDetail", Request.Action.Trim()) : null;

    public string Description => Request.Description;

    public string RiskLabel => Request.Risk switch
    {
        RiskLevel.Critical => Texts.Get("Orchestrator_Approval_RiskCritical"),
        RiskLevel.Destructive => Texts.Get("Orchestrator_Approval_RiskDestructive"),
        _ => Texts.Get("Orchestrator_Approval_RiskLow"),
    };

    public Tone RiskTone => Request.Risk switch
    {
        RiskLevel.Critical => Tone.Error,
        RiskLevel.Destructive => Tone.Warning,
        _ => Tone.Neutral,
    };

    public IReadOnlyList<string> AffectedPaths => Request.AffectedPaths;

    public bool HasPaths => AffectedPaths.Count > 0;

    public string PathsHeader => AffectedPaths.Count == 1 ? Texts.Get("Orchestrator_Approval_PathOne") : Texts.Format("Orchestrator_Approval_PathMany", AffectedPaths.Count);

    public string? RequestedBy => Request.RequestedBy is { } agent ? Texts.Format("Orchestrator_Approval_RequestedBy", agent) : null;

    /// <summary>
    /// Project, run and task the action belongs to, when the request says. The project goes by its name, and is left out
    /// when its name is not known: the id of a project is never shown.
    /// </summary>
    public string? Origin
    {
        get
        {
            var parts = new List<string>();
            if (Request.ProjectId is not null && _projectName is { } project)
            {
                parts.Add(Texts.Format("Orchestrator_Approval_OriginProject", project));
            }

            if (Request.RunId is { } run)
            {
                parts.Add(Texts.Format("Orchestrator_Approval_OriginRun", run));
            }

            if (Request.TaskId is { } task)
            {
                parts.Add(Texts.Format("Orchestrator_Approval_OriginTask", task));
            }

            return parts.Count == 0 ? null : Texts.Format("Orchestrator_Approval_Origin", string.Join(" · ", parts));
        }
    }

    public bool RequiresConfirmation => Request.Risk == RiskLevel.Critical;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    private bool _isConfirmed;

    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>The answer given with the buttons; null while there is none (closing the window gives none).</summary>
    public ApprovalAnswer? Answer { get; private set; }

    /// <summary>Raised when a button answered; the window closes itself.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve() => Respond(new ApprovalAnswer(true, NullIfEmpty(Comment)));

    private bool CanApprove() => !RequiresConfirmation || IsConfirmed;

    [RelayCommand]
    private void Deny() => Respond(new ApprovalAnswer(false, NullIfEmpty(Comment)));

    private void Respond(ApprovalAnswer answer)
    {
        Answer ??= answer;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
