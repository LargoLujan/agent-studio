namespace AgentStudio.Domain.Hosting;

/// <summary>
/// The character-by-character rules of the identifiers of the remote (docs/21 D-04): ASCII only, no regular expression, so a
/// lookalike letter or a culture can never make something pass. Written once and shared by <see cref="RemoteRepository"/>,
/// <see cref="RemoteLogin"/>, <see cref="RemoteBranch"/> and the commits that travel in a snapshot.
/// </summary>
internal static class HostingIdentifiers
{
    /// <summary>The longest owner of GitHub: one letter or digit and up to 38 more.</summary>
    public const int MaxOwnerLength = 39;

    public const int MaxRepositoryNameLength = 100;

    /// <summary>An owner or an account: <c>[A-Za-z0-9]</c> and up to 38 characters of <c>[A-Za-z0-9-]</c> (the rule of <c>GitHubSkillOrigin</c>, docs/20 D-14).</summary>
    public static bool IsOwner(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxOwnerLength || !IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A repository name: <c>[A-Za-z0-9._-]{1,100}</c>, never <c>.</c> or <c>..</c> and never <c>..</c> inside.</summary>
    public static bool IsRepositoryName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxRepositoryNameLength || value is "." or "..")
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return !value.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>A full commit id: 40 or 64 lowercase hexadecimal characters.</summary>
    public static bool IsCommitId(string? value) =>
        value is { Length: 40 or 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static bool IsAsciiLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
}
