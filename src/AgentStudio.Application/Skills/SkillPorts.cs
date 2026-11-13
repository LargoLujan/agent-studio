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

// Phase 6 contracts (docs/20 D-13 to D-15 and section 4.2, ADR-0063): the doctor of skills, the only piece of the app that talks to GitHub.
// WP-S implements them (src/AgentStudio.Infrastructure/Skills/GitHub and Security); the lead composes them with the permission of Settings.

/// <summary>Why a skill could not be checked against GitHub (docs/20 D-14): each failure has a code, and its message never carries the address, the headers or the body.</summary>
public enum SkillHealthFailure
{
    /// <summary>The permission of Settings is off, so nothing was asked.</summary>
    NetworkNotAllowed,

    /// <summary>The origin of the skill is not valid (<see cref="GitHubSkillOrigin"/>): «origen no válido». Nothing was asked.</summary>
    InvalidOrigin,

    /// <summary>No connection.</summary>
    NoNetwork,

    /// <summary>GitHub answered 403 (the limit, or a private repository).</summary>
    Forbidden,

    /// <summary>GitHub answered 404: the repository was deleted or is private (a warning).</summary>
    NotFound,

    /// <summary>The limit of requests was reached: the skills that are left are «sin comprobar: límite de GitHub».</summary>
    RateLimited,

    /// <summary>A redirection to another host, or more than three: refused.</summary>
    RedirectRefused,

    /// <summary>The answer was not what the schema expects, or was longer than 1 MB.</summary>
    UnreadableResponse,
}

/// <summary>One skill of a batch: its report, and why the checks that need GitHub were not made when they were not.</summary>
public sealed record SkillHealthBatchEntry(SkillId Skill, SkillHealthReport Report, SkillHealthFailure? Failure);

/// <summary>The result of a batch of checks against GitHub (docs/20 D-14): one entry per skill, whether the token was used and, at the limit, when it is reset.</summary>
/// <param name="TokenState">Whether the token was used and, if not, why: <see cref="SkillsDoctorTokenState.PresenceDenied"/> says the batch went without it because the presence was not confirmed.</param>
public sealed record SkillHealthBatchResult(
    IReadOnlyList<SkillHealthBatchEntry> Entries,
    bool UsedToken,
    DateTimeOffset? RateLimitResetsAt,
    SkillsDoctorTokenState TokenState = SkillsDoctorTokenState.NoneStored);

/// <summary>
/// Checks a batch of skills against GitHub, in series, with the answers cached by <c>ETag</c> (docs/20 D-14). It asks only when
/// <see cref="ISkillsDoctorPolicy.NetworkAllowed"/> is true, and only when the person pressed «Comprobar en GitHub». <see cref="ISkillHealthChecker"/> stays as it is.
/// </summary>
public interface ISkillHealthBatchChecker
{
    Task<SkillHealthBatchResult> CheckAsync(IReadOnlyList<SkillEntry> skills, CancellationToken ct);
}

/// <summary>
/// The permission «Comprobar las skills en GitHub» of Settings, seen from below (docs/20 §4.2): <c>AppSettings</c> lives in Desktop, so the client
/// asks this port before each batch. It is off by default; the test of F6-13 turns it off with a double and expects no request.
/// </summary>
public interface ISkillsDoctorPolicy
{
    bool NetworkAllowed { get; }
}

/// <summary>
/// The revision of the scripts of a skill (docs/20 D-14): the person marks the scripts as reviewed and what is stored is the SHA-256 of the
/// content of <c>scripts/</c>. If a script changes, the stored one no longer matches and the skill is «sin revisar» again. No script is ever run.
/// </summary>
public interface ISkillScriptReviews
{
    /// <summary>The SHA-256 the person reviewed, or null when they never did.</summary>
    Task<string?> ReviewedSha256Async(SkillId skill, CancellationToken ct);

    /// <exception cref="ArgumentException">The hash is not a SHA-256.</exception>
    Task MarkReviewedAsync(SkillId skill, string scriptsSha256, CancellationToken ct);
}

/// <summary>
/// The token of GitHub (docs/20 D-15). Its text is in one place only, <see cref="Reveal"/>: it is not part of <see cref="ToString"/>, it is not an
/// argument of any text of the app and it never goes in the log, in the journal, in a message of an error or in a diagnostic.
/// </summary>
public sealed class GitHubToken
{
    /// <summary>The longest token (a fine-grained token of GitHub is under 100 characters).</summary>
    public const int MaxLength = 255;

    private readonly string _value;

    /// <exception cref="ArgumentException">Empty, longer than <see cref="MaxLength"/> or with anything but visible ASCII.</exception>
    public GitHubToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value.Any(c => c is < '!' or > '~'))
        {
            throw new ArgumentException($"A token is 1 to {MaxLength} visible ASCII characters.", nameof(value));
        }

        _value = value;
    }

    /// <summary>The token, for the one place that puts it in an <c>Authorization</c> header after checking the host of the request.</summary>
    public string Reveal() => _value;

    public override string ToString() => "[GitHub token]";
}

/// <summary>Whether the token can be used in a batch, and why not.</summary>
public enum SkillsDoctorTokenState
{
    /// <summary>The person has no token stored: the batch goes without one (60 requests an hour).</summary>
    NoneStored,

    /// <summary>The presence was confirmed (Windows Hello) and the vault was read.</summary>
    Granted,

    /// <summary>The presence was denied or could not be asked: the vault was not read, and the batch goes without a token and says so.</summary>
    PresenceDenied,
}

/// <summary>What <see cref="ISkillsDoctorSecrets.UseTokenAsync"/> gives: the token only when the state is <see cref="SkillsDoctorTokenState.Granted"/>.</summary>
public sealed record SkillsDoctorTokenUse
{
    public SkillsDoctorTokenUse(SkillsDoctorTokenState state, GitHubToken? token)
    {
        if (!Enum.IsDefined(state) || (state == SkillsDoctorTokenState.Granted) != (token is not null))
        {
            throw new ArgumentException("A token comes with the state Granted and only with it.", nameof(token));
        }

        State = state;
        Token = token;
    }

    public SkillsDoctorTokenState State { get; }

    public GitHubToken? Token { get; }

    public static SkillsDoctorTokenUse None { get; } = new(SkillsDoctorTokenState.NoneStored, null);

    public static SkillsDoctorTokenUse Denied { get; } = new(SkillsDoctorTokenState.PresenceDenied, null);
}

/// <summary>
/// The only way the checker of skills gets the token (docs/20 D-15): it asks for presence first (Windows Hello; <c>RemoteOrSecretOperation</c> is on the
/// fixed list of ADR-0053) and reads the vault only after it is granted. With the presence denied, the vault gets no reading at all.
/// </summary>
public interface ISkillsDoctorSecrets
{
    Task<SkillsDoctorTokenUse> UseTokenAsync(CancellationToken ct);
}

/// <summary>Why the person is asked to confirm their presence (Windows Hello) around the token of GitHub: every one is <c>RemoteOrSecretOperation</c>, «siempre» (ADR-0053).</summary>
public enum SkillsDoctorPresenceReason
{
    /// <summary>A batch of «Comprobar en GitHub» is going to use the stored token: one confirmation for the whole batch.</summary>
    UseToken,

    /// <summary>The token is going to be stored or replaced.</summary>
    StoreToken,

    /// <summary>The token is going to be deleted.</summary>
    DeleteToken,
}

/// <summary>
/// Confirms the presence of the person for one use of the token (docs/20 D-15). The desktop implements it over <c>ReinforcedConfirmation</c>, the one rule of the
/// fixed barrier (Windows Hello, or the verified lock of Windows without it). Anything but a confirmation, an error included, is a no.
/// </summary>
public interface ISkillsDoctorPresence
{
    Task<bool> ConfirmAsync(SkillsDoctorPresenceReason reason, CancellationToken ct);
}

/// <summary>What storing or deleting the token did.</summary>
public enum SkillsDoctorTokenChange
{
    Done,

    /// <summary>The presence was not confirmed: nothing was written.</summary>
    PresenceDenied,

    /// <summary>There was no token to delete.</summary>
    NothingStored,
}

/// <summary>
/// Storing, replacing and deleting the token of GitHub from the screen (docs/20 D-15). The three ask for presence first; the token is never read back
/// and the screen only learns <see cref="HasTokenAsync"/>, which decrypts nothing.
/// </summary>
public interface ISkillsDoctorTokenManager
{
    Task<bool> HasTokenAsync(CancellationToken ct);

    Task<SkillsDoctorTokenChange> StoreAsync(GitHubToken token, CancellationToken ct);

    Task<SkillsDoctorTokenChange> DeleteAsync(CancellationToken ct);
}

/// <summary>What the scripts of a skill are right now: the SHA-256 of their content and their relative paths, for the review.</summary>
public sealed record SkillScriptsInfo(string Sha256, IReadOnlyList<string> RelativePaths);

/// <summary>
/// Reads the <c>scripts/</c> folder of a skill without running anything (docs/20 D-14): the fingerprint of what is there, to compare with the one the person
/// reviewed. Null when the skill has no scripts.
/// </summary>
public interface ISkillScriptsInspector
{
    Task<SkillScriptsInfo?> InspectAsync(string skillFolder, CancellationToken ct);
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
