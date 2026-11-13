using System.Text;

namespace AgentStudio.Memory.Consistency;

/// <summary>A link or an image of a Markdown file: its destination as written, and the line (from 1) it is on.</summary>
internal readonly record struct MarkdownLink(string Destination, int Line);

/// <summary>A span of code (between backticks) of a Markdown file: its text and the line (from 1). Only spans that start and end on one line are listed.</summary>
internal readonly record struct MarkdownCode(string Text, int Line);

/// <summary>
/// The only reader of Markdown of the checks (docs/20 D-28): it finds the links and images (<c>[text](destination)</c>, <c>![alt](destination)</c>) and the spans of code, and
/// leaves out what is not a link: fenced blocks of code, blocks of code indented four spaces (after a blank line), HTML comments (also over several lines) and code spans (also
/// over several lines of one paragraph). A Markdown file of a repository is data that is not trusted (ADR-0018), so this is a scanner of characters of linear cost, with
/// no regular expression that can backtrack: the code spans are matched with the next run of backticks of the same length (worked out in one pass from the
/// right), the brackets with a stack (one pass), and what follows a <c>](</c> is read up to a fixed length and, in all, up to <see cref="LineBudget"/> characters per
/// line (plus one per character of the line, so a long line of prose keeps all its real links), so a line made of <c>[](a</c> repeated cannot make every candidate read to the
/// end. A line is read whole, however long: the size of the file is already bounded by its reader. A block starts at a list item, a heading, a table row, a quote or a blank line,
/// and a lone backtick does not cross it; an indented block is code only outside a list item. It does not
/// follow a link of reference style (<c>[text][name]</c>) or an autolink: only what D-28 names.
/// </summary>
internal static class MarkdownScanner
{
    /// <summary>The most characters of a destination (with its title) that are read.</summary>
    public const int MaxDestinationLength = 500;

    /// <summary>The most characters that the destinations of one line may make the scanner read, all candidates together, besides one per character of the line.</summary>
    public const int LineBudget = 4_000;

    /// <summary>The most lines a code span that began on an earlier line may go on for: past it the backtick was only a backtick.</summary>
    public const int MaxSpanLines = 20;

    /// <summary>The links and code spans of <paramref name="text"/>, in order. Never throws.</summary>
    public static (IReadOnlyList<MarkdownLink> Links, IReadOnlyList<MarkdownCode> Code) Scan(string text) => Scan(text, out _);

    /// <summary>The same, and how many characters of destinations were read in all: a count the tests use to hold the cost, which does not depend on the clock.</summary>
    internal static (IReadOnlyList<MarkdownLink> Links, IReadOnlyList<MarkdownCode> Code) Scan(string text, out long steps)
    {
        ArgumentNullException.ThrowIfNull(text);
        var links = new List<MarkdownLink>();
        var code = new List<MarkdownCode>();
        var fence = default(Fence?);
        var lineNumber = 0;
        var position = 0;
        var inComment = false;
        var previousBlank = true;
        var inIndented = false;
        var inList = false;
        var span = new SpanState();
        long cost = 0;
        while (position <= text.Length)
        {
            var end = NextLineEnd(text, position);
            lineNumber++;
            var raw = text.AsSpan(position, end - position);
            var blank = raw.Trim().IsEmpty;
            if (fence is { Quoted: true } && !StartsWithQuote(raw))
            {
                // The quote that held the fence ended: so did the fence.
                fence = null;
            }

            if (fence is { } open)
            {
                if (ClosesFence(raw, open))
                {
                    fence = null;
                }
            }
            else
            {
                // A blank line and the start of a block (an item, a heading, a table row, a quote) end a code span: a lone backtick must not cross blocks.
                var startsBlock = !inComment && StartsBlock(raw, out _);
                if (blank || startsBlock)
                {
                    span.Reset();
                }

                if (!blank && !inComment)
                {
                    var flush = raw[0] is not (' ' or '\t');
                    if (startsBlock)
                    {
                        StartsBlock(raw, out var isItem);
                        inList = isItem || (inList && !flush);
                    }
                    else if (flush && previousBlank)
                    {
                        inList = false;
                    }
                }

                // A line indented four spaces is a block of code only outside a list item: inside one it is a nested list or a paragraph that goes on.
                var indented = !blank && !inComment && !inList && span.OpenLength == 0 && IsIndented(raw) && (previousBlank || inIndented);
                if (indented)
                {
                    inIndented = true;
                }
                else
                {
                    inIndented = inIndented && blank;
                    if (!inComment && OpensFence(raw, inList) is { } opened)
                    {
                        fence = opened;
                        span.Reset();
                    }
                    else
                    {
                        ScanLine(raw, lineNumber, links, code, span, ref inComment, ref cost);
                    }
                }
            }

            previousBlank = blank;
            if (end >= text.Length)
            {
                break;
            }

            position = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
        }

        steps = cost;
        return (links, code);
    }

    private static bool StartsWithQuote(ReadOnlySpan<char> line) => StripQuote(ref line);

    private static int NextLineEnd(string text, int from)
    {
        var index = text.AsSpan(from).IndexOfAny('\r', '\n');
        return index < 0 ? text.Length : from + index;
    }

    /// <summary>A line that starts with a tab or four spaces: a line of an indented block of code when it follows a blank line (or another such line).</summary>
    private static bool IsIndented(ReadOnlySpan<char> line) => line.Length > 0 && (line[0] == '\t' || line.StartsWith("    ", StringComparison.Ordinal));

    /// <summary>
    /// Whether the line starts a block of Markdown (a list item, a heading, a table row, a quote); <paramref name="isItem"/> tells a list item (<c>-</c>, <c>*</c>, <c>+</c>, <c>1.</c>
    /// followed by a space) from the others.
    /// </summary>
    private static bool StartsBlock(ReadOnlySpan<char> line, out bool isItem)
    {
        isItem = false;
        var rest = line.TrimStart(" \t");
        if (rest.IsEmpty)
        {
            return false;
        }

        if (rest[0] is '#' or '|' or '>')
        {
            return true;
        }

        var digits = 0;
        while (digits < rest.Length && digits < 9 && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        var marker = digits > 0 ? (digits < rest.Length && rest[digits] is '.' or ')' ? digits : -1) : (rest[0] is '-' or '*' or '+' ? 0 : -1);
        if (marker >= 0 && (marker + 1 >= rest.Length || rest[marker + 1] is ' ' or '\t'))
        {
            isItem = true;
            return true;
        }

        return false;
    }

    /// <summary>What a code span that began on an earlier line of the paragraph is waiting for: a run of backticks of this length.</summary>
    private sealed class SpanState
    {
        public int OpenLength { get; private set; }

        public int Lines { get; private set; }

        public void Open(int length)
        {
            OpenLength = length;
            Lines = 0;
        }

        public bool Continue()
        {
            Lines++;
            if (Lines > MaxSpanLines)
            {
                Reset();
                return false;
            }

            return true;
        }

        public void Reset() => (OpenLength, Lines) = (0, 0);
    }

    /// <summary>An open fence: its mark and length, whether it began inside a quote (it ends where the quote does) and whether it may be indented any amount (it is under a list item).</summary>
    private readonly record struct Fence(char Mark, int Length, bool Quoted, bool Deep);

    /// <summary>Takes the quote markers (<c>&gt;</c>, each with up to three spaces before and one after) off the start of <paramref name="line"/>; true when there was at least one.</summary>
    private static bool StripQuote(ref ReadOnlySpan<char> line)
    {
        var quoted = false;
        while (true)
        {
            var spaces = 0;
            while (spaces < line.Length && spaces < 4 && line[spaces] == ' ')
            {
                spaces++;
            }

            if (spaces > 3 || spaces >= line.Length || line[spaces] != '>')
            {
                return quoted;
            }

            quoted = true;
            line = line[(spaces + 1)..];
            if (line.Length > 0 && line[0] == ' ')
            {
                line = line[1..];
            }
        }
    }

    /// <summary>The count of the leading spaces (a tab counts as four), or -1 when it passes <paramref name="limit"/>.</summary>
    private static int Indentation(ReadOnlySpan<char> line, int limit)
    {
        var indent = 0;
        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            indent += line[i] == '\t' ? 4 : 1;
            if (indent > limit)
            {
                return -1;
            }

            i++;
        }

        return i;
    }

    private static Fence? OpensFence(ReadOnlySpan<char> line, bool inList)
    {
        var quoted = StripQuote(ref line);
        var deep = inList && !quoted;
        var indent = Indentation(line, deep ? int.MaxValue : 3);
        if (indent < 0 || indent >= line.Length || line[indent] is not ('`' or '~'))
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
        return length >= 3 && !(mark == '`' && line[(indent + length)..].Contains('`')) ? new Fence(mark, length, quoted, deep) : null;
    }

    private static bool ClosesFence(ReadOnlySpan<char> line, Fence open)
    {
        if (open.Quoted)
        {
            StripQuote(ref line);
        }

        var indent = Indentation(line, open.Deep ? int.MaxValue : 3);
        if (indent < 0)
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
    /// <summary>The next <c>&lt;!--</c> at or after <paramref name="from"/> that is not escaped, or -1.</summary>
    private static int IndexOfComment(ReadOnlySpan<char> line, int from, bool[] escaped)
    {
        var at = from;
        while (at < line.Length)
        {
            var found = line[at..].IndexOf("<!--", StringComparison.Ordinal);
            if (found < 0)
            {
                return -1;
            }

            if (!escaped[at + found])
            {
                return at + found;
            }

            at += found + 1;
        }

        return -1;
    }

    /// <summary>
    /// One line, read left to right so that what starts first wins (as in CommonMark): a code span (the next run of backticks of the same length closes it), an HTML comment
    /// (<c>&lt;!-- … --&gt;</c>, carried over lines by <paramref name="inComment"/>) or a link. A <c>&lt;!--</c> inside a code span opens no comment, and a backtick inside a comment opens no span.
    /// </summary>
    private static void ScanLine(ReadOnlySpan<char> raw, int lineNumber, List<MarkdownLink> links, List<MarkdownCode> code, SpanState span, ref bool inComment, ref long cost)
    {
        if (raw.IsEmpty)
        {
            return;
        }

        var escaped = new bool[raw.Length];
        for (var i = 0; i < raw.Length - 1; i++)
        {
            if (raw[i] == '\\')
            {
                escaped[i + 1] = true;
                i++;
            }
        }

        // The runs of backticks, and for each the next run of the same length: that one closes the span.
        var runs = new List<(int Start, int Length)>();
        for (var i = 0; i < raw.Length;)
        {
            if (raw[i] == '`')
            {
                var start = i;
                while (i < raw.Length && raw[i] == '`')
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

        var inCode = new bool[raw.Length];
        var comment = new bool[raw.Length];
        var anyComment = false;
        var position = 0;
        var run = 0;

        // What began on an earlier line: a comment goes on until its end; a span until a run of its length (without one, the whole line is code).
        if (inComment)
        {
            var close = raw.IndexOf("-->", StringComparison.Ordinal);
            if (close < 0)
            {
                return;
            }

            position = close + 3;
            Array.Fill(comment, true, 0, position);
            anyComment = true;
            inComment = false;
        }
        else if (span.OpenLength > 0)
        {
            var closes = runs.FindIndex(r => r.Length == span.OpenLength);
            if (closes < 0)
            {
                if (span.Continue())
                {
                    return;
                }
            }
            else
            {
                position = runs[closes].Start + runs[closes].Length;
                Array.Fill(inCode, true, 0, position);
                span.Reset();
                run = closes + 1;
            }
        }

        var nextComment = -2;
        while (position < raw.Length)
        {
            while (run < runs.Count && runs[run].Start < position)
            {
                run++;
            }

            if (nextComment != -1 && nextComment < position)
            {
                nextComment = IndexOfComment(raw, position, escaped);
            }

            var runStart = run < runs.Count ? runs[run].Start : int.MaxValue;
            if (nextComment >= 0 && nextComment < runStart)
            {
                var afterOpen = nextComment + 4;
                var close = afterOpen <= raw.Length ? raw[afterOpen..].IndexOf("-->", StringComparison.Ordinal) : -1;
                var stop = close < 0 ? raw.Length : afterOpen + close + 3;
                Array.Fill(comment, true, nextComment, stop - nextComment);
                anyComment = true;
                inComment = close < 0;
                position = stop;
                continue;
            }

            if (run >= runs.Count)
            {
                break;
            }

            var (start, length) = runs[run];
            if (escaped[start])
            {
                // A backslash escapes a backtick only outside a code span: this one is a plain character (and its whole run, which is the rare case).
                run++;
                position = start + length;
                continue;
            }

            if (closing[run] < 0)
            {
                // A run that nothing on this line closes: the span may close on a later line of the paragraph, and everything after it is code until then.
                span.Open(length);
                Array.Fill(inCode, true, start, raw.Length - start);
                break;
            }

            var (closeStart, closeLength) = runs[closing[run]];
            code.Add(new MarkdownCode(raw[(start + length)..closeStart].ToString(), lineNumber));
            Array.Fill(inCode, true, start, closeStart + closeLength - start);

            // The runs inside the span are part of it: the next run to look at is the one after the closing run.
            run = closing[run] + 1;
            position = closeStart + closeLength;
        }

        // The parts of comments are written as spaces: what is in them is neither a link nor a bracket.
        ReadOnlySpan<char> line = raw;
        if (anyComment)
        {
            var copy = raw.ToArray();
            for (var i = 0; i < copy.Length; i++)
            {
                if (comment[i])
                {
                    copy[i] = ' ';
                    inCode[i] = true;
                }
            }

            line = copy;
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

        // The budget grows with the line (one character per character), so the real links of a long line of prose are all read, and the cost stays linear in its length.
        var budget = LineBudget + line.Length;
        for (var i = 0; i < line.Length && budget > 0; i++)
        {
            if (line[i] != '[' || match[i] < 0 || inCode[i] || escaped[i])
            {
                continue;
            }

            var close = match[i];
            if (close + 1 < line.Length && line[close + 1] == '(' && !inCode[close + 1] && TryDestination(line, close + 2, ref budget, ref cost) is { } destination)
            {
                links.Add(new MarkdownLink(destination, lineNumber));
            }
        }
    }

    /// <summary>
    /// The destination that starts at <paramref name="from"/> (just after <c>](</c>), and what it cost: the characters read are taken from <paramref name="budget"/> of the line and
    /// added to <paramref name="cost"/>, whether it closes or not.
    /// </summary>
    private static string? TryDestination(ReadOnlySpan<char> line, int from, ref int budget, ref long cost)
    {
        var i = from;
        try
        {
            return ReadDestination(line, from, ref i);
        }
        finally
        {
            var read = Math.Max(1, i - from);
            budget -= read;
            cost += read;
        }
    }

    /// <summary>
    /// <c>&lt;…&gt;</c>, or up to a space or the closing parenthesis, with balanced parentheses inside; an optional title in quotes or parentheses; and the closing <c>)</c>. Null when
    /// it does not close within <see cref="MaxDestinationLength"/> characters. <paramref name="i"/> ends where it stopped reading.
    /// </summary>
    private static string? ReadDestination(ReadOnlySpan<char> line, int from, ref int i)
    {
        var limit = Math.Min(line.Length, from + MaxDestinationLength);
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
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
