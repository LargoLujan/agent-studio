using System.Globalization;
using System.Text;
using AgentStudio.Domain.Common;

namespace AgentStudio.Memory.Diagrams;

/// <summary>
/// The only escaper of the diagrams (docs/20 D-24, ADR-0045). Every text that comes from data (a name of project, namespace or agent, the title of a task,
/// the name of a branch) is written to a diagram through here and nowhere else: none of it is trusted (ADR-0018). The rules, in one pass over the characters:
/// <list type="number">
/// <item>a control character, a line or paragraph separator, a tab or a line end become a space, and a format character (a direction mark, a zero width one,
/// a byte-order mark) is taken out: <see cref="UntrustedText.Clean"/>;</item>
/// <item>white space is joined into one space and the text is trimmed, so the same name always gives the same bytes;</item>
/// <item>the text is cut at <see cref="MaxLength"/> characters (never inside a surrogate pair), with «…», <b>before</b> escaping, so an entity is never cut in half;</item>
/// <item>every character of <see cref="Escaped"/> (the quote, <c>#</c>, <c>&amp;</c>, <c>;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>%</c>, the backtick, the backslash and <c>|</c>) becomes
/// its decimal entity of Mermaid (<c>#NN;</c>), also the <c>#</c> of an entity written by the data and the <c>;</c> of its end, so the text cannot be read as an entity, as a
/// statement end, as a comment (<c>%%</c>), as an arrow, as a markdown string (a backtick), as the end of a quoted label or of the label of an edge; and, for the name of a
/// participant of a sequence diagram, the <c>:</c> that would split it from its message.</item>
/// </list>
/// What is left has only letters, digits, spaces and the characters that no syntax of the grammar of the generators reads inside a quoted label.
/// </summary>
internal static class MermaidText
{
    /// <summary>The longest text written to a diagram, in characters, before the ellipsis (docs/20 D-24).</summary>
    public const int MaxLength = 80;

    /// <summary>What an empty text is written as: the name of something that has none.</summary>
    public const string Unnamed = "(sin nombre)";

    private const string Ellipsis = "…";

    /// <summary>The characters that are always written as an entity. A name cannot carry any of them into a diagram.</summary>
    private const string Escaped = "\"#&;<>%`\\|";

    /// <summary>Escapes <paramref name="untrusted"/> for a quoted label (a node, a subgraph, an edge label, a note).</summary>
    public static string Label(string? untrusted) => Write(untrusted, participant: false);

    /// <summary>Escapes <paramref name="untrusted"/> for the alias of a participant of a sequence diagram: also the <c>:</c>.</summary>
    public static string Participant(string? untrusted) => Write(untrusted, participant: true);

    private static string Write(string? untrusted, bool participant)
    {
        if (untrusted is null)
        {
            return Unnamed;
        }

        var text = Normalize(UntrustedText.Clean(untrusted));
        if (text.Length == 0)
        {
            return Unnamed;
        }

        var cut = false;
        var runes = 0;
        var builder = new StringBuilder(text.Length + 8);
        foreach (var rune in text.EnumerateRunes())
        {
            if (runes == MaxLength)
            {
                cut = true;
                break;
            }

            runes++;
            if (rune.IsBmp && IsEscaped((char)rune.Value, participant))
            {
                builder.Append('#').Append(rune.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            else
            {
                builder.Append(rune.ToString());
            }
        }

        if (cut)
        {
            builder.Append(Ellipsis);
        }

        return builder.ToString();
    }

    private static bool IsEscaped(char c, bool participant) => Escaped.Contains(c, StringComparison.Ordinal) || (participant && c == ':');

    /// <summary>White space is one space and the ends are trimmed. A character that is white space for .NET but is not hidden (a no-break space) is also a space.</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
