using System.Text;
using System.Xml;
using AgentStudio.Application.Verification;

namespace AgentStudio.Orchestration.Verification.Reports;

/// <summary>
/// Collects the names of the tests that failed while a report is read (P50). A report is untrusted: only the first
/// <see cref="MaxFailures"/> failures are kept, each name and message is cut, and a message of any size is read in chunks, so a
/// hostile report costs no more memory than its bounds.
/// </summary>
internal sealed class FailureCollector
{
    public const int MaxFailures = 200;
    public const int MaxNameCharacters = 300;
    public const int MaxMessageCharacters = 400;

    private readonly List<FailedTest> _failures = [];

    public IReadOnlyList<FailedTest> Failures => _failures;

    public void Add(string? name, string? message)
    {
        if (_failures.Count >= MaxFailures)
        {
            return;
        }

        var shownName = Clean(name, MaxNameCharacters);
        _failures.Add(new FailedTest(shownName.Length == 0 ? "?" : shownName, message is null ? null : NullIfEmpty(Clean(message, MaxMessageCharacters))));
    }

    /// <summary>
    /// The text of the element the reader is on, at most <paramref name="max"/> characters of it (plus one, to know it was cut);
    /// the reader is left past the element. Child elements are skipped.
    /// </summary>
    public static string ReadBoundedText(XmlReader reader, int max = MaxMessageCharacters)
    {
        var text = new StringBuilder();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        var depth = reader.Depth;
        var buffer = new char[256];
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                int read;
                while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length <= max)
                    {
                        text.Append(buffer, 0, Math.Min(read, max + 1 - text.Length));
                    }
                }

                // ReadValueChunk leaves the reader on the same node when it is done: move past it.
                reader.Read();
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                reader.Skip();
            }
            else
            {
                reader.Read();
            }
        }

        // Past the end tag.
        reader.Read();
        return text.ToString();
    }

    /// <summary>One line, without control characters, cut with an ellipsis: what a person reads, never a block of the report.</summary>
    internal static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
            {
                line.Append(' ');
                space = false;
            }

            line.Append(c);
            if (line.Length > max)
            {
                break;
            }
        }

        return line.Length > max ? line.ToString(0, max - 1) + "…" : line.ToString();
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
