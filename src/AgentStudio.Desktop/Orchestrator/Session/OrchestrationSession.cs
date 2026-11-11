using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Desktop.Orchestrator.Session;

/// <summary>
/// The trusted verification profile of a project (docs/13 section 5, ADR-0016: <c>&lt;root&gt;\.agentstudio\verification.json</c>,
/// frozen per run). A proposal is only ever offered: it becomes the profile when the user saves it.
/// </summary>
public interface IVerificationProfileSource
{
    /// <summary>The project's profile, or null when the project has none. A file that cannot be used throws, saying why.</summary>
    Task<VerificationProfile?> LoadAsync(ProjectContext project, CancellationToken cancellationToken);

    /// <summary>What to offer a project without a profile; null when there is nothing to offer.</summary>
    VerificationProfile? Propose(ProjectContext project);

    /// <summary>Makes <paramref name="profile"/> the project's profile: the user reviewed and accepted it.</summary>
    Task SaveAsync(ProjectContext project, VerificationProfile profile, CancellationToken cancellationToken);
}

/// <summary>
/// The engine-side services the orchestrator screen talks to, all through Application ports. Any of them may be missing
/// (a shell composed without the engine): the screen then says what is missing instead of pretending.
/// </summary>
/// <param name="Delivery">Delivers a finished run to the user's branch («Entregar a mi rama» in the monitor).</param>
/// <param name="WriteConfinement">
/// The probe the engine asks of the installed Claude Code when a run starts (ADR-0018): the screen asks it too, so the
/// warning next to «Aprobar plan» weighs the executors as the check of the user's repository will.
/// </param>
/// <param name="Decisions">Reads the project's decision log when it is opened, to show whether it can be trusted (ADR-0010).</param>
/// <param name="Origins">Accepts a history no anchor recognises at the project's location as a new origin, on the person's word.</param>
/// <param name="Prompt">Asks the person that question, with the approval dialog.</param>
/// <param name="Runs">
/// The headers of the runs: the monitor says whether the run it follows has the consent to resume by itself (docs/16 D-39), and
/// the plan of a run the scheduler resumed is found by its hash.
/// </param>
/// <param name="Inspector">
/// Reads a run that does not run in this process back from its log, so «Ver» opens a previous run in the monitor (its tasks,
/// evidence, review and delivery) in read-only. Without it, the previous runs are only listed.
/// </param>
/// <param name="Evidence">Reads the evidence of a failed verification step, for the line of the monitor that says why it failed.</param>
public sealed record OrchestrationBackend(
    IOrchestrator? Orchestrator = null,
    IProjectWorkspace? Workspace = null,
    IRequirementStore? Requirements = null,
    IAgentRoster? Roster = null,
    IVerificationProfileSource? Verification = null,
    IPlanStore? Plans = null,
    IDeliveryService? Delivery = null,
    IWriteConfinementProbe? WriteConfinement = null,
    IDecisionRecorder? Decisions = null,
    IDecisionLogOrigin? Origins = null,
    IApprovalPrompt? Prompt = null,
    IRunRegistry? Runs = null,
    IRunInspector? Inspector = null,
    IEvidenceStore? Evidence = null)
{
    public static OrchestrationBackend None { get; } = new();
}

/// <summary>What the screen knows about a project once it is opened for orchestration.</summary>
/// <param name="Missing">One Spanish line per piece that is not available, in the order they are needed.</param>
/// <param name="VerificationProposal">
/// The profile offered to a project that has none (never applied on its own: the user accepts it with «Usar perfil
/// propuesto»); null when the project has a profile, its file cannot be used, or there is nothing to offer.
/// </param>
/// <param name="RosterCreated">True when the project had no agents and got <see cref="DefaultAgentRoster"/> while opening.</param>
/// <param name="ClaudeWritesConfined">
/// Whether the installed Claude Code is a version whose confinement to the worktree was measured (ADR-0018, «Medición»),
/// as the probe answered when the project was opened; false when there is no probe (fail closed, like the engine).
/// <param name="IntegrityProblem">
/// Why the project's decision log cannot be trusted, in Spanish; null when it can (or nothing reads it). Nothing is planned,
/// run or resumed on such a log.
/// </param>
/// <param name="Unanchored">
/// The history no anchor recognises at the project's location, when that is the only problem: the person may accept it
/// with «Aceptar este historial como nuevo origen». Null otherwise.
/// </param>
public sealed record SessionContext(
    ProjectContext? Project,
    FrozenRequirementSet? Requirements,
    IReadOnlyList<AgentDefinition> Roster,
    VerificationProfile? Verification,
    IReadOnlyList<string> Missing,
    VerificationProfile? VerificationProposal = null,
    bool RosterCreated = false,
    bool ClaudeWritesConfined = false,
    string? IntegrityProblem = null,
    UnanchoredHistory? Unanchored = null)
{
    /// <summary>The orchestrator agent of the roster: the one that plans.</summary>
    public AgentDefinition? Orchestrator => Roster.FirstOrDefault(a => a.Role == AgentRole.Orchestrator);

    /// <summary>
    /// What <see cref="PlanValidator"/> checks a plan against; null without frozen requirements or a profile. A tool
    /// task runs a step of the profile by its id (docs/13 section 4.2), so the profile's step ids are both the
    /// verification steps and the tools a plan may name. The steps whose evidence is a test run are the ones that meet a
    /// policy of system-captured tests (a build does not), as the engine checks before anything runs.
    /// </summary>
    public PlanContext? PlanContext(PlanLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (Requirements is null || Verification is null)
        {
            return null;
        }

        var steps = Verification.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var tests = Verification.Steps.Where(s => s.Kind == EvidenceKind.TestRun).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return new PlanContext(Requirements, Roster, steps, steps, limits, tests);
    }

    public EditorOptions EditorOptions => new(
        Roster,
        Requirements?.Requirements ?? [],
        [.. (Verification?.Steps ?? []).Select(s => (s.Id, s.Title))]);
}

/// <summary>What freezing a set of requirements did.</summary>
public enum FreezeOutcome
{
    /// <summary>The set was stored as the project's new current version.</summary>
    Frozen,

    /// <summary>The content already was the current version: nothing was stored.</summary>
    Unchanged,

    /// <summary>
    /// The content is that of an earlier version. The store keeps one row per content and the current version is the
    /// last one stored, so an earlier version cannot become current again: nothing was stored.
    /// </summary>
    EarlierVersion,
}

/// <param name="Set">The new version (<see cref="FreezeOutcome.Frozen"/>), the current one (<see cref="FreezeOutcome.Unchanged"/>) or the earlier one with that content.</param>
public sealed record RequirementsFreeze(FreezeOutcome Outcome, FrozenRequirementSet Set);

/// <summary>The facade of the orchestrator screen over the optional backend: it opens a project and forwards the calls.</summary>
public sealed class OrchestrationSession
{
    private readonly OrchestrationBackend _backend;

    public OrchestrationSession(OrchestrationBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    public bool HasEngine => _backend.Orchestrator is not null;

    /// <summary>Delivers a finished run to the user's branch; null when the shell was composed without it.</summary>
    public IDeliveryService? Delivery => _backend.Delivery;

    /// <summary>Reads the evidence of a run (the error line of a failed step); null when the shell was composed without it.</summary>
    public IEvidenceStore? Evidence => _backend.Evidence;

    /// <summary>A previous run can be opened in the monitor, read back from its log (<see cref="InspectAsync"/>).</summary>
    public bool CanInspectRuns => _backend.Inspector is not null;

    /// <summary>A run that does not run in this process, read back from its log to look at it: nothing is launched or written.</summary>
    public Task<IOrchestrationRun> InspectAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var inspector = _backend.Inspector ?? throw new InvalidOperationException("Previous runs cannot be opened: the run inspector was not composed.");
        return inspector.InspectAsync(project, run, cancellationToken);
    }

    /// <summary>Frozen requirements can be stored (the requirement store is available).</summary>
    public bool CanStoreRequirements => _backend.Requirements is not null;

    /// <summary>
    /// Opens the project (database, migrations, ids) and reads what planning and validation need. A project without
    /// agents gets <see cref="DefaultAgentRoster"/>; a project without a verification profile is offered one, which is
    /// not applied. Every piece that is missing or fails becomes a line of <see cref="SessionContext.Missing"/>; nothing
    /// is made up to fill the gap.
    /// </summary>
    public async Task<SessionContext> OpenAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var missing = new List<string>();
        if (_backend.Orchestrator is null)
        {
            missing.Add(Texts.Get("Orchestrator_Session_EngineMissing"));
        }

        ProjectContext? project = null;
        if (_backend.Workspace is null)
        {
            missing.Add(Texts.Get("Orchestrator_Session_StoreMissing"));
        }
        else
        {
            (project, _) = await Try(() => _backend.Workspace.OpenAsync(projectRoot, cancellationToken), Texts.Get("Orchestrator_Session_OpenFailed"), missing).ConfigureAwait(true);
        }

        FrozenRequirementSet? requirements = null;
        IReadOnlyList<AgentDefinition> roster = [];
        VerificationProfile? profile = null;
        VerificationProfile? proposal = null;
        var rosterCreated = false;
        string? integrityProblem = null;
        UnanchoredHistory? unanchored = null;
        if (project is not null)
        {
            if (_backend.Decisions is not null)
            {
                var (log, _) = await Try(() => _backend.Decisions.LoadAsync(project.ProjectId, cancellationToken), Texts.Get("Orchestrator_Session_LogFailed"), missing).ConfigureAwait(true);
                if (log is { Verification.IsValid: false })
                {
                    integrityProblem = HistoryText.Problem(log);
                    unanchored = log.Unanchored;
                }
            }

            if (_backend.Requirements is null)
            {
                missing.Add(Texts.Get("Orchestrator_Session_RequirementsStoreMissing"));
            }
            else
            {
                (requirements, var failed) = await Try(() => _backend.Requirements.LatestAsync(project.ProjectId, cancellationToken), Texts.Get("Orchestrator_Session_RequirementsFailed"), missing).ConfigureAwait(true);
                if (requirements is null && !failed)
                {
                    missing.Add(Texts.Get("Orchestrator_Session_NoRequirements"));
                }
            }

            if (_backend.Roster is null)
            {
                missing.Add(Texts.Get("Orchestrator_Session_RosterMissing"));
            }
            else
            {
                (roster, rosterCreated) = await OpenRosterAsync(_backend.Roster, project, missing, cancellationToken).ConfigureAwait(true);
            }

            if (_backend.Verification is null)
            {
                missing.Add(Texts.Get("Orchestrator_Session_ProfileMissing"));
            }
            else
            {
                (profile, var failed) = await Try(() => _backend.Verification.LoadAsync(project, cancellationToken), Texts.Get("Orchestrator_Session_ProfileFailed"), missing).ConfigureAwait(true);
                if (profile is null && !failed)
                {
                    proposal = _backend.Verification.Propose(project);
                    missing.Add(proposal is null
                        ? Texts.Get("Orchestrator_Session_NoProfile")
                        : Texts.Get("Orchestrator_Session_NoProfileProposal"));
                }
            }
        }

        // Asked every time, as the engine asks when a run starts: a Claude Code updated between two openings is seen.
        var claudeWritesConfined = project is not null
            && _backend.WriteConfinement is { } probe
            && await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, cancellationToken).ConfigureAwait(true);
        return new SessionContext(project, requirements, roster, profile, missing, proposal, rosterCreated, claudeWritesConfined, integrityProblem, unanchored);
    }

    /// <summary>«Aceptar este historial como nuevo origen» can be asked and recorded (the recorder and the approval dialog are there).</summary>
    public bool CanAcceptHistory => _backend.Origins is not null && _backend.Prompt is not null;

    /// <summary>
    /// «Aceptar este historial como nuevo origen» (ADR-0010, addendum of 2026-10-18): the person is asked, with the approval
    /// dialog and its explicit confirmation for a critical action, whether the history shown is theirs. Only a yes records
    /// the acceptance (the alert, the question and the answer) and anchors the history at the project's location. A no
    /// writes nothing, since the log cannot be written while it is blocked, and it stays blocked.
    /// </summary>
    /// <returns>True when the person accepted it and the acceptance was recorded; false when the person did not.</returns>
    public async Task<bool> AcceptHistoryAsync(ProjectContext project, UnanchoredHistory history, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(history);
        var origins = _backend.Origins ?? throw new InvalidOperationException("Accepting a history as a new origin is not available: the decision recorder was not composed.");
        var prompt = _backend.Prompt ?? throw new InvalidOperationException("Accepting a history as a new origin is not available: there is no approval dialog to ask the person.");
        var request = new ApprovalRequest(
            UnanchoredHistory.AcceptAction,
            HistoryText.Question(project, history),
            RiskLevel.Critical,
            null,
            [project.RootPath],
            project.ProjectId,
            Binding: history.Binding(project.ProjectId));

        var decision = await prompt.AskAsync(request, cancellationToken).ConfigureAwait(true);
        if (!decision.Approved)
        {
            return false;
        }

        // The answer is to this question, as the approval gate makes it: the recorder checks both against the history.
        await origins.AcceptAsNewOriginAsync(project.ProjectId, request, decision with { Answers = request.Binding }, cancellationToken).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Freezes <paramref name="requirements"/> as the project's current version, frozen at <paramref name="at"/>. A frozen
    /// set never changes: a new version is a new set with its own hash. Nothing is stored when the content already is
    /// the current version or is that of an earlier one (<see cref="FreezeOutcome"/>).
    /// </summary>
    /// <exception cref="Domain.Common.DomainRuleViolationException">The requirements break a rule of the domain.</exception>
    public async Task<RequirementsFreeze> FreezeRequirementsAsync(ProjectContext project, IEnumerable<Requirement> requirements, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(requirements);
        var store = _backend.Requirements ?? throw new InvalidOperationException("The requirement store is not available.");
        var candidate = FrozenRequirementSet.Freeze(requirements, at);
        var current = await store.LatestAsync(project.ProjectId, cancellationToken).ConfigureAwait(true);
        if (current is not null && string.Equals(current.ContentHash, candidate.ContentHash, StringComparison.Ordinal))
        {
            return new RequirementsFreeze(FreezeOutcome.Unchanged, current);
        }

        if (await store.FindAsync(project.ProjectId, candidate.ContentHash, cancellationToken).ConfigureAwait(true) is { } earlier)
        {
            return new RequirementsFreeze(FreezeOutcome.EarlierVersion, earlier);
        }

        await store.SaveAsync(project.ProjectId, candidate, cancellationToken).ConfigureAwait(true);
        return new RequirementsFreeze(FreezeOutcome.Frozen, candidate);
    }

    /// <summary>The agents of the project's template can be saved (the roster store is available).</summary>
    public bool CanSaveAgents => _backend.Roster is not null;

    /// <summary>
    /// Saves one agent of the project's template, replacing the one with its id («Memoria del proyecto» of the
    /// preparation, docs/14 section 9, WP5). Each run freezes the template it started with, so no past run changes.
    /// </summary>
    public Task SaveAgentAsync(ProjectContext project, AgentDefinition agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(agent);
        var roster = _backend.Roster ?? throw new InvalidOperationException("The agent roster is not available.");
        return roster.SaveAsync(project.ProjectId, agent, cancellationToken);
    }

    /// <summary>
    /// Takes one agent out of the project's template (docs/17 D-37, P48), under the same condition as <see cref="SaveAgentAsync"/>: the
    /// roster store is available. The headers of past runs and debates freeze whole definitions, so none of them changes.
    /// </summary>
    public Task RemoveAgentAsync(ProjectContext project, AgentId agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var roster = _backend.Roster ?? throw new InvalidOperationException("The agent roster is not available.");
        return roster.DeleteAsync(project.ProjectId, agent, cancellationToken);
    }

    /// <summary>Saves the profile the user accepted (a proposal becomes the project's profile only here).</summary>
    public Task SaveVerificationProfileAsync(ProjectContext project, VerificationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        var source = _backend.Verification ?? throw new InvalidOperationException("The verification profile source is not available.");
        return source.SaveAsync(project, profile, cancellationToken);
    }

    public Task<PlanProposal> PlanAsync(PlanningRequest request, IProgress<AgentEvent>? progress, CancellationToken cancellationToken) =>
        Engine.PlanAsync(request, progress, cancellationToken);

    public Task<IOrchestrationRun> StartAsync(RunRequest request, CancellationToken cancellationToken) =>
        Engine.StartAsync(request, cancellationToken);

    public Task<IOrchestrationRun> ResumeAsync(ProjectContext project, RunId run, CancellationToken cancellationToken) =>
        Engine.ResumeAsync(project, run, cancellationToken);

    public Task<IReadOnlyList<RunSummary>> ListRunsAsync(ProjectContext project, CancellationToken cancellationToken) =>
        _backend.Orchestrator is { } engine ? engine.ListRunsAsync(project, cancellationToken) : Task.FromResult<IReadOnlyList<RunSummary>>([]);

    /// <summary>The plan of a run, when the plan store is available.</summary>
    public Task<PlanDefinition?> FindPlanAsync(ProjectContext project, string hash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return _backend.Plans is { } plans ? plans.FindAsync(project.ProjectId, hash, cancellationToken) : Task.FromResult<PlanDefinition?>(null);
    }

    /// <summary>The header of a run (its plan, its roster and its options), when the run registry is available.</summary>
    public Task<RunRecord?> FindRunAsync(ProjectContext project, RunId run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return _backend.Runs is { } runs ? runs.FindAsync(project.ProjectId, run, cancellationToken) : Task.FromResult<RunRecord?>(null);
    }

    private IOrchestrator Engine => _backend.Orchestrator ?? throw new InvalidOperationException("The orchestration engine is not available.");

    /// <summary>The project's agents; a project without any (read without failing) gets the default roster first.</summary>
    private static async Task<(IReadOnlyList<AgentDefinition> Agents, bool Created)> OpenRosterAsync(
        IAgentRoster store,
        ProjectContext project,
        List<string> missing,
        CancellationToken cancellationToken)
    {
        var (agents, failed) = await Try(() => store.ListAsync(project.ProjectId, cancellationToken), Texts.Get("Orchestrator_Session_RosterFailed"), missing).ConfigureAwait(true);
        if (failed || agents is not { Count: 0 })
        {
            return (agents ?? [], false);
        }

        var (_, notCreated) = await Try<object>(
            async () =>
            {
                foreach (var agent in DefaultAgentRoster.Create())
                {
                    await store.SaveAsync(project.ProjectId, agent, cancellationToken).ConfigureAwait(true);
                }

                return new object();
            },
            Texts.Get("Orchestrator_Session_RosterCreateFailed"),
            missing).ConfigureAwait(true);
        if (notCreated)
        {
            return ([], false);
        }

        (agents, _) = await Try(() => store.ListAsync(project.ProjectId, cancellationToken), Texts.Get("Orchestrator_Session_RosterFailed"), missing).ConfigureAwait(true);
        return (agents ?? [], true);
    }

    /// <summary>Reads one piece; a failure is listed for the user and reported as failed (not as "there is none").</summary>
    private static async Task<(T? Value, bool Failed)> Try<T>(Func<Task<T>> read, string failure, List<string> missing)
        where T : class?
    {
        try
        {
            return (await read().ConfigureAwait(true), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Behind a port: a failure is listed for the user and the rest of the screen keeps working.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            missing.Add($"{failure}: {ex.Message}");
            return (null, true);
        }
    }
}
