using AgentStudio.Desktop.Localization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// The validator's violations in the language of the interface. <see cref="PlanViolation.Message"/> is English and meant for logs; the UI
/// refers to the stable <see cref="PlanViolationCode"/> (as the domain asks) and fills in the details from the plan and
/// the context it was checked against.
/// </summary>
/// <remarks>
/// Some codes repeat for one task, once per offending entry (an unknown step, an unsafe scope entry...). The validator
/// reports them in a stable order, the order of the entries in the task, so the n-th violation of a code for a task
/// is about the n-th offending entry. A detail that cannot be found falls back to a text without it.
/// </remarks>
public static class PlanViolationText
{
    /// <summary>The text of every violation, in the validator's order.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<PlanViolation> violations, PlanDefinition plan, PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(violations);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        var seen = new Dictionary<(PlanViolationCode, TaskId?), int>();
        var texts = new List<string>(violations.Count);
        foreach (var violation in violations)
        {
            var key = (violation.Code, violation.Task);
            var occurrence = seen.GetValueOrDefault(key);
            seen[key] = occurrence + 1;
            texts.Add(Describe(violation, occurrence, plan, context));
        }

        return texts;
    }

    private static string Describe(PlanViolation violation, int occurrence, PlanDefinition plan, PlanContext context)
    {
        var id = violation.Task?.Value ?? "?";
        var spec = violation.Task is { } taskId ? plan.Tasks.FirstOrDefault(t => t.Id == taskId) : null;
        var agent = spec?.Agent?.Value ?? "?";
        var kind = spec is null ? "?" : NodeKinds.Label(spec.Kind);

        return violation.Code switch
        {
            PlanViolationCode.EmptyPlan => Texts.Get("Orchestrator_Violation_EmptyPlan"),
            PlanViolationCode.TooManyTasks => Texts.Format("Orchestrator_Violation_TooManyTasks", plan.Tasks.Count, context.Limits.MaxTasks),
            PlanViolationCode.InvalidTaskId => Texts.Format("Orchestrator_Violation_InvalidTaskId", id),
            PlanViolationCode.DuplicateTaskId => Texts.Format("Orchestrator_Violation_DuplicateTaskId", id),
            PlanViolationCode.TitleRequired => Texts.Format("Orchestrator_Violation_TitleRequired", id),
            PlanViolationCode.ObjectiveRequired => Texts.Format("Orchestrator_Violation_ObjectiveRequired", id),
            PlanViolationCode.UnknownDependency => Nth(UnknownPrerequisites(spec, plan), occurrence) is { } missing
                ? Texts.Format("Orchestrator_Violation_UnknownDependency", id, missing)
                : Texts.Format("Orchestrator_Violation_UnknownDependencyAny", id),
            PlanViolationCode.DuplicateDependency => Nth(RepeatedPrerequisites(spec), occurrence) is { } repeated
                ? Texts.Format("Orchestrator_Violation_DuplicateDependency", id, repeated)
                : Texts.Format("Orchestrator_Violation_DuplicateDependencyAny", id),
            PlanViolationCode.DependencyCycle => Texts.Format("Orchestrator_Violation_DependencyCycle", id),
            PlanViolationCode.UnknownAgent => Texts.Format("Orchestrator_Violation_UnknownAgent", id, agent),
            PlanViolationCode.AgentRequired => Texts.Format("Orchestrator_Violation_AgentRequired", id, kind),
            PlanViolationCode.OrchestratorHasTask => Texts.Format("Orchestrator_Violation_OrchestratorHasTask", id, agent),
            PlanViolationCode.JudgeOutsideReview => Texts.Format("Orchestrator_Violation_JudgeOutsideReview", agent, id, kind),
            PlanViolationCode.ReviewerNotJudge => Texts.Format("Orchestrator_Violation_ReviewerNotJudge", id, agent),
            PlanViolationCode.JudgeVendorNotIndependent => VendorOf(spec, context) is { } vendor
                ? Texts.Format("Orchestrator_Violation_JudgeVendorNotIndependent", id, agent, vendor)
                : Texts.Format("Orchestrator_Violation_JudgeVendorNotIndependentAny", id, agent),
            PlanViolationCode.ConditionWithoutPredicate => Texts.Format("Orchestrator_Violation_ConditionWithoutPredicate", id),
            PlanViolationCode.PredicateOnNonCondition => Texts.Format("Orchestrator_Violation_PredicateOnNonCondition", id),
            PlanViolationCode.PredicateNotAncestor => spec?.Condition is { } predicate
                ? Texts.Format("Orchestrator_Violation_PredicateNotAncestor", id, predicate.ReferencedTask)
                : Texts.Format("Orchestrator_Violation_PredicateNotAncestorAny", id),
            PlanViolationCode.BranchRequired => Texts.Format("Orchestrator_Violation_BranchRequired", id),
            PlanViolationCode.BranchOnNonCondition => Texts.Format("Orchestrator_Violation_BranchOnNonCondition", id),
            PlanViolationCode.UnknownBranch => Texts.Format("Orchestrator_Violation_UnknownBranch", id),
            PlanViolationCode.FanInWithoutIntegration => Texts.Format("Orchestrator_Violation_FanInWithoutIntegration", id),
            PlanViolationCode.UnsafeWriteScope => Nth(UnsafeScopes(spec), occurrence) is { } entry
                ? Texts.Format("Orchestrator_Violation_UnsafeWriteScope", id, entry)
                : Texts.Format("Orchestrator_Violation_UnsafeWriteScopeAny", id),
            PlanViolationCode.WriteScopeRequired => Texts.Format("Orchestrator_Violation_WriteScopeRequired", id),
            PlanViolationCode.OverlappingWriteScopes => Texts.Format("Orchestrator_Violation_OverlappingWriteScopes", id),
            PlanViolationCode.UnknownRequirement => Nth(spec?.Covers.Where(r => context.Requirements.Find(r) is null).Select(r => r.Value), occurrence) is { } requirement
                ? Texts.Format("Orchestrator_Violation_UnknownRequirement", id, requirement)
                : Texts.Format("Orchestrator_Violation_UnknownRequirementAny", id),
            PlanViolationCode.MandatoryRequirementUncovered => Nth(UncoveredMandatory(plan, context), occurrence) is { } uncovered
                ? Texts.Format("Orchestrator_Violation_MandatoryRequirementUncovered", uncovered)
                : Texts.Get("Orchestrator_Violation_MandatoryRequirementUncoveredAny"),
            PlanViolationCode.UnknownVerificationStep => Nth(spec?.VerificationSteps.Where(s => !context.VerificationSteps.Contains(s)), occurrence) is { } step
                ? Texts.Format("Orchestrator_Violation_UnknownVerificationStep", id, step)
                : Texts.Format("Orchestrator_Violation_UnknownVerificationStepAny", id),
            PlanViolationCode.ToolRequired => Texts.Format("Orchestrator_Violation_ToolRequired", id),
            PlanViolationCode.UnknownTool => Texts.Format("Orchestrator_Violation_UnknownTool", id, spec?.ToolId ?? "?"),
            PlanViolationCode.RetriesNotAllowed => Texts.Format("Orchestrator_Violation_RetriesNotAllowed", id, kind),
            PlanViolationCode.RetriesOutOfRange => Texts.Format("Orchestrator_Violation_RetriesOutOfRange", id, spec?.MaxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", context.Limits.MaxRetriesPerTask),
            PlanViolationCode.InvalidTimeout => Texts.Format("Orchestrator_Violation_InvalidTimeout", id),
            PlanViolationCode.DebateNotAvailable => Texts.Format("Orchestrator_Violation_DebateNotAvailable", id, NodeKinds.UnavailableReason),
            PlanViolationCode.IntegrationWithoutVerification => Texts.Format("Orchestrator_Violation_IntegrationWithoutVerification", id),
            PlanViolationCode.IntegrationsNotOrdered => Nth(UnorderedIntegrations(spec, plan), occurrence) is { } other
                ? Texts.Format("Orchestrator_Violation_IntegrationsNotOrdered", id, other)
                : Texts.Format("Orchestrator_Violation_IntegrationsNotOrderedAny", id),
            PlanViolationCode.VerificationRequiredByPolicy => Texts.Format("Orchestrator_Violation_VerificationRequiredByPolicy", id, agent),
            PlanViolationCode.ReviewRequiredByPolicy => Texts.Format("Orchestrator_Violation_ReviewRequiredByPolicy", id, agent),
            PlanViolationCode.TestStepRequiredByPolicy => Texts.Format("Orchestrator_Violation_TestStepRequiredByPolicy", id, agent),
            _ => violation.Task is null ? Texts.Get("Orchestrator_Violation_DefaultPlan") : Texts.Format("Orchestrator_Violation_Default", id),
        };
    }

    private static string? Nth(IEnumerable<string>? values, int index) => values?.Skip(index).FirstOrDefault();

    private static IEnumerable<string> UnknownPrerequisites(TaskSpec? spec, PlanDefinition plan) =>
        spec is null
            ? []
            : spec.DependsOn
                .Select(d => d.Prerequisite)
                .Where(p => p != spec.Id && plan.Tasks.All(t => t.Id != p))
                .Select(p => p.Value);

    private static IEnumerable<string> RepeatedPrerequisites(TaskSpec? spec)
    {
        if (spec is null)
        {
            yield break;
        }

        var seen = new HashSet<TaskId>();
        foreach (var dependency in spec.DependsOn)
        {
            if (!seen.Add(dependency.Prerequisite))
            {
                yield return dependency.Prerequisite.Value;
            }
        }
    }

    private static IEnumerable<string> UnsafeScopes(TaskSpec? spec) =>
        spec?.WriteScope.Where(entry => !WriteScope.TryNormalize(entry, out _, out _)) ?? [];

    private static IEnumerable<string> UncoveredMandatory(PlanDefinition plan, PlanContext context)
    {
        var covered = plan.Tasks.SelectMany(t => t.Covers).ToHashSet();
        return context.Requirements.Mandatory.Where(r => !covered.Contains(r.Id)).Select(r => r.Id.Value);
    }

    private static string? VendorOf(TaskSpec? spec, PlanContext context) =>
        spec?.Agent is { } agent ? context.Roster.FirstOrDefault(a => a.Id == agent)?.Model.Vendor.Value : null;

    /// <summary>The integrations before <paramref name="spec"/> in the plan's order that are neither its ancestors nor its descendants, as the validator pairs them.</summary>
    private static IEnumerable<string> UnorderedIntegrations(TaskSpec? spec, PlanDefinition plan)
    {
        if (spec is null)
        {
            yield break;
        }

        var ancestors = AncestorsOf(spec.Id, plan);
        foreach (var other in plan.Tasks.TakeWhile(t => t.Id != spec.Id).Where(t => t.Kind == WorkTaskKind.Integration))
        {
            if (!ancestors.Contains(other.Id) && !AncestorsOf(other.Id, plan).Contains(spec.Id))
            {
                yield return other.Id.Value;
            }
        }
    }

    private static HashSet<TaskId> AncestorsOf(TaskId id, PlanDefinition plan)
    {
        var byId = new Dictionary<TaskId, TaskSpec>();
        foreach (var task in plan.Tasks)
        {
            byId.TryAdd(task.Id, task);
        }

        var found = new HashSet<TaskId>();
        var stack = new Stack<TaskId>(byId.TryGetValue(id, out var start) ? start.DependsOn.Select(d => d.Prerequisite) : []);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (found.Add(current) && byId.TryGetValue(current, out var task))
            {
                foreach (var dependency in task.DependsOn)
                {
                    stack.Push(dependency.Prerequisite);
                }
            }
        }

        return found;
    }
}
