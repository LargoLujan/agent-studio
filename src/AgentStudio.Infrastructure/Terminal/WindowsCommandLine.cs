using System.Text;

namespace AgentStudio.Infrastructure.Terminal;

/// <summary>
/// Builds a Windows command line that the C runtime (<c>CommandLineToArgvW</c>) parses back into exactly the given
/// executable and arguments, and the matching Unicode environment block.
/// </summary>
public static class WindowsCommandLine
{
    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();
        // The program name is parsed with simpler rules (no backslash escaping), so it is only wrapped in quotes.
        if (executable.Contains('"'))
        {
            throw new ArgumentException("The executable path cannot contain a double quote.", nameof(executable));
        }

        builder.Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    public static void AppendArgument(StringBuilder builder, string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                // Backslashes before a quote are doubled, then the quote itself is escaped.
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
        }

        // Trailing backslashes precede the closing quote and must be doubled.
        builder.Append('\\', backslashes * 2).Append('"');
    }

    /// <summary>
    /// Merges <paramref name="overrides"/> (null value removes) over <paramref name="baseline"/> and renders the
    /// NUL-separated, double-NUL-terminated block, sorted case-insensitively as Windows expects.
    /// </summary>
    public static string BuildEnvironmentBlock(
        IReadOnlyDictionary<string, string?> baseline,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in baseline)
        {
            if (value is not null)
            {
                merged[key] = value;
            }
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null)
                {
                    merged.Remove(key);
                }
                else
                {
                    merged[key] = value;
                }
            }
        }

        var builder = new StringBuilder();
        foreach (var key in merged.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(key).Append('=').Append(merged[key]).Append('\0');
        }

        // The block ends with an extra NUL; an empty block therefore needs two.
        return builder.Append('\0').Append(merged.Count == 0 ? "\0" : string.Empty).ToString();
    }
}
