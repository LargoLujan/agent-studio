using System.Text;
using System.Text.Json.Nodes;

namespace AgentStudio.Orchestration.Planning;

/// <summary>
/// Builds JSON schemas inside the subset that both OpenAI's strict structured outputs and Claude's <c>--json-schema</c>
/// accept (ADR-0013): every object lists all its properties as required and forbids additional ones; a value that may
/// be missing is an <c>anyOf</c> with <c>null</c>; only <c>type</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>items</c>, <c>enum</c>, <c>anyOf</c> and <c>description</c> are used (no formats,
/// patterns, bounds, <c>$ref</c> or defaults). What the subset cannot say (lengths, ranges, id formats) is checked by the
/// deterministic validator after parsing, never trusted to the schema.
/// </summary>
internal static class StrictJsonSchema
{
    public static JsonObject Object(params (string Name, JsonNode Schema)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
            required.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    public static JsonObject String(string? description = null) => Describe(new JsonObject { ["type"] = "string" }, description);

    public static JsonObject Integer(string? description = null) => Describe(new JsonObject { ["type"] = "integer" }, description);

    public static JsonObject Boolean(string? description = null) => Describe(new JsonObject { ["type"] = "boolean" }, description);

    public static JsonObject Null() => new() { ["type"] = "null" };

    /// <summary>A string limited to <paramref name="values"/>; with no values it is a plain string (the validator decides).</summary>
    public static JsonObject Enum(IEnumerable<string> values, string? description = null)
    {
        var list = values.ToList();
        var schema = String(description);
        if (list.Count > 0)
        {
            var array = new JsonArray();
            foreach (var value in list)
            {
                array.Add(value);
            }

            schema["enum"] = array;
        }

        return schema;
    }

    public static JsonObject Array(JsonNode items, string? description = null) =>
        Describe(new JsonObject { ["type"] = "array", ["items"] = items }, description);

    /// <summary>The value or null. Strict mode has no optional properties: a property that may be absent is required and nullable.</summary>
    public static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, Null()) };

    /// <summary>A value that is either one of <paramref name="values"/> or null; with no values it can only be null.</summary>
    public static JsonObject NullableEnum(IReadOnlyCollection<string> values) => values.Count == 0 ? Null() : Nullable(Enum(values));

    public static string ToCompactJson(JsonNode schema) => schema.ToJsonString();

    public static int Utf8Bytes(string json) => Encoding.UTF8.GetByteCount(json);

    private static JsonObject Describe(JsonObject schema, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }

        return schema;
    }
}
