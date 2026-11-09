using AgentStudio.Application.Decisions;

namespace AgentStudio.Desktop.Localization;

/// <summary>
/// The names of the parts of the bar of context (docs/17 D-13), from the resources: one per <see cref="ContextPartKind"/>, plus
/// what the CLI reports beyond their sum («Conversación y herramientas»). The tray, the strip, the inspector and the analysis
/// of consumption say them the same way.
/// </summary>
public static class ContextPartTexts
{
    /// <summary>The key of the text of what the CLI reports beyond the parts: the conversation and the tools.</summary>
    public const string ConversationKey = "ContextPart_Conversation";

    /// <summary>The key of the text of a part: <c>ContextPart_</c> and its name.</summary>
    public static string KeyOf(ContextPartKind kind) => "ContextPart_" + kind;

    /// <summary>The name of a part in the language of <paramref name="texts"/>, or of the interface.</summary>
    public static string Label(ContextPartKind kind, IUiTextProvider? texts = null) => (texts ?? Texts.Current).Get(KeyOf(kind));
}
