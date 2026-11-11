using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Orchestration.Engine;

/// <summary>
/// How much of its context a task would start with, before any run (docs/17 D-13 and D-18, P49), measured with the same pieces
/// the engine uses to launch it: the requirements it covers quoted as its prompt quotes them, the pack of the packer, and the
/// prompt <see cref="ExecutorPrompt"/> builds, split in parts by <see cref="PromptParts"/>. It is a public facade over what is
/// internal to the engine, so the analyzer of the montage and the engine cannot drift apart. It launches nothing and spends no
/// quota; only <see cref="PackAsync"/> reads, and it reads the verified memory of the project like the engine does, so the
/// caller keeps its result and does not ask it on every edit.
/// </summary>
/// <remarks>
/// The size is a floor of what the prompt will hold, never a figure made up: what is not known before the run is not counted.
/// That is the output of the tasks before this one (it does not exist until they have run), the previous attempts and the
/// answers of the person, and the memory that depends on what the task is about (the pack is asked without a focus, so only the
/// tiers that do not depend on it go in). The skills are counted as the engine counts them, on the <c>SKILL.md</c> of each one
/// that is on for the agent, though not materialized (<see cref="SkillCharacters"/>).
/// </remarks>
public static class ExecutorContextPreview
{
    /// <summary>The characters of context the system packs for an agent that reads memory (docs/14 D-24).</summary>
    public const int BudgetCharacters = ProjectMemoryPrompt.BudgetCharacters;

    /// <summary>A run id of the shape of a real one, for the title line of the prompt; it is never recorded anywhere.</summary>
    private const string PreviewRun = "20000101-000000";

    /// <summary>Whether the packer is asked anything for <paramref name="agent"/>: it reads memory and is not a judge or an auditor.</summary>
    public static bool ReadsMemory(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.Memory == MemoryPolicy.ReadSelected && agent.Role is not (AgentRole.Judge or AgentRole.Auditor);
    }

    /// <summary>Whether the brief of the project reaches <paramref name="agent"/>: whatever its memory policy, but never a judge or an auditor.</summary>
    public static bool ReceivesBrief(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.Role is not (AgentRole.Judge or AgentRole.Auditor);
    }

    /// <summary>
    /// The characters of the mandatory requirements <paramref name="spec"/> covers, as the packer counts them (one per id): the
    /// ones that have to fit in <see cref="BudgetCharacters"/> or the task does not start.
    /// </summary>
    public static int MandatoryCharacters(TaskSpec spec, FrozenRequirementSet requirements)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(requirements);
        return ExecutorPrompt.Items(spec, requirements, RequirementPriority.Mandatory)
            .DistinctBy(i => i.Id, StringComparer.Ordinal)
            .Sum(i => i.Text.Length);
    }

    /// <summary>
    /// The pack of <paramref name="agent"/> for <paramref name="spec"/>, as the engine asks for it but without a focus (what the
    /// task will be about is not known, and a focus ranks memory by words and meaning, which reads the index); null when the
    /// agent reads no memory or there is no packer.
    /// </summary>
    /// <exception cref="ContextBudgetTooSmallException">The mandatory requirements do not fit: the engine would not launch the task.</exception>
    /// <exception cref="DecisionLogIntegrityException">The log does not verify, so no memory can be trusted.</exception>
    public static Task<ContextPack?> PackAsync(
        IContextPacker? packer,
        ProjectContext project,
        AgentDefinition agent,
        TaskSpec spec,
        FrozenRequirementSet requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(requirements);
        return ProjectMemoryPrompt.PackAsync(
            packer,
            project,
            agent,
            string.Empty,
            ExecutorPrompt.Items(spec, requirements, RequirementPriority.Mandatory),
            ExecutorPrompt.Items(spec, requirements, RequirementPriority.Desirable),
            cancellationToken);
    }

    /// <summary>
    /// What the CLI presents of the skills at <paramref name="skillFolders"/> (each the folder that holds a <c>SKILL.md</c>): the
    /// characters of the name and the description in its frontmatter, as the engine counts them in the materialized copy. A folder
    /// or a file that cannot be read adds nothing. It reads the start of each file, so the caller keeps the result.
    /// </summary>
    public static int SkillCharacters(IEnumerable<string> skillFolders)
    {
        ArgumentNullException.ThrowIfNull(skillFolders);
        return skillFolders.Take(PromptParts.MaxSkills).Sum(folder => PromptParts.Presentation(Path.Combine(folder, "SKILL.md")));
    }

    /// <summary>
    /// The size of the prompt of the first attempt of <paramref name="spec"/> as the engine would build it with
    /// <paramref name="memory"/> (the result of <see cref="PackAsync"/>) and <paramref name="brief"/>: the same text, measured in
    /// the same parts that <c>AgentRunStarted</c> records. Pure and quick.
    /// </summary>
    /// <param name="profile">The verification profile, for the steps the prompt lists; null for none.</param>
    /// <param name="brief">The brief in force; ignored for an agent it does not reach (<see cref="ReceivesBrief"/>).</param>
    /// <param name="skillCharacters">What the CLI presents of the skills of the agent (<see cref="SkillCharacters"/>); the engine adds it to the parts apart from the prompt.</param>
    public static ExecutorContextSize Measure(
        TaskSpec spec,
        FrozenRequirementSet requirements,
        VerificationProfile? profile,
        AgentDefinition agent,
        ContextPack? memory,
        ProjectBrief? brief,
        int skillCharacters = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(agent);
        var steps = profile?.StepsToRun(spec.VerificationSteps ?? [], out _) ?? [];
        var prompt = ExecutorPrompt.Build(
            new RunId(PreviewRun),
            spec,
            1,
            requirements,
            steps,
            [],
            null,
            memory,
            handoff: null,
            renewal: null,
            agent: agent,
            brief: ReceivesBrief(agent) ? brief : null);
        var parts = PromptParts.Measure(prompt, skillsDirectory: null);
        return new ExecutorContextSize(skillCharacters > 0
            ? [.. parts.Append(new ContextPartSnapshot(ContextPartKind.Skills, skillCharacters)).OrderBy(p => p.Kind)]
            : parts);
    }
}

/// <summary>The size of a prompt before the agent starts, in the parts of the bar of context (docs/17 D-13), estimated by Agent Studio.</summary>
/// <param name="Parts">The parts, in characters, as <c>AgentRunStarted</c> records them.</param>
public sealed record ExecutorContextSize(IReadOnlyList<ContextPartSnapshot> Parts)
{
    /// <summary>The characters of every part.</summary>
    public int Characters => Parts.Sum(p => p.Characters);

    /// <summary>The tokens, estimated: characters divided by four, rounded up (docs/14 D-24), as the packer estimates a pack.</summary>
    public long Tokens => (Characters + 3L) / 4;
}
