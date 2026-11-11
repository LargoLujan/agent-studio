using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Team;

// Phase 13 (docs/17 D-07 to D-11): the Team layer of the canvas. It is the template of agents seen as a graph, not a new
// model: editing it is editing the template (its RosterHash changes and the approval of the plan is withdrawn). No cable of
// the team enters the plan: the graph knows no task, and nothing here reaches BuildTasks nor DependsOn.

/// <summary>The limits of a team (docs/17 D-09), seen while it is assembled.</summary>
public static class TeamLimits
{
    /// <summary>One orchestrator, the one that plans.</summary>
    public const int MaxOrchestrators = 1;

    /// <summary>
    /// Up to 8 executors: a proposed value that the measurement of the schema of the plan fixes, because the list of agents
    /// goes into its enums and the schema cannot pass 16 KB (ADR-0013).
    /// </summary>
    public const int MaxExecutors = 8;

    /// <summary>At least one judge, of a vendor other than the orchestrator's and the executors' (ADR-0014).</summary>
    public const int MinJudges = 1;

    /// <summary>
    /// The orchestrator is the one that plans: a team that has lost it cannot prepare a plan. <see cref="TeamGraph.Validate"/> does not
    /// ask for it (a template read from the store may lack one, and the layer offers to add it); <see cref="TeamRemoval"/> does not let
    /// a removal take it away.
    /// </summary>
    public const int MinOrchestrators = 1;
}

/// <summary>
/// What taking agents out of the template must not leave behind (docs/17 D-09, P48): the team keeps the orchestrator that plans and
/// the judge that reviews. Pure and about the removal, not about the team as a whole: a team that already lacked one is not refused
/// for it when something else is removed. Everything else a removal could break (a cable to the agent, a task assigned to it) is
/// seen by the graph and the validator of the plan after the template is read again.
/// </summary>
public static class TeamRemoval
{
    /// <summary>The team has no orchestrator after a removal that took the last one.</summary>
    public const string OrchestratorRequired = "team.orchestrator-required";

    /// <summary>The team has no judge after a removal that took the last one (the same code as <see cref="TeamGraph.Validate"/>).</summary>
    public const string JudgeRequired = TeamGraph.JudgeRequired;

    /// <summary>
    /// The codes of the roles a change leaves empty, as stable codes in a fixed order; an empty list means the removal is acceptable.
    /// </summary>
    /// <param name="before">The template as it is.</param>
    /// <param name="after">The template as it would be once the change (the agents it saves and the ones it takes out) is done.</param>
    public static IReadOnlyList<string> Problems(IReadOnlyList<AgentDefinition> before, IReadOnlyList<AgentDefinition> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var problems = new List<string>();
        if (TookOut(before, after, AgentRole.Orchestrator) && after.Count(a => a.Role == AgentRole.Orchestrator) < TeamLimits.MinOrchestrators)
        {
            problems.Add(OrchestratorRequired);
        }

        if (TookOut(before, after, AgentRole.Judge) && new TeamGraph([.. after.Select(a => new AgentNode(a))], null, []).Validate().Contains(JudgeRequired))
        {
            problems.Add(JudgeRequired);
        }

        return problems;
    }

    /// <summary>Whether an agent of <paramref name="role"/> in <paramref name="before"/> is not in <paramref name="after"/>.</summary>
    private static bool TookOut(IReadOnlyList<AgentDefinition> before, IReadOnlyList<AgentDefinition> after, AgentRole role) =>
        before.Any(b => b.Role == role && after.All(a => a.Id != b.Id));
}

/// <summary>What a role does in a team (docs/17 D-08 and D-11).</summary>
public static class TeamRoles
{
    /// <summary>Whether an agent of <paramref name="role"/> writes in the repository: it is an executor of the plan, and has the cable «Trabaja en».</summary>
    public static bool Writes(AgentRole role) => role is AgentRole.Executor or AgentRole.Tester or AgentRole.Documenter;

    /// <summary>Whether an agent of <paramref name="role"/> takes part only in debates: hidden by the filter «Solo la orquestación», on by default.</summary>
    public static bool IsDebateOnly(AgentRole role) => role is AgentRole.Architect or AgentRole.Critic;

    /// <summary>Whether an agent of <paramref name="role"/> judges: it reviews, and never writes (ADR-0006).</summary>
    public static bool Judges(AgentRole role) => role is AgentRole.Judge or AgentRole.Auditor;
}

/// <summary>A node of the Team layer for one agent of the template: the definition itself, so the node is the template.</summary>
public sealed record AgentNode(AgentDefinition Agent)
{
    public AgentId Id => Agent.Id;
}

/// <summary>What a cable of the Team layer means (docs/17 D-11). Each is made real by a rule, never by the drawing.</summary>
public enum TeamEdgeKind
{
    /// <summary>«Reparte», orchestrator → executor: who plans for whom. Only a drawing: it is the template.</summary>
    Distributes,

    /// <summary>
    /// «Revisa», producer → judge: who reviews whom. Made real by <c>RunLog.ProducerVendors</c> and
    /// <c>JudgeVendorNotIndependent</c>: a judge of the vendor of a producer draws the cable in the colour of an error, with
    /// its text (<see cref="TeamGraph.SharesVendor"/>).
    /// </summary>
    Reviews,

    /// <summary>
    /// «Trabaja en», agent → main folder of the repository: where it may write. Made real by the rule
    /// <c>WriteScopeOutsideAgentArea</c> of the validator (WP1): the scope of each task must fit in the folders of its agent;
    /// an agent without folders (<see cref="TeamEdge.ToFolder"/> null) has the whole project, as today.
    /// </summary>
    WorksIn,
}

/// <summary>
/// A cable of the Team layer: from an agent to another agent (<see cref="ToAgent"/>) or to the repository
/// (<see cref="TeamEdgeKind.WorksIn"/>: a main folder, <see cref="ToFolder"/>, or the whole project when it is null). The cable
/// «Entrega» (Integration → repository) starts in the Plan layer: the canvas draws it from the plan, and it is not part of the
/// team.
/// </summary>
public sealed record TeamEdge(TeamEdgeKind Kind, AgentId From, AgentId? ToAgent = null, string? ToFolder = null);

/// <summary>
/// The Team layer as a graph (docs/17 D-08): a node per agent of the template, the node of the repository and the cables.
/// Pure: it reads nothing and decides nothing beyond its own consistency (<see cref="Validate"/>). Whether a judge is
/// independent of the plan, or a scope fits the folders of its agent, is for the validator and the analyzer.
/// </summary>
public sealed record TeamGraph(IReadOnlyList<AgentNode> Agents, RepositoryNode? Repository, IReadOnlyList<TeamEdge> Edges)
{
    /// <summary>The code of <see cref="Validate"/> for a team with no judge.</summary>
    public const string JudgeRequired = "team.judge-required";

    /// <summary>The template the graph shows, in the order of its nodes.</summary>
    public IReadOnlyList<AgentDefinition> Roster => [.. Agents.Select(a => a.Agent)];

    /// <summary>The node of <paramref name="id"/>, or null.</summary>
    public AgentNode? Find(AgentId id) => Agents.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Whether a cable «Revisa» joins a producer and a judge of the same vendor: the cable is drawn as an error (D-11). False
    /// for any other cable, or when an end is not in the graph.
    /// </summary>
    public bool SharesVendor(TeamEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Kind == TeamEdgeKind.Reviews
            && edge.ToAgent is { } judge
            && Find(edge.From) is { } producer
            && Find(judge) is { } reviewer
            && producer.Agent.Model.Vendor == reviewer.Agent.Model.Vendor;
    }

    /// <summary>
    /// Returns every rule of the graph and of the limits of a team (D-09) it breaks, as stable codes in a fixed order; an
    /// empty list means it is consistent. A code never carries a name: the screen says which node from the graph.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Add(string code)
        {
            if (!problems.Contains(code))
            {
                problems.Add(code);
            }
        }

        var agents = Agents ?? [];
        if (agents.GroupBy(a => a.Id).Any(g => g.Count() > 1))
        {
            Add("team.duplicate-agent");
        }

        var roles = agents.Select(a => a.Agent.Role).ToList();
        if (roles.Count(r => r == AgentRole.Orchestrator) > TeamLimits.MaxOrchestrators)
        {
            Add("team.too-many-orchestrators");
        }

        if (roles.Count(r => r == AgentRole.Executor) > TeamLimits.MaxExecutors)
        {
            Add("team.too-many-executors");
        }

        if (roles.Count(r => r == AgentRole.Judge) < TeamLimits.MinJudges)
        {
            Add(JudgeRequired);
        }

        foreach (var main in Repository?.MainFolders ?? [])
        {
            if (!RepositoryNode.IsMainFolder(main))
            {
                Add("team.main-folder-invalid");
            }
        }

        foreach (var edge in Edges ?? [])
        {
            CheckEdge(edge, Add);
        }

        if ((Edges ?? []).GroupBy(e => e).Any(g => g.Count() > 1))
        {
            Add("team.duplicate-edge");
        }

        return problems;
    }

    private void CheckEdge(TeamEdge edge, Action<string> add)
    {
        if (Find(edge.From) is not { } from)
        {
            add("team.edge-unknown-agent");
            return;
        }

        switch (edge.Kind)
        {
            case TeamEdgeKind.Distributes:
                if (from.Agent.Role != AgentRole.Orchestrator || edge.ToFolder is not null || edge.ToAgent is not { } executor || Find(executor) is not { } to || !TeamRoles.Writes(to.Agent.Role))
                {
                    add(edge.ToAgent is { } id && Find(id) is null ? "team.edge-unknown-agent" : "team.distributes-not-orchestrator-to-executor");
                }

                break;
            case TeamEdgeKind.Reviews:
                if (TeamRoles.Judges(from.Agent.Role) || edge.ToFolder is not null || edge.ToAgent is not { } judge || Find(judge) is not { } reviewer || !TeamRoles.Judges(reviewer.Agent.Role))
                {
                    add(edge.ToAgent is { } id && Find(id) is null ? "team.edge-unknown-agent" : "team.reviews-not-producer-to-judge");
                }

                break;
            case TeamEdgeKind.WorksIn:
                if (!TeamRoles.Writes(from.Agent.Role) || edge.ToAgent is not null)
                {
                    add("team.works-in-not-writer");
                }
                else if (edge.ToFolder is { } folder && !(Repository?.MainFolders ?? []).Contains(folder, StringComparer.Ordinal))
                {
                    add("team.works-in-unknown-folder");
                }

                break;
            default:
                add("team.edge-kind-unknown");
                break;
        }
    }
}
