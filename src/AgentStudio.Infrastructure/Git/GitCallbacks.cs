using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes.Isolation;

namespace AgentStudio.Infrastructure.Git;

/// <summary>One configuration entry that would let git run a program the repository or a worktree controls.</summary>
/// <param name="Key">The key as git prints it (<c>filter.format.clean</c>).</param>
/// <param name="Origin">Where git read it (<c>file:.git/config</c>, <c>command line:</c>...).</param>
/// <param name="Reason">Why it is refused, naming the offending word but never the whole value.</param>
public sealed record GitCallbackRefusal(string Key, string Origin, string Reason);

/// <summary>
/// Reads the effective git configuration and finds the callbacks git could run with the user's permissions that the
/// repository or a worktree controls (N-01, ADR-0012). git runs the program a filter, merge driver, textconv, external
/// diff, ssh command, editor, credential helper or gpg program names, through its own shell; an agent that rewrites that
/// program in its worktree, or points it at a repository-relative path, would make the next internal git command of
/// Agent Studio execute the agent's code outside the Low-integrity verification. git-lfs, in turn, runs the program its
/// own <c>lfs.*</c> keys name — an extension's clean/smudge, a custom transfer, the standalone transfer agent — which
/// are refused outright (A-03): only the base git-lfs filters run on content. So is <c>lfs.storage</c>, the folder
/// git-lfs writes the content of large files into, which can lead those writes out of the repository (P-03). So are a
/// remote helper the configuration chooses (<c>remote.*.vcs</c>) and a partial clone (<c>remote.*.promisor</c>,
/// <c>remote.*.partialclonefilter</c>, <c>extensions.partialClone</c>), whose missing objects git would fetch on demand,
/// from inside an ordinary command, through a transport or remote helper the configuration chooses (H-04). A key refused
/// outright is refused whatever its value, an empty one included.
/// </summary>
/// <remarks>
/// <para>
/// The rule is an ALLOWLIST (R3-01), not a denylist: git runs callbacks through <c>sh -c</c>, and a denylist of "bad"
/// first tokens let <c>exec ./payload</c>, <c>! ./payload</c> and <c>prog "-cinline-code"</c> through. A callback is
/// accepted only when every token matches a safe charset (<see cref="IsCharsetChar"/>; the first token may also be one
/// quoted absolute path, single or double quotes), the first token is neither an interpreter/shell
/// (<see cref="Interpreters"/>) nor a shell builtin (<see cref="Builtins"/>), and that program resolves — a bare name
/// through the PATH and the git bundle, an absolute path on disk — to a file whose FINAL path (every link resolved)
/// lies OUTSIDE the repository and the worktrees. git-lfs, astextplain and the credential manager keep working; a
/// relative program, one inside those roots, an interpreter, a builtin, a leading environment assignment or anything
/// outside the charset is refused. On the content only the git-lfs filters run (R4-01), and an absolute program there
/// must be the installed git-lfs itself, by final path and file id (G-03): the roots cannot list every folder the user's
/// repository controls.
/// </para>
/// <para>
/// A callback is also refused when the configuration file it comes from lives inside a worktree, because an agent with a
/// shell controls that file (for example through an <c>includeIf</c> it plants).
/// </para>
/// <para>Pure: no process is started here. <see cref="Pattern"/> is what the caller passes to <c>config --get-regexp</c>.</para>
/// </remarks>
public static class GitCallbacks
{
    /// <summary>
    /// The regex for <c>git config --get-regexp</c>: the sections and keys that can carry an executable callback,
    /// <c>lfs.*</c> among them (git-lfs extensions and custom transfers, A-03), and the keys of a remote helper or a
    /// partial clone (<c>remote.*</c> and <c>extensions.partialclone</c>, H-04). <see cref="IsCallbackKey"/> filters the
    /// matches precisely; keys whose value is a boolean or an email (so classifying the value as a program would refuse
    /// a legitimate repository), and the <c>lfs.*</c> keys that name no program, are deliberately left out (see the
    /// comment on <see cref="IsCallbackKey"/>).
    /// </summary>
    public const string Pattern = @"^(filter|diff|merge|credential|gpg|remote|uploadpack|lfs)\.|^core\.(sshcommand|editor|askpass|gitproxy|alternaterefscommand)$|^sequence\.editor$|^extensions\.partialclone$";

    private const string GitLfsProgramReason =
        "git-lfs would run it as an extension or a custom transfer on content an agent may control, and Agent Studio runs only the base git-lfs filters there";

    private const string GitLfsStorageReason =
        "it chooses where git-lfs writes the content of the large files (an absolute or a relative path, outside the repository too), and Agent Studio lets git-lfs write only to its default storage in the repository";

    private const string RemoteHelperReason =
        "it chooses the remote helper git runs for every transport of that remote (git-remote-<value>, found on the PATH), and no internal command of Agent Studio needs a remote helper";

    private const string PartialCloneReason =
        "it makes the repository a partial clone: git would fetch the objects it lacks on demand, from inside ordinary commands, through a transport or remote helper the configuration chooses, and Agent Studio needs every object in the local repository (use a full clone)";

    // Interpreters and shells: refused outright as the first token, whatever their arguments, because they run code from
    // those arguments (a repository script, or inline code in a '-c'/'-e'/'-File' option). basenames, lower case, no ".exe".
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "ksh", "fish", "ash", "busybox",
        "cmd", "command", "powershell", "pwsh",
        "python", "python2", "python3", "py", "python3.11", "python3.12",
        "node", "nodejs", "deno", "bun",
        "perl", "ruby", "php", "lua", "tclsh", "awk", "gawk", "sed",
        "java", "dotnet", "npx", "pnpm", "yarn",
        "env", "git", "osascript", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
    };

    // Shell builtins: refused outright as the first token. They run an arbitrary command given as their argument, so a
    // denylist that treated a bare first token as a safe PATH name let `exec ./payload` and `. ./payload` through (R3-01).
    private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "eval", ".", "source", "command", "builtin", "nohup", "setsid", "time", "timeout", "nice", "xargs",
    };

    /// <summary>One entry of <c>config -z --show-scope --show-origin --get-regexp</c>: scope, origin, key and value.</summary>
    public readonly record struct ConfigEntry(string Scope, string Origin, string Key, string Value);

    /// <summary>
    /// Parses <c>git config -z --show-scope --show-origin --get-regexp &lt;pattern&gt;</c>: three NUL-terminated fields per
    /// entry (scope, origin, then the key with its value after a newline; a valueless key has no newline).
    /// </summary>
    public static IReadOnlyList<ConfigEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var fields = output.Split('\0');
        var entries = new List<ConfigEntry>();

        // The last field after a trailing NUL is empty; groups of three otherwise.
        for (var index = 0; index + 2 < fields.Length; index += 3)
        {
            var scope = fields[index];
            var origin = fields[index + 1];
            var keyValue = fields[index + 2];
            var newline = keyValue.IndexOf('\n', StringComparison.Ordinal);
            var key = newline < 0 ? keyValue : keyValue[..newline];
            var value = newline < 0 ? string.Empty : keyValue[(newline + 1)..];
            entries.Add(new ConfigEntry(scope, origin, key, value));
        }

        return entries;
    }

    /// <summary>
    /// The callbacks among <paramref name="entries"/> that git could run and that the repository or a worktree controls.
    /// <paramref name="programRoots"/> are the roots a callback program may not point inside: the worktree root, the
    /// command's working directory, the root of the user's repository and its common directory (X-01). Each root counts
    /// by its path as given and by its final path, resolved with <paramref name="finalPath"/> exactly as the program's
    /// own path is, so a root named through a link, a short name or another drive still holds its programs.
    /// <paramref name="worktreeRoot"/> is the root no configuration file may live under. <paramref name="workingDirectory"/>
    /// resolves a relative origin path, as git prints it relative to the command's cwd. <paramref name="resolveOnPath"/>
    /// turns a bare program name into its full path (the PATH and the git bundle), <paramref name="finalPath"/>
    /// resolves every link on the way to a path (see <see cref="ResolveFinalPath"/>), and <paramref name="fileIdentity"/>
    /// names the file a path reaches (see <see cref="ResolveFileIdentity"/>; without it only final paths are compared): all
    /// are injected so this method stays pure and the tests can feed known values. A content callback (a git-lfs filter)
    /// whose program is absolute must also be the installed git-lfs, by final path and file id (G-03).
    /// </summary>
    public static IReadOnlyList<GitCallbackRefusal> Classify(
        IEnumerable<ConfigEntry> entries,
        IReadOnlyList<string> programRoots,
        string worktreeRoot,
        string workingDirectory,
        Func<string, string?> resolveOnPath,
        Func<string, string?> finalPath,
        Func<string, string?>? fileIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(programRoots);
        ArgumentNullException.ThrowIfNull(resolveOnPath);
        ArgumentNullException.ThrowIfNull(finalPath);

        programRoots = WithFinalPaths(programRoots, finalPath);
        var refusals = new List<GitCallbackRefusal>();
        foreach (var entry in entries)
        {
            if (!IsCallbackKey(entry.Key, out var isCredentialHelper, out var refusedOutright))
            {
                continue;
            }

            // The key is judged before its value (A-03): an empty value is no way around a key refused outright (an empty
            // remote.<name>.partialclonefilter still made git fetch lazily, measured, H-04). Any other callback with no
            // value names no program, and an empty one is how a configuration resets a list (credential.<url>.helper =).
            if (refusedOutright is null && string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            if (OriginInsideWorktree(entry.Origin, worktreeRoot, workingDirectory))
            {
                refusals.Add(new GitCallbackRefusal(entry.Key, entry.Origin, "it comes from a configuration file inside a worktree, which an agent controls"));
                continue;
            }

            // A git-lfs extension (clean/smudge) or a custom transfer (A-03), a remote helper or a partial clone (H-04) is
            // refused outright, whatever its program: none goes through the allowlist.
            if (refusedOutright is not null)
            {
                refusals.Add(new GitCallbackRefusal(entry.Key, entry.Origin, refusedOutright));
                continue;
            }

            var reason = RunsOnContent(entry.Key, out var isMergeDriver)
                ? AnalyzeContentCallback(entry.Value, isMergeDriver, programRoots, resolveOnPath, finalPath, fileIdentity)
                : Analyze(entry.Value, isCredentialHelper, programRoots, resolveOnPath, finalPath);
            if (reason is not null)
            {
                refusals.Add(new GitCallbackRefusal(entry.Key, entry.Origin, reason));
            }
        }

        return refusals;
    }

    // The argument lists of the filters git-lfs installs (`git lfs install`, with or without --skip-smudge).
    private static readonly string[][] GitLfsFilterArguments =
    [
        ["clean", "--", "%f"],
        ["smudge", "--", "%f"],
        ["smudge", "--skip", "--", "%f"],
        ["filter-process"],
        ["filter-process", "--skip"],
    ];

    /// <summary>
    /// Whether <paramref name="key"/> names a program git runs on the repository's content during the internal commands
    /// themselves (a filter's clean, smudge or process; a merge driver): on every add, checkout and merge, on files an
    /// agent wrote.
    /// </summary>
    private static bool RunsOnContent(string key, out bool isMergeDriver)
    {
        isMergeDriver = false;
        var firstDot = key.IndexOf('.', StringComparison.Ordinal);
        var lastDot = key.LastIndexOf('.');
        if (firstDot < 0 || lastDot <= firstDot)
        {
            return false;
        }

        var section = key[..firstDot];
        var variable = key[(lastDot + 1)..];
        isMergeDriver = section == "merge" && variable == "driver";
        return isMergeDriver || (section == "filter" && variable is "clean" or "smudge" or "process");
    }

    /// <summary>
    /// The reason a filter or merge driver is refused, or null when it is one of the git-lfs filters (R4-01). A program
    /// outside the repository can still run code from a file of it (MSBuild builds the project it is given or finds, a
    /// tool loads a plugin or a configuration next to the file), and the allowlist cannot tell which programs do: so on
    /// the content only the exact filters git-lfs installs run, and no merge driver does. And only the INSTALLED git-lfs
    /// (G-03): the roots are the folders the repository is known to control, and another linked worktree of the user or
    /// a main working tree with a separate git directory is not among them, so a file named <c>git-lfs.exe</c> there looked
    /// outside the repository. An absolute program must be the very file the bare name <c>git-lfs</c> resolves to; the
    /// roots stay as a second line.
    /// </summary>
    private static string? AnalyzeContentCallback(
        string value,
        bool isMergeDriver,
        IReadOnlyList<string> programRoots,
        Func<string, string?> resolveOnPath,
        Func<string, string?> finalPath,
        Func<string, string?>? fileIdentity)
    {
        const string OnlyGitLfs = "it runs a program on files an agent may have written, and Agent Studio only runs the filters git-lfs installs there (a program outside the repository can still run code from them, as MSBuild does with a project)";
        if (isMergeDriver)
        {
            return "it is a merge driver: " + OnlyGitLfs;
        }

        if (TokenizeStrict(value.Trim(), out var tokens, out var syntaxError) is false)
        {
            return syntaxError;
        }

        var program = tokens[0];
        var isGitLfs = string.Equals(program, "git-lfs", StringComparison.Ordinal)
            || (Path.IsPathRooted(program) && Path.GetFileName(program) is { } name
                && (name.Equals("git-lfs.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("git-lfs", StringComparison.OrdinalIgnoreCase)));
        if (!isGitLfs || !GitLfsFilterArguments.Any(expected => tokens.Skip(1).SequenceEqual(expected, StringComparer.Ordinal)))
        {
            return OnlyGitLfs;
        }

        // The roots first, so a program named or found inside the repository or a worktree is refused for that; then, for an
        // absolute program, its identity. The bare name is the installed git-lfs by definition, found the same way.
        return LocateProgram(program, programRoots, resolveOnPath, finalPath)
            ?? (string.Equals(program, "git-lfs", StringComparison.Ordinal) ? null : NotTheInstalledGitLfs(program, resolveOnPath, finalPath, fileIdentity));
    }

    /// <summary>
    /// G-03: null when the absolute <paramref name="program"/> is the installed git-lfs, the file the bare name
    /// <c>git-lfs</c> resolves to with <paramref name="resolveOnPath"/> (the user's PATH, then the git bundle): the same
    /// final path and, when <paramref name="fileIdentity"/> is given, the same file id (volume and file index). A second
    /// name of that file (a hard link) has another final path and is refused; a link whose final path is that file is the
    /// file. Anything that cannot be resolved or identified fails closed.
    /// </summary>
    private static string? NotTheInstalledGitLfs(string program, Func<string, string?> resolveOnPath, Func<string, string?> finalPath, Func<string, string?>? fileIdentity)
    {
        if (resolveOnPath("git-lfs") is not { } installed)
        {
            return $"its program '{Display(program)}' is named git-lfs, but no git-lfs is installed (the bare name resolves nowhere), so it cannot be confirmed to be the installed git-lfs";
        }

        var sameFinalPath = finalPath(program) is { } programFinal && finalPath(installed) is { } installedFinal
            && Normalize(programFinal) is { } left && Normalize(installedFinal) is { } right && GitNaming.SamePath(left, right);
        var sameFile = sameFinalPath
            && (fileIdentity is null || (fileIdentity(program) is { } programId && fileIdentity(installed) is { } installedId
                && string.Equals(programId, installedId, StringComparison.Ordinal)));
        return sameFile
            ? null
            : $"its program '{Display(program)}' is named git-lfs but is not the installed git-lfs ('{Display(installed)}', what the bare name resolves to): on the content Agent Studio only runs that file, the same by its final path and its file id";
    }

    /// <summary>
    /// The full path of a callback program named as a bare name: first the user's PATH (<paramref name="locator"/>),
    /// then the directories of the git bundle (the <c>sh</c> git uses finds <c>git-lfs</c>, <c>astextplain</c> and the
    /// credential manager there, and they are not always on the user's PATH). Null when it resolves nowhere.
    /// </summary>
    public static string? ResolveProgramOnPath(IExecutableLocator locator, string? gitExecutable, string name)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (locator.Find(name) is { } onPath)
        {
            return onPath;
        }

        if (string.IsNullOrWhiteSpace(gitExecutable))
        {
            return null;
        }

        string? gitDir;
        try
        {
            gitDir = Path.GetDirectoryName(Path.GetFullPath(gitExecutable));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (gitDir is null)
        {
            return null;
        }

        // Git for Windows has git.exe in cmd\, bin\ and mingw64\bin\: the bundle root is one or two levels up, so the
        // tool folders are looked for under both (from Git Bash the first git is mingw64\bin\git.exe). Never higher: from
        // cmd\git.exe a third level is the drive root, where any user can create a bin folder.
        var directories = new List<string> { gitDir };
        var parent = Path.GetDirectoryName(gitDir);
        foreach (var root in new[] { parent, parent is null ? null : Path.GetDirectoryName(parent) }.OfType<string>())
        {
            directories.Add(Path.Combine(root, "mingw64", "bin"));
            directories.Add(Path.Combine(root, "mingw32", "bin"));
            directories.Add(Path.Combine(root, "usr", "bin"));
            directories.Add(Path.Combine(root, "bin"));
        }

        foreach (var directory in directories)
        {
            foreach (var candidate in new[] { Path.Combine(directory, name), Path.Combine(directory, name + ".exe") })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
                {
                    // Not a path we can probe; try the next one.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The path Windows reaches for <paramref name="path"/> with every junction and symbolic link resolved
    /// (<c>GetFinalPathNameByHandle</c>), or null when it cannot be opened: a program named by an absolute path that is a
    /// link into the roots must be seen for what it really is. Fails closed (null) so an unopenable program is refused.
    /// </summary>
    public static string? ResolveFinalPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return IntegrityLabels.FinalPath(path);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // FILE_READ_ATTRIBUTES: enough to read the file id, without the right to read the content.
    private const uint FileReadAttributes = 0x0080;

    /// <summary>
    /// The identity of the file <paramref name="path"/> reaches with every link followed: on Windows its volume serial
    /// number and file index (<c>GetFileInformationByHandle</c>), elsewhere its final path. Null when it cannot be opened,
    /// so a program that cannot be identified is refused (G-03).
    /// </summary>
    public static string? ResolveFileIdentity(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ResolveFinalPath(path);
        }

        using var handle = IsolationNativeMethods.CreateFile(
            path, FileReadAttributes, IsolationNativeMethods.FileShareAll, 0, IsolationNativeMethods.OpenExisting, IsolationNativeMethods.FileFlagBackupSemantics, 0);
        if (handle.IsInvalid || !IsolationNativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            return null;
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{information.VolumeSerialNumber:x8}-{information.FileIndexHigh:x8}{information.FileIndexLow:x8}");
    }

    /// <summary>
    /// Whether <paramref name="key"/> names a callback that runs a program, and whether it is a credential helper
    /// (<c>!</c> and bare names are special there). <paramref name="refusedOutright"/> is the reason when the key is
    /// refused whatever its value: a git-lfs extension or custom transfer (A-03), a remote helper or a partial clone (H-04).
    /// </summary>
    private static bool IsCallbackKey(string key, out bool isCredentialHelper, out string? refusedOutright)
    {
        isCredentialHelper = false;
        refusedOutright = null;
        if (key is null)
        {
            return false;
        }

        // section.(subsection.)variable; the subsection may hold dots (credential URLs), so split off the first and last.
        var firstDot = key.IndexOf('.', StringComparison.Ordinal);
        if (firstDot < 0)
        {
            return false;
        }

        var section = key[..firstDot];
        var lastDot = key.LastIndexOf('.');
        var variable = key[(lastDot + 1)..];
        var hasSubsection = lastDot > firstDot;

        // Only keys whose value is unambiguously a program are classified. core.fsmonitor (a boolean or a hook, already
        // forced false with -c), core.pager/pager.* (booleans or a pager that never runs with GIT_PAGER=cat on a
        // non-tty), sendemail.* (emails and hosts) and alias.* (git sub-commands, never dispatched by an internal
        // command) are left out: classifying their value as a program would refuse a legitimate repository.
        switch (section)
        {
            case "filter":
                return hasSubsection && variable is "clean" or "smudge" or "process";
            case "merge":
                return hasSubsection && variable is "driver";
            case "diff":
                return variable is "textconv" or "command" ? hasSubsection : !hasSubsection && variable is "external";
            case "gpg":
                // gpg.program, gpg.<format>.program and gpg.ssh.defaultKeyCommand all name a program git runs.
                return variable is "program" || (hasSubsection && variable is "defaultkeycommand");
            case "credential":
                isCredentialHelper = variable is "helper";
                return isCredentialHelper;
            case "core":
                return !hasSubsection && variable is "sshcommand" or "editor" or "askpass" or "gitproxy" or "alternaterefscommand";
            case "sequence":
                return !hasSubsection && variable is "editor";
            case "lfs":
                // git-lfs runs the program these keys name: an extension's clean/smudge (on content), a custom
                // transfer's path or the standalone transfer agent (on a transfer). Measured honoured from the git
                // config with git-lfs 3.5.1 (A-03). lfs.storage is the folder git-lfs writes the objects into (P-03):
                // measured with 3.5.1 offline, absolute or relative (to the common directory), from the repository's
                // config and from a worktree's config.worktree, it made `git add` write the object outside the
                // repository; from .lfsconfig it is ignored as unsafe. No other lfs.* key of the 3.5.1 binary names a
                // path git-lfs writes to. Other lfs.* keys (url, fetchinclude, concurrenttransfers,
                // repositoryformatversion, lfs.<url>.access, a custom transfer's args) name no program or destination.
                var subsection = hasSubsection ? key[(firstDot + 1)..lastDot] : string.Empty;
                var isGitLfsProgram = hasSubsection
                    ? (subsection.StartsWith("extension.", StringComparison.Ordinal) && variable is "clean" or "smudge")
                        || (subsection.StartsWith("customtransfer.", StringComparison.Ordinal) && variable is "path")
                    : variable is "standalonetransferagent";
                refusedOutright = isGitLfsProgram ? GitLfsProgramReason
                    : !hasSubsection && variable is "storage" ? GitLfsStorageReason
                    : null;
                return refusedOutright is not null;
            case "remote":
                // remote.<name>.vcs makes git run git-remote-<value> for any transport of that remote (measured with git
                // 2.45.2: the lazy fetch of a partial clone ran it). remote.<name>.promisor and
                // remote.<name>.partialclonefilter make the repository a partial clone, whose missing objects git fetches
                // from inside ordinary commands (H-04). Measured: a partialclonefilter alone, even an empty one, makes
                // the remote a promisor, also next to promisor=false. promisor=false alone does not, but a valueless
                // promisor (true) and an empty one (false) both reach here as "", so the key is refused whatever its value.
                refusedOutright = !hasSubsection ? null
                    : variable is "vcs" ? RemoteHelperReason
                    : variable is "promisor" or "partialclonefilter" ? PartialCloneReason
                    : null;
                return refusedOutright is not null || (hasSubsection && variable is "uploadpack" or "receivepack");
            case "extensions":
                // extensions.partialClone names the promisor remote of a partial clone too: set alone, it made git fetch
                // lazily (measured with git 2.45.2, whose own clone writes remote.<name>.promisor instead) (H-04).
                refusedOutright = !hasSubsection && variable is "partialclone" ? PartialCloneReason : null;
                return refusedOutright is not null;
            case "uploadpack":
                return !hasSubsection && variable is "packobjectshook";
            default:
                return false;
        }
    }

    /// <summary>
    /// The reason a callback value is refused, or null when it is accepted by the allowlist. A value is accepted only
    /// when every token is in the safe charset (the first token may be one quoted absolute path), the first token is
    /// neither an interpreter nor a builtin, and the program resolves to a file whose final path is outside the roots.
    /// </summary>
    private static string? Analyze(string value, bool isCredentialHelper, IReadOnlyList<string> programRoots, Func<string, string?> resolveOnPath, Func<string, string?> finalPath)
    {
        var command = value.Trim();

        // A credential helper with a leading '!' is a shell command; a bare name is git-credential-<name> on the PATH.
        if (isCredentialHelper)
        {
            if (command.StartsWith('!'))
            {
                command = command[1..].TrimStart();
            }
            else if (!command.Contains('/', StringComparison.Ordinal)
                && !command.Contains('\\', StringComparison.Ordinal)
                && !command.Contains(' ', StringComparison.Ordinal))
            {
                // git prepends "git-credential-": a PATH lookup the agent cannot alter.
                return null;
            }
        }

        if (command.Length == 0)
        {
            return null;
        }

        if (TokenizeStrict(command, out var tokens, out var syntaxError) is false)
        {
            return syntaxError;
        }

        var program = tokens[0];
        if (IsAssignment(program))
        {
            return "its value sets an environment variable before the program";
        }

        if (IsBuiltin(program))
        {
            return $"its program '{Display(program)}' is a shell builtin, which runs an arbitrary command";
        }

        if (IsInterpreter(program))
        {
            return $"its program '{Display(program)}' is an interpreter or shell, which runs code from its arguments";
        }

        return LocateProgram(program, programRoots, resolveOnPath, finalPath);
    }

    private static bool IsBuiltin(string program) => Builtins.Contains(program);

    // A character a callback value may contain outside quotes: letters, digits and the punctuation of a plain program
    // name, path or git placeholder (%f, %O...). Everything else (a shell metacharacter, a space, a quote) is handled
    // apart or refused, so no `!`, `$`, backtick, `;`, `|`, `&`, `<`, `>`, `(`, `)`, `{`, `}`, `*`, `?`, `~`, `#` or `@`.
    private static bool IsCharsetChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '%' or '_' or '.' or ',' or ':' or '=' or '/' or '\\' or '+' or '-';

    /// <summary>
    /// Splits <paramref name="command"/> into tokens and validates them as a plain command: every token is in the safe
    /// charset, except that the FIRST token may be wrapped in one pair of quotes (single or double) and then hold spaces
    /// (an absolute path such as <c>'C:\Program Files\tool\clean.exe'</c>). Any other quote, a quote inside a later
    /// token, or any character outside the charset makes it unanalysable and is refused. False with the reason on refusal.
    /// </summary>
    private static bool TokenizeStrict(string command, out List<string> tokens, out string? error)
    {
        tokens = [];
        error = null;
        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && command[index] == ' ')
            {
                index++;
            }

            if (index >= command.Length)
            {
                break;
            }

            var isFirst = tokens.Count == 0;
            var c = command[index];
            if (c is '\'' or '"')
            {
                if (!isFirst)
                {
                    error = "it quotes an argument other than the program, which cannot be checked";
                    return false;
                }

                var builder = new StringBuilder();
                index++;
                var closed = false;
                while (index < command.Length)
                {
                    var inner = command[index];
                    if (inner == c)
                    {
                        closed = true;
                        index++;
                        break;
                    }

                    if (inner != ' ' && !IsCharsetChar(inner))
                    {
                        error = $"its program contains '{Describe(inner)}', which is not allowed";
                        return false;
                    }

                    builder.Append(inner);
                    index++;
                }

                if (!closed)
                {
                    error = "its value has an unbalanced quote";
                    return false;
                }

                if (index < command.Length && command[index] != ' ')
                {
                    error = "it mixes a quote with more text in one token, which cannot be checked";
                    return false;
                }

                tokens.Add(builder.ToString());
            }
            else
            {
                var start = index;
                while (index < command.Length && command[index] != ' ')
                {
                    var inner = command[index];
                    if (inner is '\'' or '"')
                    {
                        error = "it quotes inside a token, which cannot be checked";
                        return false;
                    }

                    if (!IsCharsetChar(inner))
                    {
                        error = $"its value contains '{Describe(inner)}', which is not allowed";
                        return false;
                    }

                    index++;
                }

                tokens.Add(command[start..index]);
            }
        }

        if (tokens.Count == 0)
        {
            error = "its value is empty";
            return false;
        }

        return true;
    }

    // One character for a message, naming a control character by code so the error reads cleanly.
    private static string Describe(char c) => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString();

    /// <summary>
    /// The reason a resolved program is refused, or null when it is a file outside the roots the agent cannot alter: a
    /// bare name is resolved on the PATH and the git bundle; an absolute path is taken as is; a relative path is refused.
    /// Whatever it resolves to, its FINAL path (links resolved) must be outside the roots, or it is refused (fail closed).
    /// </summary>
    private static string? LocateProgram(string program, IReadOnlyList<string> roots, Func<string, string?> resolveOnPath, Func<string, string?> finalPath)
    {
        var hasSeparator = program.Contains('/', StringComparison.Ordinal) || program.Contains('\\', StringComparison.Ordinal);
        bool rooted;
        try
        {
            rooted = Path.IsPathRooted(program);
        }
        catch (ArgumentException)
        {
            rooted = false;
        }

        if (!rooted && !hasSeparator)
        {
            if (resolveOnPath(program) is not { } resolved)
            {
                return $"its program '{Display(program)}' is not on the PATH, so it cannot be confirmed to be outside the repository and worktrees";
            }

            return OutsideRoots(resolved, roots, finalPath)
                ? null
                : $"its program '{Display(program)}' resolves to a file inside the repository or a worktree";
        }

        if (!rooted)
        {
            return $"its program '{Display(program)}' is a relative path, which resolves inside the repository or worktree";
        }

        return OutsideRoots(program, roots, finalPath)
            ? null
            : $"its program '{Display(program)}' is a path inside the repository or a worktree, or it does not exist";
    }

    // True only when an existing file is strictly outside every protected root, both by its final path and by the path it
    // was named with: what runs is decided by that name too, so a program named inside the repository is refused even when
    // a link inside the repository leads it out. A program that cannot be opened (missing, or an unreadable link) resolves
    // to null and is treated as inside: the callback fails closed.
    private static bool OutsideRoots(string path, IReadOnlyList<string> roots, Func<string, string?> finalPath)
    {
        if (finalPath(path) is not { } resolved || Normalize(resolved) is not { } full || Normalize(path) is not { } named)
        {
            return false;
        }

        return !roots.Any(root => GitNaming.SamePath(full, root) || GitNaming.IsUnder(full, root)
            || GitNaming.SamePath(named, root) || GitNaming.IsUnder(named, root));
    }

    /// <summary>
    /// The roots by the path each was given with and by its final path (X-01): the program is judged by its final path,
    /// with every link resolved, so a root named another way (a junction, a short name, a substituted drive) must be
    /// resolved the same way, or a program inside it looks outside. A root that cannot be resolved (it does not exist yet)
    /// keeps only its path.
    /// </summary>
    private static string[] WithFinalPaths(IReadOnlyList<string> roots, Func<string, string?> finalPath)
    {
        var all = new List<string>(roots.Count * 2);
        foreach (var root in roots)
        {
            if (Normalize(root) is not { } named)
            {
                continue;
            }

            all.Add(named);
            if (finalPath(named) is { } resolved && Normalize(resolved) is { } physical)
            {
                all.Add(physical);
            }
        }

        return [.. all.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? Normalize(string path)
    {
        try
        {
            return GitNaming.NormalizePath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsInterpreter(string program)
    {
        var name = program;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return Interpreters.Contains(name);
    }

    private static bool IsAssignment(string token)
    {
        var equals = token.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return false;
        }

        for (var i = 0; i < equals; i++)
        {
            var c = token[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return !char.IsAsciiDigit(token[0]);
    }

    private static bool OriginInsideWorktree(string origin, string worktreeRoot, string workingDirectory)
    {
        const string prefix = "file:";
        if (!origin.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var path = origin[prefix.Length..];
        if (path.Length == 0)
        {
            return false;
        }

        try
        {
            var full = Path.IsPathRooted(path) ? GitNaming.NormalizePath(path) : GitNaming.NormalizePath(Path.Combine(workingDirectory, path));
            return GitNaming.SamePath(full, worktreeRoot) || GitNaming.IsUnder(full, worktreeRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // A program or argument as named in a message, trimmed so a credential token or a long path never fills the error.
    private static string Display(string token)
    {
        const int max = 80;
        return token.Length <= max ? token : token[..max] + "...";
    }
}
