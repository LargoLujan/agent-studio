using AgentStudio.Application.Security;
using AgentStudio.Desktop.Lock;
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

    /// <param name="request">What is asked.</param>
    /// <param name="confirmation">
    /// How a yes is confirmed: by the dialog alone (<see cref="ApprovalVerification.Dialog"/>), by Windows Hello after
    /// «Aprobar» (<see cref="ApprovalVerification.WindowsHello"/>), or by the dialog alone because Windows Hello is not there
    /// (<see cref="ApprovalVerification.WindowsHelloUnavailable"/>, with <paramref name="availability"/> saying why).
    /// </param>
    /// <param name="availability">What Windows said of Windows Hello, when it was asked.</param>
    public ApprovalDialogViewModel(ApprovalRequest request, ApprovalVerification confirmation = ApprovalVerification.Dialog, UserPresenceAvailability? availability = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        Confirmation = confirmation;
        _availability = availability;
    }

    public ApprovalRequest Request { get; }

    /// <summary>How a yes to this question is confirmed (docs/16 D-13).</summary>
    public ApprovalVerification Confirmation { get; }

    /// <summary>What happens after «Aprobar», or why the dialog decides alone; null when Windows Hello plays no part.</summary>
    public string? HelloNotice => Confirmation switch
    {
        ApprovalVerification.WindowsHello => "Al aprobar, Windows Hello te pedirá que confirmes que eres tú. Sin esa confirmación no se aprueba nada.",
        ApprovalVerification.WindowsHelloUnavailable =>
            $"Windows Hello no está disponible en este equipo ({HelloAnswers.WhyAbsent(_availability ?? UserPresenceAvailability.NotConfiguredForUser)}): "
            + "esta aprobación la decide solo este diálogo, y el registro lo dice.",
        _ => null,
    };

    public bool HasHelloNotice => HelloNotice is not null;

    /// <summary>A warning when Windows Hello is not there; neutral when it is asked.</summary>
    public Tone HelloNoticeTone => Confirmation == ApprovalVerification.WindowsHelloUnavailable ? Tone.Warning : Tone.Neutral;

    public string Action => Request.Action;

    public string Description => Request.Description;

    public string RiskLabel => Request.Risk switch
    {
        RiskLevel.Critical => "Riesgo crítico",
        RiskLevel.Destructive => "Acción destructiva",
        _ => "Riesgo bajo",
    };

    public Tone RiskTone => Request.Risk switch
    {
        RiskLevel.Critical => Tone.Error,
        RiskLevel.Destructive => Tone.Warning,
        _ => Tone.Neutral,
    };

    public IReadOnlyList<string> AffectedPaths => Request.AffectedPaths;

    public bool HasPaths => AffectedPaths.Count > 0;

    public string PathsHeader => AffectedPaths.Count == 1 ? "Ruta afectada" : $"Rutas afectadas ({AffectedPaths.Count})";

    public string? RequestedBy => Request.RequestedBy is { } agent ? $"Lo pide el agente «{agent}»." : null;

    /// <summary>Project, run and task the action belongs to, when the request says.</summary>
    public string? Origin
    {
        get
        {
            var parts = new List<string>();
            if (Request.RunId is { } run)
            {
                parts.Add($"ejecución {run}");
            }

            if (Request.TaskId is { } task)
            {
                parts.Add($"tarea {task}");
            }

            if (Request.ProjectId is { } project)
            {
                parts.Add($"proyecto {project}");
            }

            return parts.Count == 0 ? null : "Origen: " + string.Join(" · ", parts);
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
