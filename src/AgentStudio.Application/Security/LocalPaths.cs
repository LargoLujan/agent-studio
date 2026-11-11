using System.Text.RegularExpressions;

namespace AgentStudio.Application.Security;

/// <summary>
/// What counts as a path of the person's machine in a text (docs/17 D-57 and D-60): a drive path (<c>C:\Users\ana\repo</c>,
/// <c>D:/work</c>), a network path (<c>\\server\share</c>), a path under the home folder (<c>~/repo</c>, which is what
/// <see cref="ISecretRedactor"/> leaves of the user profile) and a <c>file:///</c> address. The context card of the help agent sends
/// a text that has one only when the person ticks «Incluir datos del proyecto», and a document for clients never has one: the
/// generator takes it out. A relative path (<c>src/App.cs</c>), a URL that is not <c>file:///</c> and an id such as <c>T3:2</c> are
/// not local paths, and neither is a Unix path with no <c>file:///</c> or <c>~</c> in front (<c>/home/ana</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where a path ends.</b> Folders have spaces in them (<c>C:\Personal Projects\Agent Studio</c>, <c>C:\Users\Ana Garcia</c>,
/// <c>\\server\mi recurso</c>), and the text after a path says nothing about where it stops, so a path starts at its anchor and runs
/// over spaces up to the first clear delimiter, on the same line: the end of the line; a quote (<c>"</c>, <c>`</c>, <c>»</c>,
/// <c>«</c>, <c>“</c>, <c>”</c>, or <c>'</c> not followed by a letter or digit, so <c>O'Brien</c> stays inside); one of
/// <c>&lt; &gt; | * ?</c>, which no Windows path has; a <c>)</c>, <c>]</c> or <c>}</c> with no opening one inside the path (a balanced
/// <c>(x86)</c> stays inside); a <c>, ; . : !</c> followed by a space or by the end; or the next path (a space and an anchor).
/// </para>
/// <para>
/// This takes out <b>more</b> than the path when words follow it with nothing to close it («Abre C:\repo y mira» is all a path),
/// and never less: a half-removed path would still tell who and where. A text that wants the path and the rest apart closes the path
/// with a quote or a punctuation mark. It is a rule on text, not a promise that no path escapes: the person reviews what goes out
/// (D-57 and D-60).
/// </para>
/// </remarks>
public static partial class LocalPaths
{
    /// <summary>
    /// What a match can carry at its end that is not part of the path: the punctuation that closes a sentence (a bracket is not here: a
    /// closing one with no opening one never enters the match, and a balanced one belongs to the path), and spaces.
    /// </summary>
    private static readonly char[] Closing = ['.', ',', ';', ':', '!', ' ', '\t'];

    // What a path holds after its anchor, up to a clear delimiter (see the remarks of the class):
    // - never a «, ; . : !» that closes a sentence (followed by a space or the end), nor a space that starts another path;
    // - any character but a line break, a quote, «< > | * ?» and a bracket; an apostrophe followed by a letter or a digit is inside a name;
    // - a bracketed group in parentheses, as in «Program Files (x86)».
    private const string Body =
        @"(?:(?![,;.:!?]+(?:\s|$))(?!\s(?:file:///|[A-Za-z]:[\\/]|\\\\|~[\\/]))"
        + @"(?:[^\r\n""'`<>|*?»«“”()\]}]|'(?=[A-Za-z0-9])|\([^\r\n""'`<>|*?»«“”()]*\)))*";

    // A «file:///» address or a single letter alone (not the end of a word such as the «s» of «https»), then «:» and a separator;
    // two backslashes, a host and a share («\\server\share\folder»); the home folder, alone at the start of a word («~/repo»).
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:file:///|[A-Za-z]:[\\/])" + Body
        + @"|(?<![\\A-Za-z0-9])\\\\[^\s\\/""'`<>|*?]+[\\/]" + Body
        + @"|(?<![A-Za-z0-9._~-])~[\\/]" + Body)]
    private static partial Regex PathPattern();

    /// <summary>The paths of the machine in <paramref name="text"/>, in order of appearance, without the punctuation or spaces that close them.</summary>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return [.. PathPattern().Matches(text).Select(m => m.Value.TrimEnd(Closing))];
    }

    /// <summary>Whether <paramref name="text"/> names a path of the machine.</summary>
    public static bool Contains(string? text) => Find(text).Count > 0;

    /// <summary>
    /// <paramref name="text"/> without its paths of the machine, each occurrence replaced by <paramref name="replacement"/> (what
    /// closes it, a punctuation mark or a space, stays), and how many occurrences were taken out. A path is taken out whole, spaces
    /// included, and a little more rather than less (see the remarks of the class).
    /// </summary>
    public static (string Text, int Removed) Remove(string? text, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, 0);
        }

        var removed = 0;
        var clean = PathPattern().Replace(text, match =>
        {
            var path = match.Value.TrimEnd(Closing);
            removed++;
            return replacement + match.Value[path.Length..];
        });

        return (clean, removed);
    }
}
