using AgentStudio.Application.Consistency;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Docs;
using AgentStudio.Desktop.Graph;
using AgentStudio.Domain.Diagnostics;
using AgentStudio.Memory.Consistency;
using AgentStudio.Tests.Memory.Diagrams;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase6;

/// <summary>
/// F6-3, F6-10 and F6-11 of docs/20 end to end (section 9.8), over the composition of the app and a small repository, with the seams the composition leaves open: the copy
/// of the commit and the analysis of the code are doubles here (the variant with the real git and the real <c>AgentStudio.GraphWorker</c> is
/// <see cref="Phase6GraphDiagramsDiscrepanciesLocalToolsTests"/>). The analysis hands its model to the real checks, the diagram is made from that same saved model, a
/// new model makes it «desactualizado» and a discrepancy, and the line of Auto Doctor and the diagram catalog agree at every step.
/// </summary>
public sealed class Phase6GraphDiagramsDiscrepanciesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_analysis_the_diagram_made_from_it_the_stale_discrepancy_and_the_line_of_auto_doctor_tell_one_story_until_the_diagram_is_regenerated()
    {
        var head = new FixedHead("fixed", Phase6Host.Commit);
        var builder = new MovingGraphBuilder(DiagramFixtures.Graph(withInference: false));
        using var host = Phase6Host.Create(services =>
        {
            services.AddSingleton<IRepositoryHistoryReader>(head);
            services.AddSingleton<IRepositorySnapshots>(new FilesSnapshots(("README.md", "[roto](no.md)")));
            services.AddSingleton<ICodeGraphBuilder>(builder);
        });
        var root = host.Temp.CreateDirectory("repositorio");
        var project = await host.Provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        var analyses = host.Provider.GetRequiredService<ICommitAnalysisService>();
        var catalog = host.Provider.GetRequiredService<IDiagramCatalog>();
        var texts = host.Provider.GetRequiredService<IDiagramTextReader>();
        var line = host.Provider.GetServices<IDoctorCheckSource>().OfType<DiscrepanciesCheckSource>().Single();
        var solution = new DiagramRef(DiagramKind.Solution, string.Empty);

        async Task<DoctorCheck> LineAsync() => Assert.Single(await line.CheckAsync(project, Ct));

        // Nothing saved: the line does not calculate by itself.
        Assert.Equal(DoctorStatus.Unknown, (await LineAsync()).Status);

        // The graph: the analysis of the commit gives the model and the discrepancies of the checks (a broken link in the README).
        var analysis = await analyses.AnalyzeAsync(project, head.Commit, Ct);
        Assert.Equal([ConsistencyCodes.LinkBroken], analysis.Consistency.Findings.Select(f => f.Code));
        Assert.Equal("1", (await LineAsync()).Fact("count"));

        // The diagram, made from the saved model: current, with the commit it describes and its Mermaid text.
        var made = await catalog.GenerateAsync(project, DiagramKind.Solution, string.Empty, Ct);
        Assert.Equal(DiagramState.Current, made.State);
        Assert.Equal(head.Commit, made.Describes);
        var text = await texts.ReadAsync(project, solution, Ct);
        Assert.NotNull(text);
        Assert.Contains("flowchart LR", text, StringComparison.Ordinal);
        Assert.Equal("1", (await LineAsync()).Fact("count"));

        // The model changes (a project renamed) and the commit is analyzed again: the diagram is out of date, and that is a discrepancy.
        builder.Graph = DiagramFixtures.Graph(appName: "Renamed", withInference: false);
        var again = await analyses.AnalyzeAsync(project, head.Commit, Ct);
        Assert.Equal([ConsistencyCodes.LinkBroken, ConsistencyCodes.DiagramStale], again.Consistency.Findings.Select(f => f.Code));
        Assert.Equal(DiagramState.Stale, Assert.Single(await catalog.ListAsync(project, Ct), s => s.Diagram == solution).State);
        var stale = await LineAsync();
        Assert.Equal("2", stale.Fact("count"));
        Assert.Equal("1", stale.Fact(ConsistencyCodes.DiagramStale));
        Assert.Equal(DoctorFixCatalog.OpenGraph, stale.FixCode);

        // Regenerating (what «Regenerar» does in «Diagramas») settles it without a new analysis: the line works the two live codes out again.
        var regenerated = await catalog.GenerateAsync(project, DiagramKind.Solution, string.Empty, Ct);
        Assert.Equal(DiagramState.Current, regenerated.State);
        Assert.Contains("Renamed", await texts.ReadAsync(project, solution, Ct), StringComparison.Ordinal);
        var settled = await LineAsync();
        Assert.Equal("1", settled.Fact("count"));
        Assert.Null(settled.Fact(ConsistencyCodes.DiagramStale));
    }

    [Fact]
    public async Task The_discrepancies_section_of_the_screen_and_the_line_of_auto_doctor_count_the_same_by_code_at_every_step()
    {
        var head = new FixedHead("fixed", Phase6Host.Commit);
        var builder = new MovingGraphBuilder(DiagramFixtures.Graph(withInference: false));
        using var host = Phase6Host.Create(services =>
        {
            services.AddSingleton<IRepositoryHistoryReader>(head);
            services.AddSingleton<IRepositorySnapshots>(new FilesSnapshots(("README.md", "[roto](no.md)")));
            services.AddSingleton<ICodeGraphBuilder>(builder);
        });
        var root = host.Temp.CreateDirectory("repositorio");
        var project = await host.Provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        var analyses = host.Provider.GetRequiredService<ICommitAnalysisService>();
        var catalog = host.Provider.GetRequiredService<IDiagramCatalog>();
        var line = host.Provider.GetServices<IDoctorCheckSource>().OfType<DiscrepanciesCheckSource>().Single();
        var section = host.Provider.GetServices<GraphSection>().OfType<DocsDiscrepanciesSection>().Single();
        await new GraphDocumentViewModel([section]).SetProjectAsync(project, Ct);

        async Task AssertSameCountAsync(int expectedTotal)
        {
            await section.RefreshCommand.ExecuteAsync(null);
            var check = Assert.Single(await line.CheckAsync(project, Ct));
            Assert.Equal(expectedTotal, section.Rows.Count);
            Assert.Equal(expectedTotal, int.Parse(check.Fact("count")!, System.Globalization.CultureInfo.InvariantCulture));
            foreach (var code in ConsistencyCodes.All)
            {
                var onScreen = section.Rows.Count(r => r.Finding.Code == code);
                var inLine = check.Fact(code) is { } text ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
                Assert.Equal(onScreen, inLine);
            }
        }

        await analyses.AnalyzeAsync(project, head.Commit, Ct);
        await AssertSameCountAsync(1);

        await catalog.GenerateAsync(project, DiagramKind.Solution, string.Empty, Ct);
        await AssertSameCountAsync(1);

        builder.Graph = DiagramFixtures.Graph(appName: "Renamed", withInference: false);
        await analyses.AnalyzeAsync(project, head.Commit, Ct);
        await AssertSameCountAsync(2);

        await catalog.GenerateAsync(project, DiagramKind.Solution, string.Empty, Ct);
        await AssertSameCountAsync(1);
    }

    [Fact]
    public async Task A_repository_with_nothing_to_report_gives_a_line_of_zero_and_no_fix()
    {
        var head = new FixedHead("fixed", Phase6Host.Commit);
        using var host = Phase6Host.Create(services =>
        {
            services.AddSingleton<IRepositoryHistoryReader>(head);
            services.AddSingleton<IRepositorySnapshots>(new FilesSnapshots(("README.md", "# sin enlaces\n")));
            services.AddSingleton<ICodeGraphBuilder>(new MovingGraphBuilder(DiagramFixtures.Graph(withInference: false)));
        });
        var root = host.Temp.CreateDirectory("limpio");
        var project = await host.Provider.GetRequiredService<IProjectWorkspace>().OpenAsync(root, Ct);
        await host.Provider.GetRequiredService<ICommitAnalysisService>().AnalyzeAsync(project, head.Commit, Ct);

        var check = Assert.Single(await host.Provider.GetServices<IDoctorCheckSource>().OfType<DiscrepanciesCheckSource>().Single().CheckAsync(project, Ct));

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Equal("0", check.Fact("count"));
        Assert.Null(check.FixCode);
    }
}
