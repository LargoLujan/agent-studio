namespace AgentStudio.Application.Localization;

/// <summary>
/// Paints a list of sentences (the layer keeps them for logs and dossiers) from the coded texts that go with it, one for one
/// (docs/17 D-47, <see cref="CodedMessages"/>): the person reads the language in use. A producer that gave no codes, or a list
/// that does not match, is shown as its sentences.
/// </summary>
public static class CodedList
{
    public static IReadOnlyList<string> Paint(IReadOnlyList<string> messages, IReadOnlyList<UserText>? texts, Func<UserText, string> render)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(render);
        return texts is not null && texts.Count == messages.Count ? [.. texts.Select(render)] : messages;
    }
}
