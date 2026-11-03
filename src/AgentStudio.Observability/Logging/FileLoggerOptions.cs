using Microsoft.Extensions.Logging;

namespace AgentStudio.Observability.Logging;

/// <summary>
/// Settings of the structured file logger (<see cref="FileLoggerProvider"/>). The defaults are the production
/// settings: Information and above, <c>%LOCALAPPDATA%\AgentStudio\logs</c>, 10 MB per file, 14 days of history.
/// The provider reads the values once, when it is created; changing the options afterwards has no effect.
/// </summary>
public sealed class FileLoggerOptions
{
    /// <summary>
    /// Entries of <see cref="SensitiveNameWords"/> by default. A property whose NAME contains one of them holds free
    /// text or a credential, so its string value is never written (see <see cref="SensitiveNameWords"/>).
    /// </summary>
    public static IReadOnlyCollection<string> DefaultSensitiveNameWords { get; } =
    [
        "prompt", "prompts", "instruction", "instructions", "text", "content", "contents", "response", "responses",
        "output", "outputs", "input", "inputs", "completion", "answer", "question", "body", "payload", "transcript",
        "reasoning", "thought", "thoughts", "stdin", "stdout", "stderr", "password", "passwd", "pwd", "secret", "secrets",
        "token", "credential", "credentials", "authorization", "bearer", "apikey", "accesskey", "privatekey",
        "secretkey", "connectionstring",
    ];

    /// <summary>Folder that receives the log files. Defaults to <c>%LOCALAPPDATA%\AgentStudio\logs</c>.</summary>
    public string LogDirectory { get; set; } = DefaultLogDirectory();

    /// <summary>Start of the file names: <c>{prefix}-YYYYMMDD.log</c>, then <c>{prefix}-YYYYMMDD-1.log</c> and so on.</summary>
    public string FileNamePrefix { get; set; } = "agentstudio";

    /// <summary>
    /// Lowest level the provider itself accepts. Information by default. The framework's level filters (for example
    /// <c>Logging:LogLevel</c> or <c>Logging:File:LogLevel</c>) are applied before the provider sees an entry, so this
    /// is a floor on top of them: set it to <see cref="LogLevel.Trace"/> to leave the decision to those filters.
    /// </summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Information;

    /// <summary>Size at which a day file rolls to the next suffix. A single line is never split across files.</summary>
    public long MaxFileSizeBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Files whose day is more than this many days before today are deleted when the logger starts.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Entries waiting for the writer. When the queue is full new entries are dropped and counted.</summary>
    public int QueueCapacity { get; set; } = 4096;

    /// <summary>A text property longer than this many characters is written as length and hash only.</summary>
    public int MaxValueLength { get; set; } = 256;

    /// <summary>A message or exception message longer than this many characters is written as length and hash only.</summary>
    public int MaxMessageLength { get; set; } = 1024;

    /// <summary>Number of stack frames kept in an exception summary.</summary>
    public int MaxStackFrames { get; set; } = 12;

    /// <summary>How long disposing the provider waits for the queue to drain before giving up.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Lower-case words that mark a property as free text or as a credential. The property name is split into words at
    /// case changes, underscores, hyphens and dots (<c>SystemInstructions</c> gives <c>system</c> and
    /// <c>instructions</c>; <c>Context</c> stays one word, so it does NOT contain <c>text</c>) and every run of one to
    /// three consecutive words, joined, is compared with this set (<c>ApiKey</c> matches <c>apikey</c>). A string value
    /// under a matching name is never written: only its length and a short SHA-256 prefix are. Starts as
    /// <see cref="DefaultSensitiveNameWords"/>; add words to extend it.
    /// </summary>
    public ISet<string> SensitiveNameWords { get; } =
        new HashSet<string>(DefaultSensitiveNameWords, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Entries of <see cref="ExemptNameWords"/> by default: words that say the value is something ABOUT a thing and not
    /// the text of it (a path, a type, an id...).
    /// </summary>
    public static IReadOnlyCollection<string> DefaultExemptNameWords { get; } =
    [
        "path", "file", "filename", "directory", "dir", "folder", "root", "type", "kind", "id", "name", "count",
        "length", "size", "code", "uri", "url", "encoding", "format", "version", "status", "mode", "index", "number",
        "label",
    ];

    /// <summary>
    /// Lower-case words that take a name out of <see cref="SensitiveNameWords"/> when the name ENDS in one of them
    /// (trailing numbers are ignored): <c>ContentRoot</c>, <c>ContentType</c> and <c>OutputPath</c> are not text even
    /// though they contain <c>content</c> or <c>output</c>, while <c>PromptPreview</c> and <c>Prompt2</c> still are.
    /// </summary>
    public ISet<string> ExemptNameWords { get; } =
        new HashSet<string>(DefaultExemptNameWords, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a level from configuration text such as <c>Debug</c> or <c>warning</c> (names are case-insensitive) or a
    /// number. Anything else, including a missing value, gives <paramref name="fallback"/>, so a typo in a setting
    /// cannot stop the application from starting.
    /// </summary>
    public static LogLevel ParseLevel(string? text, LogLevel fallback = LogLevel.Information)
    {
        var trimmed = text?.Trim();

        // Enum.TryParse would accept "Debug,Warning" as a bitwise combination; a level is exactly one value.
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains(',', StringComparison.Ordinal))
        {
            return fallback;
        }

        return Enum.TryParse<LogLevel>(trimmed, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : fallback;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LogDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(FileNamePrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(RetentionDays);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(QueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxValueLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMessageLength);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxStackFrames);
        ArgumentOutOfRangeException.ThrowIfLessThan(ShutdownTimeout, TimeSpan.Zero);
    }

    private static string DefaultLogDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "AgentStudio", "logs");
    }
}
