using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Background;

/// <summary>
/// The question of the managed exit (docs/16 D-14 and D-16, F8-9): what is going on, and what each answer does, before it
/// is chosen. «Seguir en la bandeja» only when the window is being closed; «Cancelar» is the answer of closing it any
/// other way. Each answer says whether the work resumes by itself afterwards: none of the person's does.
/// </summary>
public sealed partial class ExitPromptViewModel : ObservableObject
{
    public ExitPromptViewModel(ExitQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        Question = question;
    }

    public ExitQuestion Question { get; }

    public IReadOnlyList<ExitWorkLine> Work => Question.Work;

    public bool OffersKeepInTray => Question.OffersKeepInTray;

    public string Title => "Hay trabajo en marcha";

    public string Intro => OffersKeepInTray
        ? $"Vas a cerrar la ventana con {WorkTexts.Count(Work.Count, "trabajo en marcha", "trabajos en marcha")}. ¿Qué hago con ello?"
        : $"Vas a salir de Agent Studio con {WorkTexts.Count(Work.Count, "trabajo en marcha", "trabajos en marcha")}. ¿Qué hago con ello?";

    public string KeepInTrayText => "Seguir en la bandeja";

    public string KeepInTrayHelp => "La ventana se cierra y todo sigue en marcha. Agent Studio queda en la bandeja del sistema, junto al reloj.";

    public string PauseText => "Pausar y salir cuando terminen los intentos en curso";

    public string PauseHelp => "No se lanza nada nuevo. Agent Studio espera en la bandeja a que terminen los intentos que están en marcha y sale. Las ejecuciones quedan en pausa y no se reanudan solas: las reanudas tú."
        + (Question.HasWorkThatCannotPause ? " Los debates y las auditorías no se pueden pausar: espera a que terminen." : string.Empty);

    public string ExitNowText => "Salir ahora y dejarlas interrumpidas";

    public string ExitNowHelp => "Los agentes se detienen ahora y lo que estaba en marcha queda interrumpido. No se reanuda solo: lo reanudas tú desde el orquestador, sin repetir lo terminado.";

    public string CancelText => "Cancelar";

    /// <summary>Where the answer goes.</summary>
    public string RecordNote => "Tu elección queda en el registro de cada ejecución afectada. «Cancelar» no cambia nada ni escribe nada.";

    /// <summary>What was answered; null while nothing was, which counts as <see cref="ExitChoice.Cancel"/>.</summary>
    public ExitChoice? Answer { get; private set; }

    /// <summary>Raised once an answer was chosen, for the window to close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Choose(ExitChoice choice)
    {
        if (choice == ExitChoice.KeepInTray && !OffersKeepInTray)
        {
            return;
        }

        Answer = choice;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
