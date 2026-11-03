using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// What must stay exactly the same in the user's repository while Agent Studio works on worktrees: the status, HEAD,
/// every ref that is not Agent Studio's own (<c>refs/agentstudio/**</c> and the <c>agentstudio/**</c> branches), and
/// the index file byte for byte.
/// </summary>
public sealed record MainTreeSnapshot(string Status, string Head, string HeadFile, string UserRefs, string IndexHash);

/// <summary>
/// A throwaway git repository with one commit. The identity is configured locally (never the user's), the folder name
/// has a space and an accent to exercise quoting and UTF-8, and the worktree root is a sibling folder outside the
/// repository. All git calls here are made with a plain <see cref="Process"/>, independent of the code under test.
/// </summary>
public sealed class TempGitRepo : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _container;

    private TempGitRepo(string container, string root, string worktreeRoot)
    {
        _container = container;
        Root = root;
        WorktreeRoot = worktreeRoot;
        BaseCommit = string.Empty;
    }

    /// <summary>The main working tree.</summary>
    public string Root { get; }

    /// <summary>A folder next to the repository (never inside it) where the worktrees are created.</summary>
    public string WorktreeRoot { get; }

    /// <summary>The only commit of the repository, on branch <c>main</c>.</summary>
    public string BaseCommit { get; private set; }

    public string Head => Git(Root, "rev-parse", "HEAD").Trim();

    public static TempGitRepo Create()
    {
        var container = Path.Combine(Path.GetTempPath(), "as git tést " + Guid.NewGuid().ToString("N")[..12]);
        var repo = new TempGitRepo(container, Path.Combine(container, "repo"), Path.Combine(container, "wt"));
        try
        {
            Directory.CreateDirectory(repo.Root);
            Git(repo.Root, "init", "-q", "-b", "main");

            // Local configuration only: the tests never depend on (or leak) the identity of the user.
            Git(repo.Root, "config", "user.name", "Agent Studio Tests");
            Git(repo.Root, "config", "user.email", "tests@localhost");
            Git(repo.Root, "config", "commit.gpgsign", "false");
            Git(repo.Root, "config", "core.autocrlf", "false");

            repo.Write("README.md", "# temp\n");
            repo.Write(".gitignore", "*.log\n");
            repo.Write("shared.txt", string.Concat(Enumerable.Range(1, 8).Select(i => $"linea {i}\n")));
            repo.Write("docs/guía de uso.md", "uno\ndos\ntres\n");
            repo.Write("src/Calc/Calc.cs", "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
            repo.Write("tests/Calc.Tests/CalcTests.cs", "// tests\n");
            Git(repo.Root, "add", "-A");
            Git(repo.Root, "commit", "-q", "-m", "base");
            repo.BaseCommit = repo.Head;
            return repo;
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>Runs git in <paramref name="workingDirectory"/> and returns its standard output; a non-zero exit code throws.</summary>
    public static string Git(string workingDirectory, params string[] arguments)
    {
        var (exitCode, output, error) = TryGit(workingDirectory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
        }

        return output;
    }

    /// <summary>Like <see cref="Git"/> but returns the exit code and error output instead of throwing.</summary>
    public static (int ExitCode, string Output, string Error) TryGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Observing the repository must not refresh its index.
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }

    /// <summary>Writes a UTF-8 file (no BOM) under the main working tree, creating folders.</summary>
    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Utf8);
    }

    /// <summary>Runs git in the main working tree.</summary>
    public string Run(params string[] arguments) => Git(Root, arguments);

    /// <summary>The state of the main working tree that Agent Studio must never change.</summary>
    public MainTreeSnapshot Snapshot()
    {
        var refs = Git(Root, "for-each-ref", "--format=%(refname) %(objectname)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("refs/agentstudio/", StringComparison.Ordinal)
                && !line.StartsWith("refs/heads/agentstudio/", StringComparison.Ordinal))
            .ToArray();
        var indexPath = Path.Combine(Root, ".git", "index");
        return new MainTreeSnapshot(
            Git(Root, "status", "--porcelain=v1", "-z", "--untracked-files=all"),
            Head,
            File.ReadAllText(Path.Combine(Root, ".git", "HEAD")),
            string.Join('\n', refs),
            File.Exists(indexPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(indexPath))) : "(no index)");
    }

    // .git/objects holds read-only files on Windows, a test may plant links, and a process may still be letting go of a
    // folder: TempFolders handles the three, never through a link.
    public void Dispose() => TempFolders.Delete(_container);
}
