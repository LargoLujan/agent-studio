using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Providers.Cli.Claude;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Debate;

public sealed class VerdictSchemaTests
{
    private static readonly FrozenRequirementSet TwoRequirements = FrozenRequirementSet.Freeze(
        [Req("RQ-02", RequirementPriority.Desirable, "b", Crit("c2")), Req("RQ-01", RequirementPriority.Mandatory, "a", Crit("c1"))],
        T0);

    private static readonly JudgeIdentity SystemJudge = new(new AgentId("juez"), new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI, "gpt-5"));

    private static JsonElement Parse(string schema) => JsonDocument.Parse(schema).RootElement.Clone();

    private static FrozenRequirementSet ManyRequirements(int count, int idLength) => FrozenRequirementSet.Freeze(
        Enumerable.Range(0, count).Select(i => Req(("RQ-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).PadRight(idLength, 'x'))),
        T0);

    /// <summary>
    /// The portable subset that both OpenAI strict mode (Codex <c>--output-schema</c>) and Claude <c>--json-schema</c>
    /// accept: every object closes with <c>additionalProperties: false</c> and requires all its properties, no nullable
    /// unions, and only the keywords below. Format, pattern, length and range keywords are not portable.
    /// </summary>
    internal static void AssertStrictShape(JsonElement node, string path)
    {
        string[] portable = ["type", "properties", "required", "additionalProperties", "items", "enum", "description"];
        foreach (var keyword in node.EnumerateObject())
        {
            Assert.True(portable.Contains(keyword.Name), $"{path}: '{keyword.Name}' is not a portable keyword.");
        }

        Assert.True(node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String, $"{path}: one type, never a union with null.");
        switch (type.GetString())
        {
            case "object":
                var properties = node.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
                var required = node.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Equal(properties.Order(StringComparer.Ordinal), required.Order(StringComparer.Ordinal));
                Assert.Equal(JsonValueKind.False, node.GetProperty("additionalProperties").ValueKind);
                Assert.NotEmpty(properties);
                foreach (var property in node.GetProperty("properties").EnumerateObject())
                {
                    AssertStrictShape(property.Value, path + "." + property.Name);
                }

                break;
            case "array":
                AssertStrictShape(node.GetProperty("items"), path + "[]");
                break;
            case "string":
                if (node.TryGetProperty("enum", out var values))
                {
                    var list = values.EnumerateArray().ToList();
                    Assert.NotEmpty(list);
                    Assert.All(list, v => Assert.Equal(JsonValueKind.String, v.ValueKind));
                    Assert.Equal(list.Count, list.Select(v => v.GetString()).Distinct(StringComparer.Ordinal).Count());
                }

                break;
            case "integer" or "boolean":
                Assert.False(node.TryGetProperty("enum", out _), $"{path}: enums only on strings.");
                break;
            default:
                Assert.Fail($"{path}: unexpected type '{type.GetString()}'.");
                break;
        }
    }

    /// <summary>The smallest JSON the schema accepts, with <paramref name="pick"/> choosing each enum value.</summary>
    internal static JsonNode? Sample(JsonElement node, Func<string, IReadOnlyList<string>, string> pick, string path = "$")
    {
        switch (node.GetProperty("type").GetString())
        {
            case "object":
                var result = new JsonObject();
                foreach (var property in node.GetProperty("properties").EnumerateObject())
                {
                    result[property.Name] = Sample(property.Value, pick, path + "." + property.Name);
                }

                return result;
            case "array":
                return new JsonArray(Sample(node.GetProperty("items"), pick, path + "[]"));
            case "string":
                return node.TryGetProperty("enum", out var values)
                    ? JsonValue.Create(pick(path, values.EnumerateArray().Select(v => v.GetString()!).ToList()))
                    : JsonValue.Create("texto");
            case "integer":
                return JsonValue.Create(1);
            case "boolean":
                return JsonValue.Create(false);
            default:
                throw new InvalidOperationException(path);
        }
    }

    [Fact]
    public void The_verdict_schema_keeps_to_the_strict_mode_rules_both_clis_accept()
    {
        var root = Parse(VerdictSchema.Build(TwoRequirements));

        Assert.Equal("object", root.GetProperty("type").GetString());
        AssertStrictShape(root, "$");
    }

    [Fact]
    public void An_assessment_can_only_name_a_requirement_of_the_frozen_set()
    {
        var root = Parse(VerdictSchema.Build(TwoRequirements));

        var requirement = root.GetProperty("properties").GetProperty("assessments").GetProperty("items").GetProperty("properties").GetProperty("requirement");
        Assert.Equal(["RQ-01", "RQ-02"], requirement.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void The_judge_writes_its_analysis_before_its_decision()
    {
        var root = Parse(VerdictSchema.Build(TwoRequirements));

        var order = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(order.IndexOf("assessments") < order.IndexOf("decision"), string.Join(",", order));
        Assert.True(order.IndexOf("findings") < order.IndexOf("decision"), string.Join(",", order));
    }

    [Fact]
    public void Whatever_the_schema_accepts_the_parser_turns_into_a_well_formed_verdict()
    {
        var root = Parse(VerdictSchema.Build(TwoRequirements));
        var dossier = Dossier(TwoRequirements.Requirements);

        // Every value of every enum, one sample per value: the schema and the parser must agree on all of them.
        var enums = new List<(string Path, string Value)>();
        Sample(root, (path, values) =>
        {
            enums.AddRange(values.Select(v => (path, v)));
            return values[0];
        });

        Assert.Contains(enums, e => e.Path == "$.decision");
        foreach (var (path, value) in enums)
        {
            var json = Sample(root, (p, values) => p == path ? value : values[0])!.ToJsonString();

            var verdict = VerdictJson.Parse(json, SystemJudge, T0);

            Assert.DoesNotContain(VerdictValidator.Validate(verdict, dossier), v => v.Code == VerdictViolationCode.MalformedVerdict);
        }
    }

    [Fact]
    public void The_schema_fits_in_the_16_KB_that_claude_accepts_on_its_command_line()
    {
        Assert.Equal(ClaudeCodeArguments.MaxJsonSchemaBytes, VerdictSchema.MaxBytes);
        var schema = VerdictSchema.Build(ManyRequirements(count: 60, idLength: 32));

        Assert.InRange(Encoding.UTF8.GetByteCount(schema), 1, VerdictSchema.MaxBytes);

        var judge = new AgentDefinition(new AgentId("juez"), "Juez", AgentRole.Judge, new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic), "", [], [],
            [], new ExecutionBudget(5, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));
        var arguments = ClaudeCodeArguments.Build(
            new AgentRunRequest(judge, "prompt", "C:\\work", new PermissionProfile(false, false, false, [], [], []), schema),
            Guid.NewGuid().ToString(),
            null);
        Assert.Contains("--json-schema", arguments);
    }

    [Fact]
    public void A_requirement_set_too_large_for_a_16_KB_schema_is_refused_instead_of_truncated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VerdictSchema.Build(ManyRequirements(count: 600, idLength: 32)));

        Assert.Contains("16384", ex.Message, StringComparison.Ordinal);
    }
}
