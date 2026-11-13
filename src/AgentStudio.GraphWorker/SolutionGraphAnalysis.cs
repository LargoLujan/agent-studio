using System.Xml;
using AgentStudio.Application.Graph;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Graph;

namespace AgentStudio.GraphWorker;

/// <summary>
/// The analysis of Auto Graph (docs/20 D-16 to D-20, ADR-0062): reads the solution, the projects and the C# files of a copy of a commit as text and
/// builds the static model. It evaluates nothing: no MSBuild, no <c>Directory.Build.props</c>, no globs, no <c>Compilation</c>. It opens a file only
/// through <see cref="RepositoryFiles"/>, so a path that leads out of the copy, or crosses a link, is never opened and ends as a gap. A file that fails
/// (too long, too slow, too deep, malformed, or the one an earlier launch died on) is a <see cref="GraphGapCodes.FileSkipped"/> gap and its project is
/// partial: the graph does not fall. What was cut by a cap is a gap too.
/// </summary>
internal sealed class SolutionGraphAnalysis : IGraphAnalysis
{
    /// <summary>The version of the generator: it enters the hash of the model, so a change of what the analysis reads raises it.</summary>
    public const int GeneratorVersion = 1;

    public const int MaxProjects = 2_000;

    public const int MaxCsFiles = 50_000;

    public const long MaxCsFileBytes = 1024 * 1024;

    public const long MaxProjectFileBytes = 2 * 1024 * 1024;

    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules"];

    private readonly AnalysisLimits _limits;
    private readonly ScanOptions _scan;
    private readonly Action<string> _onStuck;

    /// <param name="limits">The caps of the analysis; the real ones by default. A test gives small ones so each cap can be reached.</param>
    /// <param name="scan">How a file is parsed.</param>
    /// <param name="onStuck">What happens when the parser does not return for a file: by default the process ends itself with <see cref="GraphWorkerExitCodes.FileStuck"/> and the host skips the file.</param>
    public SolutionGraphAnalysis(AnalysisLimits? limits = null, ScanOptions? scan = null, Action<string>? onStuck = null)
    {
        _limits = limits ?? new AnalysisLimits();
        _scan = scan ?? ScanOptions.Default;
        _onStuck = onStuck ?? (_ => Environment.Exit(GraphWorkerExitCodes.FileStuck));
    }

    public Task<GraphBuild> AnalyzeAsync(GraphWorkerRequest request, Action<string> fileStarted, CancellationToken ct) =>
        Task.FromResult(new Run(request, fileStarted, ct, _limits, _scan, _onStuck).Execute());

    /// <summary>One analysis: the state is here so the class that implements the port keeps none.</summary>
    private sealed class Run
    {
        private readonly GraphWorkerRequest _request;
        private readonly Action<string> _fileStarted;
        private readonly CancellationToken _ct;
        private readonly RepositoryFiles _files;
        private readonly HashSet<string> _skipped;
        private readonly AnalysisLimits _limits;
        private readonly ScanOptions _scan;
        private readonly Action<string> _onStuck;
        private readonly HashSet<(string, string)> _limitGaps = [];

        private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _partial = new(StringComparer.Ordinal);
        private readonly List<GraphGap> _gaps = [];
        private readonly HashSet<(string, string, GraphEdgeKind)> _edgeKeys = [];
        private readonly List<GraphEdge> _edges = [];
        private readonly Dictionary<string, SortedSet<string>> _namespaceFiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProjectState> _projects = new(StringComparer.Ordinal);
        private readonly List<string> _projectOrder = [];
        private bool _gapLimitReported;
        private int _csFiles;

        public Run(GraphWorkerRequest request, Action<string> fileStarted, CancellationToken ct, AnalysisLimits limits, ScanOptions scan, Action<string> onStuck)
        {
            _limits = limits;
            _scan = scan;
            _onStuck = onStuck;
            _request = request;
            _fileStarted = fileStarted;
            _ct = ct;
            _files = new RepositoryFiles(request.Root);
            _skipped = new HashSet<string>(request.Skipped, StringComparer.OrdinalIgnoreCase);
        }

        public GraphBuild Execute()
        {
            var solution = ReadSolution(out var solutionId);
            var listed = new List<(string Path, string? Folder)>();
            if (solution is not null)
            {
                foreach (var folder in solution.Folders)
                {
                    EnsureFolder(solutionId!, folder);
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = 0;
                foreach (var entry in solution.Projects)
                {
                    if (++entries > _limits.MaxSolutionEntries)
                    {
                        LimitGap(solutionId!, solutionId!);
                        break;
                    }

                    var problem = RepositoryFiles.TryResolve(solutionId!, entry.RawPath, out var relative);
                    if (problem != PathProblem.None)
                    {
                        Gap(solutionId!, PathProblems.Code(problem), solutionId!, entry.Line, partial: true);
                        continue;
                    }

                    if (!relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seen.Add(relative))
                    {
                        Gap(solutionId!, GraphGapCodes.Duplicate, solutionId!, entry.Line, partial: false);
                        continue;
                    }

                    listed.Add((relative, entry.Folder));
                }
            }
            else
            {
                foreach (var path in FindProjects())
                {
                    listed.Add((path, null));
                }
            }

            foreach (var (path, folder) in listed)
            {
                if (_projects.Count >= _limits.MaxProjects)
                {
                    Gap(solutionId ?? path, GraphGapCodes.Limit, solutionId ?? path, null, partial: solutionId is not null);
                    break;
                }

                AddProject(path, listedInSolution: solutionId is not null, solutionId, folder);
            }

            // Projects that the listed ones reference, found by walking the references: they are read the same way and belong to no solution folder.
            for (var i = 0; i < _projectOrder.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                var state = _projects[_projectOrder[i]];
                if (state.Referenced is null)
                {
                    continue;
                }

                foreach (var target in state.Referenced)
                {
                    if (!_projects.ContainsKey(target) && _projects.Count < _limits.MaxProjects && _files.Probe(target, directory: false) == PathProblem.None)
                    {
                        AddProject(target, listedInSolution: false, solutionId, null);
                    }
                }
            }

            foreach (var path in _projectOrder.ToList())
            {
                ScanSources(_projects[path]);
            }

            AddReferences();
            AddNamespaceUses();
            return Assemble();
        }

        private SolutionDescription? ReadSolution(out string? id)
        {
            id = null;
            var (_, rootFiles, _) = _files.List(string.Empty);
            var solutions = rootFiles
                .Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.Ordinal)
                .ToList();
            if (solutions.Count == 0)
            {
                return null;
            }

            var path = solutions[0];
            id = path;
            AddNode(new GraphNode(path, GraphNodeKind.Solution, Path.GetFileNameWithoutExtension(path), path, false));
            foreach (var other in solutions.Skip(1))
            {
                Gap(path, GraphGapCodes.IgnoredSolution, other, null, partial: false);
            }

            if (_skipped.Contains(path))
            {
                Gap(path, GraphGapCodes.FileSkipped, path, null, partial: true);
                return null;
            }

            try
            {
                _fileStarted(path);
                using var stream = _files.OpenRead(path, MaxProjectFileBytes);
                return path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? SlnxReader.Read(stream) : SlnReader.Read(stream);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException or InvalidOperationException)
            {
                Gap(path, GraphGapCodes.FileSkipped, path, ex is XmlException xml && xml.LineNumber > 0 ? xml.LineNumber : null, partial: true);
                return null;
            }
        }

        /// <summary>The projects of a copy that has no solution file (or whose solution could not be read): every <c>.csproj</c> in it, in ordinal order, links left out.</summary>
        private IEnumerable<string> FindProjects()
        {
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(string.Empty);
            while (pending.Count > 0 && found.Count < _limits.MaxProjects)
            {
                _ct.ThrowIfCancellationRequested();
                var (folders, files, _) = _files.List(pending.Pop());
                found.AddRange(files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
                foreach (var folder in folders.Reverse())
                {
                    if (!SkippedFolders.Contains(folder[(folder.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(folder);
                    }
                }
            }

            return found.OrderBy(f => f, StringComparer.Ordinal).Take(_limits.MaxProjects);
        }

        private void AddProject(string path, bool listedInSolution, string? solutionId, string? folder)
        {
            if (_projects.ContainsKey(path))
            {
                return;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var node = new GraphNode(path, GraphNodeKind.Project, name, path, false);
            if (!AddNode(node))
            {
                LimitGap(solutionId ?? _nodes.Keys.FirstOrDefault() ?? path, solutionId ?? path);
                return;
            }

            var state = new ProjectState(path);
            _projects.Add(path, state);
            _projectOrder.Add(path);

            if (solutionId is not null && listedInSolution)
            {
                var parent = folder is null ? solutionId : EnsureFolder(solutionId, folder);
                AddEdge(parent, path, GraphEdgeKind.Contains);
            }

            switch (_files.Probe(path, directory: false))
            {
                case PathProblem.None:
                    break;
                case var problem:
                    Gap(path, PathProblems.Code(problem), path, null, partial: true);
                    state.Unreadable = true;
                    return;
            }

            if (_skipped.Contains(path))
            {
                Gap(path, GraphGapCodes.FileSkipped, path, null, partial: true);
                state.Unreadable = true;
                return;
            }

            ProjectDescription description;
            try
            {
                _fileStarted(path);
                using var stream = _files.OpenRead(path, MaxProjectFileBytes);
                description = CsprojReader.Read(stream);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException or InvalidOperationException)
            {
                Gap(path, GraphGapCodes.FileSkipped, path, ex is XmlException xml && xml.LineNumber > 0 ? xml.LineNumber : null, partial: true);
                state.Unreadable = true;
                return;
            }

            foreach (var line in description.UnevaluatedLines)
            {
                Gap(path, GraphGapCodes.Unevaluated, path, line, partial: true);
            }

            foreach (var propsFile in BuildPropsAbove(path))
            {
                Gap(path, GraphGapCodes.BuildProps, propsFile, null, partial: true);
            }

            state.Referenced = [];
            var references = 0;
            foreach (var reference in description.ProjectReferences)
            {
                if (++references > _limits.MaxReferencesPerProject)
                {
                    LimitGap(path, path);
                    break;
                }

                var problem = RepositoryFiles.TryResolve(path, reference.Value, out var target);
                if (problem == PathProblem.None && !target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    // A reference to something that is not a C# project (a shared project, a native one): nothing of it is read.
                    continue;
                }

                if (problem == PathProblem.None)
                {
                    problem = _files.Probe(target, directory: false);
                }

                if (problem != PathProblem.None)
                {
                    Gap(path, PathProblems.Code(problem), path, reference.Line, partial: true);
                    continue;
                }

                state.Referenced.Add(target);
            }

            foreach (var package in description.PackageReferences)
            {
                if (state.Packages.Count >= _limits.MaxPackagesPerProject)
                {
                    LimitGap(path, path);
                    break;
                }

                var id = "package:" + package.Value;
                try
                {
                    if (AddNode(new GraphNode(id, GraphNodeKind.Package, package.Value, null, false)))
                    {
                        state.Packages.Add(id);
                    }
                    else
                    {
                        LimitGap(path, path);
                        break;
                    }
                }
                catch (ArgumentException)
                {
                    Gap(path, GraphGapCodes.InvalidPath, path, package.Line, partial: true);
                }
            }
        }

        /// <summary>The <c>Directory.Build.props</c> and <c>.targets</c> in the folder of the project and above it, up to the root: they are named, never applied.</summary>
        private IEnumerable<string> BuildPropsAbove(string projectPath)
        {
            var folder = projectPath.Contains('/', StringComparison.Ordinal) ? projectPath[..projectPath.LastIndexOf('/')] : string.Empty;
            while (true)
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    var candidate = folder.Length == 0 ? name : folder + "/" + name;
                    if (_files.Probe(candidate, directory: false) == PathProblem.None)
                    {
                        yield return candidate;
                    }
                }

                if (folder.Length == 0)
                {
                    yield break;
                }

                folder = folder.Contains('/', StringComparison.Ordinal) ? folder[..folder.LastIndexOf('/')] : string.Empty;
            }
        }

        private string EnsureFolder(string solutionId, string fullPath)
        {
            // "/src/tests/" -> the folders "/src/" and "/src/tests/", each inside the one above (the solution holds the first).
            var segments = fullPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = solutionId;
            var current = "/";
            string id = parent;
            for (var i = 0; i < segments.Length && i < 64; i++)
            {
                current += segments[i] + "/";
                id = "folder:" + current;
                if (!_nodes.ContainsKey(id))
                {
                    try
                    {
                        if (!AddNode(new GraphNode(id, GraphNodeKind.SolutionFolder, segments[i], null, false)))
                        {
                            LimitGap(solutionId, solutionId);
                            return parent;
                        }
                    }
                    catch (ArgumentException)
                    {
                        Gap(solutionId, GraphGapCodes.InvalidPath, solutionId, null, partial: true);
                        return parent;
                    }

                    AddEdge(parent, id, GraphEdgeKind.Contains);
                }

                parent = id;
            }

            return id;
        }

        private void ScanSources(ProjectState project)
        {
            if (project.Unreadable)
            {
                return;
            }

            if (_request.WithoutSources)
            {
                // The last resort of the host: the projects are read and no source is, and each project says so.
                Gap(project.Path, GraphGapCodes.Limit, project.Path, null, partial: true);
                return;
            }

            var folder = project.Path.Contains('/', StringComparison.Ordinal) ? project.Path[..project.Path.LastIndexOf('/')] : string.Empty;
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                _ct.ThrowIfCancellationRequested();
                var current = pending.Pop();
                var (folders, files, sawLink) = _files.List(current);
                if (sawLink)
                {
                    Gap(project.Path, GraphGapCodes.Link, project.Path, null, partial: true);
                }

                foreach (var file in files)
                {
                    if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsGenerated(file))
                    {
                        continue;
                    }

                    if (_csFiles >= _limits.MaxCsFiles)
                    {
                        Gap(project.Path, GraphGapCodes.Limit, project.Path, null, partial: true);
                        return;
                    }

                    _csFiles++;
                    ScanFile(project, file);
                }

                foreach (var sub in folders.Reverse())
                {
                    var name = sub[(sub.LastIndexOf('/') + 1)..];
                    if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || ContainsProject(sub))
                    {
                        continue;
                    }

                    pending.Push(sub);
                }
            }
        }

        /// <summary>A folder with a project of its own belongs to that project, not to the one above.</summary>
        private bool ContainsProject(string folder) =>
            _files.List(folder).Files.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        private static bool IsGenerated(string file) =>
            file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

        private void ScanFile(ProjectState project, string file)
        {
            if (_skipped.Contains(file))
            {
                Gap(project.Path, GraphGapCodes.FileSkipped, file, null, partial: true);
                return;
            }

            string text;
            try
            {
                _fileStarted(file);
                using var stream = _files.OpenRead(file, MaxCsFileBytes);
                using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Gap(project.Path, GraphGapCodes.FileSkipped, file, null, partial: true);
                return;
            }

            var scanned = CSharpSyntaxScanner.TryScan(text, _scan, out var declarations);
            if (scanned == ScanFailure.Stuck)
            {
                // The parser did not return and cannot be stopped: the process ends itself and the host skips this file (the last one named in the progress).
                _onStuck(file);
            }

            if (scanned != ScanFailure.None)
            {
                Gap(project.Path, GraphGapCodes.FileSkipped, file, null, partial: true);
                return;
            }

            if (declarations.Truncated)
            {
                LimitGap(project.Path, file);
            }

            foreach (var declared in declarations.Namespaces)
            {
                var id = project.Path + "|" + declared.Name;
                if (!_nodes.ContainsKey(id))
                {
                    try
                    {
                        if (!AddNode(new GraphNode(id, GraphNodeKind.Namespace, declared.Name, null, false)))
                        {
                            LimitGap(project.Path, project.Path);
                            continue;
                        }
                    }
                    catch (ArgumentException)
                    {
                        Gap(project.Path, GraphGapCodes.FileSkipped, file, null, partial: true);
                        continue;
                    }

                    AddEdge(project.Path, id, GraphEdgeKind.Contains);
                    project.Namespaces.Add(declared.Name);
                }

                if (!_namespaceFiles.TryGetValue(id, out var set))
                {
                    _namespaceFiles[id] = set = new SortedSet<string>(StringComparer.Ordinal);
                }

                if (set.Count < NamespaceFiles.MaxFilesPerNamespace)
                {
                    set.Add(file);
                }
                else
                {
                    LimitGap(project.Path, project.Path);
                }

                project.Usings.Add((declared.Name, declared.Scope));
            }
        }

        private void AddReferences()
        {
            foreach (var path in _projectOrder)
            {
                var state = _projects[path];
                foreach (var target in state.Referenced ?? [])
                {
                    if (_nodes.ContainsKey(target) && target != path)
                    {
                        AddEdge(path, target, GraphEdgeKind.ProjectReference);
                    }
                }

                foreach (var package in state.Packages)
                {
                    AddEdge(path, package, GraphEdgeKind.PackageReference);
                }
            }
        }

        /// <summary>
        /// A namespace uses another when a file that declares it has a <c>using</c> of it (also a static one or an alias) and the other is declared in the
        /// solution (docs/20 D-17). Syntactic: where several projects declare the name, the one in the same project, else the ones the project reaches by
        /// its references, else all of them.
        /// </summary>
        private void AddNamespaceUses()
        {
            var declaredIn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in _projectOrder)
            {
                foreach (var name in _projects[path].Namespaces)
                {
                    if (!declaredIn.TryGetValue(name, out var list))
                    {
                        declaredIn[name] = list = [];
                    }

                    list.Add(path);
                }
            }

            foreach (var path in _projectOrder)
            {
                _ct.ThrowIfCancellationRequested();
                var project = _projects[path];
                var reach = Reachable(path);
                foreach (var (declaredName, scope) in project.Usings)
                {
                    foreach (var directive in scope.All())
                    {
                        var target = Resolve(directive, declaredName, declaredIn);
                        if (target is null)
                        {
                            continue;
                        }

                        var owners = declaredIn[target];
                        var reached = owners.Where(reach.Contains).ToList();
                        var chosen = owners.Contains(path, StringComparer.Ordinal) ? new List<string> { path } : reached.Count > 0 ? reached : owners;
                        foreach (var owner in chosen)
                        {
                            var from = path + "|" + declaredName;
                            var to = owner + "|" + target;
                            if (from != to && _nodes.ContainsKey(from) && _nodes.ContainsKey(to))
                            {
                                AddEdge(from, to, GraphEdgeKind.NamespaceUse);
                            }
                        }
                    }
                }
            }
        }

        private static string? Resolve(UsingDirective directive, string insideNamespace, Dictionary<string, List<string>> declaredIn)
        {
            // The names the directive can mean, innermost first: inside namespace A.B, "Sub" is A.B.Sub, then A.Sub, then Sub.
            var prefixes = new List<string>();
            for (var current = insideNamespace; current.Length > 0; current = current.Contains('.', StringComparison.Ordinal) ? current[..current.LastIndexOf('.')] : string.Empty)
            {
                prefixes.Add(current + ".");
            }

            prefixes.Add(string.Empty);
            foreach (var prefix in prefixes)
            {
                var candidate = prefix + directive.Name;
                if (declaredIn.ContainsKey(candidate))
                {
                    return candidate;
                }

                if (directive.IsStatic || directive.IsAlias)
                {
                    // A static using or an alias names a type: its namespace is the longest dotted prefix that is declared.
                    // The cut falls inside the name of the directive: the namespace the directive is inside is never what it names.
                    for (var cut = candidate.LastIndexOf('.'); cut > prefix.Length; cut = candidate.LastIndexOf('.', cut - 1))
                    {
                        if (declaredIn.ContainsKey(candidate[..cut]))
                        {
                            return candidate[..cut];
                        }
                    }
                }
            }

            return null;
        }

        private HashSet<string> Reachable(string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { path };
            var queue = new Queue<string>();
            queue.Enqueue(path);
            while (queue.Count > 0)
            {
                foreach (var next in _projects[queue.Dequeue()].Referenced ?? [])
                {
                    if (_projects.ContainsKey(next) && seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            return seen;
        }

        /// <summary>Adds a node; false (and nothing added) when the cap of nodes is reached: a cap truncates, it never throws.</summary>
        private bool AddNode(GraphNode node)
        {
            if (_nodes.ContainsKey(node.Id))
            {
                return true;
            }

            if (_nodes.Count >= Math.Min(_limits.MaxNodes, CodeGraph.MaxNodes))
            {
                return false;
            }

            _nodes.Add(node.Id, node);
            return true;
        }

        private void AddEdge(string from, string to, GraphEdgeKind kind)
        {
            if (from == to || !_nodes.ContainsKey(from) || !_nodes.ContainsKey(to) || _edgeKeys.Contains((from, to, kind)))
            {
                return;
            }

            if (_edges.Count >= Math.Min(_limits.MaxEdges, CodeGraph.MaxEdges))
            {
                LimitGap(_nodes.Keys.FirstOrDefault(k => _nodes[k].Kind == GraphNodeKind.Solution) ?? from, _nodes.Values.FirstOrDefault(n => n.Kind == GraphNodeKind.Solution)?.Path ?? (_nodes.TryGetValue(from, out var user) ? user.Path : null));
                return;
            }

            _edgeKeys.Add((from, to, kind));
            _edges.Add(new GraphEdge(from, to, kind, GraphEdgeOrigin.Static));
        }

        /// <summary>A cap was reached: a gap that says so, once for each node, and the node is partial. <paramref name="path"/> is the file that tells it; none is no gap.</summary>
        private void LimitGap(string nodeId, string? path)
        {
            if (path is not null && RepositoryPaths.IsSafeRelative(path) && _limitGaps.Add((nodeId, path)))
            {
                Gap(nodeId, GraphGapCodes.Limit, path, null, partial: true);
            }
        }

        private void Gap(string nodeId, string code, string path, int? line, bool partial)
        {
            if (_gaps.Count >= Math.Min(_limits.MaxGaps, CodeGraph.MaxGaps) - 1)
            {
                if (!_gapLimitReported)
                {
                    _gapLimitReported = true;
                    var node = _nodes.Keys.FirstOrDefault(k => _nodes[k].Kind == GraphNodeKind.Solution) ?? nodeId;
                    _gaps.Add(new GraphGap(node, GraphGapCodes.Limit, _nodes.TryGetValue(node, out var solution) && solution.Path is { } p ? p : path, null));
                }

                return;
            }

            if (!_nodes.ContainsKey(nodeId))
            {
                return;
            }

            _gaps.Add(new GraphGap(nodeId, code, path, line));
            if (partial)
            {
                _partial.Add(nodeId);
            }
        }

        private GraphBuild Assemble()
        {
            // The files that declare the namespaces: cut at the cap of the list, never failing; a gap says so.
            var files = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var total = 0;
            var cap = Math.Min(_limits.MaxNamespaceFilesTotal, NamespaceFiles.MaxTotal);
            foreach (var (id, paths) in _namespaceFiles.Where(e => _nodes.ContainsKey(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (total + paths.Count > cap)
                {
                    var solution = _nodes.Values.FirstOrDefault(n => n.Kind == GraphNodeKind.Solution);
                    var owner = solution ?? _nodes.Values.FirstOrDefault(n => n.Kind == GraphNodeKind.Project);
                    if (owner?.Path is { } ownerPath)
                    {
                        LimitGap(owner.Id, ownerPath);
                    }

                    break;
                }

                total += paths.Count;
                files[id] = [.. paths];
            }

            var nodes = _nodes.Values.Select(n => _partial.Contains(n.Id) ? new GraphNode(n.Id, n.Kind, n.Name, n.Path, true) : n).ToList();

            // A partial node always has a gap that says why; a node whose gap was dropped at the cap keeps its mark only if one remained.
            var gapped = _gaps.Select(g => g.NodeId).ToHashSet(StringComparer.Ordinal);
            nodes = [.. nodes.Select(n => n.Partial && !gapped.Contains(n.Id) ? new GraphNode(n.Id, n.Kind, n.Name, n.Path, false) : n)];
            var graph = new CodeGraph(_request.Commit, GeneratorVersion, nodes, _edges, _gaps);
            return new GraphBuild(graph, new NamespaceFiles(files));
        }
    }

    private sealed class ProjectState(string path)
    {
        public string Path { get; } = path;

        public bool Unreadable { get; set; }

        public List<string>? Referenced { get; set; }

        public List<string> Packages { get; } = [];

        public SortedSet<string> Namespaces { get; } = new(StringComparer.Ordinal);

        public List<(string Namespace, UsingScope Scope)> Usings { get; } = [];
    }
}
