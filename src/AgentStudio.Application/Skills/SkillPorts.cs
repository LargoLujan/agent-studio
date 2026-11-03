using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Application.Skills;

/// <summary>Result of importing a catalog: the valid entries plus user-facing warnings (Spanish) for what was skipped.</summary>
public sealed record SkillImportResult(IReadOnlyList<SkillEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>Imports the skills index (a catalog.json) into domain entries.</summary>
public interface ISkillCatalogImporter
{
    Task<IReadOnlyList<SkillEntry>> ImportAsync(string catalogPath, CancellationToken ct);

    /// <summary>Same as <see cref="ImportAsync"/> but also reports the entries that were omitted and why.</summary>
    Task<SkillImportResult> ImportWithReportAsync(string catalogPath, CancellationToken ct);
}

/// <summary>An activated skill together with the local folder that holds its files (the one containing SKILL.md).</summary>
public sealed record ActivatedSkill(SkillEntry Skill, string LocalPath);

/// <summary>Where the skills of one run are materialized.</summary>
/// <param name="RunId">Run identifier; names the per-run session folder (<c>[A-Za-z0-9_-]+</c>).</param>
/// <param name="SessionBaseDirectory">Base folder for session directories (used by CLIs that load a plugin directory).</param>
/// <param name="WorkingDirectory">Working directory (worktree) of the run (used by CLIs that discover <c>.agents/skills</c>).</param>
public sealed record SkillSessionContext(string RunId, string SessionBaseDirectory, string WorkingDirectory);

/// <summary>
/// Prepares the activated skills for one provider CLI, so the CLI discovers them without touching the user's own CLI
/// configuration (<c>~/.claude</c>, <c>~/.codex</c>, <c>~/.gemini</c>, <c>~/.agents</c> are never written).
/// </summary>
public interface ISkillMaterializer
{
    /// <summary>
    /// Copies the skills (never following symbolic links) and returns the folder to put in
    /// <c>AgentRunRequest.SkillsDirectory</c>, or null when <paramref name="activatedSkills"/> is empty.
    /// </summary>
    Task<string?> MaterializeAsync(
        ProviderId cli,
        IReadOnlyList<ActivatedSkill> activatedSkills,
        SkillSessionContext session,
        CancellationToken ct);

    /// <summary>Removes what <see cref="MaterializeAsync"/> created in <paramref name="materializedDirectory"/> (only what Agent Studio created).</summary>
    void Cleanup(string materializedDirectory);
}

// Phase 3 contracts (docs/13 section 3.6): which skills each agent gets in each run.

/// <summary>A catalog skill and where its files are. <see cref="LocalPath"/> is null for linked skills, which cannot be materialized.</summary>
public sealed record CatalogSkill(SkillEntry Entry, string? LocalPath);

/// <summary>The imported skills catalog, with the local folder of every vendored skill (the one holding its SKILL.md).</summary>
public interface ISkillCatalog
{
    Task<IReadOnlyList<CatalogSkill>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Explicit activation decisions, as a history: with several decisions for the same skill and scope the last one wins.
/// Project and agent decisions live in the project database; global ones in a file in the user profile.
/// </summary>
public interface ISkillActivationStore
{
    /// <summary>Every decision that applies to the project (global, project and agent scope), oldest first.</summary>
    Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct);

    Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct);
}

/// <summary>The skills prepared for one agent run: <see cref="Directory"/> goes in <c>AgentRunRequest.SkillsDirectory</c> (null when there are none).</summary>
public sealed record ProvisionedSkills(ProviderId Cli, string? Directory, IReadOnlyList<SkillId> Skills, IReadOnlyList<string> Warnings);

/// <summary>
/// Decides which skills an agent gets (Agent over Project over Global; nothing is active by default) and materializes
/// them for its CLI without touching the user's CLI configuration.
/// </summary>
public interface ISkillProvisioner
{
    /// <param name="projectId">The project whose activations apply.</param>
    /// <param name="agent">The agent the skills are for; its provider decides the layout.</param>
    /// <param name="runKey">Names the per-run session folder; <c>[A-Za-z0-9_-]+</c>.</param>
    /// <param name="workingDirectory">The worktree of the run, where Codex and Gemini discover <c>.agents/skills</c>.</param>
    /// <param name="ct">Cancellation.</param>
    Task<ProvisionedSkills> ProvisionAsync(string projectId, AgentDefinition agent, string runKey, string workingDirectory, CancellationToken ct);

    /// <summary>Removes what <see cref="ProvisionAsync"/> created. Safe to call on every path, including failure and cancellation.</summary>
    void Release(ProvisionedSkills skills);
}

/// <summary>
/// Contract only, no implementation yet (Phase 6, doctor): runs the checks of a skill, including those that need
/// network (upstream, archived) or reading its files (frontmatter, scripts, size).
/// </summary>
public interface ISkillHealthChecker
{
    Task<SkillHealthReport> CheckAsync(SkillEntry skill, CancellationToken ct);
}

// Phase 4 contracts (docs/14 section 5.5): the catalog screen and the assisted recommendation.

/// <summary>
/// One skill as the local MCP server sees it (docs/14 D-30): catalog metadata only, never the body of a <c>SKILL.md</c>
/// nor a path. <paramref name="ActiveAgents"/> are the agents of the roster that have it active
/// (<see cref="SkillActivationResolver"/>); a <paramref name="Linked"/> skill cannot be given to an agent; the context
/// cost is the name and description divided by 4, «estimado por Agent Studio» (D-26).
/// </summary>
public sealed record SnapshotSkill(
    string Id,
    string Name,
    string Summary,
    string SectorId,
    string SectorTitle,
    IReadOnlyList<string> Tags,
    SkillHealthStatus Health,
    IReadOnlyList<string> ActiveAgents,
    bool Linked,
    long EstimatedContextTokens);

/// <summary>What the MCP server reads: a snapshot of the catalog that Agent Studio writes in the folder of the run with <c>CreateNew</c> (D-30).</summary>
public sealed record SkillCatalogSnapshot(DateTimeOffset At, IReadOnlyList<SnapshotSkill> Skills);

/// <summary>
/// One recommendation of the agent as the system validated it (D-29): a skill of the catalog, the scope, the agent of an
/// agent scope (one of the roster) and the reason, at most 300 characters, shown as «razón del agente». It activates nothing.
/// </summary>
public sealed record AssistedRecommendation(SkillId Skill, SkillScopeKind Scope, AgentId? Agent, string Reason);

/// <summary>
/// The answer of an assisted recommendation: what survived the validation (10 at most), what was discarded and why (in
/// Spanish), and the auxiliary run that produced it (<c>AgentRunStarted</c> and <c>AgentRunFinished</c> of that run).
/// </summary>
public sealed record AssistedRecommendationResult(IReadOnlyList<AssistedRecommendation> Recommendations, IReadOnlyList<string> Warnings, RunId? Run);

/// <summary>
/// The user's OK to spend quota on one assisted recommendation (D-29, decision 2 of docs/14 section 12): who gave it,
/// when, and what they were shown. Without it nothing is launched.
/// </summary>
public sealed record QuotaConsent(string DecidedBy, DateTimeOffset DecidedAt, string Description);

/// <summary>
/// Recommends skills (D-28 and D-29): deterministically for free, or by asking an agent through the local MCP server, with
/// the user's consent each time. Neither ever activates a skill: only the user does, with a <c>SkillActivation</c> of
/// their own.
/// </summary>
public interface ISkillRecommendationService
{
    IReadOnlyList<SkillRecommendation> Recommend(SkillQuery query, IReadOnlyList<CatalogSkill> catalog);

    Task<AssistedRecommendationResult> AskAgentAsync(ProjectContext project, string task, QuotaConsent consent, CancellationToken ct);
}

/// <summary>
/// The local health of a skill (D-26): frontmatter, license and pinning, with no network. Upstream, archived and scripts
/// come back «sin comprobar (Fase 6)».
/// </summary>
public interface ISkillHealthReader
{
    Task<SkillHealthReport> ReadLocalAsync(CatalogSkill skill, CancellationToken ct);
}
