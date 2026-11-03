using System.IO;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// The pure allowlist behind N-01/R3-01 (<see cref="GitCallbacks.Classify"/>): a callback is accepted only when every
/// token is in the safe charset (the first token may be one quoted absolute path), the program is neither an interpreter
/// nor a shell builtin, and it resolves to a file outside the repository and the worktrees. A relative program, one under
/// those roots, an interpreter, a builtin, shell syntax or a configuration file inside a worktree is refused. No git here:
/// the PATH resolution and the final-path resolution are injected, except in the live-machine test, which uses the real
/// ones so this machine's own configuration is checked.
/// </summary>
public sealed class GitCallbackClassifierTests
{
    private const string WorktreeRoot = @"C:\wt";
    private const string RepoRoot = @"C:\proyecto";

    // A bare program name resolves to a fixed path outside the roots when "installed"; an absolute path is judged where
    // it points (the final path is the identity here). The live-machine test below uses the real resolver and final path.
    private static readonly Dictionary<string, string> Installed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git-lfs"] = @"C:\Program Files\Git\cmd\git-lfs.exe",
        ["astextplain"] = @"C:\Program Files\Git\usr\bin\astextplain",
        ["my-merge-tool"] = @"C:\tools\my-merge-tool.exe",
        ["exiftool"] = @"C:\tools\exiftool.exe",
        ["gpg"] = @"C:\Program Files\GnuPG\bin\gpg.exe",
    };

    private static readonly ExecutableLocator LiveLocator = new();
    private static readonly string? LiveGit = new ExecutableLocator().Find("git");

    private static string? FakeResolve(string name) => Installed.TryGetValue(name, out var path) ? path : null;

    private static string? Identity(string path) => path;

    private static IReadOnlyList<GitCallbackRefusal> Classify(string key, string value, string origin = "file:C:/proyecto/.git/config") =>
        GitCallbacks.Classify(
            [new GitCallbacks.ConfigEntry("local", origin, key, value)],
            [WorktreeRoot, RepoRoot],
            WorktreeRoot,
            RepoRoot,
            FakeResolve,
            Identity);

    private static bool Allowed(string key, string value, string origin = "file:C:/proyecto/.git/config") => Classify(key, value, origin).Count == 0;

    // ---- the live configuration of the development machine must all be allowed, or the whole suite goes red ----
    // These use the real PATH + git bundle resolver and the real final-path resolution, so they check this machine.

    private static bool AllowedLive(string key, string value) =>
        GitCallbacks.Classify(
            [new GitCallbacks.ConfigEntry("system", "file:C:/Program Files/Git/etc/gitconfig", key, value)],
            [WorktreeRoot, RepoRoot],
            WorktreeRoot,
            RepoRoot,
            name => GitCallbacks.ResolveProgramOnPath(LiveLocator, LiveGit, name),
            GitCallbacks.ResolveFinalPath,
            GitCallbacks.ResolveFileIdentity).Count == 0;

    public static TheoryData<string, string> LiveMachineConfig() => new()
    {
        { "filter.lfs.clean", "git-lfs clean -- %f" },
        { "filter.lfs.smudge", "git-lfs smudge -- %f" },
        { "filter.lfs.process", "git-lfs filter-process" },
        { "filter.lfs.required", "true" },
        { "diff.astextplain.textconv", "astextplain" },
        { "credential.helper", "manager" },
        { "credential.https://dev.azure.com.usehttppath", "true" },
        { "credential.https://github.com.helper", "!'C:\\Program Files\\GitHub CLI\\gh.exe' auth git-credential" },
        { "credential.https://github.com.helper", "" },
        { "http.sslbackend", "openssl" },
        { "core.autocrlf", "true" },
        { "init.defaultbranch", "master" },
    };

    [Theory]
    [MemberData(nameof(LiveMachineConfig))]
    public void The_live_configuration_of_this_machine_is_allowed(string key, string value)
    {
        Assert.True(AllowedLive(key, value), $"{key} = {value} was refused.");
    }

    // ---- programs outside the roots are allowed ----

    [Theory]
    [InlineData("diff.bin.textconv", "exiftool")]
    [InlineData("core.sshcommand", "'C:/Program Files/OpenSSH/ssh.exe'")]
    [InlineData("gpg.program", "gpg")]
    public void A_program_outside_the_repository_and_worktrees_is_allowed(string key, string value)
    {
        Assert.True(Allowed(key, value), $"{key} = {value} was refused.");
    }

    // ---- what runs on the repository's content: only git-lfs (R4-01) ----
    // A filter or a merge driver runs on every internal add, checkout or merge, on files an agent wrote. A program outside
    // the repository can still run code from one of them (MSBuild builds a project, a tool loads a plugin), so only the
    // git-lfs forms git-lfs itself installs are accepted.

    [Theory]
    [InlineData("filter.lfs.clean", "git-lfs clean -- %f")]
    [InlineData("filter.lfs.smudge", "git-lfs smudge -- %f")]
    [InlineData("filter.lfs.smudge", "git-lfs smudge --skip -- %f")]
    [InlineData("filter.lfs.process", "git-lfs filter-process")]
    [InlineData("filter.lfs.process", "git-lfs filter-process --skip")]
    [InlineData("filter.lfs.process", "'C:\\Program Files\\Git\\cmd\\git-lfs.exe' filter-process")] // the installed one (G-03)
    [InlineData("filter.lfs.clean", "'C:/Program Files/Git/cmd/git-lfs.exe' clean -- %f")]
    public void The_git_lfs_filters_are_allowed(string key, string value)
    {
        Assert.True(Allowed(key, value), $"{key} = {value} was refused.");
    }

    // ---- G-03: an absolute git-lfs must be the installed one ----
    // A content callback is the only one an internal command runs, and the allowlist judged its program by where it is:
    // outside the roots it knows. Another linked worktree of the user, or a main working tree with a separate git
    // directory, is not among them, so a file named git-lfs.exe there was accepted. Now an absolute program must be, by
    // its final path and its file id, the same file the bare name git-lfs resolves to (the PATH, then the git bundle).

    [Theory]
    [InlineData("filter.lfs.process", "'C:\\Program Files\\Git LFS\\git-lfs.exe' filter-process")]
    [InlineData("filter.x.process", "'D:\\usuario\\otro-worktree\\tools\\git-lfs.exe' filter-process")]
    [InlineData("filter.x.clean", "C:/tools/git-lfs clean -- %f")]
    [InlineData("filter.x.smudge", "'C:\\Program Files\\Git\\mingw64\\bin\\git-lfs.exe' smudge -- %f")] // the bundle's own copy: not the one the name resolves to
    public void An_absolute_git_lfs_that_is_not_the_installed_one_is_refused(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("installed", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absolute_git_lfs_is_refused_when_no_git_lfs_is_installed()
    {
        var refusals = GitCallbacks.Classify(
            [new GitCallbacks.ConfigEntry("local", "file:C:/proyecto/.git/config", "filter.lfs.process", "'C:\\Program Files\\Git\\cmd\\git-lfs.exe' filter-process")],
            [WorktreeRoot, RepoRoot],
            WorktreeRoot,
            RepoRoot,
            name => name == "git-lfs" ? null : FakeResolve(name),
            Identity);

        Assert.Contains("installed", Assert.Single(refusals).Reason, StringComparison.Ordinal);
    }

    private static IReadOnlyList<GitCallbackRefusal> ClassifyWithIdentity(string value, IReadOnlyDictionary<string, string> finalPaths, IReadOnlyDictionary<string, string> fileIds) =>
        GitCallbacks.Classify(
            [new GitCallbacks.ConfigEntry("local", "file:C:/proyecto/.git/config", "filter.lfs.process", value)],
            [WorktreeRoot, RepoRoot],
            WorktreeRoot,
            RepoRoot,
            FakeResolve,
            path => finalPaths.TryGetValue(path, out var final) ? final : path,
            path => fileIds.TryGetValue(path, out var id) ? id : null);

    private static readonly Dictionary<string, string> InstalledFileId = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Program Files\Git\cmd\git-lfs.exe"] = "vol1-0001",
    };

    [Fact]
    public void A_link_whose_final_path_and_file_id_are_the_installed_git_lfs_is_allowed()
    {
        var finalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\tools\git-lfs.exe"] = @"C:\Program Files\Git\cmd\git-lfs.exe" };
        var fileIds = new Dictionary<string, string>(InstalledFileId, StringComparer.OrdinalIgnoreCase) { [@"C:\tools\git-lfs.exe"] = "vol1-0001" };

        Assert.Empty(ClassifyWithIdentity(@"'C:\tools\git-lfs.exe' filter-process", finalPaths, fileIds));
    }

    [Fact]
    public void The_same_final_path_with_another_file_id_is_refused()
    {
        // The file a path reaches changed between the two lookups, or the final path lies: the file id must agree too.
        var finalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\tools\git-lfs.exe"] = @"C:\Program Files\Git\cmd\git-lfs.exe" };
        var fileIds = new Dictionary<string, string>(InstalledFileId, StringComparer.OrdinalIgnoreCase) { [@"C:\tools\git-lfs.exe"] = "vol1-0002" };

        Assert.Contains("installed", Assert.Single(ClassifyWithIdentity(@"'C:\tools\git-lfs.exe' filter-process", finalPaths, fileIds)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_name_of_the_installed_git_lfs_is_refused_by_its_final_path()
    {
        // A hard link has the same file id but its own final path: it is not the installed program by name.
        var fileIds = new Dictionary<string, string>(InstalledFileId, StringComparer.OrdinalIgnoreCase) { [@"C:\tools\git-lfs.exe"] = "vol1-0001" };

        Assert.Contains("installed", Assert.Single(ClassifyWithIdentity(@"'C:\tools\git-lfs.exe' filter-process", new Dictionary<string, string>(), fileIds)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_whose_id_cannot_be_read_is_refused()
    {
        var finalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"C:\tools\git-lfs.exe"] = @"C:\Program Files\Git\cmd\git-lfs.exe" };

        Assert.Contains("installed", Assert.Single(ClassifyWithIdentity(@"'C:\tools\git-lfs.exe' filter-process", finalPaths, InstalledFileId)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installed_git_lfs_by_its_absolute_path_is_allowed_on_this_machine()
    {
        Assert.SkipUnless(LiveLocator.Find("git-lfs") is not null, "git-lfs is not installed.");
        var installed = GitCallbacks.ResolveProgramOnPath(LiveLocator, LiveGit, "git-lfs")!;

        Assert.True(AllowedLive("filter.lfs.process", "'" + installed.Replace('\\', '/') + "' filter-process"), $"the installed git-lfs at '{installed}' was refused.");
    }

    [Theory]
    [InlineData("filter.format.clean", "'C:\\Program Files\\Microsoft Visual Studio\\MSBuild\\Current\\Bin\\MSBuild.exe' tools/format.proj")]
    [InlineData("filter.format.clean", "'C:\\Program Files\\Microsoft Visual Studio\\MSBuild\\Current\\Bin\\MSBuild.exe'")]
    [InlineData("filter.format.clean", "'C:\\Program Files\\tool\\clean.exe' --stdin")]
    [InlineData("filter.format.smudge", "exiftool -all= -")]
    [InlineData("filter.lfs.clean", "git-lfs clean --config tools/x -- %f")]
    [InlineData("filter.lfs.clean", "git-lfs")]
    [InlineData("merge.custom.driver", "my-merge-tool %O %A %B")]
    [InlineData("merge.lfs.driver", "git-lfs merge-driver %O %A %B")]
    public void Any_other_filter_or_merge_driver_is_refused_even_outside_the_repository(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("git-lfs", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- git-lfs transitive callbacks: extensions and custom transfers are refused outright (A-03) ----
    // Measured with git-lfs 3.5.1: an extension's clean in the repository's git config runs on `git add`; a custom
    // transfer and the standalone transfer agent register and would run on a transfer. None goes through the allowlist;
    // only the base git-lfs filters (filter.lfs.*) run.

    [Theory]
    [InlineData("lfs.extension.foo.clean", "git-lfs clean")] // even a program named git-lfs: no extension at all
    [InlineData("lfs.extension.foo.smudge", "some-filter -")]
    [InlineData("lfs.customtransfer.agent.path", "C:/tools/agent.exe")]
    [InlineData("lfs.standalonetransferagent", "agent")]
    public void A_git_lfs_extension_or_custom_transfer_is_refused(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("git-lfs", refusal.Reason, StringComparison.Ordinal);
    }

    // A-03, literal gap: the key is judged before its value, so an empty value is no way around the refusal.
    [Theory]
    [InlineData("lfs.extension.foo.clean", "")]
    [InlineData("lfs.extension.foo.smudge", "  ")]
    [InlineData("lfs.customtransfer.agent.path", "")]
    [InlineData("lfs.standalonetransferagent", "")]
    public void A_git_lfs_program_key_is_refused_even_with_an_empty_value(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("git-lfs", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- a remote helper or a partial clone chosen by the configuration: refused whatever the value (H-04) ----
    // remote.<name>.vcs makes git run git-remote-<value> for any transport of that remote. A promisor remote makes git
    // fetch missing objects on demand from inside ordinary commands (measured with git 2.45.2: the lazy fetch of an
    // internal diff, archive or reset --hard ran the remote helper the repository chose).

    [Theory]
    [InlineData("remote.origin.vcs", "sentinel")]
    [InlineData("remote.origin.vcs", "")]
    [InlineData("remote.origin.vcs", " ")]
    public void A_remote_helper_chosen_by_the_configuration_is_refused_whatever_its_value(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("remote helper", refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remote.origin.promisor", "true")]
    [InlineData("remote.origin.promisor", "false")] // fail closed: a valueless key (true) also reaches here as ""
    [InlineData("remote.origin.promisor", "")]
    [InlineData("remote.origin.partialclonefilter", "blob:none")]
    [InlineData("remote.origin.partialclonefilter", "")] // measured: an empty filter alone still made git fetch lazily
    [InlineData("extensions.partialclone", "origin")]
    [InlineData("extensions.partialclone", "")]
    public void A_partial_clone_or_a_promisor_remote_is_refused_whatever_its_value(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("partial clone", refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remote.origin.url", "https://example.com/repo.git")]
    [InlineData("remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*")]
    [InlineData("remote.origin.pushurl", "")]
    [InlineData("extensions.worktreeconfig", "true")]
    [InlineData("extensions.objectformat", "sha256")]
    public void A_remote_or_extension_key_that_names_no_program_is_not_classified(string key, string value)
    {
        Assert.True(Allowed(key, value), $"{key} = {value} was refused.");
    }

    [Theory]
    [InlineData("remote.origin.vcs")]
    [InlineData("remote.origin.promisor")]
    [InlineData("remote.origin.partialclonefilter")]
    [InlineData("extensions.partialclone")]
    public void The_configuration_query_asks_git_for_the_keys_of_a_remote_helper_and_a_partial_clone(string key)
    {
        Assert.Matches(GitCallbacks.Pattern, key);
    }

    // ---- P-03: where git-lfs writes is not the configuration's to choose ----
    // Measured with git-lfs 3.5.1: lfs.storage in the repository's git config, absolute or relative (to the common
    // directory) and also in a worktree's config.worktree, made `git add` write the object outside the repository; in
    // .lfsconfig it is ignored ("unsafe"). No other lfs.* key of the binary names a path git-lfs writes to.

    [Theory]
    [InlineData("lfs.storage", "C:/fuera/lfs")]
    [InlineData("lfs.storage", "../../fuera")]
    [InlineData("lfs.storage", "lfs")]
    [InlineData("lfs.storage", "")]
    public void The_git_lfs_storage_is_refused_whatever_its_value(string key, string value)
    {
        var refusal = Assert.Single(Classify(key, value));
        Assert.Contains("git-lfs", refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lfs.url", "https://example.com/repo.git/info/lfs")]
    [InlineData("lfs.fetchinclude", "*.dat")]
    [InlineData("lfs.fetchexclude", "*.big")]
    [InlineData("lfs.concurrenttransfers", "8")]
    [InlineData("lfs.repositoryformatversion", "0")]
    [InlineData("lfs.https://example.com.access", "basic")]
    [InlineData("lfs.customtransfer.agent.args", "--run")] // args alone names no program; only .path does
    [InlineData("lfs.extension.foo.priority", "0")]
    public void A_non_program_lfs_key_is_not_classified(string key, string value)
    {
        Assert.True(Allowed(key, value), $"{key} = {value} was refused.");
    }

    // ---- a bare name that resolves nowhere cannot be confirmed to be outside, so it is refused (fail closed) ----

    [Fact]
    public void A_bare_program_that_is_not_installed_is_refused_because_it_cannot_be_located()
    {
        Assert.NotEmpty(Classify("filter.format.clean", "no-such-tool --stdin"));
    }

    // ---- programs the repository or a worktree controls are refused ----

    [Theory]
    [InlineData("filter.format.clean", "tools/filter.sh")]
    [InlineData("filter.format.clean", "./clean.sh")]
    [InlineData("filter.format.smudge", ".git\\hooks\\smudge.bat")]
    [InlineData("merge.custom.driver", @"C:\proyecto\tools\merge.sh")]
    [InlineData("merge.custom.driver", @"C:\wt\abcd\run\T1\evil.exe")]
    [InlineData("diff.ext.command", "scripts/diff.py")]
    public void A_program_that_is_relative_or_under_the_roots_is_refused(string key, string value)
    {
        Assert.NotEmpty(Classify(key, value));
    }

    // ---- the roots and the program are compared by their final paths (X-01) ----
    // The program's final path has every link resolved (GetFinalPathNameByHandle). A root named another way (a junction,
    // a short name, a substituted drive) must be resolved the same way, or a program inside it looks outside.

    private static IReadOnlyList<GitCallbackRefusal> ClassifyWithFinalPaths(string key, string value, IReadOnlyList<string> roots, IReadOnlyDictionary<string, string> finalPaths) =>
        GitCallbacks.Classify(
            [new GitCallbacks.ConfigEntry("local", "file:C:/proyecto/.git/config", key, value)],
            roots,
            WorktreeRoot,
            RepoRoot,
            FakeResolve,
            path => finalPaths.TryGetValue(path, out var final) ? final : path);

    [Fact]
    public void A_program_inside_a_root_named_through_a_link_is_refused_by_the_roots_final_path()
    {
        // The repository root reaches its files through a junction: its final path is another folder, where the program is.
        var finalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\alias\proyecto"] = @"D:\fisico\proyecto",
        };

        var refusal = Assert.Single(ClassifyWithFinalPaths(
            "filter.x.process",
            @"'D:\fisico\proyecto\tools\git-lfs.exe' filter-process",
            [WorktreeRoot, @"C:\alias\proyecto"],
            finalPaths));

        Assert.Contains("inside the repository or a worktree", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_named_inside_a_root_is_refused_even_when_its_final_path_is_outside()
    {
        // tools\ is a link out of the repository: what runs is decided by a name the repository controls.
        var finalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\proyecto\tools\git-lfs.exe"] = @"C:\Program Files\Git LFS\git-lfs.exe",
        };

        var refusal = Assert.Single(ClassifyWithFinalPaths(
            "filter.x.process",
            @"'C:\proyecto\tools\git-lfs.exe' filter-process",
            [WorktreeRoot, RepoRoot],
            finalPaths));

        Assert.Contains("inside the repository or a worktree", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- interpreters and shells are refused outright, whatever their arguments ----

    [Theory]
    [InlineData("filter.format.clean", "sh tools/filter.sh")]
    [InlineData("filter.format.clean", "bash filter")]
    [InlineData("filter.format.clean", "powershell.exe -File tools\\filter.ps1")]
    [InlineData("merge.custom.driver", "python merge.py %O %A %B")]
    [InlineData("filter.format.clean", @"sh C:\tools\clean.sh --stdin")]
    [InlineData("filter.format.clean", @"C:\Python\python.exe -c import-os")]
    public void An_interpreter_or_shell_is_refused_whatever_its_arguments(string key, string value)
    {
        Assert.NotEmpty(Classify(key, value));
    }

    // ---- shell builtins and shell syntax are refused (the denylist bypass R3-01 closes) ----

    [Theory]
    [InlineData("filter.format.clean", "exec ./tools/filter.sh")]
    [InlineData("filter.format.clean", "exec /usr/bin/payload")]
    [InlineData("filter.format.clean", ". ./tools/filter.sh")]
    [InlineData("filter.format.clean", "eval payload")]
    [InlineData("filter.format.clean", "command git-lfs clean")]
    public void A_shell_builtin_as_the_program_is_refused(string key, string value)
    {
        Assert.NotEmpty(Classify(key, value));
    }

    // ---- values that cannot be analysed safely are refused ----

    [Theory]
    [InlineData("filter.format.clean", "! ./tools/filter.sh")]
    [InlineData("filter.format.clean", "clean | tee /tmp/x")]
    [InlineData("filter.format.clean", "sh -c \"$(cat payload)\"")]
    [InlineData("filter.format.clean", "clean && evil")]
    [InlineData("filter.format.clean", "VAR=1 git-lfs clean")]
    [InlineData("filter.format.clean", "git-lfs; evil")]
    [InlineData("filter.format.clean", "git-lfs `evil`")]
    [InlineData("filter.format.clean", "clean 'C:\\x\\y.exe'")]
    public void A_value_with_shell_syntax_an_assignment_or_a_misplaced_quote_is_refused(string key, string value)
    {
        Assert.NotEmpty(Classify(key, value));
    }

    [Fact]
    public void An_interpreter_running_an_absolute_script_outside_the_roots_is_now_refused_too()
    {
        // Previously allowed: an interpreter is now refused outright, because its arguments can name a repository script
        // or carry inline code (`-c`), which the per-token analysis cannot safely rule out.
        Assert.NotEmpty(Classify("filter.format.clean", @"sh C:\tools\clean.sh --stdin"));
    }

    // ---- the origin of a callback inside a worktree is refused whatever its value ----

    [Fact]
    public void A_callback_from_a_configuration_file_inside_a_worktree_is_refused_even_for_a_safe_program()
    {
        var refusal = Assert.Single(Classify("filter.format.clean", "git-lfs clean -- %f", origin: @"file:C:/wt/abcd/run/T1/.gitconfig"));
        Assert.Contains("worktree", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- the new program-valued keys are classified; booleans, emails and aliases are not ----

    [Theory]
    [InlineData("gpg.ssh.defaultkeycommand", "tools/key.sh")] // git lowercases the section and variable; the subsection stays
    [InlineData("uploadpack.packobjectshook", "tools/pack.sh")]
    [InlineData("core.alternaterefscommand", "tools/alt.sh")]
    [InlineData("remote.origin.uploadpack", "tools/up.sh")]
    public void A_program_valued_key_is_classified_and_an_in_repository_program_is_refused(string key, string value)
    {
        Assert.NotEmpty(Classify(key, value));
    }

    [Theory]
    [InlineData("core.fsmonitor", "./tools/fsmonitor")] // forced false with -c; never read as a program by the classifier
    [InlineData("core.pager", "less")]
    [InlineData("pager.diff", "false")]
    [InlineData("sendemail.from", "me@example.com")]
    [InlineData("alias.co", "checkout")]
    public void A_boolean_email_or_alias_key_is_not_classified_as_a_program(string key, string value)
    {
        Assert.True(Allowed(key, value), $"{key} = {value} was refused.");
    }

    // ---- keys that are not executable callbacks are ignored ----

    [Theory]
    [InlineData("filter.lfs.required", "true")]
    [InlineData("merge.custom.name", "custom merge")]
    [InlineData("diff.astextplain.cachetextconv", "false")]
    [InlineData("credential.https://x.usehttppath", "true")]
    [InlineData("core.editor", "")]
    public void A_key_that_is_not_an_executable_callback_or_has_no_value_is_ignored(string key, string value)
    {
        Assert.True(Allowed(key, value));
    }

    // ---- a message never repeats a whole value (a credential helper can embed a token) ----

    [Fact]
    public void A_refusal_names_the_key_but_not_the_whole_value()
    {
        var secret = "sh " + new string('t', 300) + "/token.sh";
        var refusal = Assert.Single(Classify("filter.format.clean", secret));

        Assert.Equal("filter.format.clean", refusal.Key);
        Assert.DoesNotContain(new string('t', 120), refusal.Reason, StringComparison.Ordinal);
    }

    // ---- the parser reads the -z --show-scope --show-origin layout ----

    [Fact]
    public void Parse_reads_scope_origin_key_and_value_and_an_empty_value()
    {
        const string output = "local\0file:.git/config\0filter.format.clean\nsh tools/filter.sh\0global\0file:C:/Users/x/.gitconfig\0credential.helper\n\0";

        var entries = GitCallbacks.Parse(output);

        Assert.Equal(2, entries.Count);
        Assert.Equal(("local", "file:.git/config", "filter.format.clean", "sh tools/filter.sh"), (entries[0].Scope, entries[0].Origin, entries[0].Key, entries[0].Value));
        Assert.Equal(("global", "credential.helper", ""), (entries[1].Scope, entries[1].Key, entries[1].Value));
    }

    private sealed class NothingOnPath : AgentStudio.Application.Execution.IExecutableLocator
    {
        public string? Find(string command) => null;
    }

    /// <summary>
    /// Git for Windows has more than one git.exe: <c>cmd\git.exe</c>, the usual one on the PATH, and
    /// <c>mingw64\bin\git.exe</c>, the first one from Git Bash. From either, the bundle's tools (<c>usr\bin\astextplain</c>
    /// has no extension, so the PATH lookup never finds it) must resolve, or the system configuration of git itself
    /// would be refused.
    /// </summary>
    [Theory]
    [InlineData(@"cmd\git.exe")]
    [InlineData(@"mingw64\bin\git.exe")]
    [InlineData(@"bin\git.exe")]
    public void A_bundle_tool_resolves_from_any_git_executable_of_the_bundle(string git)
    {
        var root = Path.Combine(Path.GetTempPath(), "as-bundle-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var file in new[] { git, @"usr\bin\astextplain" })
            {
                var path = Path.Combine(root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }

            var resolved = GitCallbacks.ResolveProgramOnPath(new NothingOnPath(), Path.Combine(root, git), "astextplain");

            Assert.Equal(Path.Combine(root, "usr", "bin", "astextplain"), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
