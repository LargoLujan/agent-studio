using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Desktop.Tools;

/// <summary>
/// The tab of «Herramientas» (docs/17 D-46): the <see cref="ToolsViewModel"/> in a document of the dock, one for the session. The shell
/// loads it with the template and the profile of the open project each time it is opened.
/// </summary>
public sealed class ToolsDocumentViewModel : DocumentViewModel
{
    public const string Id = "tools";

    public ToolsDocumentViewModel(ToolsViewModel tools)
        : base(Id, Texts.Get("Tools_Title"))
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools = tools;
    }

    public ToolsViewModel Tools { get; }
}
