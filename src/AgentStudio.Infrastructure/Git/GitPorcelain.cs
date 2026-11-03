using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Infrastructure.Git;

/// <summary>One entry of <c>git worktree list --porcelain -z</c>. The first entry of a listing is the main working tree.</summary>
public sealed record WorktreeListEntry(
    string Path,
    string? Head,
    string? Branch,
    bool Detached,
    bool Bare,
    bool Locked,
    string? LockReason,
    bool Prunable);

/// <summary>Parsed output of <c>git merge-tree --write-tree --name-only --messages -z</c>.</summary>
/// <param name="TreeId">The merged tree. With conflicts it holds conflict markers and must never be committed.</param>
/// <param name="ConflictedPaths">Paths with conflicts, each listed once; empty for a clean merge.</param>
/// <param name="Messages">Informational messages of the merge ("Auto-merging x", "CONFLICT (content): ..."), one per line.</param>
public sealed record MergeTreeOutput(string TreeId, IReadOnlyList<string> ConflictedPaths, string Messages);

/// <summary>
/// Parsers for the machine-readable output of git (<c>--porcelain</c> and <c>-z</c> formats), which does not depend on
/// the language of the user. Pure functions: no process is started here.
/// </summary>
public static partial class GitPorcelain
{
    [GeneratedRegex(@"git version (\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectIdPattern();

    /// <summary>Reads <c>git version 2.45.2.windows.1</c> as 2.45.2: only the first three numbers count.</summary>
    public static Version ParseVersion(string versionOutput)
    {
        ArgumentNullException.ThrowIfNull(versionOutput);
        var match = VersionPattern().Match(versionOutput);
        if (!match.Success)
        {
            throw new FormatException($"Unrecognised git version output: '{versionOutput.Trim()}'.");
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(major, minor, patch);
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain -z</c>: every attribute ends with a NUL and an empty attribute ends
    /// the entry. Unlike the line-based format, a lock reason is never quoted.
    /// </summary>
    public static IReadOnlyList<WorktreeListEntry> ParseWorktreeList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var entries = new List<WorktreeListEntry>();
        string? path = null;
        string? head = null;
        string? branch = null;
        var detached = false;
        var bare = false;
        var locked = false;
        string? lockReason = null;
        var prunable = false;

        void Flush()
        {
            if (path is not null)
            {
                entries.Add(new WorktreeListEntry(path, head, branch, detached, bare, locked, lockReason, prunable));
            }

            path = null;
            head = null;
            branch = null;
            detached = false;
            bare = false;
            locked = false;
            lockReason = null;
            prunable = false;
        }

        foreach (var attribute in output.Split('\0'))
        {
            if (attribute.Length == 0)
            {
                Flush();
            }
            else if (attribute.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = attribute["worktree ".Length..];
            }
            else if (path is null)
            {
                continue;
            }
            else if (attribute.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = attribute["HEAD ".Length..];
            }
            else if (attribute.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = attribute["branch ".Length..];
            }
            else if (attribute == "detached")
            {
                detached = true;
            }
            else if (attribute == "bare")
            {
                bare = true;
            }
            else if (attribute == "locked")
            {
                locked = true;
            }
            else if (attribute.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
                lockReason = attribute["locked ".Length..];
            }
            else if (attribute == "prunable" || attribute.StartsWith("prunable ", StringComparison.Ordinal))
            {
                prunable = true;
            }
        }

        Flush();
        return entries;
    }

    /// <summary>
    /// Turns <c>git status --porcelain=v1 -z</c> into the lines <c>git status --porcelain</c> prints, without quoting:
    /// <c>XY path</c>, and <c>XY original -&gt; path</c> for a rename or a copy (with <c>-z</c> git lists the new path
    /// first and the original after it, as a separate field).
    /// </summary>
    public static IReadOnlyList<string> ParseStatusLines(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var fields = output.Split('\0');
        var lines = new List<string>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 4)
            {
                continue;
            }

            var code = field[..2];
            var path = field[3..];
            if (code.IndexOfAny(['R', 'C']) >= 0 && index + 1 < fields.Length)
            {
                lines.Add($"{code} {fields[index + 1]} -> {path}");
                index++;
            }
            else
            {
                lines.Add($"{code} {path}");
            }
        }

        return lines;
    }

    /// <summary>
    /// Every path <c>git status --porcelain=v1 -z</c> lists, unquoted: both the new and the original path of a rename or
    /// a copy. With <c>--ignored=matching</c> an ignored folder is listed once, with its trailing '/'.
    /// </summary>
    public static IReadOnlyList<string> ParseStatusPaths(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var fields = output.Split('\0');
        var paths = new List<string>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.Length < 4)
            {
                continue;
            }

            paths.Add(field[3..]);
            if (field[..2].IndexOfAny(['R', 'C']) >= 0 && index + 1 < fields.Length)
            {
                paths.Add(fields[index + 1]);
                index++;
            }
        }

        return paths;
    }

    /// <summary>
    /// Parses <c>git config -z --show-scope --get-all &lt;key&gt;</c>: a scope and a value per entry, each ended by a NUL,
    /// lowest precedence first. The last entry is the value git uses.
    /// </summary>
    public static IReadOnlyList<(string Scope, string Value)> ParseScopedConfigValues(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var fields = output.Split('\0');
        var values = new List<(string Scope, string Value)>();
        for (var index = 0; index + 1 < fields.Length; index += 2)
        {
            values.Add((fields[index], fields[index + 1]));
        }

        return values;
    }

    /// <summary>
    /// Parses the standard output of <c>git merge-tree --write-tree --name-only --messages -z</c>. Returns false when the
    /// output does not start with a tree id: git also exits with 1 for errors such as "not something we can merge", so
    /// the exit code alone does not tell a conflict from a failure.
    /// </summary>
    /// <remarks>
    /// Layout (git 2.45): <c>tree NUL</c>, the conflicted paths (each followed by NUL), an empty field, then for each
    /// message <c>count NUL path... NUL type NUL text NUL</c>. Older versions print the messages as free text; anything
    /// that does not fit the structured layout is kept as it is.
    /// </remarks>
    public static bool TryParseMergeTree(string output, out MergeTreeOutput? result)
    {
        ArgumentNullException.ThrowIfNull(output);
        result = null;

        var fields = output.Split('\0');
        if (fields.Length == 0 || !ObjectIdPattern().IsMatch(fields[0]))
        {
            return false;
        }

        var paths = new List<string>();
        var index = 1;
        while (index < fields.Length && fields[index].Length > 0)
        {
            if (!paths.Contains(fields[index], StringComparer.Ordinal))
            {
                paths.Add(fields[index]);
            }

            index++;
        }

        // Skip the empty field that ends the list of conflicted paths.
        index++;

        result = new MergeTreeOutput(fields[0], paths, ReadMessages(fields, index));
        return true;
    }

    private static string ReadMessages(string[] fields, int start)
    {
        var messages = new List<string>();
        var index = start;
        while (index < fields.Length)
        {
            if (fields[index].Length == 0)
            {
                index++;
                continue;
            }

            if (int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var pathCount)
                && index + pathCount + 2 < fields.Length)
            {
                // count, <count> paths, conflict type, message
                messages.Add(fields[index + pathCount + 2].TrimEnd('\r', '\n'));
                index += pathCount + 3;
                continue;
            }

            // Not the structured layout: keep the rest as free text.
            for (; index < fields.Length; index++)
            {
                if (fields[index].Length > 0)
                {
                    messages.Add(fields[index].TrimEnd('\r', '\n'));
                }
            }
        }

        return string.Join('\n', messages);
    }
}
