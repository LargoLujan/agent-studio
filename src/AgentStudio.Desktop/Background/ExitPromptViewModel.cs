using AgentStudio.Desktop.Localization;
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

    public string Title => Texts.Get("Background_Exit_Title");

    /// <summary>The question: how many pieces of work go on, and whether the window is being closed or the app left.</summary>
    public string Intro => WorkTexts.Count(Work.Count, OffersKeepInTray ? "Background_Exit_IntroWindow" : "Background_Exit_IntroExit");

    public string KeepInTrayText => Texts.Get("Background_Exit_KeepInTray");

    public string KeepInTrayHelp => Texts.Get("Background_Exit_KeepInTray_Help");

    public string PauseText => Texts.Get("Background_Exit_Pause");

    public string PauseHelp => Texts.Get("Background_Exit_Pause_Help")
        + (Question.HasWorkThatCannotPause ? " " + Texts.Get("Background_Exit_Pause_HelpCannotPause") : string.Empty);

    public string ExitNowText => Texts.Get("Background_Exit_Now");

    public string ExitNowHelp => Texts.Get("Background_Exit_Now_Help");

    public string CancelText => Texts.Get("Background_Exit_Cancel");

    /// <summary>Where the answer goes.</summary>
    public string RecordNote => Texts.Get("Background_Exit_RecordNote");

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
