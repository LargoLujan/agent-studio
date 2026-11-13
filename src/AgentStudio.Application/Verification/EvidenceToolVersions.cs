using System.Text;

namespace AgentStudio.Application.Verification;

/// <summary>
/// The version of one tool of a verification step as its evidence says it (docs/20 D-12): its name, its version when it could be told, and a short note
/// (where the number comes from, or why there is none). Nothing in it was asked of the tool: see <see cref="IStepToolVersionReader"/>.
/// </summary>
public sealed record EvidenceToolVersion
{
    /// <summary>The longest note.</summary>
    public const int MaxNoteLength = 120;

    /// <summary>The longest name or version.</summary>
    public const int MaxTokenLength = 64;

    /// <exception cref="ArgumentException">A tool or a version that is not one token of letters, digits and <c>. _ + -</c>, or a note that is empty, longer than <see cref="MaxNoteLength"/> or has more than one line.</exception>
    public EvidenceToolVersion(string tool, string? version, string? note = null)
    {
        if (!IsToken(tool) || (version is not null && !IsToken(version)))
        {
            throw new ArgumentException($"A tool and a version are one token of 1 to {MaxTokenLength} letters, digits, '.', '_', '+' or '-'.");
        }

        if (note is not null && (note.Length is < 1 or > MaxNoteLength || note.Any(char.IsControl) || note != note.Trim() || note.Contains(')', StringComparison.Ordinal)))
        {
            throw new ArgumentException($"A note is one line of 1 to {MaxNoteLength} characters with no control character and no closing parenthesis.", nameof(note));
        }

        Tool = tool;
        Version = version;
        Note = note;
    }

    public string Tool { get; }

    /// <summary>The version, or null when it is unknown (the header then says <c>unknown</c>).</summary>
    public string? Version { get; }

    public string? Note { get; }

    /// <summary>A tool whose version could not be told, with the reason as its note. The reason is cleaned and cut to fit; it is for people.</summary>
    public static EvidenceToolVersion Unknown(string tool, string reason) => new(tool, null, Clean(reason));

    /// <summary>A tool name made of what a file name has, as one token; anything else becomes <c>_</c>. The name is only a label of the header.</summary>
    public static string TokenOf(string? name)
    {
        var token = new string([.. (name ?? string.Empty).Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' ? c : '_')]);
        return token.Length == 0 ? "_" : token.Length > MaxTokenLength ? token[..MaxTokenLength] : token;
    }

    /// <summary>A reason or a file name as a note: one line, no closing parenthesis, cut to <see cref="MaxNoteLength"/>; a blank text says that no reason was given.</summary>
    public static string Clean(string text)
    {
        var line = new string([.. text.Select(c => char.IsControl(c) || c == ')' ? ' ' : c)]).Trim();
        return line.Length == 0 ? "no reason given" : line.Length > MaxNoteLength ? line[..MaxNoteLength].TrimEnd() : line;
    }

    private static bool IsToken(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= MaxTokenLength && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-');
}

/// <summary>
/// The versions of the tools of a verification step (docs/20 D-12), taken without running anything of the repository and without running the tool at all:
/// from the version resource of the executable that the launch resolves (the same file that is started), as <c>NodeVerificationProfile.InstalledVersion</c> does
/// for Node. Auto Doctor's closed door is not used: it would answer for a neutral folder, not for the file the step starts, and this reading starts nothing.
/// </summary>
public interface IStepToolVersionReader
{
    /// <summary>
    /// The tools of <paramref name="steps"/> (a step and the steps it requires), one entry per distinct executable in the order they are met, each with its version
    /// or the reason there is none. It never throws and never starts a process.
    /// </summary>
    IReadOnlyList<EvidenceToolVersion> Read(IReadOnlyList<VerificationStep> steps);
}

/// <summary>
/// The block <c>tools:</c> of the header of the content of the evidence of a verification step (docs/20 D-12), and its reading. The content is already
/// anchored by its SHA-256 in <c>EvidenceRegistered</c>, so there is no new field and no migration. The header is the run of lines that start with <c>#</c> at the top
/// of the content; the lines of the process come after it, each prefixed with <c>stdout| </c> or <c>stderr| </c>, so nothing a process writes can forge the block:
/// <code>
/// # tools:
/// #   dotnet 10.0.303
/// #   node 22.11.0 (version resource of node.exe)
/// #   npm unknown (npm-cli.js has no version file)
/// </code>
/// </summary>
public static class EvidenceToolVersions
{
    /// <summary>The line that opens the block.</summary>
    public const string BlockLine = "# tools:";

    /// <summary>The prefix of each entry of the block.</summary>
    public const string EntryPrefix = "#   ";

    /// <summary>The word the header writes for a version that could not be told.</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>The most entries written or read.</summary>
    public const int MaxEntries = 20;

    /// <summary>The lines of the block (the opening one and one per tool), without line endings. Empty when there is no tool.</summary>
    public static IReadOnlyList<string> Format(IEnumerable<EvidenceToolVersion> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var entries = tools.Take(MaxEntries).ToList();
        if (entries.Count == 0)
        {
            return [];
        }

        return
        [
            BlockLine,
            .. entries.Select(t => EntryPrefix + t.Tool + " " + (t.Version ?? UnknownVersion) + (t.Note is null ? string.Empty : " (" + t.Note + ")")),
        ];
    }

    /// <summary>
    /// The tools that the header of <paramref name="content"/> lists: empty for an evidence that has no block (every one written before this one), for text that
    /// is not a header and for entries that are not well formed (they are skipped). It reads only the header, so the output of a process never counts. Total: it never throws.
    /// </summary>
    public static IReadOnlyList<EvidenceToolVersion> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var tools = new List<EvidenceToolVersion>();
        var inBlock = false;
        var start = 0;
        var index = 0;
        while (start < content.Length)
        {
            var end = content.IndexOf('\n', start);
            var line = (end < 0 ? content[start..] : content[start..end]).TrimEnd('\r');
            start = end < 0 ? content.Length : end + 1;
            if (line.Length == 0 || line[0] != '#')
            {
                break;
            }

            if (!inBlock)
            {
                // The block has one place: after the title line, the command line, the isolation line and, when there is one, the source line.
                // Anything else that looks like a block (a title or a command that spans lines) is not it.
                var expected = index switch
                {
                    0 => line.StartsWith("# Agent Studio", StringComparison.Ordinal),
                    1 => line.StartsWith("# command: ", StringComparison.Ordinal),
                    2 => line.StartsWith("# isolation:", StringComparison.Ordinal),
                    3 => line.StartsWith("# source: ", StringComparison.Ordinal) || line == BlockLine,
                    _ => line == BlockLine,
                };
                if (!expected || index > 4)
                {
                    break;
                }

                index++;
                inBlock = line == BlockLine;
                continue;
            }

            if (!line.StartsWith(EntryPrefix, StringComparison.Ordinal) || tools.Count >= MaxEntries)
            {
                break;
            }

            if (ParseEntry(line[EntryPrefix.Length..]) is { } tool)
            {
                tools.Add(tool);
            }
        }

        return tools;
    }

    /// <summary>The version of <paramref name="tool"/> in <paramref name="content"/>, or null when the header does not list it or it was unknown.</summary>
    public static string? VersionOf(string? content, string tool) =>
        Parse(content).FirstOrDefault(t => string.Equals(t.Tool, tool, StringComparison.OrdinalIgnoreCase))?.Version;

    private static EvidenceToolVersion? ParseEntry(string text)
    {
        var parts = text.Split(' ', 3);
        if (parts.Length < 2)
        {
            return null;
        }

        string? note = null;
        if (parts.Length == 3)
        {
            var rest = parts[2];
            if (rest.Length < 3 || rest[0] != '(' || rest[^1] != ')')
            {
                return null;
            }

            note = rest[1..^1];
        }

        try
        {
            return new EvidenceToolVersion(parts[0], parts[1] == UnknownVersion ? null : parts[1], note);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The whole block as text, with line endings <c>\n</c>, for a test to compare.</summary>
    public static string FormatText(IEnumerable<EvidenceToolVersion> tools) =>
        new StringBuilder().AppendJoin('\n', Format(tools)).ToString();
}
