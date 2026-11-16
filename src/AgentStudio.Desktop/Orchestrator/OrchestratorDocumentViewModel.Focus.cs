using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Orchestrator;

/// <summary>Where the keyboard goes when another screen brings the person to the orchestrator (docs/24 §2, principle 4).</summary>
public enum OrchestratorFocus
{
    /// <summary>The box of the request, in «Preparación».</summary>
    Request,

    /// <summary>The section «Requisitos», in «Preparación».</summary>
    Requirements,
}

// «Ningún aviso sin camino» (docs/24 §2): the screens that send the person here (Inicio, Auditar, the status band) open the band
// «Preparación», which folds by itself once it is complete, and leave the keyboard where the person has to write.
public sealed partial class OrchestratorDocumentViewModel
{
    /// <summary>
    /// The part of the tab the keyboard should go to, until the view takes it (<see cref="TakePendingFocus"/>). The dock creates the
    /// view only when the tab is shown, and again when a layout is restored, so the request waits here for it.
    /// </summary>
    public OrchestratorFocus? PendingFocus { get; private set; }

    /// <summary>Raised when <see cref="PendingFocus"/> is set: a view already on screen takes it at once.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>
    /// Opens «Preparación» and puts the keyboard in the box of the request. «Inicio» uses it for «Escribir tu primera petición»;
    /// whatever else brings the person here to write (the configurator after «Aplicar») can use it too.
    /// </summary>
    public void FocusRequestBox() => RequestFocus(OrchestratorFocus.Request);

    /// <summary>«Ir a los requisitos»: opens «Preparación» and brings «Requisitos» into view, with the keyboard in it.</summary>
    [RelayCommand]
    public void ShowRequirements() => RequestFocus(OrchestratorFocus.Requirements);

    /// <summary>The part the keyboard should go to, once: the view that takes it clears it.</summary>
    public OrchestratorFocus? TakePendingFocus()
    {
        var pending = PendingFocus;
        PendingFocus = null;
        return pending;
    }

    /// <summary>
    /// The status band says the project has no frozen requirements: «Ir a los requisitos» is offered beside it, so the line that
    /// names the section is also the way to it.
    /// </summary>
    public bool ShowsGoToRequirements => Context is { Project: not null, Requirements: null };

    private void RequestFocus(OrchestratorFocus target)
    {
        IsPreparationExpanded = true;
        PendingFocus = target;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }
}
