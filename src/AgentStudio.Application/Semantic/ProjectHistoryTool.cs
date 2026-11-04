using AgentStudio.Application.Decisions;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Semantic;

// Phase 12, integration (docs/17 D-06 and F12-9; ADR-0024 and its addendum of 2026-11-03; ADR-0047): the planner's tool
// search_project_history. Like the assisted skill recommendation (docs/14 D-30), Agent Studio writes a snapshot of what is
// trusted to a file of its own, outside every project, and the local MCP server reads it with --history: the server never
// sees the database nor the index, and never loads the model. Only a read-only launch of the planner gets it.

/// <summary>The snapshot file of one planning (<see cref="ProjectHistorySnapshot"/>), outside every project.</summary>
public sealed record ProjectHistoryFolder(string SnapshotPath);

/// <summary>
/// Creates and removes the snapshot files of the planner's tool. Application does no file I/O of its own, so the
/// composition root decides where they live (under the app's local data, never in a repository), as
/// <see cref="ISkillRecommendationFolders"/> does for the assisted recommendation.
/// </summary>
public interface IProjectHistoryFolders
{
    /// <summary>A new folder with <paramref name="snapshotJson"/> written with <c>CreateNew</c>.</summary>
    Task<ProjectHistoryFolder> CreateAsync(string snapshotJson, CancellationToken ct);

    /// <summary>Removes what <see cref="CreateAsync"/> made. Safe to call on every path, and never throws.</summary>
    void Delete(ProjectHistoryFolder folder);
}

/// <summary>
/// Where the local MCP server is (<see cref="ServerExecutable"/>, the absolute path of <c>AgentStudio.SkillsMcp.exe</c>,
/// decided by the composition root) and how many fragments a snapshot carries at most (the newest).
/// </summary>
public sealed record ProjectHistoryToolOptions(string ServerExecutable)
{
    public int MaxFragments { get; init; } = ProjectHistoryNames.MaxFragments;
}

/// <summary>
/// What a launch of the planner gets of the history tool: the server to launch (<see cref="Server"/>) over a snapshot that
/// lives until the offer is disposed, or why it gets none (<see cref="Withheld"/>, in Spanish). Disposing it deletes the
/// snapshot; it is safe more than once.
/// </summary>
public sealed class ProjectHistoryOffer : IDisposable
{
    private readonly IProjectHistoryFolders? _folders;
    private ProjectHistoryFolder? _folder;

    private ProjectHistoryOffer(McpServerSpec? server, string? withheld, IProjectHistoryFolders? folders, ProjectHistoryFolder? folder)
    {
        Server = server;
        Withheld = withheld;
        _folders = folders;
        _folder = folder;
    }

    /// <summary>The server with <c>--history</c> over the snapshot; null when the launch gets no tool.</summary>
    public McpServerSpec? Server { get; }

    /// <summary>Why the launch gets no tool, in Spanish; null when it gets it.</summary>
    public string? Withheld { get; }

    /// <summary>Whether the launch gets the tool.</summary>
    public bool Offered => Server is not null;

    /// <summary>No tool, for <paramref name="reason"/>.</summary>
    public static ProjectHistoryOffer None(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ProjectHistoryOffer(null, reason, null, null);
    }

    /// <summary>The tool over <paramref name="folder"/>, which disposing the offer deletes through <paramref name="folders"/>.</summary>
    public static ProjectHistoryOffer Of(McpServerSpec server, IProjectHistoryFolders folders, ProjectHistoryFolder folder)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(folder);
        return new ProjectHistoryOffer(server, null, folders, folder);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _folder, null) is { } folder)
        {
            _folders!.Delete(folder);
        }
    }
}

/// <summary>
/// The planner's tool <c>search_project_history</c> (docs/17 D-06; F12-9). Only a launch of the planner (an agent with the
/// role <see cref="AgentRole.Orchestrator"/> and <see cref="MemoryPolicy.ReadSelected"/>) that does not write, has no shell
/// and no network, on Claude Code (the only CLI that launches the local MCP servers), gets it: never an executor, a judge
/// or an auditor (F12-8), nor a launch that writes. Anything else gets an offer without a server and the reason.
/// </summary>
public interface IProjectHistoryTool
{
    /// <exception cref="DecisionLogIntegrityException">The decision log does not verify: nothing is offered nor written.</exception>
    Task<ProjectHistoryOffer> OfferAsync(ProjectContext project, AgentDefinition agent, PermissionProfile permissions, CancellationToken ct);
}

/// <summary>
/// The tool over the semantic index (<see cref="ISemanticIndex.SnapshotAsync"/>: only what may reach a prompt, trusted and
/// never a daily summary) and the folders of its snapshots. A snapshot without fragments is not offered (there is nothing
/// to find), and one that cannot be taken or written leaves the planner without the tool, never without its planning; only
/// a log that does not verify stops it, as it stops the memory.
/// </summary>
public sealed class ProjectHistoryTool : IProjectHistoryTool
{
    /// <summary>Why a launch that is not the planner's gets no tool.</summary>
    public const string NotThePlannerReason = "La herramienta del historial solo es para el planificador.";

    /// <summary>Why a planner without memory gets no tool.</summary>
    public const string NoMemoryReason = "El orquestador no recibe memoria en la plantilla: planifica sin la herramienta del historial.";

    /// <summary>Why a launch that writes, has a shell or has network gets no tool (docs/14 D-30).</summary>
    public const string NotReadOnlyReason = "La herramienta del historial solo va en lanzamientos de solo lectura, sin shell ni red.";

    /// <summary>Why a project with nothing trusted yet gets no tool.</summary>
    public const string EmptyReason = "El proyecto aún no tiene historial de confianza: el orquestador planifica sin la herramienta del historial.";

    private readonly ISemanticIndex _index;
    private readonly IProjectHistoryFolders _folders;
    private readonly ProjectHistoryToolOptions _options;

    public ProjectHistoryTool(ISemanticIndex index, IProjectHistoryFolders folders, ProjectHistoryToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServerExecutable);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFragments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxFragments, ProjectHistoryNames.MaxFragments);
        _index = index;
        _folders = folders;
        _options = options;
    }

    /// <summary>Why <paramref name="provider"/> gets no tool: it does not launch the local MCP servers of Agent Studio.</summary>
    public static string NoMcpReason(ProviderId provider) =>
        $"{provider} no carga los servidores MCP locales de Agent Studio: el orquestador planifica sin la herramienta del historial.";

    /// <summary>Why the snapshot could not be taken or written.</summary>
    public static string FailedReason(string problem) =>
        $"No se pudo preparar el historial del proyecto ({problem.TrimEnd().TrimEnd('.')}): el orquestador planifica sin la herramienta.";

    public async Task<ProjectHistoryOffer> OfferAsync(ProjectContext project, AgentDefinition agent, PermissionProfile permissions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(permissions);
        if (agent.Role != AgentRole.Orchestrator)
        {
            return ProjectHistoryOffer.None(NotThePlannerReason);
        }

        if (agent.Memory != MemoryPolicy.ReadSelected)
        {
            return ProjectHistoryOffer.None(NoMemoryReason);
        }

        if (!ProjectHistoryLaunch.IsReadOnly(permissions))
        {
            return ProjectHistoryOffer.None(NotReadOnlyReason);
        }

        if (agent.Model.Provider != ProviderId.ClaudeCode)
        {
            return ProjectHistoryOffer.None(NoMcpReason(agent.Model.Provider));
        }

        ProjectHistoryFolder folder;
        try
        {
            var snapshot = await _index.SnapshotAsync(project, _options.MaxFragments, ct).ConfigureAwait(false);
            if (snapshot.Fragments.Count == 0)
            {
                return ProjectHistoryOffer.None(EmptyReason);
            }

            folder = await _folders.CreateAsync(ProjectHistorySnapshotJson.Write(snapshot), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (DecisionLogIntegrityException or OperationCanceledException))
        {
            // The tool is a help for the planner, not a condition of the planning: without it, it plans as before.
            return ProjectHistoryOffer.None(FailedReason(ex.Message));
        }

        return ProjectHistoryOffer.Of(ProjectHistoryLaunch.Server(_options.ServerExecutable, folder.SnapshotPath), _folders, folder);
    }
}

/// <summary>How the history tool goes into a launch (ADR-0024, addendum of 2026-11-03).</summary>
public static class ProjectHistoryLaunch
{
    /// <summary>The allow rule of the tool: <c>mcp__agentstudio__search_project_history</c>.</summary>
    public static string AllowRule { get; } = SkillsMcpNames.AllowRule(ProjectHistoryNames.Tool);

    /// <summary>A profile that does not write, has no shell and no network: the only one a local MCP server may go to.</summary>
    public static bool IsReadOnly(PermissionProfile permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return !permissions.AllowFileWrites && !permissions.AllowShell && !permissions.AllowNetwork;
    }

    /// <summary>The server of Agent Studio with <c>--history &lt;snapshot&gt;</c> alone, and the allow rule of its tool.</summary>
    public static McpServerSpec Server(string serverExecutable, string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        return new McpServerSpec(SkillsMcpNames.Server, serverExecutable, [ProjectHistoryNames.SnapshotArgument, snapshotPath], [AllowRule]);
    }

    /// <summary>
    /// <paramref name="request"/> with the history server <paramref name="history"/>. A launch that already has the server of
    /// Agent Studio (with <c>--snapshot</c> of the skills catalog) keeps it, with <c>--history</c> and the rule of the tool
    /// added: one server, both snapshots (ADR-0024, addendum). Any other server stays as it is.
    /// </summary>
    /// <exception cref="ArgumentException">The launch writes, has a shell or has network (docs/14 D-30), or already has a history.</exception>
    public static AgentRunRequest With(AgentRunRequest request, McpServerSpec history)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(history);
        if (!IsReadOnly(request.Permissions))
        {
            throw new ArgumentException("The history tool only goes to a read-only launch: without file writes, shell or network (docs/14 D-30).", nameof(request));
        }

        var servers = request.McpServers ?? [];
        var existing = servers.FirstOrDefault(s => string.Equals(s.Name, history.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return request with { McpServers = [.. servers, history] };
        }

        if (existing.Arguments.Contains(ProjectHistoryNames.SnapshotArgument, StringComparer.Ordinal))
        {
            throw new ArgumentException("The launch already has a history snapshot.", nameof(request));
        }

        var merged = existing with
        {
            Arguments = [.. existing.Arguments, .. history.Arguments],
            AllowedTools = [.. existing.AllowedTools, .. history.AllowedTools.Where(rule => !existing.AllowedTools.Contains(rule, StringComparer.Ordinal))],
        };
        return request with { McpServers = [.. servers.Select(s => ReferenceEquals(s, existing) ? merged : s)] };
    }
}
