using System.Text.RegularExpressions;
using AgentStudio.Application.Hosting;

namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>What the line of a ref in the <c>--porcelain</c> output of <c>git push</c> said.</summary>
internal enum RemotePushLine
{
    /// <summary>No line for the refspec that was pushed.</summary>
    Missing,

    /// <summary>A new branch or a fast-forward (<c>*</c> or a space).</summary>
    Updated,

    /// <summary>The remote already had that commit there (<c>=</c>).</summary>
    UpToDate,

    /// <summary>Rejected because the remote branch is not an ancestor of the commit: <c>non-fast-forward</c> or <c>fetch first</c>.</summary>
    RejectedNotFastForward,

    /// <summary>Any other flag: a rejection by the remote (<c>remote rejected</c>), a forced update or a deletion, which this executor never asks for.</summary>
    Other,
}

/// <summary>
/// Reads the output of git (docs/21 D-06): text that is not trusted. Standard output is read by structure (<c>--porcelain</c>, the lines of <c>ls-remote</c>) and standard error is only classified into a code;
/// neither is ever quoted in a message of the app. With <c>LC_ALL=C</c> (the environment sets it) the error output has the one language the classifier reads.
/// </summary>
internal static class RemoteGitOutput
{
    /// <summary>
    /// The line of <paramref name="refspec"/> in the porcelain output: <c>&lt;flag&gt;\t&lt;from&gt;:&lt;to&gt;\t&lt;summary&gt;</c>, one per ref, between <c>To &lt;url&gt;</c> and <c>Done</c>. Only a line whose second field is
    /// exactly the refspec is taken; the others are ignored.
    /// </summary>
    public static RemotePushLine ReadPush(string standardOutput, string refspec)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(refspec);
        foreach (var raw in standardOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var fields = line.Split('\t', 3);
            if (fields.Length < 3 || fields[0].Length != 1 || !string.Equals(fields[1], refspec, StringComparison.Ordinal))
            {
                continue;
            }

            var summary = fields[2];
            return fields[0][0] switch
            {
                '*' or ' ' => RemotePushLine.Updated,
                '=' => RemotePushLine.UpToDate,
                '!' when summary.Contains("(non-fast-forward)", StringComparison.Ordinal) || summary.Contains("(fetch first)", StringComparison.Ordinal) => RemotePushLine.RejectedNotFastForward,
                _ => RemotePushLine.Other,
            };
        }

        return RemotePushLine.Missing;
    }

    /// <summary>
    /// The commit <c>ls-remote</c> printed for exactly <paramref name="refName"/>: a line of 40 lowercase hexadecimal characters, a tab and that name. Other lines are ignored (a pattern can match more than one ref); two
    /// different commits for the same name are not an answer (null).
    /// </summary>
    public static string? ReadBranchCommit(string standardOutput, string refName)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(refName);
        string? found = null;
        foreach (var raw in standardOutput.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split('\t');
            if (fields.Length != 2 || !string.Equals(fields[1], refName, StringComparison.Ordinal) || !RemoteGitCommandLine.IsCommit(fields[0]))
            {
                continue;
            }

            if (found is not null && !string.Equals(found, fields[0], StringComparison.Ordinal))
            {
                return null;
            }

            found = fields[0];
        }

        return found;
    }

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant;

    // The messages of git that were measured (docs/21 «Ajustes de WP1»), written as the start of a line (fatal:, error:, warning:, remote:) and not as loose words: a word inside another word, or inside a hint or the text of
    // a commit, is not a code. A hostile remote can still write «remote: Repository not found»; the code is only what the screen says, and it is not trusted to do anything else.
    private static readonly (string Code, Regex Pattern)[] Classifiers =
    [
        (RemoteGitCodes.Redirected, new Regex(@"^(fatal: unable to update url base from redirection|warning: redirecting to |(fatal|error): .*The requested URL returned error: 30[1278]\b)", Options)),
        (RemoteGitCodes.ProtocolRefused, new Regex(@"^fatal: (transport '[^']*' not allowed|protocol '[^']*' is not supported)", Options)),
        (
            RemoteGitCodes.CredentialsNeeded,
            new Regex(
                @"^(fatal: (could not read (Username|Password) for|Authentication failed for|.*terminal prompts disabled|repository '[^']*' not found|unable to access '[^']*': The requested URL returned error: 40[13]\b)"
                + @"|remote: (Invalid username or password|Permission to .* denied|Repository not found|Support for password authentication was removed))",
                Options)),
        (
            RemoteGitCodes.RemoteUnreachable,
            new Regex(
                @"^fatal: (unable to access '[^']*': (Could not resolve host|Failed to connect|Couldn't connect|Connection (timed out|refused|reset)|Recv failure|Operation timed out|Network is unreachable|Empty reply|SSL |schannel|OpenSSL)"
                + @"|'[^']*' does not appear to be a git repository|Could not read from remote repository)",
                Options)),
    ];

    /// <summary>
    /// The code of a failed command, from its error output: a redirect and a refused protocol first, then what asks for credentials (GitHub answers «Repository not found» to a private repository it does not let you see),
    /// then what could not be reached. Each is a line that starts as git writes it. Anything else is <see cref="RemoteGitCodes.GitFailed"/>. Nothing of the text is kept.
    /// </summary>
    public static string ClassifyError(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        foreach (var (code, pattern) in Classifiers)
        {
            if (pattern.IsMatch(standardError))
            {
                return code;
            }
        }

        return RemoteGitCodes.GitFailed;
    }
}