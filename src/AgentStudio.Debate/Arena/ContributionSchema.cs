using System.Text;
using System.Text.Json.Nodes;
using static System.FormattableString;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The JSON schema a participant answers with (docs/15 D-10), built for each turn from its <see cref="ContributionRules"/>,
/// with the closed sets the parser checks as enums: the kinds the round admits; the published contributions and
/// <see cref="ContributionLabels.None"/> for <c>repliesTo</c>; those contributions, the evidence of the material and the
/// citation ids for the support of a claim; the published contributions for <c>concedes</c>; and <c>CITA-1</c> to
/// <c>CITA-8</c> for the id of a citation.
/// </summary>
/// <remarks>
/// <para>It keeps to the subset both CLIs accept (ADR-0013): OpenAI strict mode (Codex <c>--output-schema</c>) and Claude
/// <c>--json-schema</c>. Every object requires all its properties and closes with <c>additionalProperties: false</c>; no
/// member is nullable (the sentinel stands for no reply) and there are no format, pattern, length or range keywords. What
/// the subset cannot say (the caps, each id once, a citation id in a support only when the contribution has that citation,
/// 1 to 80 lines, the syntax of a path, a list that must stay empty) the descriptions say and
/// <see cref="ContributionParser"/> checks: the schema makes a well-formed answer likely, and the parser is the authority.</para>
/// <para>A closed set with nothing in it has no portable enum, so it becomes a plain string the parser closes: what can be
/// conceded before anything is published. The schema fits in <see cref="MaxBytes"/>, which Claude takes inline on its
/// command line, and within OpenAI's limits on enums (1 000 values in all, and 7 500 characters for one enum of more than
/// 250 values, the stricter figure OpenAI has published): a material too large for the enum of the support leaves that
/// enum out, and the parser alone closes the set.</para>
/// <para>The descriptions are in English, like the prompt; the free texts are asked in the response language (a block of data of the prompt, V44), because the user reads
/// them. The schema of round 1 names no contribution, because its rules hold none (D-17).</para>
/// </remarks>
public static class ContributionSchema
{
    /// <summary>The most Claude takes inline on its command line: <c>--json-schema</c> has no file form.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>OpenAI strict mode: enum values in the whole schema.</summary>
    private const int MaxEnumValues = 1_000;

    /// <summary>OpenAI strict mode: one enum of more values than this…</summary>
    private const int LargeEnumValues = 250;

    /// <summary>…takes at most these characters in all.</summary>
    private const int MaxLargeEnumChars = 7_500;

    /// <summary>The schema as compact JSON, on one line (the command line rejects line breaks).</summary>
    /// <exception cref="InvalidOperationException">Even without the enum of the support, the schema does not fit.</exception>
    public static string Build(ContributionRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var bytes = 0;
        foreach (var supportEnum in new[] { true, false })
        {
            var schema = Schema(rules, supportEnum);
            var text = schema.ToJsonString();
            bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes <= MaxBytes && EnumsFit(schema))
            {
                return text;
            }
        }

        throw new InvalidOperationException(Invariant(
            $"The contribution schema of round {rules.Round}, with {rules.Contributions.Count} published contributions, takes {bytes} bytes without the enum of the support, or more enum values than OpenAI strict mode allows; a participant on Claude accepts at most {MaxBytes} bytes."));
    }

    private static JsonObject Schema(ContributionRules rules, bool supportEnum)
    {
        var published = rules.Contributions;
        List<string> support = [.. published, .. rules.Evidence.Select(e => e.Value), .. ContributionLabels.Citations];

        var claim = ObjectOf(
            ("claim", StringOf(Invariant($"One assertion, at most {ContributionLimits.MaxClaimChars} characters. In the response language."))),
            ("support", ArrayOf(
                "What backs the claim, each id once: published contributions, evidence of the material and citations of this contribution only.",
                supportEnum
                    ? EnumOf("An id from the list; a citation id only if this contribution has that citation.", support)
                    : StringOf("A published contribution (C1, C2...), an evidence id of the material or the id of a citation of this contribution (CITA-1...)."))));

        var citation = ObjectOf(
            ("id", EnumOf("Each citation of this contribution has its own id.", ContributionLabels.Citations)),
            ("path", StringOf(Invariant($"The file, relative to the root of the copy of the commit, with / between folders (src/Program.cs); at most {ContributionLimits.MaxPathChars} characters."))),
            ("startLine", IntegerOf("The first cited line; lines are numbered from 1.")),
            ("endLine", IntegerOf(Invariant($"The last cited line, not before startLine; at most {ContributionLimits.MaxCitedLines} lines in all."))),
            ("why", StringOf(Invariant($"Why these lines matter, at most {ContributionLimits.MaxWhyChars} characters. In the response language."))));

        return ObjectOf(
            ("kind", EnumOf(
                rules.Round == 1 ? "Your independent contribution: nothing of anyone else is published yet." : "An objection to, a rebuttal of, or evidence about what is published.",
                rules.Kinds.Select(k => k.ToString()))),
            ("repliesTo", EnumOf(
                published.Count == 0 ? "Nothing is published yet, so it is ninguna." : "The published contribution this one answers, or ninguna when it answers none.",
                [.. published, ContributionLabels.None])),
            ("summary", StringOf(Invariant($"The contribution in one or two sentences, at most {ContributionLimits.MaxSummaryChars} characters. In the response language."))),
            ("argument", StringOf(Invariant($"The reasoning, at most {ContributionLimits.MaxArgumentChars} characters. In the response language."))),
            ("claims", ArrayOf(Invariant($"At most {ContributionLimits.MaxClaims} claims."), claim)),
            ("citations", ArrayOf(Invariant($"At most {ContributionLimits.MaxCitations} citations of the code of the copy of the commit."), citation)),
            ("concedes", published.Count == 0
                ? ArrayOf("Leave it empty: nothing is published yet.", StringOf("Nothing can be conceded yet."))
                : ArrayOf("The published contributions this one concedes, each once.", EnumOf("A published contribution.", published))),
            ("finished", BooleanOf("True when you have nothing more to add.")));
    }

    /// <summary>OpenAI's limits on the enums of a strict schema (see the remarks of the class).</summary>
    private static bool EnumsFit(JsonObject schema)
    {
        var enums = Enums(schema).ToList();
        return enums.Sum(e => e.Count) <= MaxEnumValues
            && enums.All(e => e.Count <= LargeEnumValues || e.Sum(v => v.Length) <= MaxLargeEnumChars);
    }

    private static IEnumerable<IReadOnlyList<string>> Enums(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject schema:
                if (schema["enum"] is JsonArray values)
                {
                    yield return [.. values.Select(v => v!.GetValue<string>())];
                }

                foreach (var (_, child) in schema)
                {
                    foreach (var inner in Enums(child))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in Enums(item))
                    {
                        yield return inner;
                    }
                }

                break;
            default:
                break;
        }
    }

    private static JsonObject ObjectOf(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            props[name] = schema;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)JsonValue.Create(p.Name))]),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject ArrayOf(string description, JsonObject items) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items };

    private static JsonObject StringOf(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject IntegerOf(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject BooleanOf(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject EnumOf(string description, IEnumerable<string> values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]),
    };
}
