namespace AgentStudio.Application.Localization;

/// <summary>
/// A list of messages (the sentences the layer keeps for logs, dossiers and anchors) and, in step with each one, the coded text the
/// desktop paints (docs/17 D-47). A producer adds both at once, so the two lists never drift apart.
/// </summary>
public sealed class CodedMessages
{
    private readonly List<string> _messages = [];
    private readonly List<UserText> _texts = [];

    public int Count => _messages.Count;

    /// <summary>The sentences, as the layer wrote them.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>The coded texts, one per sentence and in the same order.</summary>
    public IReadOnlyList<UserText> Texts => _texts;

    public void Add(string message, UserText text)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentNullException.ThrowIfNull(text);
        _messages.Add(message);
        _texts.Add(text);
    }
}
