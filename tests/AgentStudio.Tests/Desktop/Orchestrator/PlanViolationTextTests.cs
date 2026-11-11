using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The validator speaks English for the logs; the screen shows each <see cref="PlanViolationCode"/> in Spanish with the
/// details taken from the plan. Each case breaks one thing in the valid fixture plan and runs the real validator.
/// </summary>
public sealed class PlanViolationTextTests
{
    private static IReadOnlyList<string> TextsFor(IReadOnlyList<TaskSpec> tasks, PlanContext? context = null)
    {
        var plan = PlanFixtures.Plan(tasks);
        context ??= PlanFixtures.Context();
        return PlanViolationText.Describe(PlanValidator.Validate(plan, context), plan, context);
    }

    private static PlanContext WithPolicy(string agent, ReviewPolicy policy) =>
        PlanFixtures.Context(roster: [.. PlanFixtures.Roster().Select(a => a.Id == Aid(agent) ? a with { Review = policy } : a)]);

    [Fact]
    public void A_missing_objective_names_the_task()
    {
        Assert.Equal(["La tarea T1 no tiene objetivo."], TextsFor(PlanFixtures.Replace("T1", t => t with { Objective = " " })));
    }

    [Fact]
    public void Each_unknown_verification_step_is_named_in_the_order_the_validator_reports_it()
    {
        var texts = TextsFor(PlanFixtures.Replace("T1", t => t with { VerificationSteps = ["build", "deploy", "test", "publish"] }));

        Assert.Equal(
            [
                "La tarea T1 cita el paso de verificación «deploy», que no está en el perfil.",
                "La tarea T1 cita el paso de verificación «publish», que no está en el perfil.",
            ],
            texts);
    }

    [Fact]
    public void An_uncovered_mandatory_requirement_is_named()
    {
        Assert.Equal(
            ["El requisito obligatorio R1 no lo cubre ninguna tarea."],
            TextsFor(PlanFixtures.Replace("T1", t => t with { Covers = [] })));
    }

    [Fact]
    public void A_judge_on_the_vendor_of_the_producers_is_reported_with_its_vendor()
    {
        Assert.Equal(
            ["El juez «juezA» de T7 usa un modelo de anthropic, igual que el orquestador o un ejecutor del plan. El juez tiene que ser de otra empresa."],
            TextsFor(PlanFixtures.Replace("T7", t => t with { Agent = Aid("juezA") })));
    }

    [Fact]
    public void A_debate_node_is_reported_as_Phase_5()
    {
        var texts = TextsFor(PlanFixtures.Replace("T8", t => t with { Kind = WorkTaskKind.Debate, ToolId = null }));

        Assert.Contains("T8 es un nodo de debate: los nodos de debate del lienzo están aplazados; abre el debate desde «Debates».", texts);
    }

    [Fact]
    public void A_condition_reading_a_task_that_is_not_an_ancestor_names_both()
    {
        Assert.Equal(
            ["La condición T4 lee la tarea T8, que no es una de sus antecesoras."],
            TextsFor(PlanFixtures.Replace("T4", t => t with { Condition = new TestsPassed(Tid("T8")) })));
    }

    [Fact]
    public void Integrations_that_could_run_together_are_named_pair_by_pair()
    {
        static TaskSpec Integration(string id) =>
            PlanFixtures.Spec(id, WorkTaskKind.Integration) with { DependsOn = [PlanFixtures.Dep("T1")], VerificationSteps = ["build"] };

        var texts = TextsFor([.. PlanFixtures.ValidTasks(), Integration("T9"), Integration("Ta")]);

        Assert.Equal(
            [
                "Las integraciones T3 y T9 pueden ejecutarse a la vez. Las integraciones de un plan van en cadena, cada una después de la anterior.",
                "Las integraciones T3 y Ta pueden ejecutarse a la vez. Las integraciones de un plan van en cadena, cada una después de la anterior.",
                "Las integraciones T9 y Ta pueden ejecutarse a la vez. Las integraciones de un plan van en cadena, cada una después de la anterior.",
            ],
            texts);
    }

    [Fact]
    public void A_writer_whose_agent_asks_for_tests_is_told_to_add_verification_steps()
    {
        Assert.Equal(
            ["La tarea T6 la hace «ejecutor». Su política exige pruebas hechas por el sistema. Añade pasos de verificación a la tarea."],
            TextsFor(PlanFixtures.ValidTasks(), WithPolicy("ejecutor", new ReviewPolicy(false, true))));
    }

    [Fact]
    public void A_writer_whose_agent_asks_for_tests_and_only_builds_is_told_to_add_a_test_step()
    {
        Assert.Equal(
            ["La tarea T2 la hace «ejecutor2», cuya política exige pruebas capturadas por el sistema. Ninguno de sus pasos de verificación ejecuta pruebas. Añade un paso de pruebas del perfil: una compilación no es una prueba."],
            TextsFor(PlanFixtures.ValidTasks(), WithPolicy("ejecutor2", new ReviewPolicy(false, true))));
    }

    [Fact]
    public void A_writer_whose_agent_asks_for_review_is_told_that_one_review_must_come_after_all_of_them()
    {
        var late = PlanFixtures.Spec("T9") with
        {
            Agent = Aid("ejecutor2"),
            Workspace = WorkspaceMode.Write,
            WriteScope = ["lib/"],
            DependsOn = [PlanFixtures.Dep("T7")],
        };

        Assert.Equal(
            ["La tarea T9 la hace «ejecutor2». Su política exige una revisión independiente. Añade una tarea de revisión que dependa de todas las que escriben."],
            TextsFor([.. PlanFixtures.ValidTasks(), late], WithPolicy("ejecutor2", new ReviewPolicy(true, false))));
    }

    [Fact]
    public void Every_code_has_a_Spanish_text_that_names_its_task()
    {
        var plan = PlanFixtures.Plan();
        var context = PlanFixtures.Context();
        var violations = Enum.GetValues<PlanViolationCode>()
            .Select(code => new PlanViolation(code, Tid("T1"), $"English message for {code}."))
            .ToList();

        var texts = PlanViolationText.Describe(violations, plan, context);

        Assert.Equal(violations.Count, texts.Count);
        foreach (var (violation, text) in violations.Zip(texts))
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{violation.Code} has no text.");
            Assert.DoesNotContain("English", text, StringComparison.Ordinal);
            Assert.DoesNotContain(violation.Code.ToString(), text, StringComparison.Ordinal);
            if (violation.Code is not (PlanViolationCode.EmptyPlan or PlanViolationCode.TooManyTasks or PlanViolationCode.MandatoryRequirementUncovered))
            {
                Assert.Contains("T1", text, StringComparison.Ordinal);
            }
        }
    }
}
