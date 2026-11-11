using AgentStudio.Application.Security;

namespace AgentStudio.Memory.Documents;

/// <summary>
/// What every string goes through before it enters the model of a document for clients (docs/17 D-58, D-60): hidden characters out,
/// secrets redacted (<see cref="ISecretRedactor"/>) and the paths of the machine taken out (<see cref="LocalPaths"/>), in that order,
/// so the rules of the model (<see cref="AgentStudio.Application.Documents.ClientDocumentRules"/>) then find nothing. It counts what
/// it took out: that is the list of what goes out. One cleaner per build.
/// </summary>
internal sealed class DocumentTextCleaner(ISecretRedactor redactor)
{
    /// <summary>What replaces a path of the machine.</summary>
    public const string PathPlaceholder = "[…]";

    private readonly List<string> _paths = [];

    /// <summary>How many strings had a secret the redactor took out.</summary>
    public int SecretsRedacted { get; private set; }

    /// <summary>The paths taken out, in order, as the person will be shown them (what the redactor left of them).</summary>
    public IReadOnlyList<string> PathsRemoved => _paths;

    /// <summary>The clean text, trimmed; one line unless <paramref name="multiline"/>. Null stays empty.</summary>
    public string Clean(string? text, bool multiline = false)
    {
        var plain = PlainText.Strip(text, keepLineBreaks: multiline).Trim();
        if (plain.Length == 0)
        {
            return string.Empty;
        }

        var redacted = redactor.Redact(plain);
        if (!string.Equals(redacted, plain, StringComparison.Ordinal))
        {
            SecretsRedacted++;
        }

        _paths.AddRange(LocalPaths.Find(redacted));
        return LocalPaths.Remove(redacted, PathPlaceholder).Text.Trim();
    }
}
