namespace AgentStudio.Application.Security;

/// <summary>
/// Removes secrets and personal data from free text before it is persisted or shown outside the process (log files,
/// error reports, journals). Implementations are pure and thread-safe, and they fail closed: if the text cannot be
/// cleaned, the original must never be returned.
/// </summary>
public interface ISecretRedactor
{
    /// <summary>
    /// Returns <paramref name="text"/> with every secret replaced by a placeholder and the user profile path collapsed
    /// to <c>~</c>. The result is idempotent: redacting an already redacted text changes nothing.
    /// </summary>
    /// <param name="text">The text to clean; never <see langword="null"/>.</param>
    string Redact(string text);
}
