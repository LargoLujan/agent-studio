using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Desktop.Localization;
using AgentStudio.Memory.Consistency;

namespace AgentStudio.Desktop.Docs;

/// <summary>What «Copiar» asks of the desktop: put a text on the clipboard. Nothing else.</summary>
public interface IDocsClipboard
{
    void Copy(string text);
}

/// <summary>The clipboard of Windows. A clipboard held by another program leaves the person to select the text and copy it by hand.</summary>
public sealed class SystemDocsClipboard : IDocsClipboard
{
    public void Copy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Held by another program: the text is on the screen, selectable.
        }
    }
}

/// <summary>
/// What the sections «Diagramas» and «Discrepancias» read, all optional (docs/20 D-25 to D-30): without the catalog they say they are not available, and without the
/// analysis there is no result to show. Nothing here starts an analysis by itself: only the buttons of the person do.
/// </summary>
/// <param name="Diagrams">The cache of the diagrams, their states, regenerating, undoing and exporting.</param>
/// <param name="Reader">The text of a diagram in the cache.</param>
/// <param name="Analysis">The analysis of a commit: the discrepancies are in its report. It is asked for what is saved; it is only calculated when the person presses «Calcular».</param>
/// <param name="Git">The history of the repository, to know the version the project is at.</param>
/// <param name="Clipboard">«Copiar».</param>
/// <param name="Live">The discrepancies that come from the cache and the log (the diagrams out of date, the requirements with no task), worked out again each time: the saved ones go stale.</param>
public sealed record DocsBackend(
    IDiagramCatalog? Diagrams = null,
    IDiagramTextReader? Reader = null,
    ICommitAnalysisService? Analysis = null,
    IRepositoryHistoryReader? Git = null,
    IDocsClipboard? Clipboard = null,
    LiveConsistency? Live = null)
{
    /// <summary>Nothing composed.</summary>
    public static DocsBackend None { get; } = new();
}

/// <summary>The words of the sections «Diagramas» and «Discrepancias», from the codes and the states of the layers below (docs/20 D-26 and D-30, ADR-0054).</summary>
public static class DocsTexts
{
    /// <summary>The name of a diagram for the person: the kind and, for the ones of a project or a run, which one.</summary>
    public static string Name(IUiTextProvider texts, DiagramRef diagram)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(diagram);
        return diagram.Kind == DiagramKind.Solution
            ? texts.Get("Docs_Kind_Solution")
            : texts.Format("Docs_Kind_" + diagram.Kind, diagram.Subject);
    }

    /// <summary>The state of a diagram; one that is out of date says which saved version it describes.</summary>
    public static string State(IUiTextProvider texts, DiagramStatus status)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(status);
        return status.State == DiagramState.Stale && status.Describes is { Length: >= 7 } commit
            ? texts.Format("Docs_State_StaleVersion", commit[..7])
            : texts.Get("Docs_State_" + status.State);
    }

    /// <summary>The state of the copy exported to the repository.</summary>
    public static string Export(IUiTextProvider texts, DiagramExportState state)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return texts.Get("Docs_Export_" + state);
    }

    /// <summary>
    /// Why something failed, in words of the resources by kind of failure: never the message of the exception, which is English technical detail and can carry a path of this
    /// computer (ADR-0054). Anything that is not one of the known kinds says the model is not available.
    /// </summary>
    public static string Failure(IUiTextProvider texts, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(exception);
        return texts.Get(exception switch
        {
            DiagramExportBusyException => "Docs_Failure_Busy",
            DecisionLogIntegrityException => "Docs_Failure_Log",
            DiagramIndexUnreadableException => "Docs_Failure_Index",
            UnauthorizedAccessException => "Docs_Failure_Access",
            IOException => "Docs_Failure_Io",
            _ => "Docs_Failure_Model",
        });
    }

    /// <summary>The kind of diagram a finding of <c>doc.diagram-stale</c> names by its fixed word, or null.</summary>
    public static DiagramKind? KindOfWord(string word) => DiagramStaleCheck.KindOf(word);
}
