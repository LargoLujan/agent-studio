using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Desktop.Orchestrator.Session;

/// <summary>
/// What the orchestrator screen says, in Spanish, about a decision log that cannot be trusted (ADR-0010): why, and, for a
/// history no anchor recognises at the project's location, the question «Aceptar este historial como nuevo origen».
/// </summary>
public static class HistoryText
{
    /// <summary>The problem of <paramref name="log"/>, which does not verify, as the screen shows it.</summary>
    public static string Problem(DecisionLogState log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Unanchored switch
        {
            { PreviousProjectId: { } previous } history =>
                Texts.Format("Orchestrator_History_Replaced", previous, Entries(history.PreviousEntryCount ?? 0)),
            { } history =>
                Texts.Format("Orchestrator_History_NoAnchor", Entries(history.EntryCount)),
            _ => Texts.Format("Orchestrator_History_NotIntact", log.Verification.Problem),
        };
    }

    /// <summary>What the approval dialog asks before <paramref name="history"/> of <paramref name="project"/> is accepted.</summary>
    public static string Question(ProjectContext project, UnanchoredHistory history)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        return history.PreviousProjectId is { } previous
            ? $"Empezar un historial de decisiones nuevo en «{project.RootPath}». El ancla de esta ubicación era del proyecto {previous}, con {LoggedEntries(history.PreviousEntryCount ?? 0)}, y el registro de ahora está vacío: aquel historial ya no está aquí. "
              + "La aceptación queda en el registro, con lo que había."
            : $"Aceptar como nuevo origen el historial de decisiones de «{project.RootPath}»: {LoggedEntries(history.EntryCount)}, cabeza {Short(history.HeadHash)}. "
              + "No hay ancla de este historial para esta ubicación: puede que movieras o copiaras la carpeta, o que el registro venga de otra máquina o con el repositorio. "
              + "Acéptalo solo si lo reconoces: lo que dice pasará a ser la base de las revisiones y de las reanudaciones, y la aceptación queda en el registro.";
    }

    private static string Entries(long count) => count == 1 ? Texts.Get("Orchestrator_History_EntryOne") : Texts.Format("Orchestrator_History_Entries", count.ToString(CultureInfo.InvariantCulture));

    /// <summary>The count of entries as the question writes it: the question is the description of an approval, which goes to the decision log and stays as it was written (D-48).</summary>
    private static string LoggedEntries(long count) => count == 1 ? "1 entrada" : count.ToString(CultureInfo.InvariantCulture) + " entradas";

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
