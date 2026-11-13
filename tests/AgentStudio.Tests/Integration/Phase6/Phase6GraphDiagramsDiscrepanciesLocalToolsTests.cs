using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using AgentStudio.Infrastructure.Git;
using AgentStudio.Infrastructure.Graph;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Infrastructure.Processes.Isolation;
using AgentStudio.Memory.Consistency;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase6;

/// <summary>
/// The graph, the diagrams and the discrepancies of F6-3, F6-10, F6-11 and F6-12 (docs/20 section 9.8) for real: a small git repository built here (never committed to this
/// one), its commit copied by the hardened git layer into a work area at low integrity and read by the real <c>AgentStudio.GraphWorker</c>, then the real checks, the diagram
/// catalog and the line of Auto Doctor of the app's composition. A second commit changes the model, so the diagram of the first one is out of date. It needs git, so it is
/// left out of CI with the rest of <c>LocalTools</c>.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class Phase6GraphDiagramsDiscrepanciesLocalToolsTests : IDisposable
{
    private readonly TempGitRepo _repo = TempGitRepo.Create();
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as-phase6-lt-" + Guid.NewGuid().ToString("N")[..8]);

    public Phase6GraphDiagramsDiscrepanciesLocalToolsTests() => Directory.CreateDirectory(_container);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _repo.Dispose();
        TempFolders.Delete(_container);
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

    private RepositorySnapshots Snapshots()
    {
        var factory = new LowIntegrityWorkAreaFactory(new LowIntegrityWorkAreaOptions(Path.Combine(_container, "areas"), LockFile: Path.Combine(_container, "areas.lock")));
        var git = new GitWorktreeManager(new ProcessRunner(), new ExecutableLocator(), new GitWorktreeOptions(_repo.WorktreeRoot));
        return new RepositorySnapshots(factory, (project, commit, destination, ct) => git.ArchiveCommitAsync(_repo.Root, commit, destination, ct));
    }

    [Fact]
    public async Task A_real_commit_goes_through_git_and_the_worker_to_a_diagram_and_the_next_commit_makes_that_diagram_out_of_date()
    {
        CopySample();
        _repo.Write("README.md", "# Muestra\n\nVer [la guía](docs/no-existe.md).\n");
        Commit("sample");
        var first = _repo.Head;
        var head = new FixedHead(_repo.Root, first);
        using var host = Phase6Host.Create(services =>
        {
            services.AddSingleton<IRepositorySnapshots>(Snapshots());
            services.AddSingleton<IRepositoryHistoryReader>(head);
        });
        var project = await host.Provider.GetRequiredService<IProjectWorkspace>().OpenAsync(_repo.Root, Ct);
        var analyses = host.Provider.GetRequiredService<ICommitAnalysisService>();
        var catalog = host.Provider.GetRequiredService<IDiagramCatalog>();
        var line = host.Provider.GetServices<IDoctorCheckSource>().OfType<DiscrepanciesCheckSource>().Single();

        // The graph, from the real worker: the three projects of the sample, and the broken link of the README as a discrepancy.
        var analysis = await analyses.AnalyzeAsync(project, first, Ct);
        Assert.Equal(3, analysis.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Project));
        var broken = Assert.Single(analysis.Consistency.Findings, f => f.Code == ConsistencyCodes.LinkBroken);
        Assert.Equal("README.md", broken.Path);

        // The diagram from that model, and the line of Auto Doctor over the saved analysis.
        var made = await catalog.GenerateAsync(project, DiagramKind.Solution, string.Empty, Ct);
        Assert.Equal(DiagramState.Current, made.State);
        Assert.Equal(first, made.Describes);
        Assert.Equal("1", Assert.Single(await line.CheckAsync(project, Ct)).Fact("count"));

        // A second commit adds a project to the solution: the new model is not the one the diagram was made from.
        _repo.Write("Sample.slnx", File.ReadAllText(Path.Combine(_repo.Root, "Sample.slnx")).Replace("</Solution>", "  <Project Path=\"src/Extra/Extra.csproj\" />\n</Solution>", StringComparison.Ordinal));
        _repo.Write("src/Extra/Extra.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        _repo.Write("src/Extra/Extra.cs", "namespace Sample.Extra { public class Extra { } }");
        Commit("extra");
        head.Commit = _repo.Head;
        var next = await analyses.AnalyzeAsync(project, head.Commit, Ct);
        Assert.Equal(4, next.Graph.Nodes.Count(n => n.Kind == GraphNodeKind.Project));
        Assert.Contains(next.Consistency.Findings, f => f.Code == ConsistencyCodes.DiagramStale);
        var stale = Assert.Single(await line.CheckAsync(project, Ct));
        Assert.Equal(DoctorStatus.Warning, stale.Status);
        Assert.Equal("1", stale.Fact(ConsistencyCodes.DiagramStale));
        Assert.Equal(DoctorFixCatalog.OpenGraph, stale.FixCode);

        // «Regenerar»: the diagram describes the new commit and the discrepancy is gone, with no new analysis.
        var regenerated = await catalog.GenerateAsync(project, DiagramKind.Solution, string.Empty, Ct);
        Assert.Equal(DiagramState.Current, regenerated.State);
        Assert.Equal(head.Commit, regenerated.Describes);
        Assert.Null(Assert.Single(await line.CheckAsync(project, Ct)).Fact(ConsistencyCodes.DiagramStale));
    }
}
