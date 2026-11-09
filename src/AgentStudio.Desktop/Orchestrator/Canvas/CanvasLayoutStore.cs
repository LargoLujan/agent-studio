using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>Where the nodes of a project's canvas were last placed, by task id. Only positions: the plan lives elsewhere.</summary>
public sealed record CanvasLayout(IReadOnlyDictionary<string, Point> Positions)
{
    public static CanvasLayout Empty { get; } = new(new Dictionary<string, Point>(StringComparer.Ordinal));

    /// <summary>
    /// Where the nodes of the Team layer were last placed (docs/17 D-08, layout version 2), by agent id and <c>repository</c>
    /// for the node of the repository. <see cref="Positions"/> are the Plan layer's: a version 1 file has only those.
    /// </summary>
    public IReadOnlyDictionary<string, Point> TeamPositions { get; init; } = new Dictionary<string, Point>(StringComparer.Ordinal);
}

/// <summary>Stores the canvas layout of each project.</summary>
public interface ICanvasLayoutStore
{
    /// <summary>The layout file of a project: <c>&lt;root&gt;\.agentstudio\canvas-layout.json</c>.</summary>
    string GetLayoutPath(string projectRoot);

    /// <summary>The saved layout; empty when there is none or the file cannot be read.</summary>
    CanvasLayout Load(string projectRoot);

    void Save(string projectRoot, CanvasLayout layout);
}

/// <summary>
/// JSON file in the project's <c>.agentstudio</c> folder, written atomically through <paramref name="files"/>, the policy
/// of the project's storage: a link left at <c>.agentstudio</c>, at its temporary folder or at the file refuses the save,
/// with nothing written through it (ADR-0010, addenda of 2026-10-18). A missing, damaged, newer or oversized file is read as
/// an empty layout: positions are a convenience and must never stop the canvas from opening.
/// </summary>
/// <remarks>
/// The same file keeps the draft of the orchestrator (<see cref="IPlanDraftStore"/>): the request and the plan on the canvas
/// that was not run. It is the canvas of this machine, excluded from git, and the checks of the user's repository around a
/// run or a debate leave it out as something Agent Studio writes; a file of its own there would not be. Each save keeps what
/// the other one wrote. A draft is read apart from the positions and checked whole: one that is damaged, too big
/// (<see cref="PlanDraftLimits"/>) or not saved by Agent Studio for this user on this machine and this folder (a file that
/// came with a clone, say) is ignored, and the reading says why.
/// </remarks>
public sealed class FileCanvasLayoutStore(IProjectStorageFiles files) : ICanvasLayoutStore, IPlanDraftStore
{
    public const string FileName = "canvas-layout.json";

    /// <summary>Version 2 (docs/17 D-08): <c>{version:2, plan:{…}, team:{…}}</c>. Version 1 (<c>nodes</c>) is read as the Plan layer.</summary>
    private const int CurrentVersion = 2;

    private const string Damaged = "no se puede leer: está dañado o incompleto";

    private const string TooBig = "es demasiado grande para traerlo de vuelta";

    private const string NotOurs = "no lo guardó Agent Studio en este equipo para esta carpeta";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string GetLayoutPath(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(Path.GetFullPath(projectRoot), ".agentstudio", FileName);
    }

    public CanvasLayout Load(string projectRoot)
    {
        if (Read(projectRoot, out _) is not { } stored)
        {
            return CanvasLayout.Empty;
        }

        var plan = Positions(stored.Plan);
        var team = Positions(stored.Team);
        return plan.Count == 0 && team.Count == 0 ? CanvasLayout.Empty : new CanvasLayout(plan) { TeamPositions = team };
    }

    private static Dictionary<string, Point> Positions(Dictionary<string, LayoutPoint?>? nodes)
    {
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var (id, point) in nodes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id) && point is not null && double.IsFinite(point.X) && double.IsFinite(point.Y))
            {
                positions[id] = new Point(point.X, point.Y);
            }
        }

        return positions;
    }

    private static Dictionary<string, LayoutPoint?> Written(IReadOnlyDictionary<string, Point> positions) =>
        positions
            .Where(p => double.IsFinite(p.Value.X) && double.IsFinite(p.Value.Y))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => (LayoutPoint?)new LayoutPoint(p.Value.X, p.Value.Y), StringComparer.Ordinal);

    public void Save(string projectRoot, CanvasLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Write(projectRoot, new LayoutFile(CurrentVersion, null, Read(projectRoot, out _)?.Draft, Written(layout.Positions), Written(layout.TeamPositions)));
    }

    public DraftReading ReadDraft(string projectRoot)
    {
        var stored = Read(projectRoot, out var tooBig);
        if (tooBig)
        {
            return new DraftReading(null, TooBig);
        }

        if (stored?.Draft is not { } element)
        {
            return DraftReading.None;
        }

        DraftJson? draft;
        try
        {
            draft = element.Deserialize<DraftJson>(Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new DraftReading(null, Damaged);
        }

        if (draft is null)
        {
            return new DraftReading(null, Damaged);
        }

        return string.Equals(draft.Owner, OwnerOf(projectRoot), StringComparison.Ordinal) ? draft.ToReading() : new DraftReading(null, NotOurs);
    }

    public void SaveDraft(string projectRoot, PlanDraft? draft)
    {
        var stored = Read(projectRoot, out _);
        JsonElement? saved = draft is null || draft.IsEmpty ? null : JsonSerializer.SerializeToElement(DraftJson.From(draft, OwnerOf(projectRoot)), Json);
        Write(projectRoot, new LayoutFile(CurrentVersion, null, saved, stored?.Plan ?? [], stored?.Team ?? []));
    }

    /// <summary>
    /// Who keeps a draft: this user on this machine, for this folder of the project. Only a hash of the three is written, and a
    /// draft that does not carry it is not this installation's.
    /// </summary>
    internal static string OwnerOf(string projectRoot)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)).ToUpperInvariant();
        var who = string.Join('\n', Environment.MachineName, Environment.UserName, folder);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(who)))[..32];
    }

    /// <summary>The file as it is stored; null when it is missing, cannot be read, is of another version or is too big.</summary>
    private LayoutFile? Read(string projectRoot, out bool tooBig)
    {
        tooBig = false;
        var file = GetLayoutPath(projectRoot);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length > PlanDraftLimits.MaxFileBytes)
            {
                tooBig = true;
                return null;
            }

            return JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(file), Json) switch
            {
                // Version 1 has only the Plan layer, under «nodes»: it is read as the Plan layer of version 2.
                { Version: 1 } old => old with { Nodes = null, Plan = old.Nodes },
                { Version: CurrentVersion } stored => stored,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void Write(string projectRoot, LayoutFile content) =>
        files.Replace(projectRoot, FileName, JsonSerializer.SerializeToUtf8Bytes(content, Json));

    private static bool TooLong(string? text) => text is { Length: > PlanDraftLimits.MaxText };

    /// <summary>The file: its version, the positions by task id and, kept as it is until it is read, the draft.</summary>
    private sealed record LayoutFile(
        int Version,
        Dictionary<string, LayoutPoint?>? Nodes,
        JsonElement? Draft = null,
        Dictionary<string, LayoutPoint?>? Plan = null,
        Dictionary<string, LayoutPoint?>? Team = null);

    private sealed record LayoutPoint(double X, double Y);

    /// <summary>The draft as it is stored: who saved it, every field of each task of the plan, and its proposal.</summary>
    private sealed record DraftJson(
        string? Owner,
        string? Request,
        string? Summary,
        IReadOnlyList<string?>? Risks,
        IReadOnlyList<DraftTaskJson?>? Tasks,
        DraftProposalJson? Proposal)
    {
        public static DraftJson From(PlanDraft draft, string owner) => new(
            owner,
            draft.Request,
            draft.Summary,
            draft.Risks,
            [.. draft.Tasks.Select(DraftTaskJson.From)],
            draft.Proposal is { } proposal ? new DraftProposalJson(proposal.Run.Value, proposal.PlanHash, proposal.Corrected, proposal.HistoryToolNotice) : null);

        /// <summary>The draft, checked whole: past a limit it is too big, and anything missing or out of place makes it damaged.</summary>
        public DraftReading ToReading()
        {
            var tasks = Tasks ?? [];
            var risks = Risks ?? [];
            if (tasks.Count > PlanDraftLimits.MaxTasks || risks.Count > PlanDraftLimits.MaxRisks || TooLong(Request) || TooLong(Summary)
                || risks.Any(TooLong) || tasks.Any(t => t is not null && t.IsTooBig()))
            {
                return new DraftReading(null, TooBig);
            }

            var specs = new List<TaskSpec>(tasks.Count);
            foreach (var task in tasks)
            {
                if (task?.ToDomain() is not { } spec)
                {
                    return new DraftReading(null, Damaged);
                }

                specs.Add(spec);
            }

            if (risks.Any(r => r is null))
            {
                return new DraftReading(null, Damaged);
            }

            PlanDraftProposal? proposal = null;
            if (Proposal is { } stored)
            {
                if (stored.ToDomain() is not { } origin)
                {
                    return new DraftReading(null, Damaged);
                }

                proposal = origin;
            }

            var draft = new PlanDraft(Request ?? string.Empty, Summary ?? string.Empty, [.. risks.OfType<string>()], specs, proposal);
            return draft.IsEmpty ? DraftReading.None : new DraftReading(draft);
        }
    }

    /// <summary>The proposal as it is stored; a draft of before the search in the history has no notice, and reads as null.</summary>
    private sealed record DraftProposalJson(string? Run, string? PlanHash, bool Corrected, string? HistoryToolNotice = null)
    {
        /// <summary>The proposal, or null when its run is not a run id, its hash is not a SHA-256 or its notice is too long.</summary>
        public PlanDraftProposal? ToDomain()
        {
            if (Run is null || !ContentHash.IsSha256Hex(PlanHash) || TooLong(HistoryToolNotice))
            {
                return null;
            }

            try
            {
                return new PlanDraftProposal(new RunId(Run), PlanHash!, Corrected, string.IsNullOrWhiteSpace(HistoryToolNotice) ? null : HistoryToolNotice);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private sealed record DraftTaskJson(
        string? Id,
        string? Title,
        WorkTaskKind Kind,
        string? Agent,
        string? Objective,
        string? Instructions,
        IReadOnlyList<DraftDependencyJson?>? DependsOn,
        JoinPolicy Join,
        IReadOnlyList<string?>? Covers,
        IReadOnlyList<string?>? WriteScope,
        IReadOnlyList<string?>? VerificationSteps,
        DraftPredicateJson? Condition,
        string? ToolId,
        int MaxRetries,
        TimeSpan? Timeout,
        WorkspaceMode Workspace)
    {
        private const int MaxListed = 200;

        public static DraftTaskJson From(TaskSpec spec) => new(
            spec.Id.Value,
            spec.Title,
            spec.Kind,
            spec.Agent?.Value,
            spec.Objective,
            spec.Instructions,
            [.. spec.DependsOn.Select(d => new DraftDependencyJson(d.Prerequisite.Value, d.Branch))],
            spec.Join,
            [.. spec.Covers.Select(r => r.Value)],
            spec.WriteScope,
            spec.VerificationSteps,
            DraftPredicateJson.From(spec.Condition),
            spec.ToolId,
            spec.MaxRetries,
            spec.Timeout,
            spec.Workspace);

        public bool IsTooBig() =>
            TooLong(Id) || TooLong(Title) || TooLong(Agent) || TooLong(Objective) || TooLong(Instructions) || TooLong(ToolId)
            || (DependsOn?.Count ?? 0) > MaxListed || (Covers?.Count ?? 0) > MaxListed || (WriteScope?.Count ?? 0) > MaxListed
            || (VerificationSteps?.Count ?? 0) > MaxListed
            || (Covers ?? []).Any(TooLong) || (WriteScope ?? []).Any(TooLong) || (VerificationSteps ?? []).Any(TooLong);

        /// <summary>The task, or null when a field is missing, blank where it cannot be, or out of its range.</summary>
        public TaskSpec? ToDomain()
        {
            var dependencies = DependsOn ?? [];
            var covers = Covers ?? [];
            var scope = WriteScope ?? [];
            var steps = VerificationSteps ?? [];
            if (string.IsNullOrWhiteSpace(Id) || !Enum.IsDefined(Kind) || !Enum.IsDefined(Join) || !Enum.IsDefined(Workspace)
                || (Agent is not null && string.IsNullOrWhiteSpace(Agent))
                || dependencies.Any(d => d is null || string.IsNullOrWhiteSpace(d.Prerequisite))
                || covers.Any(string.IsNullOrWhiteSpace) || scope.Any(s => s is null) || steps.Any(s => s is null)
                || MaxRetries is < 0 or > 100 || Timeout is { } limit && (limit <= TimeSpan.Zero || limit > TimeSpan.FromDays(1)))
            {
                return null;
            }

            var condition = Condition?.ToDomain();
            if (Condition is not null && condition is null)
            {
                return null;
            }

            try
            {
                return new TaskSpec(
                    new TaskId(Id),
                    Title ?? string.Empty,
                    Kind,
                    Agent is null ? null : new AgentId(Agent),
                    Objective ?? string.Empty,
                    Instructions ?? string.Empty,
                    [.. dependencies.Select(d => new DependencySpec(new TaskId(d!.Prerequisite!), d.Branch))],
                    Join,
                    [.. covers.Select(r => new RequirementId(r!))],
                    [.. scope.OfType<string>()],
                    [.. steps.OfType<string>()],
                    condition,
                    ToolId,
                    MaxRetries,
                    Timeout,
                    Workspace);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DomainRuleViolationException)
            {
                return null;
            }
        }
    }

    private sealed record DraftDependencyJson(string? Prerequisite, string? Branch);

    /// <summary>The closed set of condition predicates, with <see cref="Type"/> naming which one.</summary>
    private sealed record DraftPredicateJson(string? Type, string? Task, string? Key = null, string? Value = null, string? PathPrefix = null)
    {
        public static DraftPredicateJson? From(ConditionPredicate? predicate) => predicate switch
        {
            null => null,
            TaskSucceeded p => new(nameof(TaskSucceeded), p.Task.Value),
            TestsPassed p => new(nameof(TestsPassed), p.Task.Value),
            OutputEquals p => new(nameof(OutputEquals), p.Task.Value, Key: p.Key, Value: p.Value),
            PathsChanged p => new(nameof(PathsChanged), p.Task.Value, PathPrefix: p.PathPrefix),
            _ => throw new InvalidOperationException($"Unknown predicate type {predicate.GetType().Name}."),
        };

        /// <summary>The predicate, or null when its type is unknown or a field it needs is missing.</summary>
        public ConditionPredicate? ToDomain()
        {
            if (string.IsNullOrWhiteSpace(Task))
            {
                return null;
            }

            var task = new TaskId(Task);
            return Type switch
            {
                nameof(TaskSucceeded) => new TaskSucceeded(task),
                nameof(TestsPassed) => new TestsPassed(task),
                nameof(OutputEquals) when Key is not null && Value is not null => new OutputEquals(task, Key, Value),
                nameof(PathsChanged) when PathPrefix is not null => new PathsChanged(task, PathPrefix),
                _ => null,
            };
        }
    }
}
