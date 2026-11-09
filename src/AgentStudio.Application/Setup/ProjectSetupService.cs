using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Models;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Security;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Application.Setup;

/// <summary>
/// The configurator of a project (docs/17 §4.6, D-26 and D-27; ADR-0055). <see cref="ProposeAsync"/> reads the repository and
/// the project and proposes the seven steps without network, without an agent and without quota; <see cref="ApplyAsync"/> writes
/// what the person reviewed, each part through the gate of the screen it belongs to, and leaves <c>ProjectSetupApplied</c> in the
/// decision log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>No gates of its own.</b> The brief is saved as the brief screen does (<see cref="IProjectBrief"/>, a new version of
/// the person's); the template with <see cref="IAgentRoster.SaveAsync"/>, so its hash changes and the approval of the plan that
/// waits is withdrawn by whoever reads <see cref="SetupApplyResult.RosterChanged"/>; the profile with the person's confirmation
/// (<see cref="PresenceAction.ChangeVerificationProfile"/>, always); the skills as decisions of the person
/// (<see cref="ISkillActivationStore.AppendAsync"/>); the options in the store of the project.</item>
/// <item><b>The barrier is not skipped.</b> A template that gives an agent a capability of the fixed barrier
/// (<see cref="SetupCapabilities.Granted"/>) asks <see cref="PresenceAction.GrantHighRiskCapability"/> first. Without a gate
/// composed, those two parts are denied, never applied: fail closed.</item>
/// <item><b>Nothing is applied alone.</b> A part that is not in the <see cref="SetupSelection"/> is skipped, and what the person
/// selected but the gate did not confirm is reported as denied with nothing written. A part that fails does not undo the others:
/// each is its own write, and the result says what each one did.</item>
/// <item><b>The registry.</b> <c>ProjectSetupApplied</c> carries the hash of each thing written (null for what was not), and is
/// not written when nothing was.</item>
/// </list>
/// </remarks>
public sealed class ProjectSetupService
{
    /// <summary>Who the configurator records as having decided: the person at the screen (the same as every approval).</summary>
    public const string DecidedBy = "user";

    private readonly ISetupRepositoryReader _repository;
    private readonly IAgentRoster _roster;
    private readonly ISetupVerificationProfiles _profiles;
    private readonly IProjectOptionsStore _options;
    private readonly IDecisionRecorder _recorder;
    private readonly IProjectBrief? _brief;
    private readonly ISkillCatalog? _catalog;
    private readonly ISkillActivationStore? _activations;
    private readonly IRequirementStore? _requirements;
    private readonly IModelCatalogSource? _models;
    private readonly IModelLearning? _learning;
    private readonly ISetupPresenceGate? _gate;

    /// <param name="brief">The brief of the project; without it step 2 is not offered.</param>
    /// <param name="catalog">The skills catalog; without it step 6 proposes nothing.</param>
    /// <param name="activations">The decisions of the person about skills; without it step 6 is not applied.</param>
    /// <param name="requirements">The requirements of the project, for the recommendation of the orchestrator; without it, none.</param>
    /// <param name="models">The catalog of models; without it step 4 recommends nothing.</param>
    /// <param name="learning">What the verified log teaches of each model; without it the recommendation has no history.</param>
    /// <param name="gate">The barrier of Windows Hello; without it the profile and the capabilities of the barrier are denied.</param>
    public ProjectSetupService(
        ISetupRepositoryReader repository,
        IAgentRoster roster,
        ISetupVerificationProfiles profiles,
        IProjectOptionsStore options,
        IDecisionRecorder recorder,
        IProjectBrief? brief = null,
        ISkillCatalog? catalog = null,
        ISkillActivationStore? activations = null,
        IRequirementStore? requirements = null,
        IModelCatalogSource? models = null,
        IModelLearning? learning = null,
        ISetupPresenceGate? gate = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _brief = brief;
        _catalog = catalog;
        _activations = activations;
        _requirements = requirements;
        _models = models;
        _learning = learning;
        _gate = gate;
    }

    /// <summary>Whether step 2 can save a brief.</summary>
    public bool CanSaveBrief => _brief is not null;

    /// <summary>
    /// The proposals of the seven steps for <paramref name="project"/> (the team of <paramref name="size"/>, or the one of the size
    /// of the project). Reading only: no write, no agent, no network. What cannot be read goes to
    /// <see cref="SetupProposal.Problems"/> and its step proposes nothing.
    /// </summary>
    public async Task<SetupProposal> ProposeAsync(ProjectContext project, SetupSize? size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var problems = new List<UserText>();
        var facts = await _repository.ReadAsync(project, ct).ConfigureAwait(false);
        var projectProposal = SetupProposer.ProposeProject(facts);

        ProjectBrief? brief = null;
        if (_brief is not null)
        {
            try
            {
                brief = await _brief.ReadAsync(project, ct).ConfigureAwait(false);
            }
            catch (DecisionLogIntegrityException)
            {
                problems.Add(UserText.Of("setup.problem.brief-unreadable"));
            }
        }

        var current = await _roster.ListAsync(project.ProjectId, ct).ConfigureAwait(false);
        var team = SetupProposer.ProposeTeam(size ?? projectProposal.Size, current);

        var verification = await ProposeVerificationAsync(project, facts, problems, ct).ConfigureAwait(false);
        var budget = await _options.LoadAsync(project, ct).ConfigureAwait(false) ?? ProjectRunOptions.Defaults;
        var models = await ProposeModelsAsync(project, team, projectProposal, budget, ct).ConfigureAwait(false);
        var skills = await ProposeSkillsAsync(project, team, projectProposal, problems, ct).ConfigureAwait(false);

        return new SetupProposal(
            projectProposal,
            new SetupBriefProposal(SetupProposer.BriefSections(projectProposal.Stack), brief),
            team,
            models,
            verification,
            skills,
            budget,
            problems);
    }

    /// <summary>
    /// Writes what <paramref name="selection"/> holds, part by part, through the gate of each one, and records
    /// <c>ProjectSetupApplied</c> with the hash of what was written. Never throws for a part that fails: its result says why.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<SetupApplyResult> ApplyAsync(ProjectContext project, SetupSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selection);
        var parts = new List<SetupPartResult>();

        var (briefResult, briefHash) = await ApplyBriefAsync(project, selection.BriefText, ct).ConfigureAwait(false);
        parts.Add(briefResult);

        var (rosterResult, before, after, rosterHash) = await ApplyRosterAsync(project, selection.Roster, ct).ConfigureAwait(false);
        parts.Add(rosterResult);

        var (profileResult, profileHash) = await ApplyProfileAsync(project, selection.Profile, ct).ConfigureAwait(false);
        parts.Add(profileResult);

        var (skillsResult, skillsHash) = await ApplySkillsAsync(project, selection.Skills, ct).ConfigureAwait(false);
        parts.Add(skillsResult);

        var (optionsResult, optionsHash) = await ApplyOptionsAsync(project, selection.Options, ct).ConfigureAwait(false);
        parts.Add(optionsResult);

        var recorded = false;
        UserText? recordProblem = null;
        if (new[] { briefHash, rosterHash, profileHash, skillsHash, optionsHash }.Any(h => h is not null))
        {
            try
            {
                var payload = ProjectSetupAppliedPayload.Applying(rosterHash, briefHash, profileHash, skillsHash, optionsHash, DecidedBy);
                await _recorder.RecordAsync(
                    project.ProjectId,
                    new Actor(ActorKind.User, DecidedBy),
                    DecisionKind.ProjectSetupApplied,
                    "Configuración del proyecto aplicada",
                    payload,
                    cancellationToken: ct).ConfigureAwait(false);
                recorded = true;
            }
            catch (DecisionLogIntegrityException)
            {
                recordProblem = UserText.Of("setup.problem.record-failed");
            }
        }

        return new SetupApplyResult(parts, before, after, before is not null && after is not null && before != after, recorded, recordProblem);
    }

    private async Task<SetupVerificationProposal> ProposeVerificationAsync(ProjectContext project, SetupRepositoryFacts facts, List<UserText> problems, CancellationToken ct)
    {
        VerificationProfile? current = null;
        var unreadable = false;
        try
        {
            current = await _profiles.LoadAsync(project, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            unreadable = true;
            problems.Add(UserText.Of("setup.problem.profile-unreadable"));
        }

        var proposed = _profiles.Propose(facts.Files);
        return new SetupVerificationProposal(current, proposed, unreadable, proposed is null ? "setup.verification.no-stack" : null);
    }

    private async Task<IReadOnlyList<SetupModelProposal>> ProposeModelsAsync(
        ProjectContext project,
        SetupTeamProposal team,
        SetupProjectProposal projectProposal,
        ProjectRunOptions budget,
        CancellationToken ct)
    {
        var agents = team.Changes.Select(c => c.After).ToList();
        if (_models is null)
        {
            return [.. agents.Select(a => new SetupModelProposal(a, null, null, false))];
        }

        var requirements = 0;
        var mandatory = 0;
        if (_requirements is not null)
        {
            var latest = await _requirements.LatestAsync(project.ProjectId, ct).ConfigureAwait(false);
            requirements = latest?.Requirements.Count ?? 0;
            mandatory = latest?.Requirements.Count(r => r.Priority == RequirementPriority.Mandatory) ?? 0;
        }

        var history = _learning is null ? [] : (await _learning.LearnAsync(project.RootPath, ct).ConfigureAwait(false)).Performances;
        return SetupProposer.ProposeModels(_models.Current.Catalog, agents, projectProposal, requirements, mandatory, budget.MaxAgentRuns, history);
    }

    private async Task<IReadOnlyList<SetupSkillSuggestion>> ProposeSkillsAsync(
        ProjectContext project,
        SetupTeamProposal team,
        SetupProjectProposal projectProposal,
        List<UserText> problems,
        CancellationToken ct)
    {
        if (_catalog is null)
        {
            return [];
        }

        var catalog = await _catalog.ListAsync(ct).ConfigureAwait(false);
        var decisions = _activations is null ? [] : await _activations.ListAsync(project.ProjectId, ct).ConfigureAwait(false);
        if (catalog.Count == 0)
        {
            problems.Add(UserText.Of("setup.problem.skills-catalog-empty"));
        }

        return SetupProposer.ProposeSkills(catalog, [.. team.Changes.Select(c => c.After)], projectProposal.StackTags, project.ProjectId, decisions);
    }

    private async Task<(SetupPartResult Result, string? Hash)> ApplyBriefAsync(ProjectContext project, string? text, CancellationToken ct)
    {
        if (text is null)
        {
            return (new SetupPartResult(SetupStep.Brief, SetupPartOutcome.Skipped), null);
        }

        if (_brief is null)
        {
            return (new SetupPartResult(SetupStep.Brief, SetupPartOutcome.Failed, UserText.Of("setup.problem.brief-not-available")), null);
        }

        try
        {
            var current = await _brief.ReadAsync(project, ct).ConfigureAwait(false);
            if (current is not null && string.Equals(current.Text, text.Trim(), StringComparison.Ordinal))
            {
                return (new SetupPartResult(SetupStep.Brief, SetupPartOutcome.Unchanged), null);
            }

            var saved = await _brief.SaveAsync(project, text, DecidedBy, ct).ConfigureAwait(false);
            return (new SetupPartResult(SetupStep.Brief, SetupPartOutcome.Applied), saved.Sha256);
        }
        catch (Exception ex) when (ex is ArgumentException or DecisionLogIntegrityException)
        {
            return (new SetupPartResult(SetupStep.Brief, SetupPartOutcome.Failed, UserText.Of("setup.problem.brief-not-saved")), null);
        }
    }

    private async Task<(SetupPartResult Result, string? Before, string? After, string? Hash)> ApplyRosterAsync(
        ProjectContext project,
        IReadOnlyList<AgentDefinition>? selected,
        CancellationToken ct)
    {
        if (selected is null)
        {
            return (new SetupPartResult(SetupStep.Team, SetupPartOutcome.Skipped), null, null, null);
        }

        var current = await _roster.ListAsync(project.ProjectId, ct).ConfigureAwait(false);
        var before = RosterHash.Of(current);
        var changed = selected
            .Where(a => current.FirstOrDefault(c => c.Id == a.Id) is not { } old || RosterHash.Of([old]) != RosterHash.Of([a]))
            .ToList();
        if (changed.Count == 0)
        {
            return (new SetupPartResult(SetupStep.Team, SetupPartOutcome.Unchanged), before, before, null);
        }

        // Every agent is checked before the first is stored, so a template that is not valid leaves the stored one as it was.
        var invalid = changed.Where(a => a.Validate().Count > 0).ToList();
        if (invalid.Count > 0)
        {
            return (new SetupPartResult(SetupStep.Team, SetupPartOutcome.Failed, UserText.Of("setup.problem.agent-invalid", invalid[0].Name)), before, before, null);
        }

        var granted = SetupCapabilities.Granted(current, changed);
        if (granted.Count > 0)
        {
            var gate = await ConfirmAsync(PresenceAction.GrantHighRiskCapability, UserText.Of("setup.hello.subject-team", current.Select(a => a.Id).Union(changed.Select(a => a.Id)).Count()), ct).ConfigureAwait(false);
            if (!gate.Confirmed)
            {
                return (new SetupPartResult(SetupStep.Team, SetupPartOutcome.Denied, gate.Problem), before, before, null);
            }
        }

        try
        {
            foreach (var agent in changed)
            {
                await _roster.SaveAsync(project.ProjectId, agent, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The agents saved before the error stay saved: what the store holds now is what the log names, and the result says why.
            var partial = before;
            try
            {
                partial = RosterHash.Of(await _roster.ListAsync(project.ProjectId, ct).ConfigureAwait(false));
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // The store cannot even be read: nothing is claimed of it.
            }

            return (new SetupPartResult(SetupStep.Team, SetupPartOutcome.Failed, UserText.Of("setup.problem.roster-not-saved", ex.Message)), before, partial, partial == before ? null : partial);
        }

        var after = RosterHash.Of(await _roster.ListAsync(project.ProjectId, ct).ConfigureAwait(false));
        return (new SetupPartResult(SetupStep.Team, SetupPartOutcome.Applied), before, after, after);
    }

    private async Task<(SetupPartResult Result, string? Hash)> ApplyProfileAsync(ProjectContext project, VerificationProfile? profile, CancellationToken ct)
    {
        if (profile is null)
        {
            return (new SetupPartResult(SetupStep.Verification, SetupPartOutcome.Skipped), null);
        }

        VerificationProfile? current;
        try
        {
            current = await _profiles.LoadAsync(project, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // The file of the person is never overwritten from here: they fix it, or delete it, themselves.
            return (new SetupPartResult(SetupStep.Verification, SetupPartOutcome.Failed, UserText.Of("setup.problem.profile-unreadable")), null);
        }

        if (current is not null && current.ContentHash == profile.ContentHash)
        {
            return (new SetupPartResult(SetupStep.Verification, SetupPartOutcome.Unchanged), null);
        }

        var gate = await ConfirmAsync(PresenceAction.ChangeVerificationProfile, UserText.Of("setup.hello.subject-profile", profile.Name switch { SetupStacks.DotNet => ".NET", SetupStacks.Node => "Node", SetupStacks.Python => "Python", var other => other }), ct).ConfigureAwait(false);
        if (!gate.Confirmed)
        {
            return (new SetupPartResult(SetupStep.Verification, SetupPartOutcome.Denied, gate.Problem), null);
        }

        try
        {
            await _profiles.SaveAsync(project, profile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (new SetupPartResult(SetupStep.Verification, SetupPartOutcome.Failed, UserText.Of("setup.problem.profile-not-saved", ex.Message)), null);
        }

        return (new SetupPartResult(SetupStep.Verification, SetupPartOutcome.Applied), profile.ContentHash);
    }

    private async Task<(SetupPartResult Result, string? Hash)> ApplySkillsAsync(ProjectContext project, IReadOnlyList<SetupSkillChoice>? choices, CancellationToken ct)
    {
        if (choices is null or { Count: 0 })
        {
            return (new SetupPartResult(SetupStep.Skills, SetupPartOutcome.Skipped), null);
        }

        if (_activations is null)
        {
            return (new SetupPartResult(SetupStep.Skills, SetupPartOutcome.Failed, UserText.Of("setup.problem.skills-not-available")), null);
        }

        var decided = choices
            .DistinctBy(c => (c.Agent, c.Skill))
            .OrderBy(c => c.Agent.Value, StringComparer.Ordinal)
            .ThenBy(c => c.Skill.Value, StringComparer.Ordinal)
            .ToList();
        var current = await _activations.ListAsync(project.ProjectId, ct).ConfigureAwait(false);
        var fresh = decided
            .Where(c => !SkillActivationResolver.IsEnabled(c.Skill, project.ProjectId, c.Agent.Value, current))
            .ToList();
        if (fresh.Count == 0)
        {
            return (new SetupPartResult(SetupStep.Skills, SetupPartOutcome.Unchanged), null);
        }

        foreach (var choice in fresh)
        {
            await _activations.AppendAsync(project.ProjectId, new SkillActivation(choice.Skill, SkillScopeKind.Agent, choice.Agent.Value, true), DecidedBy, ct).ConfigureAwait(false);
        }

        var fields = new List<string?> { "setup-skills/1" };
        foreach (var choice in fresh)
        {
            fields.Add(choice.Agent.Value);
            fields.Add(choice.Skill.Value);
        }

        return (new SetupPartResult(SetupStep.Skills, SetupPartOutcome.Applied), ContentHash.Sha256Hex(ContentHash.Canonical([.. fields])));
    }

    private async Task<(SetupPartResult Result, string? Hash)> ApplyOptionsAsync(ProjectContext project, ProjectRunOptions? options, CancellationToken ct)
    {
        if (options is null)
        {
            return (new SetupPartResult(SetupStep.Budget, SetupPartOutcome.Skipped), null);
        }

        if (options.Validate().Count > 0)
        {
            return (new SetupPartResult(SetupStep.Budget, SetupPartOutcome.Failed, UserText.Of(options.Validate()[0])), null);
        }

        var current = await _options.LoadAsync(project, ct).ConfigureAwait(false) ?? ProjectRunOptions.Defaults;
        if (current == options)
        {
            return (new SetupPartResult(SetupStep.Budget, SetupPartOutcome.Unchanged), null);
        }

        try
        {
            await _options.SaveAsync(project, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new SetupPartResult(SetupStep.Budget, SetupPartOutcome.Failed, UserText.Of("setup.problem.options-not-saved", ex.Message)), null);
        }

        return (new SetupPartResult(SetupStep.Budget, SetupPartOutcome.Applied), OptionsHash(options));
    }

    /// <summary>The SHA-256 of the options as <c>ProjectSetupApplied</c> names them: the three values, the time in whole seconds or <c>-</c>.</summary>
    public static string OptionsHash(ProjectRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seconds = options.TimePerAttempt is { } time ? ((long)time.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        return ContentHash.Sha256Hex(ContentHash.Canonical(
            "setup-options/1",
            options.MaxAgentRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.MaxParallelTasks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            seconds));
    }

    private async Task<SetupPresenceResult> ConfirmAsync(PresenceAction action, UserText subject, CancellationToken ct) =>
        _gate is null
            ? new SetupPresenceResult(false, UserText.Of("setup.hello.not-composed"))
            : await _gate.ConfirmAsync(action, subject, ct).ConfigureAwait(false);
}
