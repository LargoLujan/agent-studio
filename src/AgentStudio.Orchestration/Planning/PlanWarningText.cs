using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// The validator's warnings (<see cref="PlanValidator.Warnings"/>) as the user reads them, in Spanish:
/// <see cref="PlanWarning.Message"/> is English and meant for logs. A run that starts with them gives this text among its
/// first notices (after the one about uncommitted changes, when there is one), and the review of the plan can show it
/// next to «Plan válido», before the approval.
/// </summary>
public static class PlanWarningText
{
    /// <summary>One text for <paramref name="warnings"/>, naming each task they are about in their order; null when there is none.</summary>
    public static string? Notice(IReadOnlyList<PlanWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (warnings.Any(w => w.Code == PlanWarningCode.DeliveryNotChecked))
        {
            return "Atención: el plan es demasiado grande para comprobar si lo que escriben sus tareas se podrá entregar a tu rama.";
        }

        var never = Tasks(warnings, PlanWarningCode.NotDeliverable);
        var maybe = Tasks(warnings, PlanWarningCode.MayNotBeDeliverable);
        if (never.Count == 0 && maybe.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (never.Count > 0)
        {
            parts.Add($"Atención: lo que {Writes(never)} {Enumeration(never)} no se podrá entregar a tu rama, porque en ninguna rama del plan lo fusiona una integración revisada por el juez.");
        }

        if (maybe.Count > 0)
        {
            var uncertain = warnings.Where(w => w.Code == PlanWarningCode.MayNotBeDeliverable).ToList();
            var reasons = new List<string>();
            if (uncertain.FirstOrDefault(w => w.When.Count > 0)?.When is { } example)
            {
                reasons.Add($"depende de la rama que tomen sus condiciones (por ejemplo, {Enumeration([.. example.Select(Branch)])})");
            }

            if (uncertain.Any(w => w.DependsOnTiming))
            {
                reasons.Add("depende de qué tareas terminen antes, porque solo se entrega si la revisión del juez que acaba la última partió de la última integración");
            }

            var opening = never.Count > 0 ? "Y" : "Atención:";
            parts.Add($"{opening} puede que lo que {Writes(maybe)} {Enumeration(maybe)} no se pueda entregar a tu rama: {Enumeration(reasons)}.");
        }

        parts.Add("Para poder entregarlo, en todas las ramas tiene que haber una tarea de integración después de las tareas que escriben y una revisión del juez que dependa de la última integración, sin otra tarea que escriba código entre medias; cualquier otra revisión tiene que ir antes de alguna integración o depender también de la última.");
        return string.Join(' ', parts);
    }

    private static List<string> Tasks(IReadOnlyList<PlanWarning> warnings, PlanWarningCode code) =>
        [.. warnings.Where(w => w.Code == code).Select(w => w.Task is { } task ? task.Value : null).OfType<string>().Distinct(StringComparer.Ordinal)];

    private static string Writes(IReadOnlyList<string> tasks) => tasks.Count == 1 ? "escribe" : "escriben";

    /// <summary>«si C1 toma la rama sí».</summary>
    private static string Branch(ConditionBranch branch) =>
        $"si {branch.Condition.Value} toma la rama {(branch.Branch == ConditionBranches.Yes ? "sí" : branch.Branch == ConditionBranches.No ? "no" : branch.Branch)}";

    /// <summary>«a», «a y b», «a, b y c».</summary>
    private static string Enumeration(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} y {items[^1]}";
}
