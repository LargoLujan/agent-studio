using System.Collections.Concurrent;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagrams;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Graph;

namespace AgentStudio.Memory.Diagrams;

/// <summary>A diagram of the cache that is out of date: which one and the commit it describes.</summary>
internal sealed record StaleDiagram(DiagramRef Diagram, string? Describes);

/// <summary>
/// «Desactualizado» (docs/20 D-25, ADR-0045), decided in one place and only with the index of the cache: a diagram is out of date when the fingerprint of its model
/// now, or the version of the generators, is not the one the index keeps. No exported file and no cached text is read to decide: the index is the whole truth.
/// </summary>
internal sealed class DiagramStaleness(DiagramCacheStore store, RunModels runs, TimeProvider time)
{
    /// <summary>The diagrams of the index that are out of date against <paramref name="graph"/> (the model of the commit that is being analyzed) and the log.</summary>
    public async Task<IReadOnlyList<StaleDiagram>> FindAsync(ProjectContext project, CodeGraph graph, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(graph);
        var index = await store.LoadAsync(project.ProjectId, ct).ConfigureAwait(false);
        var context = new RenderContext(project, graph, runs, time.GetUtcNow());
        var stale = new List<StaleDiagram>();
        var memo = MemoFor(graph);
        foreach (var entry in index.Entries)
        {
            // The diagrams of the model of the solution depend on nothing but that model: what they give is worked out once per model and kept (never redrawn at every refresh).
            if (entry.Diagram.Kind is DiagramKind.Solution or DiagramKind.ProjectNamespaces)
            {
                var outcome = memo.Outcomes.TryGetValue(entry.Diagram, out var known) ? known : (memo.Outcomes[entry.Diagram] = await GraphOutcomeAsync(entry.Diagram, context, ct).ConfigureAwait(false));
                if (outcome.Failed || OfHash(entry, outcome.ModelHash) == DiagramState.Stale)
                {
                    stale.Add(new StaleDiagram(entry.Diagram, entry.Current.Describes));
                }

                continue;
            }

            RenderedDiagram? now;
            try
            {
                now = await DiagramRenderer.RenderAsync(entry.Diagram, context, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DecisionLogIntegrityException or ArgumentException)
            {
                // A log that does not verify, or a plan that does not read back, cannot say what a diagram is made of: it is not called current, and it stays visible as one
                // that is out of date (the same call the list of diagrams makes), never skipped as if all were well.
                stale.Add(new StaleDiagram(entry.Diagram, entry.Current.Describes));
                continue;
            }

            if (Of(entry, now) == DiagramState.Stale)
            {
                stale.Add(new StaleDiagram(entry.Diagram, entry.Current.Describes));
            }
        }

        return stale;
    }

    /// <summary>The state of an entry against the diagram its model gives now (null: the model is not there any more).</summary>
    public static DiagramState Of(IndexEntry? entry, RenderedDiagram? now) =>
        entry is null ? DiagramState.NotGenerated : OfHash(entry, now?.Source.ModelHash);

    /// <summary>The state of an entry against the fingerprint of the model its diagram is made of now (null: the model is not there any more).</summary>
    private static DiagramState OfHash(IndexEntry entry, string? modelHash) =>
        modelHash is not null
            && string.Equals(modelHash, entry.Current.ModelHash, StringComparison.Ordinal)
            && entry.Current.Generator == DiagramGrammar.GeneratorVersion
            ? DiagramState.Current
            : DiagramState.Stale;

    /// <summary>What drawing a diagram of the model of the solution gives to decide its state: the fingerprint (null when the model has no such diagram) or that it cannot be drawn.</summary>
    private readonly record struct GraphOutcome(string? ModelHash, bool Failed);

    /// <summary>The outcomes of one model of the solution, by diagram. Replaced as a whole when the model changes, so it never grows past the diagrams of one model.</summary>
    private sealed record GraphMemo(string ModelHash, ConcurrentDictionary<DiagramRef, GraphOutcome> Outcomes);

    private GraphMemo? _memo;

    private GraphMemo MemoFor(CodeGraph graph)
    {
        var hash = graph.ModelHash;
        if (_memo is { } current && string.Equals(current.ModelHash, hash, StringComparison.Ordinal))
        {
            return current;
        }

        var fresh = new GraphMemo(hash, new ConcurrentDictionary<DiagramRef, GraphOutcome>());
        _memo = fresh;
        return fresh;
    }

    private static async Task<GraphOutcome> GraphOutcomeAsync(DiagramRef diagram, RenderContext context, CancellationToken ct)
    {
        try
        {
            var rendered = await DiagramRenderer.RenderAsync(diagram, context, ct).ConfigureAwait(false);
            return new GraphOutcome(rendered?.Source.ModelHash, Failed: false);
        }
        catch (ArgumentException)
        {
            // What it is made of cannot be built into a diagram: it stays visible as out of date, the same call the list of diagrams makes.
            return new GraphOutcome(null, Failed: true);
        }
    }
}

/// <summary>
/// The diagrams of a project (docs/20 D-25 and D-26, ADR-0045): the cache on the computer with its index and the previous text of each diagram, the state of every
/// diagram, regenerating and undoing, and the export to the repository, which is an order of the person and never pisa a file that was changed outside without
/// asking. Nothing it wrote to the repository is read back.
/// </summary>
public sealed class DiagramCatalog : IDiagramCatalog, IDiagramTextReader
{
    /// <summary>The runs listed: the newest. The cache keeps the diagrams of older ones until they are deleted.</summary>
    internal const int MaxRuns = 50;

    private readonly DiagramCacheStore _store;
    private readonly GraphModels _graphs;
    private readonly RunModels _runs;
    private readonly IRunActivity? _activity;
    private readonly TimeProvider _time;

    /// <param name="cacheFolder">Where the diagrams are kept: <c>%LOCALAPPDATA%\AgentStudio\diagrams</c>; a test gives a temporary folder.</param>
    /// <param name="activity">Whether a run has the project: with one running, nothing is exported (docs/20 D-25). Without it nothing can say so, and nothing is exported.</param>
    public DiagramCatalog(string cacheFolder, GraphModels graphs, RunModels runs, IRunActivity? activity, TimeProvider time)
        : this(new DiagramCacheStore(cacheFolder), graphs, runs, activity, time)
    {
    }

    /// <summary>The catalog over a cache that other parts of Memory read too (the check of «desactualizado» and the ones of the documents), so one queue of one writes it.</summary>
    internal DiagramCatalog(DiagramCacheStore store, GraphModels graphs, RunModels runs, IRunActivity? activity, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _graphs = graphs;
        _runs = runs;
        _activity = activity;
        _time = time;
    }

    internal DiagramCacheStore Store => _store;

    public async Task<IReadOnlyList<DiagramStatus>> ListAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        // The work below (the files, the rendering of every diagram) never starts on the thread that called, which may be the one of the interface.
        await Task.Run(static () => { }).ConfigureAwait(false);
        await _store.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await _store.LoadLockedAsync(project.ProjectId, ct).ConfigureAwait(false);
            var graph = await _graphs.LoadAsync(project, analyze: false, ct).ConfigureAwait(false);
            var head = await _graphs.HeadAsync(project, ct).ConfigureAwait(false);
            var context = new RenderContext(project, graph, _runs, _time.GetUtcNow());
            IReadOnlyList<RunLog> runs;
            try
            {
                runs = await context.RunsAsync(ct).ConfigureAwait(false);
            }
            catch (DecisionLogIntegrityException)
            {
                runs = [];
            }

            var refs = new List<DiagramRef> { new(DiagramKind.Solution, string.Empty) };
            if (graph is not null)
            {
                refs.AddRange(graph.Nodes
                    .Where(n => n.Kind == GraphNodeKind.Project)
                    .Take(DiagramExportRequest.MaxDiagrams)
                    .Select(n => new DiagramRef(DiagramKind.ProjectNamespaces, n.Id)));
            }

            foreach (var run in runs.Take(MaxRuns))
            {
                refs.Add(new DiagramRef(DiagramKind.RunPlan, run.Run));
                refs.Add(new DiagramRef(DiagramKind.RunSequence, run.Run));
            }

            // What the cache holds and the lists above do not name (a project that left the solution, an older run) is still a diagram of the cache.
            refs.AddRange(index.Entries.Select(e => e.Diagram).Where(d => !refs.Contains(d)));

            var statuses = new List<DiagramStatus>(refs.Count);
            foreach (var diagram in refs)
            {
                statuses.Add(await StatusAsync(project, index, diagram, context, graph, head, ct).ConfigureAwait(false));
            }

            return statuses;
        }
        finally
        {
            _store.Gate.Release();
        }
    }

    public async Task<DiagramStatus> GenerateAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var diagram = new DiagramRef(kind, subject);
        await Task.Run(static () => { }).ConfigureAwait(false);

        // The model is read before the gate is taken: calculating the analysis of a commit can take minutes and listing must not wait behind it.
        var graph = kind is DiagramKind.Solution or DiagramKind.ProjectNamespaces ? await _graphs.LoadAsync(project, analyze: true, ct).ConfigureAwait(false) : null;
        var context = new RenderContext(project, graph, _runs, _time.GetUtcNow());
        var rendered = await DiagramRenderer.RenderAsync(diagram, context, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is nothing to draw this diagram from: the model it is made of is not available.");

        await _store.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await _store.LoadLockedAsync(project.ProjectId, ct).ConfigureAwait(false);
            var existing = index.Find(diagram);
            var text = rendered.Source.Mermaid;
            var textSha = ContentHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text));

            if (existing is not null && DiagramStaleness.Of(existing, rendered) == DiagramState.Current
                && await _store.ReadTextAsync(project.ProjectId, diagram, previous: false, existing.Current.TextSha256, ct).ConfigureAwait(false) is not null)
            {
                // A diagram that is up to date is not rewritten (docs/20 D-25): its date changes only when its model does.
                return await StatusAsync(project, index, diagram, context, graph, null, ct).ConfigureAwait(false);
            }

            // The text it replaces is kept next to it (one per diagram), so undoing puts it back byte by byte.
            IndexVersion? previous = null;
            if (existing is not null)
            {
                var old = await _store.ReadTextAsync(project.ProjectId, diagram, previous: false, existing.Current.TextSha256, ct).ConfigureAwait(false);
                if (old is not null)
                {
                    await _store.WriteTextAsync(project.ProjectId, diagram, previous: true, old, ct).ConfigureAwait(false);
                    previous = existing.Current;
                }
                else
                {
                    _store.DeleteText(project.ProjectId, diagram, previous: true);
                }
            }

            await _store.WriteTextAsync(project.ProjectId, diagram, previous: false, text, ct).ConfigureAwait(false);
            var version = new IndexVersion(rendered.Source.ModelHash, DiagramGrammar.GeneratorVersion, rendered.Describes, _time.GetUtcNow(), textSha);
            var next = index.With(new IndexEntry(diagram, version, previous, existing?.Export));
            await _store.SaveAsync(project.ProjectId, next, ct).ConfigureAwait(false);
            return await StatusAsync(project, next, diagram, context, graph, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _store.Gate.Release();
        }
    }

    public async Task<DiagramStatus> RestorePreviousAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var diagram = new DiagramRef(kind, subject);
        await Task.Run(static () => { }).ConfigureAwait(false);
        await _store.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await _store.LoadLockedAsync(project.ProjectId, ct).ConfigureAwait(false);
            if (index.Find(diagram) is not { Previous: { } previous } entry)
            {
                throw new InvalidOperationException("This diagram has no previous text to put back.");
            }

            var text = await _store.ReadTextAsync(project.ProjectId, diagram, previous: true, previous.TextSha256, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The previous text of this diagram is not in the cache any more.");
            await _store.WriteTextAsync(project.ProjectId, diagram, previous: false, text, ct).ConfigureAwait(false);
            var next = index.With(entry with { Current = previous, Previous = null });
            await _store.SaveAsync(project.ProjectId, next, ct).ConfigureAwait(false);
            _store.DeleteText(project.ProjectId, diagram, previous: true);

            var graph = kind is DiagramKind.Solution or DiagramKind.ProjectNamespaces ? await _graphs.LoadAsync(project, analyze: false, ct).ConfigureAwait(false) : null;
            return await StatusAsync(project, next, diagram, new RenderContext(project, graph, _runs, _time.GetUtcNow()), graph, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _store.Gate.Release();
        }
    }

    public async Task<string?> ReadAsync(ProjectContext project, DiagramRef diagram, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagram);
        await Task.Run(static () => { }).ConfigureAwait(false);
        await _store.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await _store.LoadLockedAsync(project.ProjectId, ct).ConfigureAwait(false);
            return index.Find(diagram) is { } entry
                ? await _store.ReadTextAsync(project.ProjectId, diagram, previous: false, entry.Current.TextSha256, ct).ConfigureAwait(false)
                : null;
        }
        finally
        {
            _store.Gate.Release();
        }
    }

    public async Task<DiagramExportResult> ExportAsync(ProjectContext project, DiagramExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        await Task.Run(static () => { }).ConfigureAwait(false);
        RequireNoRun(project);

        await _store.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Asked again inside the gate, and again before each file is written: a run may have started while this waited for its turn.
            RequireNoRun(project);
            var index = await _store.LoadLockedAsync(project.ProjectId, ct).ConfigureAwait(false);

            // Everything that can be wrong with the request is found before the first file is written.
            var texts = new Dictionary<DiagramRef, string>();
            foreach (var diagram in request.Diagrams)
            {
                var entry = index.Find(diagram) ?? throw new InvalidOperationException("A diagram that is not generated cannot be exported.");
                texts[diagram] = await _store.ReadTextAsync(project.ProjectId, diagram, previous: false, entry.Current.TextSha256, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The text of a diagram is not in the cache any more: generate it again.");
            }

            var written = new List<DiagramExportedFile>();
            var conflicts = new List<DiagramExportConflict>();
            try
            {
                foreach (var diagram in request.Diagrams)
                {
                    var entry = index.Find(diagram)!;
                    var name = DiagramExportFiles.FileName(diagram);
                    var path = request.Folder + "/" + name;
                    var probe = DiagramExportFiles.Probe(project.RootPath, path);
                    if (probe.Exists && !(probe.Sha256 is { } sha && entry.Export is { } known && known.Path == path && known.FileSha256 == sha))
                    {
                        var state = entry.Export?.Path == path ? DiagramExportState.ChangedOutside : DiagramExportState.Unknown;
                        switch (request.OnConflict)
                        {
                            case DiagramExportConflictChoice.Ask:
                                conflicts.Add(new DiagramExportConflict(diagram, path, state));
                                continue;
                            case DiagramExportConflictChoice.Skip:
                                continue;
                            case DiagramExportConflictChoice.SaveAsNew:
                                path = NextFreePath(project.RootPath, request.Folder, name);
                                break;
                            case DiagramExportConflictChoice.Overwrite when probe.IsLink:
                                // A link is never overwritten: it would be written through, outside the project.
                                conflicts.Add(new DiagramExportConflict(diagram, path, state));
                                continue;
                            default:
                                break;
                        }
                    }

                    RequireNoRun(project);
                    var bytes = DiagramExportFiles.Markdown(texts[diagram]);
                    DiagramExportFiles.Write(project.RootPath, path, bytes);
                    var exported = new DiagramExportedFile(diagram, path, DiagramExportFiles.Sha256(bytes));
                    written.Add(exported);
                    index = index.With(entry with { Export = new IndexExport(exported.Path, exported.Sha256, entry.Current.TextSha256) });

                    // The index is saved after each file, and not with the token of the call: a cancellation between writing and saving would leave the file written and unknown, to be taken for someone else's next time.
                    await _store.SaveAsync(project.ProjectId, index, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (written.Count > 0 && ex is not OperationCanceledException)
            {
                // What was written is indexed: the failure carries the list, so nothing that was exported is lost to the caller.
                throw new DiagramExportInterruptedException(written, conflicts, ex);
            }

            return new DiagramExportResult(written, conflicts);
        }
        finally
        {
            _store.Gate.Release();
        }
    }

    private void RequireNoRun(ProjectContext project)
    {
        if (_activity is null || _activity.IsBusy(project.ProjectId))
        {
            throw new DiagramExportBusyException();
        }
    }

    private static string NextFreePath(string root, string folder, string name)
    {
        for (var number = 2; number < 1000; number++)
        {
            var candidate = folder + "/" + DiagramExportFiles.Numbered(name, number);
            if (!DiagramExportFiles.Probe(root, candidate).Exists)
            {
                return candidate;
            }
        }

        throw new IOException("There is no free name for the copy of the diagram.");
    }

    private async Task<DiagramStatus> StatusAsync(
        ProjectContext project,
        DiagramIndex index,
        DiagramRef diagram,
        RenderContext context,
        CodeGraph? graph,
        string? head,
        CancellationToken ct)
    {
        var entry = index.Find(diagram);
        if (entry is null)
        {
            return new DiagramStatus(diagram, DiagramState.NotGenerated, ExportOf(project, null, diagram), null, null, null, hasPrevious: false);
        }

        DiagramState state;
        try
        {
            var now = await DiagramRenderer.RenderAsync(diagram, context, ct).ConfigureAwait(false);
            state = graph is null && diagram.Kind is DiagramKind.Solution or DiagramKind.ProjectNamespaces
                ? UnknownGraphState(entry, head ?? await _graphs.HeadAsync(project, ct).ConfigureAwait(false))
                : DiagramStaleness.Of(entry, now);
        }
        catch (DecisionLogIntegrityException)
        {
            // The log does not verify: what a run diagram is made of cannot be confirmed, so it is not called current.
            state = DiagramState.Stale;
        }
        catch (ArgumentException)
        {
            // What it is made of cannot be built into a diagram (the text would be over its limit, the plan does not read back): one row says so and the list goes on.
            state = DiagramState.Unavailable;
        }

        return new DiagramStatus(
            diagram,
            state,
            ExportOf(project, entry, diagram),
            entry.Current.Describes,
            entry.Current.ModelHash,
            entry.Current.GeneratedAt,
            entry.Previous is not null);
    }

    /// <summary>
    /// When there is no model of the commit the project is at (it was not analyzed yet), the diagram is current only if it describes that very commit: the same
    /// commit has the same model. Anything else cannot be confirmed, and nothing is called current for not having been looked at.
    /// </summary>
    private static DiagramState UnknownGraphState(IndexEntry entry, string? head) =>
        entry.Current.Generator == DiagramGrammar.GeneratorVersion && head is not null && string.Equals(entry.Current.Describes, head, StringComparison.Ordinal)
            ? DiagramState.Current
            : DiagramState.Stale;

    private static DiagramExportState ExportOf(ProjectContext project, IndexEntry? entry, DiagramRef diagram)
    {
        try
        {
            if (entry?.Export is not { } export)
            {
                // Nothing was exported from this computer: a file that is there anyway came from somewhere else (a clone). It is only looked at, never read.
                var path = DiagramExportRequest.DefaultFolder + "/" + DiagramExportFiles.FileName(diagram);
                return DiagramExportFiles.Probe(project.RootPath, path).Exists ? DiagramExportState.Unknown : DiagramExportState.NotExported;
            }

            var probe = DiagramExportFiles.Probe(project.RootPath, export.Path);
            if (!probe.Exists || probe.IsLink || !string.Equals(probe.Sha256, export.FileSha256, StringComparison.Ordinal))
            {
                return DiagramExportState.ChangedOutside;
            }

            return string.Equals(export.TextSha256, entry.Current.TextSha256, StringComparison.Ordinal) ? DiagramExportState.SameAsCache : DiagramExportState.Older;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DiagramExportState.Unknown;
        }
    }
}
