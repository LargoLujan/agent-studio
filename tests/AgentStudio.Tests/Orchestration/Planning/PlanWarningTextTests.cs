using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Planning;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>B10: the warnings of the validator as the user reads them, before the plan runs and when it starts.</summary>
public sealed class PlanWarningTextTests
{
    private static PlanWarning Warning(PlanWarningCode code, string task, bool timing = false, params ConditionBranch[] when) =>
        new(code, new TaskId(task), "message for logs") { When = when, DependsOnTiming = timing };

    [Fact]
    public void Without_warnings_there_is_nothing_to_say()
    {
        Assert.Null(PlanWarningText.Notice([]));
    }

    [Fact]
    public void A_writer_that_can_never_be_delivered_is_named_with_what_the_plan_needs()
    {
        var text = PlanWarningText.Notice([Warning(PlanWarningCode.NotDeliverable, "T1"), Warning(PlanWarningCode.NotDeliverable, "T2")]);

        Assert.StartsWith("Atención: lo que escriben T1 y T2 no se podrá entregar a tu rama", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "una revisión del juez que dependa de la última integración, sin otra tarea que escriba código entre medias; cualquier otra revisión tiene que ir antes de alguna integración o depender también de la última.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_writer_that_depends_on_the_branches_is_named_with_one_branch_where_it_may_not_be_delivered()
    {
        var text = PlanWarningText.Notice([Warning(PlanWarningCode.MayNotBeDeliverable, "A", when: [new ConditionBranch(new TaskId("C"), ConditionBranches.Yes)])]);

        Assert.Contains("puede que lo que escribe A no se pueda entregar a tu rama: depende de la rama que tomen sus condiciones (por ejemplo, si C toma la rama sí).", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_writer_whose_delivery_depends_on_which_task_finishes_first_is_told_so()
    {
        var text = PlanWarningText.Notice([Warning(PlanWarningCode.NotDeliverable, "A"), Warning(PlanWarningCode.MayNotBeDeliverable, "B", timing: true)]);

        Assert.Contains("lo que escribe A no se podrá entregar", text, StringComparison.Ordinal);
        Assert.Contains(
            "Y puede que lo que escribe B no se pueda entregar a tu rama: depende de qué tareas terminen antes, porque solo se entrega si la revisión del juez que acaba la última partió de la última integración.",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_too_large_to_check_says_so_and_nothing_else()
    {
        var text = PlanWarningText.Notice([new PlanWarning(PlanWarningCode.DeliveryNotChecked, null, "message for logs")]);

        Assert.Equal("Atención: el plan es demasiado grande para comprobar si lo que escriben sus tareas se podrá entregar a tu rama.", text);
    }
}
