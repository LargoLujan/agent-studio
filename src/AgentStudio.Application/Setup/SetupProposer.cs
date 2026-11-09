using AgentStudio.Application.Localization;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Application.Setup;

/// <summary>The stacks the configurator proposes a verification profile for (docs/17 D-26, step 5): the values of <see cref="SetupProjectProposal.Stack"/>.</summary>
public static class SetupStacks
{
    public const string DotNet = "dotnet";

    public const string Node = "node";

    public const string Python = "python";
}

/// <summary>
/// The ids of the agents the templates of the configurator name. The first three are the ones of the default template of a
/// project, so a project that has it already has them.
/// </summary>
public static class SetupAgents
{
    public static AgentId Orchestrator { get; } = new("orquestador");

    public static AgentId Executor { get; } = new("ejecutor");

    public static AgentId Judge { get; } = new("juez");

    /// <summary>The lighter executor of the medium template, for documentation and tests.</summary>
    public static AgentId LightExecutor { get; } = new("ejecutor-ligero");

    /// <summary>The reviewer of the large template: another pair of eyes on Codex, read-only like a judge.</summary>
    public static AgentId Reviewer { get; } = new("revisor");
}

/// <summary>The codes of the capabilities that change the threat model of a template (docs/17 D-37, ADR-0018 point 3), by agent.</summary>
public static class SetupCapabilities
{
    /// <summary>A shell (<c>Bash</c>).</summary>
    public const string Shell = "shell";

    /// <summary>The network (<c>WebFetch</c>, <c>WebSearch</c>).</summary>
    public const string Network = "network";

    /// <summary>A Codex agent of a role that writes: its sandbox always has a shell.</summary>
    public const string CodexWrites = "codex-writes";

    /// <summary>An agent of a role that writes, on a CLI whose confinement was never measured.</summary>
    public const string UnmeasuredCliWrites = "unmeasured-cli-writes";

    /// <summary>Write access to folders of the repository for an agent that is not read-only by role.</summary>
    public const string MoreFolders = "more-folders";

    /// <summary>
    /// The capabilities <paramref name="agent"/> would be given that D-37 puts behind the fixed barrier, as the facts the
    /// template itself holds: its tools, its CLI and role, and its grants. The same facts <c>MediumIntegrityWriters</c> reads for a
    /// plan; whether a Claude Code executor that writes is confined is decided when a plan is approved (<c>PlanPresence</c>), with the
    /// version installed, and is not asked again here.
    /// </summary>
    public static IReadOnlyList<string> Of(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var codes = new List<string>();
        var tools = agent.AllowedTools ?? [];
        if (tools.Contains("Bash", StringComparer.OrdinalIgnoreCase))
        {
            codes.Add(Shell);
        }

        if (tools.Contains("WebFetch", StringComparer.OrdinalIgnoreCase) || tools.Contains("WebSearch", StringComparer.OrdinalIgnoreCase))
        {
            codes.Add(Network);
        }

        var writes = agent.Role is AgentRole.Executor or AgentRole.Tester or AgentRole.Documenter;
        if (writes && agent.Model.Provider == ProviderId.CodexCli)
        {
            codes.Add(CodexWrites);
        }
        else if (writes && agent.Model.Provider != ProviderId.ClaudeCode)
        {
            codes.Add(UnmeasuredCliWrites);
        }

        if (agent.Role is not (AgentRole.Judge or AgentRole.Auditor) && (agent.PathGrants ?? []).Any(g => g.Access == FileAccessLevel.ReadWrite))
        {
            codes.Add(MoreFolders);
        }

        return codes;
    }

    /// <summary>The capabilities <paramref name="after"/> has that the agent with the same id in <paramref name="before"/> did not (every one for an agent that is added).</summary>
    public static IReadOnlyList<(AgentId Agent, string Code)> Granted(IReadOnlyList<AgentDefinition> before, IReadOnlyList<AgentDefinition> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var granted = new List<(AgentId, string)>();
        foreach (var agent in after)
        {
            var had = before.FirstOrDefault(b => b.Id == agent.Id) is { } old ? Of(old) : [];
            granted.AddRange(Of(agent).Where(c => !had.Contains(c, StringComparer.Ordinal)).Select(c => (agent.Id, c)));
        }

        return granted;
    }
}

/// <summary>
/// The deterministic proposals of the configurator (docs/17 D-26): pure, from what the repository and the project hold, without
/// network, without an agent and without quota. Nothing here writes; the person reviews each proposal, and
/// <see cref="ProjectSetupService.ApplyAsync"/> writes it through the usual gates.
/// </summary>
public static class SetupProposer
{
    /// <summary>The main folders step 1 names.</summary>
    public const int MaxMainFolders = 5;

    /// <summary>The skills proposed to each agent.</summary>
    public const int SkillsPerAgent = 3;

    /// <summary>Step 1: the stack, the size and the main folders, from the names of the files and what git says.</summary>
    public static SetupProjectProposal ProposeProject(SetupRepositoryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var tags = ProjectStackDetector.Detect(facts.Files);
        var folders = facts.Files
            .Select(f => f.Replace('\\', '/').TrimStart('/'))
            .Where(f => f.Contains('/', StringComparison.Ordinal))
            .GroupBy(f => f[..f.IndexOf('/', StringComparison.Ordinal)], StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxMainFolders)
            .Select(g => g.Key + "/")
            .ToList();
        return new SetupProjectProposal(
            facts.RootPath,
            facts.Branch,
            facts.HeadShort,
            facts.HasUncommittedChanges,
            facts.Remotes,
            StackOf(tags),
            tags,
            facts.Files.Count,
            facts.FilesTruncated,
            SetupSizes.Of(facts.Files.Count),
            folders);
    }

    /// <summary>The stack the proposals follow: .NET, else Node, else Python; null when none of them is among <paramref name="tags"/>.</summary>
    public static string? StackOf(IReadOnlySet<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return tags.Contains("dotnet") ? SetupStacks.DotNet
            : tags.Contains("node") ? SetupStacks.Node
            : tags.Contains("python") ? SetupStacks.Python
            : null;
    }

    /// <summary>
    /// Step 2: the section codes of the skeleton of the brief (<c>setup.brief.section.what</c>, <c>.conventions</c>, <c>.links</c>,
    /// and the one of the stack when there is one). The person writes the text; the desktop turns each code into its heading.
    /// </summary>
    public static IReadOnlyList<string> BriefSections(string? stack)
    {
        List<string> sections = ["setup.brief.section.what", "setup.brief.section.conventions", "setup.brief.section.links"];
        if (stack is not null)
        {
            sections.Add("setup.brief.section.stack-" + stack);
        }

        return sections;
    }

    /// <summary>
    /// Step 3: the template of <paramref name="size"/> against what the project has. Small: orchestrator, one executor and judge.
    /// Medium: and a lighter executor. Large: and a reviewer. An agent the project already has keeps its definition (the person may
    /// have set it up), and the agents the size does not name stay as they are: the template of a project only grows from here.
    /// </summary>
    public static SetupTeamProposal ProposeTeam(SetupSize size, IReadOnlyList<AgentDefinition> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var changes = new List<SetupAgentChange>();
        var named = new HashSet<AgentId>();
        foreach (var template in Template(size))
        {
            named.Add(template.Id);
            var existing = current.FirstOrDefault(a => a.Id == template.Id);
            changes.Add(new SetupAgentChange(existing, existing ?? template));
        }

        changes.AddRange(current.Where(a => !named.Contains(a.Id)).Select(a => new SetupAgentChange(a, a)));
        return new SetupTeamProposal(size, changes);
    }

    /// <summary>The agents of the template of <paramref name="size"/>, with no model chosen (the CLI's own) and no effort: step 4 sets them.</summary>
    public static IReadOnlyList<AgentDefinition> Template(SetupSize size)
    {
        var agents = new List<AgentDefinition>
        {
            Agent(SetupAgents.Orchestrator, "Orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob"], 15, 30, new ReviewPolicy(false, false), MemoryPolicy.ReadSelected),
            Agent(SetupAgents.Executor, "Ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob", "Edit", "Write"], 20, 60, new ReviewPolicy(true, true), MemoryPolicy.ReadSelected),
        };
        if (size >= SetupSize.Medium)
        {
            agents.Add(Agent(SetupAgents.LightExecutor, "Ejecutor ligero", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, ["Read", "Grep", "Glob", "Edit", "Write"], 10, 40, new ReviewPolicy(true, true), MemoryPolicy.ReadSelected));
        }

        agents.Add(Agent(SetupAgents.Judge, "Juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, [], 15, 30, new ReviewPolicy(false, false), MemoryPolicy.None));
        if (size >= SetupSize.Large)
        {
            agents.Add(Agent(SetupAgents.Reviewer, "Revisor", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI, ["Read", "Grep", "Glob"], 10, 24, new ReviewPolicy(false, false), MemoryPolicy.None));
        }

        return agents;
    }

    /// <summary>
    /// Step 4: the recommendation of the catalog for each agent of <paramref name="agents"/>, deterministic and with its reason
    /// (docs/17 D-24). The lighter executor gets the «ligero» model with its own reason: the person chose a template that has one.
    /// </summary>
    public static IReadOnlyList<SetupModelProposal> ProposeModels(
        ModelCatalog catalog,
        IReadOnlyList<AgentDefinition> agents,
        SetupProjectProposal project,
        int requirements,
        int mandatoryRequirements,
        int? maxAgentRuns,
        IReadOnlyList<ModelPerformance> history)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        var producers = agents
            .Where(a => a.Role is AgentRole.Orchestrator or AgentRole.Executor)
            .Select(a => a.Model.Vendor)
            .Distinct()
            .ToList();
        var proposals = new List<SetupModelProposal>();
        foreach (var agent in agents)
        {
            var light = agent.Id == SetupAgents.LightExecutor;
            var input = new ModelRecommendationInput(
                agent.Role,
                agent.Model.Provider,
                producers,
                project.Files,
                requirements,
                mandatoryRequirements,
                project.Stack,
                maxAgentRuns,
                PlansHaveDocsOrTestsOnlyTasks: light,
                history);
            var recommendation = light ? ModelRecommender.RecommendSecondExecutor(catalog, input) : ModelRecommender.Recommend(catalog, input);
            if (recommendation is null)
            {
                proposals.Add(new SetupModelProposal(agent, null, null, false));
                continue;
            }

            var reason = light
                ? UserText.Of("setup.models.light-executor")
                : new UserText(recommendation.ReasonCode, recommendation.ReasonArguments);
            proposals.Add(new SetupModelProposal(agent, recommendation, reason, ModelRecommender.IsFollowedBy(catalog, recommendation, agent)));
        }

        return proposals;
    }

    /// <summary><paramref name="agent"/> with the model and the effort of <paramref name="recommendation"/>.</summary>
    public static AgentDefinition WithRecommendation(AgentDefinition agent, ModelRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(recommendation);
        return agent with { Model = agent.Model with { Model = recommendation.Model }, Effort = recommendation.Effort };
    }

    /// <summary>
    /// Step 6: for each agent that is not a judge or an auditor (they rest on the dossier alone), the skills of the catalog the
    /// deterministic recommender ranks first for the stack and the role (docs/14 D-28), with its reasons. Linked skills cannot be
    /// given to an agent and are left out. Nothing is activated: the person decides each one.
    /// </summary>
    public static IReadOnlyList<SetupSkillSuggestion> ProposeSkills(
        IReadOnlyList<CatalogSkill> catalog,
        IReadOnlyList<AgentDefinition> agents,
        IReadOnlySet<string> stackTags,
        string projectId,
        IReadOnlyList<SkillActivation> activations)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(stackTags);
        ArgumentNullException.ThrowIfNull(activations);
        var usable = catalog.Where(s => s.LocalPath is not null).Select(s => s.Entry).ToList();
        var suggestions = new List<SetupSkillSuggestion>();
        foreach (var agent in agents.Where(a => a.Role is not (AgentRole.Judge or AgentRole.Auditor)))
        {
            var tags = new HashSet<string>(stackTags, StringComparer.Ordinal);
            if (RoleTag(agent.Role) is { } roleTag)
            {
                tags.Add(roleTag);
            }

            var query = new SkillQuery(null, tags, null);
            suggestions.AddRange(SkillRecommender.Recommend(query, usable, SkillsPerAgent).Select(r => new SetupSkillSuggestion(
                agent.Id,
                r.Skill.Id,
                r.Skill.Name,
                r.Score,
                r.Reasons,
                SkillActivationResolver.IsEnabled(r.Skill.Id, projectId, agent.Id.Value, activations))));
        }

        return suggestions;
    }

    private static string? RoleTag(AgentRole role) => role switch
    {
        AgentRole.Orchestrator => "planning",
        AgentRole.Architect => "architecture",
        AgentRole.Tester => "testing",
        AgentRole.Documenter => "documentation",
        AgentRole.Critic => "review",
        _ => null,
    };

    private static AgentDefinition Agent(
        AgentId id,
        string name,
        AgentRole role,
        ProviderId provider,
        VendorId vendor,
        IReadOnlyList<string> tools,
        int minutes,
        int turns,
        ReviewPolicy review,
        MemoryPolicy memory) =>
        new(
            id,
            name,
            role,
            new ModelBinding(provider, vendor),
            SystemInstructions: string.Empty,
            Objectives: [],
            AllowedTools: tools,
            PathGrants: [],
            new ExecutionBudget(turns, TimeSpan.FromMinutes(minutes)),
            MaxRounds: 1,
            memory,
            review);
}
