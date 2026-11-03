namespace AgentStudio.Desktop.ViewModels;

/// <summary>
/// A change of the diff view that a <see cref="FileDocumentViewModel"/> asks its view to make. The text of the editor
/// lives inside the web page, so only the view (through the editor control) can build the comparison; the view model
/// just says what to compare. The "original" side is the one shown in red (on the left when the editor is wide enough
/// for two panes), the "modified" side the one in green.
/// </summary>
public abstract record DiffRequest;

/// <summary>Show the text saved on disk (original) next to what the editor holds now (modified).</summary>
public sealed record CompareWithSavedRequest : DiffRequest;

/// <summary>Show <paramref name="OriginalText"/> (original) next to what the editor holds now (modified).</summary>
public sealed record CompareWithTextRequest(string OriginalText) : DiffRequest;

/// <summary>Leave the comparison and show the editor again.</summary>
public sealed record HideDiffRequest : DiffRequest;
