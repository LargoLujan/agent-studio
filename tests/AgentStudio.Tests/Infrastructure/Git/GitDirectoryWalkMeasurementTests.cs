using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AgentStudio.Infrastructure.Git;

namespace AgentStudio.Tests.Infrastructure.Git;

/// <summary>
/// Measures the walk of the git directory (S-01 to S-03, ADR-0012) that runs before every command that can write into it:
/// on the git directory of the repository that <c>AGENTSTUDIO_WALK_MEASURE_ROOT</c> names, read-only (git only answers
/// <c>rev-parse --git-common-dir</c>, with <c>GIT_OPTIONAL_LOCKS=0</c>, and the walk lists folders and opens files for
/// their attributes), and on a synthetic repository with about 10 000 loose refs, their reflogs and packed objects.
/// Skipped unless the variable is set. The walk is internal, so it is reached by reflection. The report goes to the test
/// output and, when <c>AGENTSTUDIO_WALK_MEASURE_REPORT</c> names a file, is appended to it.
/// </summary>
[Trait("Category", "Measurement")]
public sealed class GitDirectoryWalkMeasurementTests
{
    public const string RootVariable = "AGENTSTUDIO_WALK_MEASURE_ROOT";

    public const string ReportVariable = "AGENTSTUDIO_WALK_MEASURE_REPORT";

    private const int Runs = 7;

    private const int LooseRefs = 10_000;

    [Fact]
    public void The_walk_of_a_real_git_directory_and_of_one_with_ten_thousand_loose_refs()
    {
        var root = Environment.GetEnvironmentVariable(RootVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"Set {RootVariable} to a folder of the repository to measure.");
        var walk = typeof(GitWorktreeManager).Assembly.GetType("AgentStudio.Infrastructure.Git.WorktreeFileSystem", throwOnError: true)!
            .GetMethod("RequireGitDirectoriesWithoutLinks", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The walk is not where this measurement looks for it.");
        var report = new StringBuilder();

        var common = Path.GetFullPath(TempGitRepo.Git(root!, "rev-parse", "--path-format=absolute", "--git-common-dir").Trim());
        Measure(report, "Real git directory", common, walk);

        using var synthetic = TempGitRepo.Create();
        var syntheticCommon = BuildSynthetic(synthetic);
        Measure(report, $"Synthetic: {LooseRefs} loose refs and their reflogs, packed objects", syntheticCommon, walk);

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable(ReportVariable) is { Length: > 0 } file)
        {
            File.AppendAllText(file, report.AppendLine().ToString());
        }
    }

    /// <summary>A repository whose objects are packed, with <see cref="LooseRefs"/> loose branches and a reflog for each, written as git writes them.</summary>
    private static string BuildSynthetic(TempGitRepo repo)
    {
        for (var i = 0; i < 2_000; i++)
        {
            repo.Write(string.Create(CultureInfo.InvariantCulture, $"datos/f{i:D4}.txt"), string.Create(CultureInfo.InvariantCulture, $"fichero {i}\n"));
        }

        repo.Run("add", "-A");
        repo.Run("commit", "-q", "-m", "dos mil ficheros");
        repo.Run("repack", "-a", "-d", "-q");
        var head = repo.Head;
        var common = Path.Combine(repo.Root, ".git");
        var line = head + " " + head + " Agent Studio Tests <tests@localhost> 1760000000 +0200\tbranch: Created from HEAD\n";
        for (var i = 0; i < LooseRefs; i++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"carga/{i / 100:D3}/r{i:D5}");
            var reference = Path.Combine(common, "refs", "heads", name.Replace('/', Path.DirectorySeparatorChar));
            var reflog = Path.Combine(common, "logs", "refs", "heads", name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
            Directory.CreateDirectory(Path.GetDirectoryName(reflog)!);
            File.WriteAllText(reference, head + "\n");
            File.WriteAllText(reflog, line);
        }

        Assert.Equal(LooseRefs + 1, repo.Run("for-each-ref", "--format=%(refname)", "refs/heads/").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        return common;
    }

    private static void Measure(StringBuilder report, string title, string common, MethodInfo walk)
    {
        var (folders, files, objectFiles) = Count(common);
        var times = new List<double>();
        string? refusal = null;
        for (var run = 0; run < Runs && refusal is null; run++)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                walk.Invoke(null, [common, null]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is WorktreeOwnershipException refused)
            {
                refusal = refused.Message;
            }

            times.Add(clock.Elapsed.TotalMilliseconds);
        }

        var sorted = times.Order().ToList();
        report
            .AppendLine(CultureInfo.InvariantCulture, $"{title}: {common}")
            .AppendLine(CultureInfo.InvariantCulture, $"  {folders} folders, {files} files ({objectFiles} in the object stores, read from the listing only; {files - objectFiles} opened to count their names)")
            .AppendLine(CultureInfo.InvariantCulture, $"  {times.Count} walks: first {times[0]:F1} ms; median {sorted[sorted.Count / 2]:F1} ms; min {sorted[0]:F1} ms; max {sorted[^1]:F1} ms")
            .AppendLine(refusal is null ? "  Result: no link found." : $"  Result: refused: {refusal}");
    }

    /// <summary>Folders and files under <paramref name="common"/>, never entering a reparse point, and how many files lie in an object store.</summary>
    private static (int Folders, int Files, int ObjectFiles) Count(string common)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        var (folders, files, objectFiles) = (0, 0, 0);
        var pending = new Stack<string>([common]);
        while (pending.TryPop(out var folder))
        {
            folders++;
            var inObjects = IsObjectStore(Path.GetRelativePath(common, folder));
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
                else
                {
                    files++;
                    objectFiles += inObjects ? 1 : 0;
                }
            }
        }

        return (folders, files, objectFiles);
    }

    // objects/<xx>, objects/pack, objects/info and below, lfs/objects/<aa>/<bb>, also under modules/: what the walk lists only.
    private static bool IsObjectStore(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        var start = parts[0] == "modules" ? Array.FindLastIndex(parts, p => p is "objects" or "lfs") : 0;
        if (start < 0 || start >= parts.Length)
        {
            return false;
        }

        var tail = parts[start..];
        return tail switch
        {
            ["objects", var second, ..] => second.Length == 2 || second is "pack" or "info",
            ["lfs", "objects", var first, var second] => first.Length == 2 && second.Length == 2,
            _ => false,
        };
    }
}
