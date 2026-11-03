using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// The question about unsaved edits (see <see cref="WpfUnsavedChangesPrompt"/>): which documents, and whether they are saved,
/// thrown away or kept as they are. When one of them cannot be saved from here, only «Descartar» and «Volver» are offered.
/// Closing it any other way than with its buttons is <see cref="UnsavedChangesChoice.Cancel"/>.
/// </summary>
public sealed partial class UnsavedChangesPromptViewModel : ObservableObject
{
    /// <summary>How many titles are listed; the rest are counted.</summary>
    public const int MaxListed = 10;

    public UnsavedChangesPromptViewModel(IReadOnlyList<string> titles, bool canSave)
    {
        ArgumentNullException.ThrowIfNull(titles);
        CanSave = canSave;
        var listed = titles.Take(MaxListed).Select(t => "• " + t).ToList();
        if (titles.Count > MaxListed)
        {
            listed.Add($"… y {titles.Count - MaxListed} más");
        }

        Listed = listed;
    }

    /// <summary>Whether every document can be saved from here; otherwise «Guardar» is not offered.</summary>
    public bool CanSave { get; }

    /// <summary>The titles shown, at most <see cref="MaxListed"/>, and how many more there are.</summary>
    public IReadOnlyList<string> Listed { get; }

    public string Title => "Cambios sin guardar";

    public string Intro => CanSave ? "Hay cambios sin guardar en:" : "Hay cambios sin guardar que no se pueden guardar desde aquí:";

    public string Question => CanSave ? "¿Quieres guardarlos?" : "¿Descartarlos?";

    public string SaveText => "Guardar";

    public string DiscardText => CanSave ? "No guardar" : "Descartar";

    public string CancelText => CanSave ? "Cancelar" : "Volver";

    /// <summary>What was answered; null while nothing was, which counts as <see cref="UnsavedChangesChoice.Cancel"/>.</summary>
    public UnsavedChangesChoice? Answer { get; private set; }

    /// <summary>Raised once an answer was chosen, for the window to close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Choose(UnsavedChangesChoice choice)
    {
        if (choice == UnsavedChangesChoice.Save && !CanSave)
        {
            return;
        }

        Answer = choice;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
