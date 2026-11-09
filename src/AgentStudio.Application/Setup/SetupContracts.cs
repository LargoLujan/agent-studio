using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Team;

namespace AgentStudio.Application.Setup;

// Phase 13 (docs/17 D-26 and D-27, ADR-0055), WP7: the contracts of the configurator of a project. The configurator proposes
// from the repository without network, lets the person review each proposal and applies it through the gates the screens of
// always use. It has no gates of its own. The layers below speak in codes (UserText), never in sentences (ADR-0054).

/// <summary>The steps of the configurator, in order (docs/17 D-26). <see cref="Summary"/> is the last: what changes, and «Aplicar».</summary>
public enum SetupStep
{
    /// <summary>The stack, the size, the branch and the main folders of the repository.</summary>
    Project,

    /// <summary>The brief of the project (D-10), which the person writes over a skeleton.</summary>
    Brief,

    /// <summary>The template of agents, by size of project.</summary>
    Team,

    /// <summary>The model and the effort of each agent, with the deterministic recommendation and its reason.</summary>
    Models,

    /// <summary>The verification profile of the stack.</summary>
    Verification,

    /// <summary>The skills recommended to each agent.</summary>
    Skills,

    /// <summary>The budget and the work without a person at the screen.</summary>
    Budget,

    /// <summary>Everything that changes, with what was there before next to it.</summary>
    Summary,
}

/// <summary>The size of the project, from the files of the index of the explorer (<see cref="SetupSizes.Of"/>); it picks the template of agents.</summary>
public enum SetupSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Where the size classes of a project start, in files (docs/17 D-26 and D-24: the large one starts where the recommendation makes the orchestrator «máximo»).</summary>
public static class SetupSizes
{
    /// <summary>With this many files or more the project is medium.</summary>
    public const int MediumFromFiles = 300;

    /// <summary>With more files than this the project is large (the same limit as <c>ModelRecommendationRules.OrchestratorMaximumFromFiles</c>).</summary>
    public const int LargeAboveFiles = 2000;

    /// <summary>The size class of a project with <paramref name="files"/> files.</summary>
    public static SetupSize Of(int files) =>
        files > LargeAboveFiles ? SetupSize.Large : files >= MediumFromFiles ? SetupSize.Medium : SetupSize.Small;
}

/// <summary>
/// What the repository says of itself, read without network and without running anything of it (docs/17 D-26, step 1): the
/// paths of its files for the stack and the size, and what git says through the hardened layer.
/// </summary>
/// <param name="Files">The files of the project as relative paths with <c>/</c> (the index of the explorer); only their names are read.</param>
/// <param name="FilesTruncated">The index was cut at its cap, so the count is a floor.</param>
/// <param name="Remotes">The remotes with their URL clean of credentials; empty when the reader cannot tell.</param>
public sealed record SetupRepositoryFacts(
    string RootPath,
    string? Branch,
    string? HeadShort,
    bool HasUncommittedChanges,
    IReadOnlyList<string> Files,
    bool FilesTruncated,
    IReadOnlyList<RepositoryRemote> Remotes);

/// <summary>Reads the facts of the repository of a project. It launches no agent, runs nothing of the repository and goes to no network.</summary>
public interface ISetupRepositoryReader
{
    Task<SetupRepositoryFacts> ReadAsync(ProjectContext project, CancellationToken ct);
}

/// <summary>What step 1 shows: the repository as the configurator read it.</summary>
/// <param name="Stack">The stack the proposals follow: <c>dotnet</c>, <c>node</c> or <c>python</c> (in that order of precedence when a repository has several); null when none is recognised.</param>
/// <param name="StackTags">Every tag <c>ProjectStackDetector</c> found, such as <c>sql</c> or <c>docker</c>.</param>
/// <param name="MainFolders">The top-level folders with most files, as <c>name/</c>, at most <see cref="SetupProposer.MaxMainFolders"/>.</param>
public sealed record SetupProjectProposal(
    string RootPath,
    string? Branch,
    string? HeadShort,
    bool HasUncommittedChanges,
    IReadOnlyList<RepositoryRemote> Remotes,
    string? Stack,
    IReadOnlySet<string> StackTags,
    int Files,
    bool FilesTruncated,
    SetupSize Size,
    IReadOnlyList<string> MainFolders);

/// <summary>
/// What step 2 shows: the section codes of the skeleton the person writes over (the desktop turns each into its heading from
/// its resources), and the brief in force when there is one.
/// </summary>
/// <param name="Current">The brief in force; null when the project has none, or when it could not be read (<see cref="SetupProposal.Problems"/>).</param>
public sealed record SetupBriefProposal(IReadOnlyList<string> SkeletonSections, ProjectBrief? Current);

/// <summary>What one agent of the template becomes when it is applied.</summary>
/// <param name="Before">The agent as the template stores it; null for one that is added.</param>
/// <param name="After">The agent as it would be stored.</param>
public sealed record SetupAgentChange(AgentDefinition? Before, AgentDefinition After)
{
    /// <summary>The agent is not in the template yet.</summary>
    public bool IsAdded => Before is null;

    /// <summary>Whether storing <see cref="After"/> changes the template (the canonical hash of the agent differs).</summary>
    public bool IsChange => Before is null || RosterHash.Of([Before]) != RosterHash.Of([After]);

    /// <summary>What a screen reader says of the change: the agent.</summary>
    public override string ToString() => After.Name;
}

/// <summary>What step 3 shows: the template of the size, as additions and changes to what the project has. The configurator never removes an agent.</summary>
/// <param name="Size">The size the template is for (the one of the project unless the person chose another).</param>
/// <param name="Changes">Every agent of the template of the size, then those of the project the size does not name, unchanged.</param>
public sealed record SetupTeamProposal(SetupSize Size, IReadOnlyList<SetupAgentChange> Changes);

/// <summary>What step 4 says of one agent: the model and the effort it has, and the deterministic recommendation with its reason.</summary>
/// <param name="Recommendation">The recommendation; null when the catalog has no model for the CLI of the agent.</param>
/// <param name="Reason">Why, as a coded text (the reasons of <c>ModelRecommendationReasons</c> or one of the configurator's); null without recommendation.</param>
/// <param name="Follows">The agent already runs on the model and the effort recommended: applying it changes nothing.</param>
public sealed record SetupModelProposal(AgentDefinition Agent, ModelRecommendation? Recommendation, UserText? Reason, bool Follows)
{
    /// <summary>What a screen reader says of the proposal: the agent.</summary>
    public override string ToString() => Agent.Name;
}

/// <summary>What step 5 shows: the profile of the stack and the one in force.</summary>
/// <param name="Current">The profile in force; null when the project has none or its file cannot be used.</param>
/// <param name="Proposed">The profile of the stack; null with «sin perfil propuesto» (<see cref="NoProposalCode"/>), and then the person writes it.</param>
/// <param name="CurrentUnreadable">There is a file and it breaks a rule: it is never overwritten from here.</param>
/// <param name="NoProposalCode">Why nothing is proposed, as a code (<c>setup.verification.no-stack</c>); null when there is a proposal.</param>
public sealed record SetupVerificationProposal(VerificationProfile? Current, VerificationProfile? Proposed, bool CurrentUnreadable, string? NoProposalCode)
{
    /// <summary>Whether applying the proposal would change the profile in force.</summary>
    public bool WouldChange => Proposed is not null && !CurrentUnreadable && Current?.ContentHash != Proposed.ContentHash;
}

/// <summary>One skill recommended to one agent (step 6), with the reasons of the deterministic recommender (docs/14 D-28).</summary>
public sealed record SetupSkillSuggestion(AgentId Agent, SkillId Skill, string Name, double Score, IReadOnlyList<string> Reasons, bool AlreadyActive)
{
    /// <summary>What a screen reader says of the suggestion: the skill.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The budget of the runs of a project and the work without a person at the screen (step 7): the values of <c>RunOptions</c> the
/// person chooses once for the project. The consent to resume by itself is not here: it is given at each run (ADR-0034).
/// </summary>
/// <param name="MaxAgentRuns">Agent executions per run (<c>RunOptions.MaxAgentRuns</c>).</param>
/// <param name="MaxParallelTasks">Tasks at once (<c>RunOptions.MaxParallelTasks</c>).</param>
/// <param name="TimePerAttempt">Time per attempt (<c>RunOptions.DefaultTaskTimeout</c>); null is the engine's own.</param>
public sealed record ProjectRunOptions(int MaxAgentRuns, int MaxParallelTasks, TimeSpan? TimePerAttempt)
{
    /// <summary>The fewest and the most agent executions per run the configurator accepts.</summary>
    public const int MinAgentRuns = 1;

    public const int MaxAgentRunsLimit = 200;

    /// <summary>The fewest and the most tasks at once.</summary>
    public const int MinParallelTasks = 1;

    public const int MaxParallelTasksLimit = 8;

    /// <summary>The shortest and the longest time per attempt.</summary>
    public static readonly TimeSpan MinTimePerAttempt = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxTimePerAttempt = TimeSpan.FromHours(4);

    /// <summary>The values of <c>RunOptions</c> without arguments: what the project has until the person chooses.</summary>
    public static ProjectRunOptions Defaults { get; } = new(12, 3, null);

    /// <summary>The codes of what is wrong with the values; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxAgentRuns is < MinAgentRuns or > MaxAgentRunsLimit)
        {
            problems.Add("setup.budget.agent-runs-out-of-range");
        }

        if (MaxParallelTasks is < MinParallelTasks or > MaxParallelTasksLimit)
        {
            problems.Add("setup.budget.parallel-tasks-out-of-range");
        }

        if (TimePerAttempt is { } time && (time < MinTimePerAttempt || time > MaxTimePerAttempt))
        {
            problems.Add("setup.budget.time-out-of-range");
        }

        return problems;
    }

    /// <summary>The options of a run with these values; what the orchestrator uses when it starts one.</summary>
    public RunOptions ToRunOptions() => new(MaxParallelTasks, MaxAgentRuns, DefaultTaskTimeout: TimePerAttempt);
}

/// <summary>Where the options of the project are kept. They are the person's, per project and per computer; nothing of them goes into the repository.</summary>
public interface IProjectOptionsStore
{
    /// <summary>The options chosen for the project; null when none were.</summary>
    Task<ProjectRunOptions?> LoadAsync(ProjectContext project, CancellationToken ct);

    Task SaveAsync(ProjectContext project, ProjectRunOptions options, CancellationToken ct);
}

/// <summary>
/// The verification profiles the configurator can propose, read and write (docs/17 D-26, step 5). The desktop implements it
/// over the profiles of the stacks and the project's file of the profile (ADR-0016): the profile is only ever a proposal until
/// the person approves it.
/// </summary>
public interface ISetupVerificationProfiles
{
    /// <summary>
    /// The profile of the stack the file names of the project say (<c>.sln</c>/<c>.csproj</c>: .NET; <c>package.json</c>: Node;
    /// <c>pyproject.toml</c>: Python), in that order of precedence; null with none. Nothing of the repository is run or read.
    /// </summary>
    VerificationProfile? Propose(IReadOnlyList<string> relativeFiles);

    /// <summary>The profile in force, or null when there is none. A file that cannot be used throws <see cref="InvalidDataException"/>.</summary>
    Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken ct);

    /// <summary>Makes <paramref name="profile"/> the profile of the project.</summary>
    Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken ct);
}

/// <summary>The answer of the barrier of Windows Hello (docs/17 D-37 to D-41) to one critical change of the configurator.</summary>
/// <param name="Problem">Why the person was not confirmed, as a coded text; null when confirmed.</param>
public sealed record SetupPresenceResult(bool Confirmed, UserText? Problem = null);

/// <summary>
/// Asks the person to confirm their presence for one critical change, by the one rule of the fixed barrier
/// (<c>PresencePolicy</c>, ADR-0053): Windows Hello, or without it the verified lock of Windows. The desktop implements it over
/// <c>ReinforcedConfirmation</c>, the same instance the approvals use.
/// </summary>
public interface ISetupPresenceGate
{
    /// <param name="action">What is asked: <see cref="PresenceAction.ChangeVerificationProfile"/> or <see cref="PresenceAction.GrantHighRiskCapability"/>.</param>
    /// <param name="subject">What is going to be written, as a coded text the confirmation says in words (the verification profile of Node, the team with 4 agents), never a hash.</param>
    Task<SetupPresenceResult> ConfirmAsync(PresenceAction action, UserText subject, CancellationToken ct);
}

/// <summary>Everything the configurator proposes for a project. Read-only: nothing here is written, and nothing runs.</summary>
/// <param name="Models">One entry per agent of <see cref="Team"/>, in its order.</param>
/// <param name="Problems">What could not be read, as codes; the step that needs it says so and does not apply.</param>
public sealed record SetupProposal(
    SetupProjectProposal Project,
    SetupBriefProposal Brief,
    SetupTeamProposal Team,
    IReadOnlyList<SetupModelProposal> Models,
    SetupVerificationProposal Verification,
    IReadOnlyList<SetupSkillSuggestion> Skills,
    ProjectRunOptions Budget,
    IReadOnlyList<UserText> Problems);

/// <summary>A skill the person chose to activate for one agent: a decision of theirs, written as one of the skills screen.</summary>
public sealed record SetupSkillChoice(AgentId Agent, SkillId Skill)
{
    /// <summary>What a screen reader says of the choice: the skill.</summary>
    public override string ToString() => Skill.Value;
}

/// <summary>
/// What the person reviewed and wants applied. A part left null is skipped: every step can be skipped (docs/17 D-26).
/// </summary>
/// <param name="BriefText">The brief to save as a new version; null skips step 2.</param>
/// <param name="Roster">The agents to store, with their model and effort (steps 3 and 4); null skips both. Only those that differ from the template are written.</param>
/// <param name="Profile">The profile to write (step 5); null skips it.</param>
/// <param name="Skills">The skills to activate, each for one agent (step 6); null or empty skips it.</param>
/// <param name="Options">The options of the project (step 7); null skips it.</param>
public sealed record SetupSelection(
    string? BriefText,
    IReadOnlyList<AgentDefinition>? Roster,
    VerificationProfile? Profile,
    IReadOnlyList<SetupSkillChoice>? Skills,
    ProjectRunOptions? Options);

/// <summary>What happened to one part of the selection.</summary>
public enum SetupPartOutcome
{
    /// <summary>It was written through its gate.</summary>
    Applied,

    /// <summary>It was already as selected: nothing was written.</summary>
    Unchanged,

    /// <summary>It was not selected.</summary>
    Skipped,

    /// <summary>The person was not confirmed (Windows Hello or the lock of Windows): nothing was written.</summary>
    Denied,

    /// <summary>It could not be written: <see cref="SetupPartResult.Detail"/> says why.</summary>
    Failed,
}

/// <param name="Detail">Why, as a coded text, for <see cref="SetupPartOutcome.Denied"/> and <see cref="SetupPartOutcome.Failed"/>.</param>
public sealed record SetupPartResult(SetupStep Step, SetupPartOutcome Outcome, UserText? Detail = null)
{
    /// <summary>What a screen reader says of the result: its step and its outcome.</summary>
    public override string ToString() => $"{Step}: {Outcome}";
}

/// <summary>What <see cref="ProjectSetupService.ApplyAsync"/> did.</summary>
/// <param name="RosterChanged">The template changed: the plan waiting for approval is no longer the one approved (the hash differs).</param>
/// <param name="Recorded"><c>ProjectSetupApplied</c> is in the log; false when nothing was applied, or when it could not be written (<see cref="RecordProblem"/>).</param>
public sealed record SetupApplyResult(
    IReadOnlyList<SetupPartResult> Parts,
    string? RosterHashBefore,
    string? RosterHashAfter,
    bool RosterChanged,
    bool Recorded,
    UserText? RecordProblem);
