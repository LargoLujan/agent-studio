using System.Text;

namespace AgentStudio.Memory.Consistency;

/// <summary>A link or an image of a Markdown file: its destination as written, and the line (from 1) it is on.</summary>
internal readonly record struct MarkdownLink(string Destination, int Line);

/// <summary>A span of code (between backticks) of a Markdown file: its text and the line (from 1).</summary>
internal readonly record struct MarkdownCode(string Text, int Line);

/// <summary>
/// The only reader of Markdown of the checks (docs/20 D-28): it finds the links and images (<c>[text](destination)</c>, <c>![alt](destination)</c>) and the spans of code, outside
/// the fenced blocks of code. A Markdown file of a repository is data that is not trusted (ADR-0018), so this is a scanner of characters of linear cost, with
/// no regular expression that can backtrack: the code spans are matched with the next run of backticks of the same length (worked out in one pass from the
/// right), the brackets with a stack (one pass), and what follows a <c>](</c> is read up to a fixed length. A line is read only up to
/// <see cref="MaxLineLength"/> characters. It does not follow a reference, a link of reference style (<c>[text][name]</c>) or an autolink: only what D-28 names.
/// </summary>
internal static class MarkdownScanner
{
    /// <summary>The most characters of a line that are read: the rest of a longer line is not looked at.</summary>
    public const int MaxLineLength = 2_000;

    /// <summary>The most characters of a destination (with its title) that are read.</summary>
    public const int MaxDestinationLength = 500;

    /// <summary>The links and code spans of <paramref name="text"/>, in order. Never throws.</summary>
    public static (IReadOnlyList<MarkdownLink> Links, IReadOnlyList<MarkdownCode> Code) Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var links = new List<MarkdownLink>();
        var code = new List<MarkdownCode>();
        var fence = default(Fence?);
        var lineNumber = 0;
        var position = 0;
        while (position <= text.Length)
        {
            var end = NextLineEnd(text, position);
            lineNumber++;
            var line = text.AsSpan(position, Math.Min(end - position, MaxLineLength));
            if (fence is { } open)
            {
                if (ClosesFence(line, open))
                {
                    fence = null;
                }
            }
            else if (OpensFence(line) is { } opened)
            {
                fence = opened;
            }
            else
            {
                ScanLine(line, lineNumber, links, code);
            }

            if (end >= text.Length)
            {
                break;
            }

            position = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
        }

        return (links, code);
    }

    private static int NextLineEnd(string text, int from)
    {
        var index = text.AsSpan(from).IndexOfAny('\r', '\n');
        return index < 0 ? text.Length : from + index;
    }

    private readonly record struct Fence(char Mark, int Length);

    private static Fence? OpensFence(ReadOnlySpan<char> line)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3 || indent >= line.Length || line[indent] is not ('`' or '~'))
        {
            return null;
        }

        var mark = line[indent];
        var length = 0;
        while (indent + length < line.Length && line[indent + length] == mark)
        {
            length++;
        }

        // The information string of a backtick fence holds no backtick (otherwise it is inline code, not a fence).
        return length >= 3 && !(mark == '`' && line[(indent + length)..].Contains('`')) ? new Fence(mark, length) : null;
    }

    private static bool ClosesFence(ReadOnlySpan<char> line, Fence open)
    {
        var indent = 0;
        while (indent < line.Length && indent < 4 && line[indent] == ' ')
        {
            indent++;
        }

        if (indent > 3)
        {
            return false;
        }

        var length = 0;
        while (indent + length < line.Length && line[indent + length] == open.Mark)
        {
            length++;
        }

        return length >= open.Length && line[(indent + length)..].Trim().IsEmpty;
    }

    private static void ScanLine(ReadOnlySpan<char> line, int lineNumber, List<MarkdownLink> links, List<MarkdownCode> code)
    {
        if (line.IsEmpty)
        {
            return;
        }

        var escaped = new bool[line.Length];
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '\\')
            {
                escaped[i + 1] = true;
                i++;
            }
        }

        // The runs of backticks, and for each the next run of the same length: that one closes the span.
        var runs = new List<(int Start, int Length)>();
        for (var i = 0; i < line.Length;)
        {
            if (line[i] == '`' && !escaped[i])
            {
                var start = i;
                while (i < line.Length && line[i] == '`')
                {
                    i++;
                }

                runs.Add((start, i - start));
            }
            else
            {
                i++;
            }
        }

        var closing = new int[runs.Count];
        var nextOfLength = new Dictionary<int, int>();
        for (var r = runs.Count - 1; r >= 0; r--)
        {
            closing[r] = nextOfLength.GetValueOrDefault(runs[r].Length, -1);
            nextOfLength[runs[r].Length] = r;
        }

        var inCode = new bool[line.Length];
        for (var r = 0; r < runs.Count; r++)
        {
            if (closing[r] < 0)
            {
                continue;
            }

            var (start, length) = runs[r];
            var (closeStart, closeLength) = runs[closing[r]];
            var innerStart = start + length;
            code.Add(new MarkdownCode(line[innerStart..closeStart].ToString(), lineNumber));
            for (var k = start; k < closeStart + closeLength; k++)
            {
                inCode[k] = true;
            }

            // The runs inside the span are part of it: the next run to look at is the one after the closing run.
            r = closing[r];
        }

        // Brackets: a stack, one pass.
        var open = new Stack<int>();
        var match = new int[line.Length];
        Array.Fill(match, -1);
        for (var i = 0; i < line.Length; i++)
        {
            if (inCode[i] || escaped[i])
            {
                continue;
            }

            if (line[i] == '[')
            {
                open.Push(i);
            }
            else if (line[i] == ']' && open.Count > 0)
            {
                match[open.Pop()] = i;
            }
        }

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '[' || match[i] < 0 || inCode[i] || escaped[i])
            {
                continue;
            }

            var close = match[i];
            if (close + 1 < line.Length && line[close + 1] == '(' && !inCode[close + 1] && TryDestination(line, close + 2) is { } destination)
            {
                links.Add(new MarkdownLink(destination, lineNumber));
            }
        }
    }

    /// <summary>
    /// The destination that starts at <paramref name="from"/> (just after <c>](</c>): <c>&lt;…&gt;</c>, or up to a space or the closing parenthesis, with balanced
    /// parentheses inside; an optional title in quotes or parentheses; and the closing <c>)</c>. Null when it does not close within <see cref="MaxDestinationLength"/> characters.
    /// </summary>
    private static string? TryDestination(ReadOnlySpan<char> line, int from)
    {
        var limit = Math.Min(line.Length, from + MaxDestinationLength);
        var i = from;
        while (i < limit && line[i] == ' ')
        {
            i++;
        }

        string destination;
        if (i < limit && line[i] == '<')
        {
            var start = ++i;
            while (i < limit && line[i] != '>')
            {
                i++;
            }

            if (i >= limit)
            {
                return null;
            }

            destination = line[start..i].ToString();
            i++;
        }
        else
        {
            var start = i;
            var depth = 0;
            while (i < limit)
            {
                var c = line[i];
                if (c == '\\' && i + 1 < limit)
                {
                    i += 2;
                    continue;
                }

                if (c == ' ')
                {
                    break;
                }

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }

                i++;
            }

            destination = line[start..i].ToString();
        }

        while (i < limit && line[i] == ' ')
        {
            i++;
        }

        if (i < limit && line[i] is '"' or '\'' or '(')
        {
            var terminator = line[i] == '(' ? ')' : line[i];
            i++;
            while (i < limit && line[i] != terminator)
            {
                i += line[i] == '\\' && i + 1 < limit ? 2 : 1;
            }

            if (i >= limit)
            {
                return null;
            }

            i++;
            while (i < limit && line[i] == ' ')
            {
                i++;
            }
        }

        return i < limit && line[i] == ')' ? destination : null;
    }

    /// <summary>Decodes the bytes of a file as UTF-8, taking out a byte-order mark; an invalid sequence becomes the replacement character, never an error.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var text = new UTF8Encoding(false, false).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
