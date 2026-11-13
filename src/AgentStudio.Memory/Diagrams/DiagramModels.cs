using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Git;
using AgentStudio.Application.Graph;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Graph;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Memory.Diagrams;

/// <summary>
/// Where the model of the solution comes from: the analysis of the commit the project is at (docs/20 D-19), by its port. «Find» reads only what is cached and never
/// starts the copy nor the analysis; «analyze» is for the person's own order (generate a diagram) and may take minutes.
/// </summary>
public sealed class GraphModels(ICommitAnalysisService? analysis, IRepositoryHistoryReader? git)
{
    /// <summary>The commit the project is at, or null when there is no repository, no commit or no port to ask.</summary>
    public async Task<string?> HeadAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (git is null)
        {
            return null;
        }

        var summary = await git.SummaryAsync(project, ct).ConfigureAwait(false);
        return summary.Accepted && summary.HeadCommit is { } head && TraceRef.IsFullCommitId(head) ? head : null;
    }

    /// <summary>The model of the commit the project is at: the cached one, or, with <paramref name="analyze"/>, the one calculated now. Null when there is none.</summary>
    public async Task<CodeGraph?> LoadAsync(ProjectContext project, bool analyze, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (analysis is null || await HeadAsync(project, ct).ConfigureAwait(false) is not { } head)
        {
            return null;
        }

        var found = await analysis.FindAsync(project, head, ct).ConfigureAwait(false);
        if (found is null && analyze)
        {
            found = await analysis.AnalyzeAsync(project, head, ct).ConfigureAwait(false);
        }

        return found?.Graph;
    }
}

/// <summary>Where the models of a run come from: its entries in the verified log and its plan (docs/20 D-23).</summary>
public sealed class RunModels(IDecisionRecorder? recorder, IPlanStore? plans)
{
    /// <summary>The runs of the project that have a start, newest first. A log that does not verify fails closed.</summary>
    /// <exception cref="DecisionLogIntegrityException">The log of the project does not verify.</exception>
    internal async Task<IReadOnlyList<RunLog>> RunsAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (recorder is null)
        {
            return [];
        }

        var state = await recorder.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (!state.Verification.IsValid)
        {
            throw new DecisionLogIntegrityException(project.ProjectId, state.Verification);
        }

        return RunLogReader.Read(state.Entries);
    }

    internal async Task<PlanDefinition?> FindPlanAsync(ProjectContext project, string planHash, CancellationToken ct) =>
        plans is null ? null : await plans.FindAsync(project.ProjectId, planHash, ct).ConfigureAwait(false);
}

/// <summary>What a render has to read: the model of the solution when there is one, and the runs, read once however many diagrams of a run are asked.</summary>
internal sealed class RenderContext(ProjectContext project, CodeGraph? graph, RunModels? runModels, DateTimeOffset at)
{
    private IReadOnlyList<RunLog>? _runs;

    public ProjectContext Project { get; } = project;

    public CodeGraph? Graph { get; } = graph;

    public DateTimeOffset At { get; } = at;

    public async Task<IReadOnlyList<RunLog>> RunsAsync(CancellationToken ct) =>
        _runs ??= runModels is null ? [] : await runModels.RunsAsync(Project, ct).ConfigureAwait(false);

    public Task<PlanDefinition?> PlanAsync(string planHash, CancellationToken ct) =>
        runModels is null ? Task.FromResult<PlanDefinition?>(null) : runModels.FindPlanAsync(Project, planHash, ct);
}

/// <summary>A diagram made and the commit it describes (null for the diagrams of a run).</summary>
internal sealed record RenderedDiagram(DiagramSource Source, string? Describes);

/// <summary>The one place that turns a kind and a subject into a diagram: the generators, the catalog and the check of «desactualizado» all go through here.</summary>
internal static class DiagramRenderer
{
    /// <summary>The diagram, or null when what it is made from is not there (the project left the model, the run has no entry, the plan is not stored, or there is no model of the solution).</summary>
    public static async Task<RenderedDiagram?> RenderAsync(DiagramRef diagram, RenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        ArgumentNullException.ThrowIfNull(context);
        switch (diagram.Kind)
        {
            case DiagramKind.Solution:
                return context.Graph is { } solution ? new RenderedDiagram(GraphDiagrams.Solution(solution, context.At), solution.Commit) : null;
            case DiagramKind.ProjectNamespaces:
                if (context.Graph is not { } graph || !graph.Nodes.Any(n => n.Kind == GraphNodeKind.Project && string.Equals(n.Id, diagram.Subject, StringComparison.Ordinal)))
                {
                    return null;
                }

                return new RenderedDiagram(GraphDiagrams.ProjectNamespaces(graph, diagram.Subject, context.At), graph.Commit);
            case DiagramKind.RunPlan:
                var run = (await context.RunsAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => string.Equals(r.Run, diagram.Subject, StringComparison.Ordinal));
                if (run is null || await context.PlanAsync(run.PlanHash, ct).ConfigureAwait(false) is not { } plan)
                {
                    return null;
                }

                return new RenderedDiagram(RunDiagrams.Plan(run.Run, plan, context.At), null);
            case DiagramKind.RunSequence:
                var logged = (await context.RunsAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => string.Equals(r.Run, diagram.Subject, StringComparison.Ordinal));
                return logged is null ? null : new RenderedDiagram(RunDiagrams.Sequence(logged, context.At), null);
            default:
                throw new ArgumentOutOfRangeException(nameof(diagram), diagram.Kind, "Unknown kind of diagram.");
        }
    }
}

/// <summary>Base of the generators of the port <see cref="IDiagramGenerator"/>: it loads the model (calculating it if the person asks to generate) and renders it.</summary>
public abstract class DiagramGeneratorBase(GraphModels graphs, RunModels runs, TimeProvider time) : IDiagramGenerator
{
    public abstract DiagramKind Kind { get; }

    public async Task<DiagramSource> GenerateAsync(ProjectContext project, string subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var diagram = new DiagramRef(Kind, subject);
        var graph = Kind is DiagramKind.Solution or DiagramKind.ProjectNamespaces ? await graphs.LoadAsync(project, analyze: true, ct).ConfigureAwait(false) : null;
        var rendered = await DiagramRenderer.RenderAsync(diagram, new RenderContext(project, graph, runs, time.GetUtcNow()), ct).ConfigureAwait(false);
        return rendered?.Source ?? throw new InvalidOperationException("There is nothing to draw this diagram from: the model it is made of is not available.");
    }
}

/// <summary>The diagram of the solution (docs/20 D-23).</summary>
public sealed class SolutionDiagramGenerator(GraphModels graphs, RunModels runs, TimeProvider time) : DiagramGeneratorBase(graphs, runs, time)
{
    public override DiagramKind Kind => DiagramKind.Solution;
}

/// <summary>The diagram of the namespaces of one project.</summary>
public sealed class ProjectNamespacesDiagramGenerator(GraphModels graphs, RunModels runs, TimeProvider time) : DiagramGeneratorBase(graphs, runs, time)
{
    public override DiagramKind Kind => DiagramKind.ProjectNamespaces;
}

/// <summary>The diagram of the plan of a run.</summary>
public sealed class RunPlanDiagramGenerator(GraphModels graphs, RunModels runs, TimeProvider time) : DiagramGeneratorBase(graphs, runs, time)
{
    public override DiagramKind Kind => DiagramKind.RunPlan;
}

/// <summary>The diagram of the sequence of agents, orchestrator and judge of a run.</summary>
public sealed class RunSequenceDiagramGenerator(GraphModels graphs, RunModels runs, TimeProvider time) : DiagramGeneratorBase(graphs, runs, time)
{
    public override DiagramKind Kind => DiagramKind.RunSequence;
}
