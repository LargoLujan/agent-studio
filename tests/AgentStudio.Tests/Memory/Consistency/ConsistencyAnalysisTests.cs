using System.IO;
using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Graph;
using AgentStudio.Infrastructure.Graph;
using AgentStudio.Memory;
using AgentStudio.Memory.Consistency;
using AgentStudio.Memory.Diagrams;
using AgentStudio.Tests.Memory.Diagrams;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Memory.Consistency;

/// <summary>
/// F6-11 end to end (docs/20 D-19 and D-27): the analysis of a commit hands the model it just made to the checks, so the diagram that is out of date is a finding; the
/// report with a finding of each kind goes through the real cache of WP-G and comes back equal; and the composition of Memory resolves without a cycle.
/// </summary>
public sealed class ConsistencyAnalysisTests
{
    private sealed class Snapshots(FakeSnapshot snapshot) : IRepositorySnapshots
    {
        public Task<IRepositorySnapshot> OpenAsync(ProjectContext project, string commit, CancellationToken ct) => Task.FromResult<IRepositorySnapshot>(snapshot);
    }

    private sealed class Builder(CodeGraph graph) : ICodeGraphBuilder
    {
        public Task<GraphBuild> BuildAsync(IRepositorySnapshot snapshot, CancellationToken ct) => Task.FromResult(new GraphBuild(graph, NamespaceFiles.Empty));
    }

    [Fact]
    public async Task The_analysis_of_a_commit_gives_the_checks_its_model_and_the_report_survives_the_real_cache()
    {
        using var world = new DiagramFakes.World();
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);
        var checks = new IConsistencyCheck[]
        {
            new LinkBrokenCheck(world.Catalog.Store),
            new AdrCitesMissingCheck(world.Catalog.Store),
            new DiagramStaleCheck(new DiagramStaleness(world.Catalog.Store, world.Runs, world.Clock)),
        };
        var snapshot = new FakeSnapshot(("README.md", "[roto](no.md)"), ("docs/adr/0001-a.md", "`src/gone/x.cs` y [otra](nada.md)"));
        using var cacheFolder = new TemporaryFolder();
        var cache = new CommitAnalysisFileCache(cacheFolder.Path);
        var service = new CommitAnalysisService(new Snapshots(snapshot), new Builder(DiagramFixtures.Graph(appName: "Renamed", withInference: false)), cache, new ConsistencyService(checks));

        var analysis = await service.AnalyzeAsync(world.Project, DiagramFixtures.Commit, CancellationToken.None);

        var codes = analysis.Consistency.Findings.Select(f => f.Code).ToList();
        Assert.Equal(
            [ConsistencyCodes.LinkBroken, ConsistencyCodes.LinkBroken, ConsistencyCodes.AdrCitesMissing, ConsistencyCodes.DiagramStale],
            codes);

        // A new service over the same folder finds the same analysis, with the findings and the checks that did not apply, as the cache kept them.
        var again = new CommitAnalysisService(new Snapshots(snapshot), new Builder(DiagramFixtures.Graph()), cache, new ConsistencyService(checks));
        var found = await again.FindAsync(world.Project, DiagramFixtures.Commit, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(analysis.Consistency, found.Consistency);
        Assert.Equal(analysis.Graph, found.Graph);
    }

    [Fact]
    public async Task A_report_with_a_finding_of_every_code_goes_through_the_cache_unchanged()
    {
        using var cacheFolder = new TemporaryFolder();
        var cache = new CommitAnalysisFileCache(cacheFolder.Path);
        var project = DiagramFakes.Project(@"C:\work\p");
        var graph = DiagramFixtures.Graph();
        var report = new ConsistencyReport(
            graph.Commit,
            [
                new ConsistencyFinding(ConsistencyCodes.LinkBroken, "docs/a.md", 3, [DoctorFact.Of("target", "../x.md"), DoctorFact.Of("reason", "outside")]),
                new ConsistencyFinding(ConsistencyCodes.AdrCitesMissing, "docs/adr/0001-a.md", 7, [DoctorFact.Of("cited", "src/gone/x.cs")]),
                new ConsistencyFinding(ConsistencyCodes.DiagramStale, null, null, [DoctorFact.Of("diagram", "solucion"), DoctorFact.Of("describes", graph.Commit)]),
                new ConsistencyFinding(ConsistencyCodes.RequirementUncovered, null, null, [DoctorFact.Of("milestone", "Hito"), DoctorFact.Of("milestone-id", "mil-uno"), DoctorFact.Of("requirement", "R1"), DoctorFact.Of("title", "Sumar")]),
                new ConsistencyFinding(ConsistencyCodes.CheckFailed, null, null, [DoctorFact.Of("check", ConsistencyCodes.RequirementUncovered)]),
            ],
            [],
            truncated: true);

        await cache.SaveAsync(project, new CachedAnalysis(new CommitAnalysis(graph, report), true), CancellationToken.None);
        var back = await cache.LoadAsync(project, graph.Commit, CancellationToken.None);

        Assert.NotNull(back);
        Assert.True(back.ConsistencyChecked);
        Assert.Equal(report, back.Analysis.Consistency);
    }

    [Fact]
    public void The_composition_of_memory_resolves_the_catalog_the_generators_and_the_checks_without_a_cycle()
    {
        using var world = new DiagramFakes.World();
        var services = new ServiceCollection();
        services.AddSingleton<ICommitAnalysisService>(provider => new CommitAnalysisService(
            new Snapshots(new FakeSnapshot(("README.md", "ok"))),
            new Builder(DiagramFixtures.Graph()),
            new CommitAnalysisFileCache(world.Cache),
            provider.GetService<IConsistencyService>()));
        services.AddSingleton<IRepositoryHistoryReader>(world.History);
        services.AddSingleton<AgentStudio.Application.Decisions.IDecisionRecorder>(world.Recorder);
        services.AddSingleton<AgentStudio.Application.Persistence.IPlanStore>(world.Plans);
        services.AddSingleton<AgentStudio.Application.Orchestration.IRunActivity>(world.Activity);
        services.AddSingleton<AgentStudio.Application.Work.IMilestoneService>(new NoMilestones());
        services.AddSingleton<AgentStudio.Application.Work.IBoardProjector>(new NoBoard());
        services.AddAgentStudioDiagrams(Path.Combine(world.Cache, "diagrams"));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<IDiagramCatalog>());
        Assert.Same(provider.GetRequiredService<IDiagramCatalog>(), provider.GetRequiredService<IDiagramTextReader>());
        Assert.Equal(
            [DiagramKind.Solution, DiagramKind.ProjectNamespaces, DiagramKind.RunPlan, DiagramKind.RunSequence],
            provider.GetServices<IDiagramGenerator>().Select(g => g.Kind).Order().ToArray());
        Assert.Equal(
            ConsistencyCodes.All.Order(StringComparer.Ordinal).ToArray(),
            provider.GetServices<IConsistencyCheck>().Select(c => c.Code).Order(StringComparer.Ordinal).ToArray());
        Assert.NotNull(provider.GetRequiredService<IConsistencyService>());
        Assert.NotNull(provider.GetRequiredService<ICommitAnalysisService>());
    }

    [Fact]
    public async Task The_generators_of_the_port_make_the_same_text_as_the_catalog()
    {
        using var world = new DiagramFakes.World();
        var generator = new SolutionDiagramGenerator(world.Graphs, world.Runs, world.Clock);
        var run = new RunSequenceDiagramGenerator(world.Graphs, world.Runs, world.Clock);

        var source = await generator.GenerateAsync(world.Project, string.Empty, CancellationToken.None);
        await world.Catalog.GenerateAsync(world.Project, DiagramKind.Solution, string.Empty, CancellationToken.None);

        Assert.Equal(await world.Catalog.ReadAsync(world.Project, new DiagramRef(DiagramKind.Solution, string.Empty), CancellationToken.None), source.Mermaid);
        Assert.Equal(DiagramKind.RunSequence, (await run.GenerateAsync(world.Project, DiagramFixtures.Run, CancellationToken.None)).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.GenerateAsync(world.Project, "no-such-run", CancellationToken.None));
    }

    private sealed class NoMilestones : AgentStudio.Application.Work.IMilestoneService
    {
        public Task<IReadOnlyList<AgentStudio.Application.Work.MilestoneView>> ListAsync(ProjectContext project, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentStudio.Application.Work.MilestoneView>>([]);

        public Task<AgentStudio.Application.Work.MilestoneView> OpenAsync(AgentStudio.Application.Work.OpenMilestoneRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentStudio.Application.Work.MilestoneView> ReviseRequirementsAsync(ProjectContext project, AgentStudio.Domain.Work.Milestones.MilestoneId milestone, AgentStudio.Domain.Requirements.FrozenRequirementSet requirements, string revisedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentStudio.Application.Work.MilestoneView> LinkAsync(ProjectContext project, AgentStudio.Domain.Work.Milestones.MilestoneId milestone, AgentStudio.Domain.Work.Milestones.MilestoneTarget target, string linkedBy, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentStudio.Application.Work.MilestoneView> ReopenAsync(ProjectContext project, AgentStudio.Domain.Work.Milestones.MilestoneId milestone, AgentStudio.Domain.Work.Milestones.MilestoneReopenReason reason, AgentStudio.Domain.Common.EvidenceId? evidence, string reopenedBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AgentStudio.Application.Work.MilestoneView?> GetAsync(ProjectContext project, AgentStudio.Domain.Work.Milestones.MilestoneId milestone, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoBoard : AgentStudio.Application.Work.IBoardProjector
    {
        public Task<AgentStudio.Application.Work.MilestoneBoard> ProjectAsync(ProjectContext project, AgentStudio.Domain.Work.Milestones.MilestoneId milestone, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentStudio.Application.Work.BoardCard>> BacklogAsync(ProjectContext project, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("as-consistency-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left to the system.
            }
        }
    }
}
