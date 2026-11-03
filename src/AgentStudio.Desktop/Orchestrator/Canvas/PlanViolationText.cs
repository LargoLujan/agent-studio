using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;

namespace AgentStudio.Desktop.Orchestrator.Canvas;

/// <summary>
/// The validator's violations in Spanish. <see cref="PlanViolation.Message"/> is English and meant for logs; the UI
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
            PlanViolationCode.EmptyPlan => "El plan no tiene tareas.",
            PlanViolationCode.TooManyTasks => $"El plan tiene {plan.Tasks.Count} tareas; el límite es {context.Limits.MaxTasks}.",
            PlanViolationCode.InvalidTaskId => $"El identificador «{id}» no es válido: empieza por una letra y tiene como mucho 32 letras, dígitos, «_» o «-».",
            PlanViolationCode.DuplicateTaskId => $"El identificador {id} se repite en el plan.",
            PlanViolationCode.TitleRequired => $"La tarea {id} no tiene título.",
            PlanViolationCode.ObjectiveRequired => $"La tarea {id} no tiene objetivo.",
            PlanViolationCode.UnknownDependency => Nth(UnknownPrerequisites(spec, plan), occurrence) is { } missing
                ? $"La tarea {id} depende de {missing}, que no está en el plan."
                : $"La tarea {id} depende de una tarea que no está en el plan.",
            PlanViolationCode.DuplicateDependency => Nth(RepeatedPrerequisites(spec), occurrence) is { } repeated
                ? $"La tarea {id} cita {repeated} como prerrequisito más de una vez."
                : $"La tarea {id} cita un prerrequisito más de una vez.",
            PlanViolationCode.DependencyCycle => $"Hay un ciclo de dependencias que pasa por {id}: una tarea no puede esperarse a sí misma.",
            PlanViolationCode.UnknownAgent => $"La tarea {id} está asignada a «{agent}», que no está en la plantilla de agentes.",
            PlanViolationCode.AgentRequired => $"La tarea {id} ({kind}) necesita un agente.",
            PlanViolationCode.OrchestratorHasTask => $"La tarea {id} está asignada al orquestador «{agent}», que solo planifica.",
            PlanViolationCode.JudgeOutsideReview => $"«{agent}» es juez o auditor y solo puede hacer revisiones; {id} es «{kind}».",
            PlanViolationCode.ReviewerNotJudge => $"La revisión {id} está asignada a «{agent}»; solo un juez o un auditor puede revisar.",
            PlanViolationCode.JudgeVendorNotIndependent => VendorOf(spec, context) is { } vendor
                ? $"El juez «{agent}» de {id} usa el vendor {vendor}, el mismo que el orquestador o un ejecutor del plan: el juez tiene que ser de otro vendor."
                : $"El juez «{agent}» de {id} comparte vendor con el orquestador o un ejecutor del plan: el juez tiene que ser de otro vendor.",
            PlanViolationCode.ConditionWithoutPredicate => $"La condición {id} no tiene predicado: elige qué comprueba.",
            PlanViolationCode.PredicateOnNonCondition => $"La tarea {id} no es una condición pero lleva un predicado.",
            PlanViolationCode.PredicateNotAncestor => spec?.Condition is { } predicate
                ? $"La condición {id} lee la tarea {predicate.ReferencedTask}, que no es una de sus antecesoras."
                : $"La condición {id} lee una tarea que no es una de sus antecesoras.",
            PlanViolationCode.BranchRequired => $"La tarea {id} depende de una condición sin decir de qué rama (Sí o No).",
            PlanViolationCode.BranchOnNonCondition => $"La tarea {id} espera una rama de una tarea que no es una condición.",
            PlanViolationCode.UnknownBranch => $"La tarea {id} espera una rama desconocida; una condición solo tiene las ramas Sí y No.",
            PlanViolationCode.FanInWithoutIntegration => $"La tarea {id} parte del código de varias tareas que no se han unido: añade antes una tarea de integración.",
            PlanViolationCode.UnsafeWriteScope => Nth(UnsafeScopes(spec), occurrence) is { } entry
                ? $"El alcance «{entry}» de {id} no está permitido: usa rutas relativas, sin «..», sin comodines y fuera de .git y .agentstudio."
                : $"Un alcance de escritura de {id} no está permitido: usa rutas relativas, sin «..», sin comodines y fuera de .git y .agentstudio.",
            PlanViolationCode.WriteScopeRequired => $"La tarea {id} escribe en su worktree pero no declara qué carpetas o ficheros puede cambiar.",
            PlanViolationCode.OverlappingWriteScopes => $"El alcance de escritura de {id} se solapa con el de otra tarea que puede ejecutarse a la vez.",
            PlanViolationCode.UnknownRequirement => Nth(spec?.Covers.Where(r => context.Requirements.Find(r) is null).Select(r => r.Value), occurrence) is { } requirement
                ? $"La tarea {id} cubre el requisito {requirement}, que no está en los requisitos congelados."
                : $"La tarea {id} cubre un requisito que no está en los requisitos congelados.",
            PlanViolationCode.MandatoryRequirementUncovered => Nth(UncoveredMandatory(plan, context), occurrence) is { } uncovered
                ? $"El requisito obligatorio {uncovered} no lo cubre ninguna tarea."
                : "Hay un requisito obligatorio que no cubre ninguna tarea.",
            PlanViolationCode.UnknownVerificationStep => Nth(spec?.VerificationSteps.Where(s => !context.VerificationSteps.Contains(s)), occurrence) is { } step
                ? $"La tarea {id} cita el paso de verificación «{step}», que no está en el perfil."
                : $"La tarea {id} cita un paso de verificación que no está en el perfil.",
            PlanViolationCode.ToolRequired => $"La herramienta {id} no dice qué paso del perfil ejecutar.",
            PlanViolationCode.UnknownTool => $"La herramienta {id} cita «{spec?.ToolId ?? "?"}», que no está en el perfil de verificación.",
            PlanViolationCode.RetriesNotAllowed => $"La tarea {id} ({kind}) no admite reintentos: un reintento gastaría cuota o volvería a preguntar.",
            PlanViolationCode.RetriesOutOfRange => $"La tarea {id} admite {spec?.MaxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} reintentos; el límite es {context.Limits.MaxRetriesPerTask}.",
            PlanViolationCode.InvalidTimeout => $"La tarea {id} tiene un tiempo límite que no es positivo.",
            PlanViolationCode.DebateNotAvailable => $"{id} es un nodo de debate: {NodeKinds.UnavailableReason}.",
            PlanViolationCode.IntegrationWithoutVerification => $"La integración {id} no tiene pasos de verificación: el código integrado no se compilaría ni se probaría.",
            PlanViolationCode.IntegrationsNotOrdered => Nth(UnorderedIntegrations(spec, plan), occurrence) is { } other
                ? $"Las integraciones {other} y {id} pueden ejecutarse a la vez: las integraciones de un plan van en cadena, cada una después de la anterior."
                : $"La integración {id} puede ejecutarse a la vez que otra integración: las integraciones de un plan van en cadena, cada una después de la anterior.",
            PlanViolationCode.VerificationRequiredByPolicy => $"La tarea {id} la hace «{agent}», cuya política exige pruebas capturadas por el sistema: añade pasos de verificación a la tarea.",
            PlanViolationCode.ReviewRequiredByPolicy => $"La tarea {id} la hace «{agent}», cuya política exige una revisión independiente: hace falta una tarea de revisión que dependa de todas las tareas que escriben con esa exigencia.",
            PlanViolationCode.TestStepRequiredByPolicy => $"La tarea {id} la hace «{agent}», cuya política exige pruebas capturadas por el sistema, pero ninguno de sus pasos de verificación ejecuta pruebas: añade un paso de pruebas del perfil (una compilación no es una prueba).",
            _ => violation.Task is null ? "El plan incumple una regla del validador." : $"La tarea {id} incumple una regla del validador.",
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
