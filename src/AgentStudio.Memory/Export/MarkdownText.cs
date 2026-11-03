using System.Buffers;
using System.Text;

namespace AgentStudio.Memory.Export;

/// <summary>
/// Text that is data inside a Markdown that Agent Studio writes (the export of the memory, the daily summary): every
/// Markdown syntax character gets a backslash, the same set the prompt journal escapes, so a topic, a content or a line
/// of the journal can never become a heading, a link, a list, an HTML tag or a code span of its own.
/// </summary>
internal static class MarkdownText
{
    private static readonly SearchValues<char> Specials = SearchValues.Create("\\*_[]()#`<>|");

    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny(Specials) < 0)
        {
            return text;
        }

        var escaped = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (Specials.Contains(c))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }
}
