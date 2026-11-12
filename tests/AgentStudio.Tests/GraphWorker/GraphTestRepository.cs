using System.Diagnostics;
using System.IO;
using System.Text;
using AgentStudio.Application.Graph;
using AgentStudio.GraphWorker;
using AgentStudio.Tests.TestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.GraphWorker;

/// <summary>
/// A repository the tests of Auto Graph build in a temporary folder: a <c>copy</c> folder that is the root the analysis reads, beside an <c>outside</c> folder
/// that stands for everything of the person's that the copy is not (docs/20 F6-12). Whatever the analysis must never open is put in <c>outside</c>.
/// </summary>
internal sealed class GraphTestRepository : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agentstudio-graph-" + Guid.NewGuid().ToString("N"));

    public GraphTestRepository()
    {
        Root = Path.Combine(_base, "copy");
        Outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
    }

    /// <summary>The root of the copy of the commit.</summary>
    public string Root { get; }

    /// <summary>A folder next to the copy: nothing of it may be read.</summary>
    public string Outside { get; }

    /// <summary>The sample solution of the golden tests, copied into the root.</summary>
    public static GraphTestRepository FromSample()
    {
        var repository = new GraphTestRepository();
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "GraphWorker", "Fixtures", "SampleSolution"), repository.Root);
        return repository;
    }

    public string Add(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    public string AddOutside(string relativePath, string content)
    {
        var full = Path.Combine(Outside, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    /// <summary>A junction (it needs no privilege) at <paramref name="link"/> inside the copy that points to <paramref name="target"/>.</summary>
    public void Junction(string link, string target)
    {
        var full = Path.Combine(Root, link.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", full, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("mklink did not start.");
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("The junction could not be made.");
        }
    }

    /// <summary>Runs the analysis in-process, as the process would, and returns what it built and the progress lines.</summary>
    public async Task<(AgentStudio.Application.Graph.GraphBuild Build, List<string> Progress)> AnalyzeAsync(params string[] skipped)
    {
        var progress = new List<string>();
        var build = await new SolutionGraphAnalysis().AnalyzeAsync(new GraphWorkerRequest(Root, CommitA, skipped), progress.Add, TestContext.Current.CancellationToken);
        return (build, progress);
    }

    public void Dispose() => TempFolders.Delete(_base);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.GetDirectories(from))
        {
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }
}
