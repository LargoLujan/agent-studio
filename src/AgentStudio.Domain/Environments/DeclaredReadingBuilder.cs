using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Environments;

/// <summary>
/// Collects what the parsers of <see cref="DeclaredFileParser"/> find in a manifest (docs/20 D-06), so that no hostile file can make the reading throw: every
/// value that came from a repository is cleaned of hidden characters, trimmed and cut at <see cref="DeclaredEntry.MaxValueLength"/> characters before the
/// constructors of <see cref="DeclaredEntry"/> and <see cref="DeclaredIssue"/> see it, an empty value becomes an issue instead of an entry, and nothing is
/// kept past <see cref="DeclaredEnvironment.MaxItems"/> entries or issues.
/// </summary>
public sealed class DeclaredReadingBuilder
{
    private const string Ellipsis = "…";

    private const int MaxNameLength = 214;

    private readonly List<DeclaredEntry> _entries = [];

    private readonly List<DeclaredIssue> _issues = [];

    private readonly List<string> _filesFound = [];

    public IReadOnlyList<DeclaredEntry> Entries => _entries;

    public IReadOnlyList<DeclaredIssue> Issues => _issues;

    public IReadOnlyList<string> FilesFound => _filesFound;

    /// <summary>The environment collected so far.</summary>
    public DeclaredEnvironment Build() => new(_entries, _issues, _filesFound);

    /// <summary>Says that <paramref name="file"/>, one of the closed list, exists.</summary>
    public void Found(string file)
    {
        if (DeclaredFiles.IsDeclared(file) && !_filesFound.Contains(file, StringComparer.Ordinal))
        {
            _filesFound.Add(file);
        }
    }

    /// <summary>
    /// An entry from a value that was read from a file. A value that is empty once cleaned is not an entry: it is a <see cref="DeclaredIssueKind.NotInterpreted"/> issue.
    /// </summary>
    public void Entry(DeclaredKind kind, string? name, string? value, DeclaredSource source)
    {
        var cleanValue = CleanLine(value, DeclaredEntry.MaxValueLength);
        var cleanName = name is null ? null : CleanLine(name, MaxNameLength);
        if (cleanValue.Length == 0)
        {
            Issue(source, DeclaredIssueKind.NotInterpreted, cleanName);
            return;
        }

        if (_entries.Count >= DeclaredEnvironment.MaxItems)
        {
            return;
        }

        _entries.Add(new DeclaredEntry(kind, string.IsNullOrEmpty(cleanName) ? null : cleanName, cleanValue, source));
    }

    /// <summary>An issue; <paramref name="text"/> is what was there, cleaned and cut, and it is left out when nothing is left of it.</summary>
    public void Issue(DeclaredSource source, DeclaredIssueKind kind, string? text = null)
    {
        if (_issues.Count >= DeclaredEnvironment.MaxItems)
        {
            return;
        }

        var clean = text is null ? string.Empty : CleanLine(text, DeclaredEntry.MaxValueLength);
        _issues.Add(new DeclaredIssue(source, kind, clean.Length == 0 ? null : clean));
    }

    /// <summary>One line, with no hidden character, trimmed and cut at <paramref name="max"/> characters (with «…», never in the middle of a surrogate pair).</summary>
    public static string CleanLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var clean = UntrustedText.Clean(text).Trim();
        if (clean.Length <= max)
        {
            return clean;
        }

        var length = max - Ellipsis.Length;
        if (char.IsHighSurrogate(clean[length - 1]))
        {
            length--;
        }

        return clean[..length].TrimEnd() + Ellipsis;
    }
}
