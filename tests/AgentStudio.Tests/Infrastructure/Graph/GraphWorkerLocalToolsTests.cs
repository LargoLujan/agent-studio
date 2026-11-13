using System.IO;
using System.Text;
using System.Xml.Linq;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Graph;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Graph;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Graph;

/// <summary>
/// docs/20 F6-3 and F6-12 (WP-G), for real: a commit of a real git repository copied by the hardened git layer into a work area at low integrity and read by the
/// real <c>AgentStudio.GraphWorker</c> at low integrity, through the host that relaunches it. The repository of the second test is hostile on purpose, built here
/// and never committed to this repository: nothing in it may run, nothing outside the copy may be read, and the graph comes out with its gaps.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class GraphWorkerLocalToolsTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as-graph-lt-" + Guid.NewGuid().ToString("N")[..8]);

    public GraphWorkerLocalToolsTests() => Directory.CreateDirectory(_container);

    public void Dispose()
    {
        _repo.Dispose();
        TempFolders.Delete(_container);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Outside => Path.Combine(_container, "outside");

    private ProjectContext Project => new("0f8fad5b-d9cb-469f-a165-70867728950e", _repo.Root);

    private sealed class RecordingLauncher(IGraphWorkerLauncher inner) : IGraphWorkerLauncher
    {
        public List<GraphWorkerRequest> Requests { get; } = [];

        public Task<GraphWorkerLaunch> LaunchAsync(GraphWorkerRequest request, string workingDirectory, string tempDirectory, CancellationToken ct)
        {
            Requests.Add(request);
            return inner.LaunchAsync(request, workingDirectory, tempDirectory, ct);
        }
    }

    private RepositorySnapshots Snapshots(string repositoryRoot)
    {
        var factory = new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(Path.Combine(_container, "areas"), LockFile: Path.Combine(_container, "areas.lock")));
        var git = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
        return new RepositorySnapshots(factory, (project, commit, destination, ct) => git.ArchiveCommitAsync(repositoryRoot, commit, destination, ct));
    }

    private void Commit(string message)
    {
        _repo.Run("add", "-A");
        _repo.Run("commit", "-q", "-m", message);
    }

    private void CopySample()
    {
        var from = Path.Combine(AppContext.BaseDirectory, "GraphWorker", "Fixtures", "SampleSolution");
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            _repo.Write(Path.GetRelativePath(from, file).Replace('\\', '/'), File.ReadAllText(file));
        }
    }

    private static string[] Listing(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_sample_solution_through_git_at_low_integrity_gives_the_golden_model_with_non_ascii_names_intact()
    {
        CopySample();
        Commit("sample");
        var commit = _repo.Head;
        var launcher = new RecordingLauncher(new LowIntegrityGraphWorkerLauncher());

        await using var snapshot = await Snapshots(_repo.Root).OpenAsync(Project, commit, Ct);
        var build = await new GraphWorkerHost(launcher).BuildAsync(snapshot, Ct);

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GraphWorker", "Fixtures", "sample-solution.graph.json")).Replace(new string('a', 40), commit, StringComparison.Ordinal);
        Assert.Equal(golden, CodeGraphJson.Serialize(build.Graph, build.Files));
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Sample.Núcleo");
        Assert.Single(launcher.Requests);
    }

    [Fact]
    public async Task A_hostile_repository_runs_nothing_opens_nothing_outside_the_copy_and_gives_the_graph_with_its_gaps()
    {
        // What a person would be hurt by if anything evaluated, restored or built this repository: targets, an inline task, an analyzer and a generator.
        var evidence = Path.Combine(_container, "evidence");
        Directory.CreateDirectory(evidence);
        _repo.Write("Directory.Build.props", $"<Project><Target Name=\"Boom\" BeforeTargets=\"Build\"><Exec Command=\"cmd /c echo pwned &gt; BOOM-props.txt &amp; echo pwned &gt; {evidence}\\BOOM-props.txt\" /></Target></Project>");
        _repo.Write("Directory.Build.targets", "<Project><Target Name=\"Boom2\" AfterTargets=\"Restore\"><Exec Command=\"cmd /c echo pwned &gt; BOOM-targets.txt\" /></Target></Project>");
        _repo.Write("src/App/App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="Pwn" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)\Microsoft.Build.Tasks.Core.dll">
                <Task><Code Type="Fragment" Language="cs">System.IO.File.WriteAllText(@"BOOM-inline.txt", "x");</Code></Task>
              </UsingTask>
              <Target Name="Boom3" BeforeTargets="Build"><Pwn /><Exec Command="cmd /c echo pwned &gt; BOOM-csproj.txt" /></Target>
              <ItemGroup>
                <ProjectReference Include="..\Gen\Gen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                <Analyzer Include="evil.dll" />
                <ProjectReference Include="..\..\..\outside\Evil.csproj" />
                <ProjectReference Include="{{Path.Combine(Outside, "Evil.csproj")}}" />
              </ItemGroup>
            </Project>
            """);
        _repo.Write("src/App/Normal.cs", "namespace Hostile.App { public class Normal { } }");
        _repo.Write("src/Gen/Gen.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>");
        _repo.Write("src/Gen/Generator.cs", "namespace Hostile.Gen { [Generator] public class Pwn : ISourceGenerator { public void Initialize(GeneratorInitializationContext c) { System.IO.File.WriteAllText(\"BOOM-gen.txt\", \"x\"); } public void Execute(GeneratorExecutionContext c) { } } }");
        _repo.Write(
            "Hostile.slnx",
            "<Solution>\n"
            + "  <Project Path=\"src/App/App.csproj\" />\n  <Project Path=\"src/Gen/Gen.csproj\" />\n"
            + "  <Project Path=\"..\\..\\outside\\Evil.csproj\" />\n"
            + $"  <Project Path=\"{Path.Combine(Outside, "Evil.csproj")}\" />\n"
            + "  <Project Path=\"\\\\localhost\\c$\\Evil.csproj\" />\n"
            + "  <Project Path=\"CON.csproj\" />\n  <Project Path=\"aux .csproj\" />\n  <Project Path=\"src/Dtd/Dtd.csproj\" />\n</Solution>");
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "TOP-SECRET-VALUE");
        _repo.Write("src/Dtd/Dtd.csproj", "<?xml version=\"1.0\"?><!DOCTYPE p [<!ENTITY x SYSTEM \"" + Path.Combine(Outside, "secret.txt").Replace('\\', '/') + "\">]><Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>&x;</RootNamespace></PropertyGroup></Project>");

        // Outside the copy: the sentinel that must never be opened, in the place that both the paths above name.
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "Evil.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        File.WriteAllText(Path.Combine(Outside, "Evil.cs"), "namespace Sentinel.Outside.MustNeverAppear { }");

        // Files that hurt a parser: one over the cap, one nested by expression, and one nested by casts, which really exhausts the stack of the parser (the test gives it a small one).
        _repo.Write("src/App/Huge.cs", "namespace Hostile.Huge { }\n" + new string('/', 1_200_000));
        _repo.Write("src/App/Expr.cs", "namespace Hostile.Expr { class C { int X = " + new string('(', 20_000) + "1" + new string(')', 20_000) + "; } }");
        _repo.Write("src/App/Cast.cs", "namespace Hostile.Cast { class C { int Y(int a) => " + string.Concat(Enumerable.Repeat("(int)", 20_000)) + "a; } }");
        Commit("hostile");
        var commit = _repo.Head;
        var launcher = new RecordingLauncher(new LowIntegrityGraphWorkerLauncher(extraEnvironment: new Dictionary<string, string?> { ["AGENTSTUDIO_GRAPHWORKER_PARSE_STACK_KB"] = "64" }));

        await using var snapshot = await Snapshots(_repo.Root).OpenAsync(Project, commit, Ct);
        var area = Path.GetDirectoryName(snapshot.Root)!;
        var before = Listing(area);
        var build = await new GraphWorkerHost(launcher).BuildAsync(snapshot, Ct);
        var json = CodeGraphJson.Serialize(build.Graph, build.Files);

        // Nothing ran: no file a target, a task or a generator would write exists, in the copy, in its area or in the evidence folder, and nothing was built.
        Assert.Equal(before, Listing(area));
        Assert.DoesNotContain(Listing(area), p => p.Contains("BOOM", StringComparison.OrdinalIgnoreCase) || p.Contains("obj", StringComparison.OrdinalIgnoreCase) || p.Contains("bin", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFileSystemEntries(evidence));

        // Nothing outside the copy was read, and the hostile paths are in no output, only the file that said them.
        Assert.DoesNotContain("Sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Evil", json, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", json, StringComparison.Ordinal);
        Assert.Equal(3, build.Graph.Gaps.Count(g => g is { Code: GraphGapCodes.OutsideRoot, Path: "Hostile.slnx" }) );
        Assert.Equal(2, build.Graph.Gaps.Count(g => g is { Code: GraphGapCodes.OutsideRoot, Path: "src/App/App.csproj" }));
        Assert.Equal(2, build.Graph.Gaps.Count(g => g is { Code: GraphGapCodes.InvalidPath, Path: "Hostile.slnx" }));
        Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, Path: "src/Dtd/Dtd.csproj" });
        Assert.DoesNotContain("TOP-SECRET", json, StringComparison.Ordinal);

        // The files that hurt the parser are gaps and their project is partial; the rest of the graph is whole.
        foreach (var skipped in new[] { "src/App/Huge.cs", "src/App/Expr.cs", "src/App/Cast.cs" })
        {
            Assert.Contains(build.Graph.Gaps, g => g is { Code: GraphGapCodes.FileSkipped, NodeId: "src/App/App.csproj" } && g.Path == skipped);
        }

        Assert.True(build.Graph.Nodes.Single(n => n.Id == "src/App/App.csproj").Partial);
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Hostile.App");
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Hostile.Gen");
        Assert.DoesNotContain(build.Graph.Nodes, n => n.Name is "Hostile.Huge" or "Hostile.Expr" or "Hostile.Cast");
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.BuildProps);
        Assert.Contains(build.Graph.Gaps, g => g.Code == GraphGapCodes.Unevaluated && g.Path == "src/App/App.csproj");

        // The only process launched is the analysis, and the generics file killed the first launch: the second one skipped it.
        Assert.True(launcher.Requests.Count >= 2, "the file that exhausts the stack was not relaunched");
        Assert.Contains("src/App/Cast.cs", launcher.Requests[^1].Skipped);
        Assert.All(launcher.Requests, r => Assert.Equal(snapshot.Root, r.Root));
    }

    [Fact]
    public async Task Six_files_that_kill_the_process_six_different_ways_give_a_partial_graph_with_six_gaps_and_the_rest_whole()
    {
        // A small stack for the parser (the variable the test gives the launcher) makes files of tens of thousands of characters exhaust it. None of them is turned
        // away by the counts of the scanner: they have no nested brackets, no long run of one operator and no statement without an end.
        var forms = new (string File, string Body)[]
        {
            ("Cast", "int Y(int a) => " + string.Concat(Enumerable.Repeat("(int)", 20_000)) + "a;"),
            ("Assign", "int Y(int a) { var y = " + string.Concat(Enumerable.Repeat("a=", 20_000)) + "a; return y; }"),
            ("Cond", "int Y(int a) { var y = " + string.Concat(Enumerable.Repeat("a?", 20_000)) + "a; return 0; }"),
            ("Ifs", "void Y(int x) { " + string.Concat(Enumerable.Repeat("if(x)", 20_000)) + " ; }"),
            ("Await", "async System.Threading.Tasks.Task Y(int x) { var y = " + string.Concat(Enumerable.Repeat("await ", 20_000)) + "x; }"),
            ("Lambda", "void Y(int x) { var y = " + string.Concat(Enumerable.Repeat("x=>", 20_000)) + "x; }"),
        };
        _repo.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        _repo.Write("src/App/Normal.cs", "namespace Whole.Normal { public class Normal { } }");
        foreach (var (file, body) in forms)
        {
            _repo.Write($"src/App/{file}.cs", "namespace Hostile." + file + " { class C { " + body + " } }");
        }

        Commit("six ways");
        var launcher = new RecordingLauncher(new LowIntegrityGraphWorkerLauncher(extraEnvironment: new Dictionary<string, string?> { ["AGENTSTUDIO_GRAPHWORKER_PARSE_STACK_KB"] = "64" }));

        await using var snapshot = await Snapshots(_repo.Root).OpenAsync(Project, _repo.Head, Ct);
        var build = await new GraphWorkerHost(launcher).BuildAsync(snapshot, Ct);

        var gaps = build.Graph.Gaps.Where(g => g.Code == GraphGapCodes.FileSkipped).Select(g => g.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(forms.Select(f => $"src/App/{f.File}.cs").Order(StringComparer.Ordinal), gaps);
        Assert.True(build.Graph.Nodes.Single(n => n.Id == "src/App/App.csproj").Partial);
        Assert.Contains(build.Graph.Nodes, n => n.Name == "Whole.Normal");
        Assert.DoesNotContain(build.Graph.Nodes, n => n.Name.StartsWith("Hostile.", StringComparison.Ordinal));
        Assert.Equal(forms.Length + 1, launcher.Requests.Count);
        Assert.Equal(forms.Length, launcher.Requests[^1].Skipped.Count);
        Assert.False(launcher.Requests[^1].WithoutSources);
    }

    [Fact]
    public async Task A_symbolic_link_committed_to_the_repository_never_reaches_the_graph_either_the_copy_refuses_it_or_it_is_not_followed()
    {
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "Linked.cs"), "namespace Sentinel.Through.TheLink { }");
        _repo.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
        _repo.Write("src/App/Normal.cs", "namespace Whole.Normal { }");
        var target = Path.Combine(_container, "link-target.txt");
        File.WriteAllText(target, Outside.Replace('\\', '/'));
        var blob = TempGitRepo.Git(_repo.Root, "hash-object", "-w", target).Trim();
        TempGitRepo.Git(_repo.Root, "update-index", "--add", "--cacheinfo", $"120000,{blob},src/App/linked");
        Commit("with a link");

        string? json = null;
        try
        {
            await using var snapshot = await Snapshots(_repo.Root).OpenAsync(Project, _repo.Head, Ct);
            var build = await new GraphWorkerHost(new LowIntegrityGraphWorkerLauncher()).BuildAsync(snapshot, Ct);
            json = CodeGraphJson.Serialize(build.Graph, build.Files);
        }
        catch (Exception ex) when (ex is IOException or ProcessIsolationException or InvalidOperationException or InvalidDataException)
        {
            // The copy refused the link: nothing was read, nothing ran.
        }

        Assert.True(json is null || !json.Contains("Sentinel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Auto_Graph_reads_this_solution_every_project_of_it_with_a_namespace_and_no_path_outside_the_copy()
    {
        var root = RepositoryRootOfThisSolution();
        if (root is null)
        {
            // The tests run from a folder that is not a checkout of this repository: nothing to read.
            return;
        }

        var commit = TempGitRepo.Git(root, "rev-parse", "HEAD").Trim();
        var launcher = new RecordingLauncher(new LowIntegrityGraphWorkerLauncher());
        await using var snapshot = await Snapshots(root).OpenAsync(new ProjectContext("0f8fad5b-d9cb-469f-a165-70867728950e", root), commit, Ct);

        await using var slnx = snapshot.OpenRead("AgentStudio.slnx", 1024 * 1024);
        var listed = XDocument.Load(slnx).Descendants("Project").Select(p => ((string)p.Attribute("Path")!).Replace('\\', '/')).ToList();
        var build = await new GraphWorkerHost(launcher).BuildAsync(snapshot, Ct);

        Assert.True(listed.Count >= 12, "AgentStudio.slnx lists its projects");
        foreach (var project in listed)
        {
            Assert.Contains(build.Graph.Nodes, n => n.Id == project && n.Kind == GraphNodeKind.Project);
            Assert.Contains(build.Graph.Edges, e => e.From == project && e.Kind == GraphEdgeKind.Contains && build.Graph.Nodes.Single(n => n.Id == e.To).Kind == GraphNodeKind.Namespace);
        }

        Assert.All(build.Graph.Nodes.Where(n => n.Path is not null), n => Assert.True(RepositoryPaths.IsSafeRelative(n.Path)));
        Assert.All(build.Graph.Gaps, g => Assert.True(RepositoryPaths.IsSafeRelative(g.Path)));
        Assert.Contains(build.Graph.Edges, e => e is { Kind: GraphEdgeKind.ProjectReference, From: "src/AgentStudio.Application/AgentStudio.Application.csproj", To: "src/AgentStudio.Domain/AgentStudio.Domain.csproj" });
    }

    private static string? RepositoryRootOfThisSolution()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentStudio.slnx")) && (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
