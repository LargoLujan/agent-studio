using System.Text;

namespace AgentStudio.Desktop.Markdown;

/// <summary>What a piece of a line is: plain text, bold (<c>**…**</c>) or code (<c>`…`</c>).</summary>
public enum MarkdownSpanKind
{
    Text,
    Bold,
    Code,
}

/// <summary>A piece of a line, with the escapes of Markdown already resolved (<c>\(</c> is <c>(</c>).</summary>
public sealed record MarkdownSpan(MarkdownSpanKind Kind, string Text);

/// <summary>A block of a document.</summary>
public abstract record MarkdownBlock;

/// <summary>A title, <c># …</c> to <c>###### …</c>.</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>Consecutive lines of text; each one stays a line, as the journal writes them.</summary>
public sealed record MarkdownParagraph(IReadOnlyList<IReadOnlyList<MarkdownSpan>> Lines) : MarkdownBlock;

/// <summary>An entry of a list (<c>- …</c>, <c>* …</c> or <c>+ …</c>) at its depth: two spaces of indent per level.</summary>
public sealed record MarkdownListItem(int Depth, IReadOnlyList<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>The lines of a fenced block of code (<c>```</c>), as they are.</summary>
public sealed record MarkdownCodeBlock(IReadOnlyList<string> Lines) : MarkdownBlock;

/// <summary>
/// The small part of Markdown the journal uses (V31): titles, lists, bold, code and the backslash escapes the journal writes
/// (<c>MarkdownDailyJournal</c> escapes <c>\*_[]()#`&lt;&gt;|</c> in what it quotes). Anything else is text, shown as it is:
/// it never fails and never interprets more than that. No dependency: the journal needs no more.
/// </summary>
public static class MarkdownLite
{
    /// <summary>The blocks of <paramref name="markdown"/>, in order; none for an empty text.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(markdown))
        {
            return blocks;
        }

        var paragraph = new List<IReadOnlyList<MarkdownSpan>>();
        List<string>? code = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (code is not null)
            {
                if (IsFence(line))
                {
                    blocks.Add(new MarkdownCodeBlock([.. code]));
                    code = null;
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (IsFence(line))
            {
                EndParagraph(blocks, paragraph);
                code = [];
            }
            else if (line.Trim().Length == 0)
            {
                EndParagraph(blocks, paragraph);
            }
            else if (Heading(line) is { } heading)
            {
                EndParagraph(blocks, paragraph);
                blocks.Add(heading);
            }
            else if (ListItem(line) is { } item)
            {
                EndParagraph(blocks, paragraph);
                blocks.Add(item);
            }
            else
            {
                paragraph.Add(ParseSpans(line.Trim()));
            }
        }

        // A fence left open keeps its lines as code.
        if (code is not null)
        {
            blocks.Add(new MarkdownCodeBlock([.. code]));
        }

        EndParagraph(blocks, paragraph);
        return blocks;
    }

    /// <summary>
    /// <paramref name="markdown"/> as plain text, for a place that shows a text and not a document (V41: an entry of the
    /// journal in «Memoria»): one line per line, with the marks of <see cref="Parse"/> gone (bold, code and escapes resolved).
    /// An entry of a list at the top loses its dash; a sub-entry (a result, a «Hecho» line) reads «• …», two spaces in per
    /// level. A block of code keeps its lines as they are. Blank lines go.
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        var lines = new List<string>();
        foreach (var block in Parse(markdown))
        {
            switch (block)
            {
                case MarkdownHeading heading:
                    lines.Add(Plain(heading.Spans));
                    break;
                case MarkdownParagraph paragraph:
                    lines.AddRange(paragraph.Lines.Select(Plain));
                    break;
                case MarkdownListItem { Depth: 0 } item:
                    lines.Add(Plain(item.Spans));
                    break;
                case MarkdownListItem item:
                    lines.Add(new string(' ', 2 * item.Depth) + "• " + Plain(item.Spans));
                    break;
                case MarkdownCodeBlock code:
                    lines.AddRange(code.Lines);
                    break;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The pieces of one line: an escape first (<c>\*</c> is a literal asterisk, never the start of bold), then code (its
    /// content as it is), then bold; an unclosed <c>`</c> or <c>**</c> is text.
    /// </summary>
    public static IReadOnlyList<MarkdownSpan> ParseSpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<MarkdownSpan>();
        var plain = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[++i]);
            }
            else if (c == '`' && text.IndexOf('`', i + 1) is var close and > 0)
            {
                Flush(spans, plain);
                spans.Add(new MarkdownSpan(MarkdownSpanKind.Code, text[(i + 1)..close]));
                i = close;
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && ClosingBold(text, i + 2) is var end && end > i + 2)
            {
                Flush(spans, plain);
                spans.Add(new MarkdownSpan(MarkdownSpanKind.Bold, Unescape(text[(i + 2)..end])));
                i = end + 1;
            }
            else
            {
                plain.Append(c);
            }
        }

        Flush(spans, plain);
        return spans;
    }

    private static string Plain(IReadOnlyList<MarkdownSpan> spans) => string.Concat(spans.Select(s => s.Text));

    private static void EndParagraph(List<MarkdownBlock> blocks, List<IReadOnlyList<MarkdownSpan>> paragraph)
    {
        if (paragraph.Count > 0)
        {
            blocks.Add(new MarkdownParagraph([.. paragraph]));
            paragraph.Clear();
        }
    }

    private static void Flush(List<MarkdownSpan> spans, StringBuilder plain)
    {
        if (plain.Length > 0)
        {
            spans.Add(new MarkdownSpan(MarkdownSpanKind.Text, plain.ToString()));
            plain.Clear();
        }
    }

    private static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static MarkdownHeading? Heading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        return level is >= 1 and <= 6 && level < line.Length && line[level] == ' '
            ? new MarkdownHeading(level, ParseSpans(line[(level + 1)..].Trim()))
            : null;
    }

    private static MarkdownListItem? ListItem(string line)
    {
        var indent = 0;
        var start = 0;
        while (start < line.Length && line[start] is ' ' or '\t')
        {
            indent += line[start] == '\t' ? 4 : 1;
            start++;
        }

        return start + 1 < line.Length && (line[start] is '-' or '*' or '+') && line[start + 1] == ' '
            ? new MarkdownListItem(indent / 2, ParseSpans(line[(start + 2)..].Trim()))
            : null;
    }

    /// <summary>Where the <c>**</c> that closes a bold piece starting at <paramref name="from"/> is, past escaped characters; -1 when there is none.</summary>
    private static int ClosingBold(string text, int from)
    {
        for (var i = from; i + 1 < text.Length; i++)
        {
            if (text[i] == '\\' && IsEscapable(text[i + 1]))
            {
                i++;
            }
            else if (text[i] == '*' && text[i + 1] == '*')
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unescape(string text)
    {
        var plain = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            plain.Append(text[i] == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]) ? text[++i] : text[i]);
        }

        return plain.ToString();
    }

    /// <summary>The ASCII punctuation a backslash escapes in Markdown (CommonMark).</summary>
    private static bool IsEscapable(char c) => c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');
}
