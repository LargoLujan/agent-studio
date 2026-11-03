using System.Text.Json.Nodes;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Planning;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using static AgentStudio.Tests.Orchestration.Planning.PlanJson;

namespace AgentStudio.Tests.Orchestration.Planning;

public sealed class PlanParserTests
{
    private static readonly AgentId Author = new("orquestador");

    /// <summary>T1 writes and is tested, C1 asks whether its tests passed, R1 reviews on the yes branch, A1 approves on the no branch.</summary>
    private static JsonObject ValidDocument() =>
        Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/", "tests/Calc.Tests/"], steps: ["build", "test"], timeoutMinutes: 30),
            Task("C1", kind: "Condition", agent: null, workspace: "None", dependsOn: [Dependency("T1")], condition: Predicate("TestsPassed", "T1")),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("C1", "yes")]),
            Task("A1", kind: "HumanApproval", agent: null, workspace: "None", maxRetries: 0, dependsOn: [Dependency("C1", "no")]),
            Task("J1", kind: "ToolRun", agent: null, workspace: "None", toolId: "lint", join: "AnyCompleted", dependsOn: [Dependency("R1"), Dependency("A1")]));

    private static PlanParseResult Parse(JsonNode document) => PlanParser.Parse(document.ToJsonString(), Author);

    private static void AssertRejected(PlanParseResult result, string? mentions = null)
    {
        Assert.Null(result.Plan);
        Assert.NotEmpty(result.Problems);
        if (mentions is not null)
        {
            Assert.Contains(result.Problems, p => p.Contains(mentions, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_document_written_to_the_schema_becomes_the_plan_it_describes()
    {
        var expected = PlanDefinition.Create(
            "Añadir Calc.Multiply",
            [
                Work("T1", scope: ["src/Calc/", "tests/Calc.Tests/"]).Verified("build", "test") with { Timeout = TimeSpan.FromMinutes(30) },
                Node("C1", WorkTaskKind.Condition).After(On("T1")) with
                {
                    Condition = new TestsPassed(new TaskId("T1")),
                    MaxRetries = 1,
                },
                Node("R1", WorkTaskKind.Review).After(On("C1", "yes")),
                Node("A1", WorkTaskKind.HumanApproval).After(On("C1", "no")),
                Node("J1", WorkTaskKind.ToolRun).After(On("R1"), On("A1")) with { Join = JoinPolicy.AnyCompleted },
            ],
            ["La compilación puede tardar"],
            PlanSource.OrchestratorAgent,
            Author);
        var expectedWithTitles = PlanDefinition.Create(
            expected.Summary,
            [.. expected.Tasks.Select(t => t with { Title = "Tarea " + t.Id, Objective = "Objetivo de " + t.Id, Instructions = "Instrucciones de " + t.Id })],
            expected.Risks,
            PlanSource.OrchestratorAgent,
            Author);

        var result = Parse(ValidDocument());

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Plan);
        Assert.Equal(expectedWithTitles.ContentHash, result.Plan.ContentHash);
        Assert.Equal(PlanSource.OrchestratorAgent, result.Plan.Source);
        Assert.Equal(Author, result.Plan.Author);
        Assert.Empty(PlanValidator.Validate(result.Plan, Context()));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("task")]
    [InlineData("dependency")]
    [InlineData("condition")]
    public void An_unknown_property_is_rejected_at_every_level(string level)
    {
        var document = ValidDocument();
        var tasks = document["tasks"]!.AsArray();
        var target = level switch
        {
            "root" => document,
            "task" => tasks[0]!.AsObject(),
            "dependency" => tasks[1]!["dependsOn"]![0]!.AsObject(),
            _ => tasks[1]!["condition"]!.AsObject(),
        };
        target["shellCommand"] = "rm -rf /";

        AssertRejected(Parse(document), "shellCommand");
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("risks")]
    [InlineData("task.workspace")]
    [InlineData("task.condition")]
    [InlineData("task.timeoutMinutes")]
    [InlineData("dependency.branch")]
    [InlineData("condition.pathPrefix")]
    public void A_missing_property_is_rejected_even_when_it_may_be_null(string property)
    {
        var document = ValidDocument();
        var tasks = document["tasks"]!.AsArray();
        var (target, name) = property.Split('.') switch
        {
            [var root] => (document, root),
            ["task", var p] => (tasks[0]!.AsObject(), p),
            ["dependency", var p] => (tasks[1]!["dependsOn"]![0]!.AsObject(), p),
            [_, var p] => (tasks[1]!["condition"]!.AsObject(), p),
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };
        Assert.True(target.Remove(name));

        AssertRejected(Parse(document), name);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("tasks")]
    [InlineData("task.title")]
    [InlineData("task.kind")]
    [InlineData("task.dependsOn")]
    [InlineData("task.covers")]
    [InlineData("task.maxRetries")]
    [InlineData("dependency.prerequisite")]
    [InlineData("condition.task")]
    public void Null_is_rejected_where_the_schema_does_not_allow_it(string property)
    {
        var document = ValidDocument();
        var tasks = document["tasks"]!.AsArray();
        var (target, name) = property.Split('.') switch
        {
            [var root] => (document, root),
            ["task", var p] => (tasks[0]!.AsObject(), p),
            ["dependency", var p] => (tasks[1]!["dependsOn"]![0]!.AsObject(), p),
            [_, var p] => (tasks[1]!["condition"]!.AsObject(), p),
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };
        target[name] = null;

        AssertRejected(Parse(document));
    }

    [Fact]
    public void A_null_inside_a_list_is_rejected()
    {
        var document = ValidDocument();
        document["risks"]!.AsArray().Add(null);

        AssertRejected(Parse(document), "risks");
    }

    [Theory]
    [InlineData("kind", "Script")]
    [InlineData("kind", "agentwork")]
    [InlineData("kind", "0")]
    [InlineData("join", "First")]
    [InlineData("workspace", "Full")]
    public void A_value_outside_the_closed_sets_is_rejected(string property, string value)
    {
        var document = ValidDocument();
        document["tasks"]![0]![property] = value;

        AssertRejected(Parse(document), value);
    }

    [Fact]
    public void A_predicate_outside_the_closed_set_is_rejected()
    {
        var document = ValidDocument();
        document["tasks"]![1]!["condition"]!["type"] = "ShellExitCode";

        AssertRejected(Parse(document), "ShellExitCode");
    }

    [Theory]
    [InlineData("maxRetries", "\"2\"")]
    [InlineData("maxRetries", "1.5")]
    [InlineData("timeoutMinutes", "\"30\"")]
    [InlineData("covers", "\"RQ-01\"")]
    [InlineData("dependsOn", "{}")]
    [InlineData("title", "7")]
    public void A_value_of_the_wrong_type_is_rejected(string property, string json)
    {
        var document = ValidDocument();
        document["tasks"]![0]![property] = JsonNode.Parse(json);

        AssertRejected(Parse(document));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no es JSON")]
    [InlineData("[]")]
    [InlineData("\"plan\"")]
    public void Text_that_is_not_a_plan_object_is_rejected(string? text)
    {
        AssertRejected(PlanParser.Parse(text, Author));
    }

    [Fact]
    public void Trailing_content_after_the_object_is_rejected()
    {
        AssertRejected(PlanParser.Parse(ValidDocument().ToJsonString() + " {}", Author));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_rejected()
    {
        var json = ValidDocument().ToJsonString();

        AssertRejected(PlanParser.Parse("/* plan */" + json, Author));
        AssertRejected(PlanParser.Parse(json[..^1] + ",}", Author));
    }

    [Fact]
    public void A_property_written_twice_is_rejected()
    {
        var json = ValidDocument().ToJsonString();
        var duplicated = "{\"summary\":\"otro\"," + json[1..];

        AssertRejected(PlanParser.Parse(duplicated, Author), "summary");
    }

    [Fact]
    public void Property_names_are_case_sensitive()
    {
        var document = ValidDocument();
        var summary = document["summary"]!.GetValue<string>();
        document.Remove("summary");
        document["Summary"] = summary;

        AssertRejected(Parse(document));
    }

    [Theory]
    [InlineData("OutputEquals", null, "v")]
    [InlineData("OutputEquals", "k", null)]
    public void OutputEquals_needs_its_key_and_value(string type, string? key, string? value)
    {
        var document = ValidDocument();
        document["tasks"]![1]!["condition"] = Predicate(type, "T1", key, value);

        AssertRejected(Parse(document), "OutputEquals");
    }

    [Fact]
    public void PathsChanged_needs_its_prefix_and_maps_with_it()
    {
        var missing = ValidDocument();
        missing["tasks"]![1]!["condition"] = Predicate("PathsChanged", "T1");
        var given = ValidDocument();
        given["tasks"]![1]!["condition"] = Predicate("PathsChanged", "T1", pathPrefix: "src/");
        var output = ValidDocument();
        output["tasks"]![1]!["condition"] = Predicate("OutputEquals", "T1", "resultado", "ok");

        AssertRejected(Parse(missing), "PathsChanged");
        var paths = Parse(given).Plan!.Tasks.Single(t => t.Id == new TaskId("C1")).Condition;
        var equals = Parse(output).Plan!.Tasks.Single(t => t.Id == new TaskId("C1")).Condition;

        Assert.Equal(new PathsChanged(new TaskId("T1"), "src/"), paths);
        Assert.Equal(new OutputEquals(new TaskId("T1"), "resultado", "ok"), equals);
    }

    [Theory]
    [InlineData("id", " ")]
    [InlineData("agent", "")]
    public void A_blank_identifier_is_a_problem_not_a_crash(string property, string value)
    {
        var document = ValidDocument();
        document["tasks"]![0]![property] = value;

        AssertRejected(Parse(document));
    }

    [Fact]
    public void Every_mapping_problem_is_reported_not_only_the_first()
    {
        var document = ValidDocument();
        document["tasks"]![0]!["kind"] = "Script";
        document["tasks"]![2]!["workspace"] = "Full";

        var result = Parse(document);

        AssertRejected(result, "Script");
        Assert.Contains(result.Problems, p => p.Contains("Full", StringComparison.Ordinal));
    }

    [Fact]
    public void Parsing_is_not_validating_a_plan_the_validator_rejects_still_parses()
    {
        var document = ValidDocument();
        document["tasks"]![0]!["agent"] = "fantasma";

        var result = Parse(document);

        Assert.Empty(result.Problems);
        Assert.Contains(PlanValidator.Validate(result.Plan!, Context()), v => v.Code == PlanViolationCode.UnknownAgent);
    }
}
