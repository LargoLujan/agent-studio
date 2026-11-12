namespace AgentStudio.Application.Localization;

/// <summary>
/// A refusal whose reason the person reads (docs/17 D-47, ADR-0054): the message is in English, for logs and tests, and
/// <see cref="Text"/> is the coded text the desktop paints in the language of the person. It is an
/// <see cref="InvalidOperationException"/>, so whoever catches that keeps working.
/// </summary>
public class UserTextException : InvalidOperationException
{
    public UserTextException(UserText text, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    public UserTextException(UserText text, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>The reason, as a code and its arguments.</summary>
    public UserText Text { get; }
}
