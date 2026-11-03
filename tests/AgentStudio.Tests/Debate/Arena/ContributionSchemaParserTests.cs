using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Debate.Arena;
using static AgentStudio.Tests.Debate.Arena.ContributionSamples;

namespace AgentStudio.Tests.Debate.Arena;

/// <summary>
/// The schema of a turn and the parser agree (docs/15 D-10). What the subset can say (members, types, closed sets) the
/// schema says exactly as the parser checks it; what it cannot say (caps, each id once, a citation named before it is
/// supported, line ranges, path syntax) the schema describes and the parser checks. So: an answer the schema admits within
/// the limits it describes parses; whatever the schema refuses, the parser refuses; and whatever the parser accepts, the
/// schema admits. The schema is applied by a validator of the portable subset written for these tests (<see cref="Admits"/>),
/// independently of the builder.
/// </summary>
public sealed class ContributionSchemaParserTests
{
    private const int Cases = 200;

    private static readonly string[] Mutations =
        ["missing member", "unknown member", "null member", "null item", "wrong type", "fraction", "outside a closed set"];

    [Fact]
    public void Any_answer_the_schema_admits_within_the_limits_it_describes_parses()
    {
        var random = new Random(20261021);
        for (var i = 0; i < Cases; i++)
        {
            var rules = RandomRules(random);
            var schema = ContributionSchema.Build(rules);
            var answer = Sample(Parse(schema), random).ToJsonString();

            Assert.True(Admits(answer, schema), $"Caso {i}: the sample is not an instance of the schema: {answer}");
            var result = ContributionParser.Parse(answer, rules);
            Assert.True(result.Problems.Count == 0, $"Caso {i}: {string.Join(" | ", result.Problems)} in {answer}");
        }
    }

    [Fact]
    public void Every_answer_the_parser_accepts_the_schema_admits()
    {
        var random = new Random(20261022);
        for (var i = 0; i < Cases; i++)
        {
            var rules = RandomRules(random);
            var answer = ContributionParser.ToJson(RandomAnswer(random, rules));

            Assert.Empty(ContributionParser.Parse(answer, rules).Problems);
            Assert.True(Admits(answer, ContributionSchema.Build(rules)), $"Caso {i}: {answer}");
        }
    }

    [Fact]
    public void Whatever_the_schema_refuses_the_parser_refuses()
    {
        var random = new Random(20261023);
        var exercised = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Cases * 2; i++)
        {
            var rules = RandomRules(random);
            var schema = ContributionSchema.Build(rules);
            var root = Parse(schema);
            var answer = Sample(root, random);
            var mutation = Mutate(answer, root, random);
            var json = answer.ToJsonString();

            Assert.False(Admits(json, schema), $"Caso {i}: '{mutation}' should take the answer out of the schema: {json}");
            var result = ContributionParser.Parse(json, rules);
            Assert.True(result.Answer is null, $"Caso {i}: the schema refuses '{mutation}' but the parser accepted {json}");
            exercised.Add(mutation);
        }

        Assert.Equal(Mutations.Order(StringComparer.Ordinal), exercised.Order(StringComparer.Ordinal));
    }

    private static JsonElement Parse(string schema) => JsonDocument.Parse(schema).RootElement.Clone();

    private static List<string>? EnumOf(JsonElement schema) =>
        schema.TryGetProperty("enum", out var values) ? [.. values.EnumerateArray().Select(v => v.GetString()!)] : null;

    // ---- the portable subset, applied to an instance ----

    /// <summary>
    /// True when <paramref name="json"/> is an instance of <paramref name="schema"/> under the subset of ADR-0013:
    /// <c>type</c> (an integer is a number token without fraction or exponent), <c>properties</c> with
    /// <c>required</c> and <c>additionalProperties: false</c>, <c>items</c>, <c>enum</c> and <c>anyOf</c>.
    /// </summary>
    private static bool Admits(string json, string schema)
    {
        using var instance = JsonDocument.Parse(json);
        using var document = JsonDocument.Parse(schema);
        return Admits(instance.RootElement, document.RootElement);
    }

    private static bool Admits(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("anyOf", out var branches))
        {
            return branches.EnumerateArray().Any(branch => Admits(value, branch));
        }

        switch (schema.GetProperty("type").GetString())
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var properties = schema.GetProperty("properties");
                if (schema.GetProperty("required").EnumerateArray().Any(r => !value.TryGetProperty(r.GetString()!, out _)))
                {
                    return false;
                }

                var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
                return value.EnumerateObject().All(member =>
                    properties.TryGetProperty(member.Name, out var memberSchema) ? Admits(member.Value, memberSchema) : !closed);
            case "array":
                return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => Admits(item, schema.GetProperty("items")));
            case "string":
                return value.ValueKind == JsonValueKind.String
                    && (EnumOf(schema) is not { } values || values.Contains(value.GetString()!, StringComparer.Ordinal));
            case "integer":
                return value.ValueKind == JsonValueKind.Number && value.GetRawText().All(c => char.IsAsciiDigit(c) || c == '-');
            case "number":
                return value.ValueKind == JsonValueKind.Number;
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "null":
                return value.ValueKind == JsonValueKind.Null;
            default:
                throw new InvalidOperationException("Not a type of the portable subset: " + schema.GetRawText());
        }
    }

    // ---- answers made from the schema ----

    /// <summary>
    /// An instance of the schema, made from its members, types and enums, that keeps the limits the schema describes (the
    /// caps, each id once, only the citations it has in the support of a claim, 1 to 80 lines from a line of the file, a
    /// relative path, an empty list where nothing can be named). A member the parser does not know gets a plain value of
    /// its type, so the parser would refuse a schema that offered one.
    /// </summary>
    private static JsonObject Sample(JsonElement root, Random random)
    {
        var ids = EnumOf(At(root, "citations", "id")) ?? [];
        var context = new SampleContext(random, Subset(random, ids));
        return (JsonObject)Value("$", root, context);
    }

    private static JsonElement At(JsonElement root, string array, string member) =>
        root.GetProperty("properties").GetProperty(array).GetProperty("items").GetProperty("properties").GetProperty(member);

    private static JsonNode Value(string name, JsonElement schema, SampleContext context)
    {
        var random = context.Random;
        var values = EnumOf(schema);
        switch (schema.GetProperty("type").GetString())
        {
            case "object":
                var result = new JsonObject();
                foreach (var member in schema.GetProperty("properties").EnumerateObject())
                {
                    result[member.Name] = Value(member.Name, member.Value, context);
                }

                return result;
            case "array":
                var items = schema.GetProperty("items");
                return name switch
                {
                    "citations" => new JsonArray([.. context.CitationIds.Select(id => (JsonNode)Citation(items, id, context))]),
                    "claims" => new JsonArray([.. Enumerable.Range(0, random.Next(0, ContributionLimits.MaxClaims + 1)).Select(_ => Value("claim", items, context))]),
                    "support" => Strings(EnumOf(items)?.Where(v => !ContributionLabels.Citations.Contains(v) || context.CitationIds.Contains(v)).ToList(), random),
                    _ => Strings(EnumOf(items), random),
                };
            case "string" when name == "id" && context.CitationId is { } id:
                return JsonValue.Create(id)!;
            case "string" when values is not null:
                return JsonValue.Create(values[random.Next(values.Count)])!;
            case "string":
                return JsonValue.Create(name switch
                {
                    "summary" => RandomText(random, ContributionLimits.MaxSummaryChars),
                    "argument" => RandomText(random, ContributionLimits.MaxArgumentChars),
                    "claim" => RandomText(random, ContributionLimits.MaxClaimChars),
                    "why" => RandomText(random, ContributionLimits.MaxWhyChars),
                    "path" => RandomPath(random),
                    _ => "texto",
                })!;
            case "integer" when name == "startLine":
                context.StartLine = random.Next(1, 1_000);
                return JsonValue.Create(context.StartLine)!;
            case "integer" when name == "endLine":
                return JsonValue.Create(context.StartLine + random.Next(0, ContributionLimits.MaxCitedLines))!;
            case "integer":
                return JsonValue.Create(1)!;
            case "boolean":
                return JsonValue.Create(random.Next(2) == 0)!;
            default:
                throw new InvalidOperationException("Not a type the sampler knows: " + schema.GetRawText());
        }
    }

    private static JsonNode Citation(JsonElement schema, string id, SampleContext context)
    {
        context.CitationId = id;
        var citation = Value("citation", schema, context);
        context.CitationId = null;
        return citation;
    }

    /// <summary>Some values of a closed set, each once; an empty list when the set is not closed in the schema.</summary>
    private static JsonArray Strings(List<string>? values, Random random) =>
        values is null ? [] : new JsonArray([.. Subset(random, values).Select(v => (JsonNode)JsonValue.Create(v)!)]);

    private sealed class SampleContext(Random random, List<string> citationIds)
    {
        public Random Random { get; } = random;

        public List<string> CitationIds { get; } = citationIds;

        public string? CitationId { get; set; }

        public int StartLine { get; set; }
    }

    // ---- what the schema refuses ----

    /// <summary>Changes <paramref name="answer"/> in one of the ways the schema refuses, and says which.</summary>
    private static string Mutate(JsonObject answer, JsonElement root, Random random)
    {
        var claims = answer["claims"]!.AsArray();
        var citations = answer["citations"]!.AsArray();
        if (claims.Count == 0)
        {
            claims.Add(new JsonObject { ["claim"] = "Afirmación.", ["support"] = new JsonArray() });
        }

        if (citations.Count == 0)
        {
            citations.Add(new JsonObject { ["id"] = "CITA-1", ["path"] = "s/x.cs", ["startLine"] = 1, ["endLine"] = 1, ["why"] = "Motivo." });
        }

        var claim = claims[random.Next(claims.Count)]!.AsObject();
        var citation = citations[random.Next(citations.Count)]!.AsObject();
        JsonObject[] objects = [answer, claim, citation];
        var target = objects[random.Next(objects.Length)];
        var member = target.Select(p => p.Key).ToList()[random.Next(target.Count)];

        var mutation = Mutations[random.Next(Mutations.Length)];
        switch (mutation)
        {
            case "missing member":
                target.Remove(member);
                break;
            case "unknown member":
                target["vote"] = "P1";
                break;
            case "null member":
                target[member] = null;
                break;
            case "null item":
                JsonArray[] lists = [claims, citations, answer["concedes"]!.AsArray(), claim["support"]!.AsArray()];
                lists[random.Next(lists.Length)].Add(null);
                break;
            case "wrong type":
                target[member] = target[member] switch
                {
                    JsonArray => new JsonObject(),
                    JsonValue value when value.GetValueKind() == JsonValueKind.String => 7,
                    JsonValue value when value.GetValueKind() == JsonValueKind.Number => "10",
                    _ => "true",
                };
                break;
            case "fraction":
                citation[random.Next(2) == 0 ? "startLine" : "endLine"] = 10.5;
                break;
            default:
                var outside = new List<Action>
                {
                    () => answer["kind"] = "Vote",
                    () => answer["repliesTo"] = "C99",
                    () => citation["id"] = "CITA-9",
                };
                if (EnumOf(root.GetProperty("properties").GetProperty("claims").GetProperty("items").GetProperty("properties").GetProperty("support").GetProperty("items")) is not null)
                {
                    outside.Add(() => claim["support"]!.AsArray().Add("EV-404"));
                }

                if (EnumOf(root.GetProperty("properties").GetProperty("concedes").GetProperty("items")) is not null)
                {
                    outside.Add(() => answer["concedes"]!.AsArray().Add("C99"));
                }

                outside[random.Next(outside.Count)]();
                break;
        }

        return mutation;
    }
}
