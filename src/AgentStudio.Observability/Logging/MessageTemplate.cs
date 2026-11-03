using System.Globalization;
using System.Text;

namespace AgentStudio.Observability.Logging;

/// <summary>
/// Renders a message template (<c>"Saving {Path} failed"</c>) with explicit values, exactly as the framework does:
/// holes are <c>{Name[,alignment][:format]}</c>, values are matched by position, <c>{{</c> and <c>}}</c> are literal
/// braces. The logger needs its own renderer because the framework's one would print the raw values, and the raw
/// values are precisely what must not reach the file.
/// </summary>
internal static class MessageTemplate
{
    private static readonly char[] FormatDelimiters = [',', ':'];

    /// <summary>Renders with the invariant culture. Returns false (and the template as it is) when it cannot be rendered.</summary>
    public static bool TryRender(string template, object?[] arguments, out string rendered)
    {
        var composite = ToCompositeFormat(template, out var holes);
        if (holes > arguments.Length)
        {
            rendered = template;
            return false;
        }

        try
        {
            rendered = string.Format(CultureInfo.InvariantCulture, composite, arguments);
            return true;
        }
        catch (FormatException)
        {
            rendered = template;
            return false;
        }
    }

    /// <summary>Replaces the name of every hole by its position, giving a <see cref="string.Format(string, object[])"/> format.</summary>
    private static string ToCompositeFormat(string format, out int holes)
    {
        holes = 0;
        var builder = new StringBuilder(format.Length + 8);
        var scanIndex = 0;
        var endIndex = format.Length;

        while (scanIndex < endIndex)
        {
            var openBraceIndex = FindBraceIndex(format, '{', scanIndex, endIndex);
            if (scanIndex == 0 && openBraceIndex == endIndex)
            {
                return format; // No holes at all.
            }

            var closeBraceIndex = FindBraceIndex(format, '}', openBraceIndex, endIndex);
            if (closeBraceIndex == endIndex)
            {
                builder.Append(format, scanIndex, endIndex - scanIndex);
                scanIndex = endIndex;
            }
            else
            {
                // Hole syntax: { name [,alignment] [:formatString] }.
                var delimiterIndex = format.IndexOfAny(FormatDelimiters, openBraceIndex, closeBraceIndex - openBraceIndex);
                if (delimiterIndex < 0)
                {
                    delimiterIndex = closeBraceIndex;
                }

                builder.Append(format, scanIndex, openBraceIndex - scanIndex + 1);
                builder.Append(holes.ToString(CultureInfo.InvariantCulture));
                holes++;
                builder.Append(format, delimiterIndex, closeBraceIndex - delimiterIndex + 1);
                scanIndex = closeBraceIndex + 1;
            }
        }

        return builder.ToString();
    }

    // Finds the brace that opens ('{': the last of a run) or closes ('}': the first of a run) a hole, skipping the
    // doubled braces that stand for a literal one. Example: {{prefix{{{Argument}}}suffix}}.
    private static int FindBraceIndex(string format, char brace, int startIndex, int endIndex)
    {
        var braceIndex = endIndex;
        var scanIndex = startIndex;
        var braceOccurrenceCount = 0;

        while (scanIndex < endIndex)
        {
            if (braceOccurrenceCount > 0 && format[scanIndex] != brace)
            {
                if (braceOccurrenceCount % 2 == 0)
                {
                    // An even run is an escape: keep looking for the next brace.
                    braceOccurrenceCount = 0;
                    braceIndex = endIndex;
                }
                else
                {
                    // An odd run contains an unescaped brace.
                    break;
                }
            }
            else if (format[scanIndex] == brace)
            {
                if (brace == '}')
                {
                    if (braceOccurrenceCount == 0)
                    {
                        braceIndex = scanIndex;
                    }
                }
                else
                {
                    braceIndex = scanIndex;
                }

                braceOccurrenceCount++;
            }

            scanIndex++;
        }

        return braceIndex;
    }
}
