using System.Collections;
using System.Text;

namespace AgentStudio.Infrastructure.Processes.Isolation;

/// <summary>
/// The command line and the environment block <c>CreateProcessAsUser</c> receives, built with the same rules
/// <see cref="System.Diagnostics.Process"/> applies to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> and
/// to <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>, so an isolated process gets exactly the arguments and
/// variables a process without isolation would.
/// </summary>
internal static class WindowsCommandLine
{
    private const char Quote = '"';
    private const char Backslash = '\\';

    /// <summary>The executable, always quoted, followed by every argument quoted as the C runtime parses it back.</summary>
    internal static string Build(string executable, IReadOnlyList<string> arguments)
    {
        if (executable.Contains(Quote, StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path contains a quote.", nameof(executable));
        }

        var builder = new StringBuilder(executable.Length + 2 + (arguments.Count * 16));
        builder.Append(Quote).Append(executable).Append(Quote);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The current environment with <paramref name="overrides"/> applied (a null value removes the variable), as a
    /// Unicode block sorted by name without regard to case, each <c>name=value</c> ending in NUL and the block in a
    /// second NUL.
    /// </summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && IsValidName(name))
            {
                variables[name] = entry.Value as string ?? string.Empty;
            }
        }

        foreach (var (name, value) in overrides ?? new Dictionary<string, string?>())
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException($"'{name}' is not a valid environment variable name.", nameof(overrides));
            }

            if (value is null)
            {
                variables.Remove(name);
            }
            else if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"The value of the environment variable '{name}' contains a NUL character.", nameof(overrides));
            }
            else
            {
                variables[name] = value;
            }
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        // An empty block still needs its two terminating NULs.
        if (variables.Count == 0)
        {
            block.Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private static bool IsValidName(string name) =>
        name.Length > 0 && !name.Contains('=', StringComparison.Ordinal) && !name.Contains('\0', StringComparison.Ordinal);

    // The algorithm of System.PasteArguments.AppendArgument (dotnet/runtime): 2N backslashes before a quote mean N
    // backslashes and the end of the quoted region, 2N+1 mean N backslashes and a literal quote.
    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == Quote))
        {
            builder.Append(argument);
            return;
        }

        builder.Append(Quote);
        var index = 0;
        while (index < argument.Length)
        {
            var c = argument[index++];
            if (c == Backslash)
            {
                var backslashes = 1;
                while (index < argument.Length && argument[index] == Backslash)
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    builder.Append(Backslash, backslashes * 2);
                }
                else if (argument[index] == Quote)
                {
                    builder.Append(Backslash, (backslashes * 2) + 1).Append(Quote);
                    index++;
                }
                else
                {
                    builder.Append(Backslash, backslashes);
                }

                continue;
            }

            if (c == Quote)
            {
                builder.Append(Backslash).Append(Quote);
                continue;
            }

            builder.Append(c);
        }

        builder.Append(Quote);
    }
}
