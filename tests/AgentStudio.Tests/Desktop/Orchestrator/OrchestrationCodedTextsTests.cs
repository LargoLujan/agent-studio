using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Verification;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The coded texts of Orchestration (docs/17 D-47, ADR-0054 addendum of 2026-11-12): every code has its text in Spanish of Spain and
/// in English with the same placeholders, and what the person reads is painted from the resources in the language in use, never
/// from a sentence of the layers below. What is anchored (the warning the approval carries, the first notice of a run) is not
/// painted, and a test of the engine fixes it.
/// </summary>
public sealed partial class OrchestrationCodedTextsTests
{
    private static readonly IUiTextProvider Spanish = new ResourceTextProvider(CultureInfo.GetCultureInfo("es-ES"));
    private static readonly IUiTextProvider English = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static IReadOnlyList<string> Markers(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public static TheoryData<string> Codes()
    {
        var data = new TheoryData<string>();
        foreach (var code in OrchestrationUserTexts.All)
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_code_has_its_text_in_both_languages_with_the_same_placeholders(string code)
    {
        var key = UserText.KeyOf(code);

        Assert.True(Spanish.Has(key), key + " (es)");
        Assert.True(English.Has(key), key + " (en)");
        Assert.Equal(Markers(Spanish.Get(key)), Markers(English.Get(key)));
        Assert.True(Spanish.Get(key) != English.Get(key) || code is OrchestrationUserTexts.ListComma or OrchestrationUserTexts.MediumTask, key);
    }

    [Fact]
    public void The_codes_are_all_different_valid_and_the_list_holds_every_constant_of_the_class()
    {
        var all = OrchestrationUserTexts.All;
        var constants = typeof(OrchestrationUserTexts)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, code => Assert.True(UserText.IsCode(code), code));
        Assert.Equal(constants.Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_refusal_with_a_code_is_painted_in_the_language_in_use_and_any_other_failure_shows_its_technical_message()
    {
        var refusal = new UserTextException(UserText.Of(OrchestrationUserTexts.DeliveryNotChecked), "The plan is too big to check.");

        Assert.Equal("Atención: el plan es demasiado grande para comprobar si se podrá entregar a tu rama.", OrchestratorErrorText.Detail(refusal, Spanish));
        Assert.Equal("Warning: the plan is too big to check whether it can be delivered to your branch.", OrchestratorErrorText.Detail(refusal, English));
        Assert.Equal("boom", OrchestratorErrorText.Detail(new InvalidOperationException("boom"), English));
    }

    private static PlanWarning Warning(PlanWarningCode code, string task, IReadOnlyList<ConditionBranch>? when = null, bool timing = false) =>
        new(code, new TaskId(task), "message for logs") { When = when ?? [], DependsOnTiming = timing };

    [Fact]
    public void The_delivery_warning_names_the_same_tasks_in_both_languages_and_the_logged_notice_stays_in_spanish()
    {
        var warnings = new[]
        {
            Warning(PlanWarningCode.NotDeliverable, "T1"),
            Warning(PlanWarningCode.NotDeliverable, "T2"),
            Warning(PlanWarningCode.MayNotBeDeliverable, "T3", [new ConditionBranch(new TaskId("C1"), ConditionBranches.Yes)], timing: true),
        };
        var texts = PlanWarningText.Texts(warnings);

        var english = OrchestratorCodedText.Sentences(texts, English)!;
        var spanish = OrchestratorCodedText.Sentences(texts, Spanish)!;

        Assert.Contains("what T1 and T2 write cannot be delivered to your branch", english, StringComparison.Ordinal);
        Assert.Contains("what T3 writes may not be deliverable", english, StringComparison.Ordinal);
        Assert.Contains("(for example, if C1 takes the yes branch) and it depends on", english, StringComparison.Ordinal);
        Assert.Contains("lo que escriben T1 y T2 no se podrá entregar", spanish, StringComparison.Ordinal);
        Assert.Contains("si C1 toma la rama sí", spanish, StringComparison.Ordinal);
        Assert.DoesNotContain("Atención", english, StringComparison.Ordinal);

        // What goes to the log as the first notice of the run is the sentence as it was.
        Assert.StartsWith("Atención: lo que escriben T1 y T2 no se podrá entregar a tu rama", PlanWarningText.Notice(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_without_delivery_warnings_has_no_coded_text()
    {
        Assert.Empty(PlanWarningText.Texts([]));
        Assert.Null(OrchestratorCodedText.Sentences(PlanWarningText.Texts([]), English));
    }

    [Fact]
    public void The_warning_about_writers_names_the_same_tasks_agents_and_steps_in_both_languages_and_the_anchored_one_is_unchanged()
    {
        var writers = new[]
        {
            new MediumIntegrityWriter(new TaskId("T1"), MediumIntegrityReason.Shell) { Agent = new AgentId("ejecutorShell") },
            new MediumIntegrityWriter(new TaskId("T1"), MediumIntegrityReason.ToolsBeyondMeasuredProfile) { Agent = new AgentId("ejecutorShell"), Tools = ["Monitor", "Workflow"] },
            new MediumIntegrityWriter(new TaskId("T2"), MediumIntegrityReason.UnmeasuredCli) { Agent = new AgentId("ejecutorGemini"), Provider = ProviderId.GeminiCli },
            new MediumIntegrityWriter(new TaskId("L1"), MediumIntegrityReason.StepWithoutIsolation) { Step = "lint" },
        };
        var texts = VerificationStepText.PlanWarningTexts(writers);

        var english = OrchestratorCodedText.Sentences(texts, English)!;
        var spanish = OrchestratorCodedText.Sentences(texts, Spanish)!;

        Assert.Contains("T1: the agent \"ejecutorShell\" has Bash and the agent \"ejecutorShell\" has tools outside the measured profile (Monitor, Workflow).", english, StringComparison.Ordinal);
        Assert.Contains("T2: the agent \"ejecutorGemini\" writes with gemini-cli, whose confinement was not measured.", english, StringComparison.Ordinal);
        Assert.Contains("L1: the step \"lint\" runs without isolation.", english, StringComparison.Ordinal);
        Assert.Contains("T1: el agente «ejecutorShell» tiene Bash y el agente «ejecutorShell» tiene herramientas fuera del perfil medido (Monitor, Workflow).", spanish, StringComparison.Ordinal);
        Assert.Contains("L1: el paso «lint» se ejecuta sin aislamiento.", spanish, StringComparison.Ordinal);

        // The approval carries the sentence as it was anchored, in Spanish, whatever the language of the screen.
        Assert.StartsWith("Atención: este plan tiene escritores con integridad Medium. T1: el agente «ejecutorShell» tiene Bash", VerificationStepText.PlanWarning(writers), StringComparison.Ordinal);
        Assert.Empty(VerificationStepText.PlanWarningTexts([]));
    }

    [Fact]
    public void A_step_line_is_painted_in_the_language_in_use_and_keeps_its_plain_part_as_it_is()
    {
        var step = DotNetVerificationProfile.Propose().Find("test")! with { Arguments = ["test", "--no-build"], Isolation = ProcessIsolation.None, Requires = ["restore", "build"] };

        Assert.Equal(
            "test · Pruebas · dotnet test --no-build · requires restore and build · NO ISOLATION: the code the agents write runs with your own rights, in the working copy",
            OrchestratorCodedText.StepLine(step, English));
        Assert.StartsWith("test · Pruebas · dotnet test --no-build · requiere restore y build · SIN AISLAMIENTO", OrchestratorCodedText.StepLine(step, Spanish), StringComparison.Ordinal);
    }
}
