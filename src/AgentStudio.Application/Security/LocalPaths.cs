using System.Text.RegularExpressions;

namespace AgentStudio.Application.Security;

/// <summary>
/// What counts as a path of the person's machine in a text (docs/17 D-57 and D-60): a drive path (<c>C:\Users\ana\repo</c>,
/// <c>D:/work</c>), a network path (<c>\\server\share</c>) or a path under the home folder (<c>~/repo</c>, which is what
/// <see cref="ISecretRedactor"/> leaves of the user profile). The context card of the help agent sends a text that has one only
/// when the person ticks «Incluir datos del proyecto», and a document for clients never has one: the generator takes it out.
/// A relative path (<c>src/App.cs</c>), a URL and an id such as <c>T3:2</c> are not local paths.
/// </summary>
/// <remarks>It is a rule on text, not a promise that no path escapes: the person reviews what goes out (D-57 and D-60).</remarks>
public static partial class LocalPaths
{
    /// <summary>The punctuation that closes a sentence after a path: it is not part of the path.</summary>
    private static readonly char[] Closing = ['.', ',', ';', ':', ')', ']', '}', '!'];

    // A single letter, alone (not the end of a word such as the «s» of «https»), then «:» and a separator.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s""'<>|*?`]*")]
    private static partial Regex Drive();

    // Two backslashes, a host and a share: «\\server\share\folder».
    [GeneratedRegex(@"(?<![\\A-Za-z0-9])\\\\[^\s\\/""'<>|*?`]+[\\/][^\s""'<>|*?`]*")]
    private static partial Regex Network();

    // The home folder, alone at the start of a word: «~/repo» or «~\repo».
    [GeneratedRegex(@"(?<![A-Za-z0-9._~-])~[\\/][^\s""'<>|*?`]*")]
    private static partial Regex Home();

    private static Regex[] Rules => [Drive(), Network(), Home()];

    /// <summary>The paths of the machine in <paramref name="text"/>, in order of appearance, without the punctuation that closes a sentence.</summary>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var found = new List<(int Index, string Path)>();
        foreach (var rule in Rules)
        {
            foreach (Match match in rule.Matches(text))
            {
                found.Add((match.Index, match.Value.TrimEnd(Closing)));
            }
        }

        return [.. found.OrderBy(f => f.Index).Select(f => f.Path)];
    }

    /// <summary>Whether <paramref name="text"/> names a path of the machine.</summary>
    public static bool Contains(string? text) => Find(text).Count > 0;

    /// <summary>
    /// <paramref name="text"/> without its paths of the machine, each occurrence replaced by <paramref name="replacement"/> (the
    /// punctuation after it stays), and how many occurrences were taken out.
    /// </summary>
    public static (string Text, int Removed) Remove(string? text, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, 0);
        }

        var removed = 0;
        var clean = text;
        foreach (var rule in Rules)
        {
            clean = rule.Replace(clean, match =>
            {
                var path = match.Value.TrimEnd(Closing);
                removed++;
                return replacement + match.Value[path.Length..];
            });
        }

        return (clean, removed);
    }
}
