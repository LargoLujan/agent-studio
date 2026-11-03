using System.Globalization;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;

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
                $"En esta ubicación vivía el historial del proyecto {previous} ({Entries(history.PreviousEntryCount ?? 0)}), y el registro de decisiones de ahora empieza vacío: aquel historial ya no está aquí. "
                + "No se planifica, ejecuta ni reanuda nada hasta que aceptes empezar uno nuevo.",
            { } history =>
                $"El registro de decisiones tiene {Entries(history.EntryCount)}, pero no hay ancla para esta ubicación: la carpeta se movió o se copió aquí, o el registro viene de otra máquina o con el repositorio. "
                + "No se planifica, ejecuta ni reanuda nada hasta que aceptes este historial como nuevo origen; acéptalo solo si lo reconoces.",
            _ => $"El registro de decisiones no es íntegro: {log.Verification.Problem} No se planifica, ejecuta ni reanuda nada.",
        };
    }

    /// <summary>What the approval dialog asks before <paramref name="history"/> of <paramref name="project"/> is accepted.</summary>
    public static string Question(ProjectContext project, UnanchoredHistory history)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        return history.PreviousProjectId is { } previous
            ? $"Empezar un historial de decisiones nuevo en «{project.RootPath}». El ancla de esta ubicación era del proyecto {previous}, con {Entries(history.PreviousEntryCount ?? 0)}, y el registro de ahora está vacío: aquel historial ya no está aquí. "
              + "La aceptación queda en el registro, con lo que había."
            : $"Aceptar como nuevo origen el historial de decisiones de «{project.RootPath}»: {Entries(history.EntryCount)}, cabeza {Short(history.HeadHash)}. "
              + "No hay ancla de este historial para esta ubicación: puede que movieras o copiaras la carpeta, o que el registro venga de otra máquina o con el repositorio. "
              + "Acéptalo solo si lo reconoces: lo que dice pasará a ser la base de las revisiones y de las reanudaciones, y la aceptación queda en el registro.";
    }

    private static string Entries(long count) => count == 1 ? "1 entrada" : count.ToString(CultureInfo.InvariantCulture) + " entradas";

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
