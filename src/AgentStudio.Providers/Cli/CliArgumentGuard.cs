using System.Text.RegularExpressions;

namespace AgentStudio.Providers.Cli;

/// <summary>
/// Validates the values that travel as command-line arguments to the provider CLIs (session ids, model names,
/// directories). A value that could be parsed as a flag (leading dash) or that carries unexpected characters is
/// rejected with an <see cref="ArgumentException"/> BEFORE anything is launched: silently starting a new session
/// instead of resuming would hide the problem from the caller.
/// </summary>
public static partial class CliArgumentGuard
{
    /// <summary>Maximum length of a session id or a model name.</summary>
    public const int MaxTokenLength = 128;

    /// <summary>Returns <paramref name="value"/> when it is a safe session id (<c>^[A-Za-z0-9][A-Za-z0-9._-]*$</c>, at most 128 chars).</summary>
    public static string SessionId(string? value, string paramName)
    {
        if (value is null || value.Length is 0 or > MaxTokenLength || !SessionIdPattern().IsMatch(value))
        {
            throw new ArgumentException("Invalid session id: it must match ^[A-Za-z0-9][A-Za-z0-9._-]*$ and be at most 128 characters.", paramName);
        }

        return value;
    }

    /// <summary>Returns <paramref name="value"/> when it is a safe model name (<c>^[A-Za-z0-9][A-Za-z0-9._:/[]-]*$</c>, at most 128 chars).</summary>
    public static string Model(string? value, string paramName)
    {
        if (value is null || value.Length is 0 or > MaxTokenLength || !ModelPattern().IsMatch(value))
        {
            throw new ArgumentException("Invalid model name: it must match ^[A-Za-z0-9][A-Za-z0-9._:/\\[\\]-]*$ and be at most 128 characters.", paramName);
        }

        return value;
    }

    /// <summary>Returns <paramref name="value"/> when it is an absolute, fully qualified path that does not start with a dash.</summary>
    public static string Directory(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value[0] == '-'
            || value.AsSpan().IndexOfAnyInRange('\0', '\u001f') >= 0
            || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Invalid directory: it must be an absolute path that does not start with '-'.", paramName);
        }

        return value;
    }

    /// <summary>Maximum length of one tool rule.</summary>
    public const int MaxToolLength = 256;

    /// <summary>
    /// Returns <paramref name="value"/> when it is a safe tool rule. Grammar: a name <c>[A-Za-z0-9_][A-Za-z0-9_.:*-]*</c>,
    /// optionally followed by a specifier in parentheses (<c>Bash(git log:*)</c>, <c>Read(./src/**)</c>). The specifier
    /// may hold spaces but no control characters and no parentheses of its own. A bare name has no whitespace, never
    /// starts with a dash and is at most 256 characters. The list is joined with spaces into one argument, so this keeps
    /// each rule from splitting or being read as a flag.
    /// </summary>
    public static string ToolName(string? value, string paramName)
    {
        if (value is null || value.Length is 0 or > MaxToolLength || !ToolPattern().IsMatch(value))
        {
            throw new ArgumentException("Invalid tool rule: expected Name or Name(specifier), without control characters, whitespace outside the parentheses or a leading '-'.", paramName);
        }

        return value;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.:*-]*(\([^\x00-\x1f()]*\))?\z")]
    private static partial Regex ToolPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:/\[\]-]*\z")]
    private static partial Regex ModelPattern();
}
