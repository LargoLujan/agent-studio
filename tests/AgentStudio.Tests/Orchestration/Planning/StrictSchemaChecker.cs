using System.Text.Json;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// Checks that a JSON schema stays inside the subset that both OpenAI's strict structured outputs and Claude's
/// <c>--json-schema</c> accept: the root is an object; every object lists all its properties as required and forbids
/// additional ones; optional values are an <c>anyOf</c> with <c>null</c>; only basic keywords (no formats, patterns,
/// length or number bounds, <c>$ref</c>, <c>oneOf</c>, <c>allOf</c>, defaults); enums are non-empty lists of strings;
/// at most 10 levels of nesting and 1000 enum values. Written independently of the builder it checks.
/// </summary>
internal static class StrictSchemaChecker
{
    private static readonly HashSet<string> AllowedKeywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "enum", "anyOf", "description",
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "integer", "number", "boolean", "null",
    };

    public static IReadOnlyList<string> Problems(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        var problems = new List<string>();
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object")
        {
            problems.Add("The root must be an object schema (not anyOf).");
        }

        var enumValues = 0;
        Walk(root, "$", 1, problems, ref enumValues);
        if (enumValues > 1000)
        {
            problems.Add($"There are {enumValues} enum values; the limit is 1000.");
        }

        return problems;
    }

    private static void Walk(JsonElement schema, string path, int depth, List<string> problems, ref int enumValues)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{path}: a schema must be an object.");
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (!AllowedKeywords.Contains(keyword.Name))
            {
                problems.Add($"{path}: keyword '{keyword.Name}' is not portable.");
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            if (schema.EnumerateObject().Any(p => p.Name is not ("anyOf" or "description")))
            {
                problems.Add($"{path}: anyOf must stand alone.");
            }

            var index = 0;
            foreach (var branch in anyOf.EnumerateArray())
            {
                Walk(branch, $"{path}.anyOf[{index++}]", depth, problems, ref enumValues);
            }

            return;
        }

        if (!schema.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String || !AllowedTypes.Contains(typeElement.GetString()!))
        {
            problems.Add($"{path}: every schema needs one basic 'type'.");
            return;
        }

        var type = typeElement.GetString();
        if (schema.TryGetProperty("enum", out var values))
        {
            var list = values.EnumerateArray().ToList();
            enumValues += list.Count;
            if (type != "string" || list.Count == 0 || list.Any(v => v.ValueKind != JsonValueKind.String))
            {
                problems.Add($"{path}: an enum must be a non-empty list of strings on a string schema.");
            }
        }

        if (type == "object")
        {
            if (depth > 10)
            {
                problems.Add($"{path}: more than 10 levels of nesting.");
            }

            if (!schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
            {
                problems.Add($"{path}: an object must set additionalProperties to false.");
            }

            var hasProperties = schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object;
            var properties = hasProperties ? props.EnumerateObject().Select(p => p.Name).ToList() : [];
            var required = schema.TryGetProperty("required", out var req) ? req.EnumerateArray().Select(r => r.GetString()!).ToList() : [];
            if (properties.Count == 0 || !properties.Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal)))
            {
                problems.Add($"{path}: every property must be required, and only those ({string.Join(",", properties)} vs {string.Join(",", required)}).");
            }

            if (hasProperties)
            {
                foreach (var property in props.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.{property.Name}", depth + 1, problems, ref enumValues);
                }
            }
        }
        else if (type == "array")
        {
            if (!schema.TryGetProperty("items", out var items))
            {
                problems.Add($"{path}: an array needs 'items'.");
            }
            else
            {
                Walk(items, path + "[]", depth, problems, ref enumValues);
            }
        }
    }
}
