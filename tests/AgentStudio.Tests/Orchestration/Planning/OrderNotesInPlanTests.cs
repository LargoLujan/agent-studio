using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Orchestration.Analysis.Rules;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Questions;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Planning;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using static AgentStudio.Tests.Orchestration.Planning.PlanJson;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// F13-10 (docs/17 D-19) and D-28: the notes of order go inside the plan, at no extra run, and the orchestrator may ask the
/// person instead of planning, before it plans. The schema stays portable to strict mode and within 16 KB; the answer is read
/// strictly, and what the notes say is checked against the plan.
/// </summary>
public sealed class OrderNotesInPlanTests
{
    private static readonly AgentId Author = new("orquestador");

    private static JsonObject Valid() =>
        Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            Task("T2", writeScope: ["docs/"]),
            Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [Dependency("T1"), Dependency("T2")], steps: ["build", "test"]),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("I1")]));

    private static JsonObject Note(string left, string leftName, string right, string rightName, string reason) =>
        new() { [leftName] = left, [rightName] = right, ["reason"] = reason };

    private static JsonObject Notes(JsonArray? dependencies = null, JsonArray? parallel = null, JsonArray? harmful = null) =>
        new() { ["dependencies"] = dependencies ?? [], ["parallelOk"] = parallel ?? [], ["harmful"] = harmful ?? [] };

    private static JsonObject WithNotes(JsonObject notes)
    {
        var plan = Valid();
        plan["orderNotes"] = notes;
        plan["questions"] = new JsonArray();
        return plan;
    }

    private static JsonObject Option(string id, string label, string effort = "small", bool recommended = false) =>
        new() { ["id"] = id, ["label"] = label, ["detail"] = "Detalle de " + label, ["effort"] = effort, ["recommended"] = recommended };

    private static JsonObject Question(string text, params JsonObject[] options) =>
        new() { ["text"] = text, ["why"] = "Cambia cómo se guardan los datos.", ["options"] = new JsonArray([.. options]), ["allowOther"] = true };

    private static JsonObject Asking(params JsonObject[] questions)
    {
        var answer = PlanJson.Plan();
        answer["orderNotes"] = Notes();
        answer["questions"] = new JsonArray([.. questions]);
        return answer;
    }

    private static PlanParseResult Parse(JsonNode document) => PlanParser.Parse(document.ToJsonString(), Author);

    // ---- the schema ----

    [Fact]
    public void The_plan_schema_has_the_notes_of_order_and_the_questions_and_stays_portable()
    {
        var schema = PlanSchema.Build(Context());
        var root = JsonDocument.Parse(schema).RootElement;

        Assert.Empty(StrictSchemaChecker.Problems(schema));
        Assert.Equal(["summary", "tasks", "risks", "orderNotes", "questions"], root.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var notes = root.GetProperty("properties").GetProperty("orderNotes");
        Assert.Equal(["dependencies", "parallelOk", "harmful"], notes.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var option = root.GetProperty("properties").GetProperty("questions").GetProperty("items").GetProperty("properties").GetProperty("options").GetProperty("items");
        Assert.Equal(["small", "medium", "large"], option.GetProperty("properties").GetProperty("effort").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("boolean", option.GetProperty("properties").GetProperty("recommended").GetProperty("type").GetString());
    }

    /// <summary>
    /// The measurement of D-09 and ADR-0049: the largest team of a project (one orchestrator, 8 executors, 3 judges and the two
    /// participants of the debates), 20 requirements with ids of 6 characters and a profile of 6 steps, with the notes and the
    /// questions in the schema, fits the 16 KB of the inline <c>--json-schema</c> with room to spare.
    /// </summary>
    [Fact]
    public void The_largest_team_fits_in_the_inline_limit_with_the_notes_and_the_questions()
    {
        var roster = Enumerable.Range(1, 8).Select(i => Agent($"ejecutor-{i}", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic))
            .Concat([
                Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic),
                Agent("juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI),
                Agent("juez-claude", AgentRole.Judge, ProviderId.ClaudeCode, VendorId.Anthropic),
                Agent("juez-revision", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI),
                Agent("arquitecto", AgentRole.Architect, ProviderId.ClaudeCode, VendorId.Anthropic),
                Agent("critico", AgentRole.Critic, ProviderId.CodexCli, VendorId.OpenAI),
            ])
            .ToList();
        var requirements = FrozenRequirementSet.Freeze(
            Enumerable.Range(1, 20).Select(i => new Requirement(new RequirementId($"RQ-{i:D3}"), "statement", RequirementPriority.Desirable, [])),
            Start);
        var steps = new HashSet<string>(["restore", "build", "test", "lint", "format", "pack"], StringComparer.Ordinal);
        var context = new PlanContext(requirements, roster, steps, steps, new PlanLimits());

        var bytes = Encoding.UTF8.GetByteCount(PlanSchema.Build(context));
        var before = Encoding.UTF8.GetByteCount(PlanSchema.Build(context)) - Encoding.UTF8.GetByteCount(OrderAndQuestions(PlanSchema.Build(context)));

        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture, $"plan schema: {bytes} bytes with the notes and the questions, {before} without them; limit {PlanSchema.MaxBytes}"));
        Assert.True(bytes <= PlanSchema.MaxBytes / 2, $"{bytes} bytes");
        Assert.True(bytes - before <= 2_048, $"the notes and the questions add {bytes - before} bytes");
    }

    /// <summary>The two properties of Phase 13 of the root, as JSON, to measure what they add.</summary>
    private static string OrderAndQuestions(string schema)
    {
        var root = JsonNode.Parse(schema)!["properties"]!;
        return root["orderNotes"]!.ToJsonString() + root["questions"]!.ToJsonString();
    }

    [Fact]
    public void The_schema_of_the_order_review_names_the_tasks_of_the_plan_as_enums()
    {
        var schema = PlanSchema.OrderReviewSchema(["T2", "T1", "T2"]);

        Assert.Empty(StrictSchemaChecker.Problems(schema));
        var from = JsonDocument.Parse(schema).RootElement.GetProperty("properties").GetProperty("dependencies").GetProperty("items").GetProperty("properties").GetProperty("from");
        Assert.Equal(["T1", "T2"], from.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    // ---- the notes in the answer ----

    [Fact]
    public void The_notes_of_a_plan_come_with_it_and_do_not_change_its_hash()
    {
        var withNotes = Parse(WithNotes(Notes(
            [Note("T1", "from", "I1", "to", "I1 integra el código de T1")],
            [Note("T1", "a", "T2", "b", "Tocan carpetas distintas")],
            [Note("R1", "first", "I1", "then", "Revisar antes de integrar no revisaría nada")])));
        var without = Parse(Valid());

        Assert.Empty(withNotes.Problems);
        Assert.Equal(without.Plan!.ContentHash, withNotes.Plan!.ContentHash);
        Assert.Equal([new OrderDependencyNote(new TaskId("T1"), new TaskId("I1"), "I1 integra el código de T1")], withNotes.OrderNotes.Dependencies);
        Assert.Single(withNotes.OrderNotes.ParallelOk);
        Assert.Equal(new HarmfulOrderNote(new TaskId("R1"), new TaskId("I1"), "Revisar antes de integrar no revisaría nada"), Assert.Single(withNotes.OrderNotes.Harmful));
        Assert.Empty(withNotes.DiscardedNotes);
        Assert.Empty(without.OrderNotes.Tasks);
    }

    [Fact]
    public void A_note_about_a_task_that_is_not_in_the_plan_or_with_a_bad_reason_is_left_out_and_the_plan_stays()
    {
        var result = Parse(WithNotes(Notes(
            [
                Note("T1", "from", "T9", "to", "T9 no existe"),
                Note("T1", "from", "T1", "to", "La misma tarea"),
                Note("T1", "from", "I1", "to", new string('x', OrderNotes.MaxReasonLength + 1)),
                Note("T2", "from", "I1", "to", "Con un control \u0007"),
                Note("T2", "from", "I1", "to", "Con dirección " + (char)0x202E),
                Note("T1", "from", "I1", "to", " "),
                Note("T1", "from", "I1", "to", "La buena"),
                Note("T1", "from", "I1", "to", "Repetida"),
            ])));

        Assert.NotNull(result.Plan);
        Assert.Equal(["La buena"], result.OrderNotes.Dependencies.Select(n => n.Reason));
        Assert.Equal(7, result.DiscardedNotes.Count);
    }

    [Fact]
    public void Notes_are_read_as_strictly_as_the_plan()
    {
        var withNull = WithNotes(Notes([null!]));
        var extra = WithNotes(Notes());
        extra["orderNotes"]!["other"] = new JsonArray();
        var blank = WithNotes(Notes([Note(" ", "from", "I1", "to", "x")]));
        var explicitNull = Valid();
        explicitNull["orderNotes"] = null;

        Assert.Contains("'orderNotes' contains null.", Parse(withNull).Problems);
        Assert.Null(Parse(extra).Plan);
        Assert.Null(Parse(blank).Plan);
        Assert.Null(Parse(explicitNull).Plan);
        Assert.NotNull(Parse(Valid()).Plan); // an answer of before Phase 13, without them
    }

    [Fact]
    public void The_answer_of_the_order_review_is_parsed_with_the_same_strictness()
    {
        var (notes, problems) = PlanParser.ParseOrderNotes(Notes([Note("T1", "from", "T2", "to", "porque sí")]).ToJsonString());
        Assert.Empty(problems);
        Assert.Equal(new TaskId("T2"), Assert.Single(notes!.Dependencies).To);

        Assert.NotEmpty(PlanParser.ParseOrderNotes("{\"dependencies\":[]}").Problems);
        Assert.NotEmpty(PlanParser.ParseOrderNotes("[]").Problems);
        Assert.NotEmpty(PlanParser.ParseOrderNotes(" ").Problems);
    }

    [Fact]
    public void Kept_notes_follow_the_tasks_they_name_and_the_cap_of_each_kind()
    {
        var many = Enumerable.Range(0, OrderNotesCheck.MaxNotesPerKind + 5).Select(i => new ParallelOrderNote(new TaskId("A"), new TaskId("B" + i.ToString(CultureInfo.InvariantCulture)), "r")).ToList();
        var tasks = many.SelectMany(n => new[] { n.A, n.B }).Distinct();

        var (kept, discarded) = OrderNotesCheck.Check(new OrderNotes([], many, []), tasks);

        Assert.Equal(OrderNotesCheck.MaxNotesPerKind, kept.ParallelOk.Count);
        Assert.Equal(5, discarded.Count);
    }

    // ---- the questions of the orchestrator (D-28) ----

    [Fact]
    public void The_orchestrator_may_ask_instead_of_planning_and_its_questions_are_validated()
    {
        var result = Parse(Asking(Question("¿SQLite o JSON?", Option("a", "SQLite", recommended: true), Option("b", "JSON", "medium"))));

        Assert.Null(result.Plan);
        Assert.Empty(result.Problems);
        var question = Assert.Single(result.Questions);
        Assert.Equal("¿SQLite o JSON?", question.Text);
        Assert.Equal([QuestionEffort.Small, QuestionEffort.Medium], question.Options.Select(o => o.Effort));
        Assert.Equal("a", question.RecommendedOption!.Id);
        Assert.True(question.AllowOther);
    }

    [Fact]
    public void A_plan_and_questions_together_or_too_many_or_broken_questions_are_not_valid()
    {
        var both = Valid();
        both["orderNotes"] = Notes();
        both["questions"] = new JsonArray(Question("¿Una?", Option("a", "Sí"), Option("b", "No")));
        var four = Asking([.. Enumerable.Range(1, QuestionRules.MaxPlannerQuestions + 1).Select(i => Question($"¿{i}?", Option("a", "Sí"), Option("b", "No")))]);
        var oneOption = Asking(Question("¿Sola?", Option("a", "Sí")));
        var capitalEffort = Asking(Question("¿Mayúscula?", Option("a", "Sí", "Small"), Option("b", "No")));
        var twoRecommended = Asking(Question("¿Dos?", Option("a", "Sí", recommended: true), Option("b", "No", recommended: true)));

        foreach (var answer in new[] { both, four, oneOption, capitalEffort, twoRecommended })
        {
            var result = Parse(answer);
            Assert.Null(result.Plan);
            Assert.Empty(result.Questions);
            Assert.NotEmpty(result.Problems);
        }

        Assert.Contains(Parse(oneOption).Problems, p => p.Contains("question.options-count", StringComparison.Ordinal));
    }
}
