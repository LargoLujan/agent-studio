using System.Text;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Application.Security;

/// <summary>
/// The characters a text that goes to the screen or to an agent must not carry (docs/17 D-33, D-57 and D-58): a control
/// character that drives the terminal or the layout, and a character that changes the direction of the text (bidi), with which a
/// label could show reversed and pass for a button of the system. The help (the card, the question and the answer of the agent)
/// and the documents for clients use the same rule as the questions of the agents (<see cref="QuestionRules"/>).
/// </summary>
public static class PlainText
{
    /// <summary>Whether <paramref name="text"/> has a control or bidi character; a line break counts only when <paramref name="allowLineBreaks"/> is false.</summary>
    public static bool HasHiddenCharacter(string? text, bool allowLineBreaks)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (IsHidden(c, allowLineBreaks))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="text"/> without its control and bidi characters. A line break stays when <paramref name="keepLineBreaks"/> is
    /// true (a carriage return never does); otherwise it becomes a space, so two words never join.
    /// </summary>
    public static string Strip(string? text, bool keepLineBreaks)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\n')
            {
                builder.Append(keepLineBreaks ? '\n' : ' ');
            }
            else if (c == '\t')
            {
                builder.Append(' ');
            }
            else if (!IsHidden(c, allowLineBreaks: false))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsHidden(char c, bool allowLineBreaks) =>
        !(allowLineBreaks && c is '\n' or '\r' or '\t') && (QuestionRules.HasControlCharacter(c.ToString()) || QuestionRules.HasBidiCharacter(c.ToString()));
}
