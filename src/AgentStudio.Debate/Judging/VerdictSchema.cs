using System.Text;
using System.Text.Json.Nodes;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// The JSON schema the judge answers with, built in code for each review from the frozen requirements.
/// </summary>
/// <remarks>
/// <para>It keeps to the subset both CLIs accept: OpenAI strict mode (Codex <c>--output-schema</c>) and Claude
/// <c>--json-schema</c>. Every object closes with <c>additionalProperties: false</c> and requires all its properties;
/// there are no nullable unions and no format, pattern, length or range keywords. The validator, not the schema, is
/// the authority on what a verdict may say.</para>
/// <para>The requirement of an assessment is an enum of the frozen ids. The judge has no field to say who it is or when
/// it judged: the system sets both. The analysis (assessments, findings) comes before the decision, so the judge writes
/// its critique before it commits to a verdict.</para>
/// </remarks>
public static class VerdictSchema
{
    /// <summary>The most Claude accepts inline on its command line: <c>--json-schema</c> has no file form.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <exception cref="InvalidOperationException">The schema would exceed <see cref="MaxBytes"/>.</exception>
    public static string Build(FrozenRequirementSet requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var requirementIds = requirements.Requirements.Select(r => r.Id.Value).ToArray();

        var assessment = ObjectOf(
            ("requirement", EnumOf("Id of the frozen requirement.", requirementIds)),
            ("status", EnumOf("Satisfied only with objective evidence that fits every acceptance criterion.", Enum.GetNames<AssessmentStatus>())),
            ("evidence", ArrayOf("Ids of dossier evidence behind this status.", StringOf("Evidence id."))),
            ("rationale", StringOf("Why, citing the evidence. In the language of the response_language block of the prompt.")));
        var finding = ObjectOf(
            ("category", EnumOf("Kind of problem.", Enum.GetNames<FindingCategory>())),
            ("severity", EnumOf("Critical makes approval unsafe.", Enum.GetNames<Severity>())),
            ("description", StringOf("What is wrong and where. In the language of the response_language block of the prompt.")),
            ("evidence", ArrayOf("Ids of dossier evidence that show it.", StringOf("Evidence id."))),
            ("decisionRefs", ArrayOf("Sequence numbers of the decision log entries involved.", IntegerOf())));
        var confidence = ObjectOf(
            ("level", EnumOf("Confidence in the verdict.", Enum.GetNames<ConfidenceLevel>())),
            ("justification", StringOf("Why this confidence. In the language of the response_language block of the prompt.")));

        var schema = ObjectOf(
            ("assessments", ArrayOf("Exactly one per frozen requirement.", assessment)),
            ("evidenceExamined", ArrayOf("Every evidence id cited anywhere in the verdict.", StringOf("Evidence id."))),
            ("findings", ArrayOf("Problems found in the run.", finding)),
            ("pendingObjections", ArrayOf("Objections still open. In the language of the response_language block of the prompt.", StringOf("Objection."))),
            ("risks", ArrayOf("Risks of accepting the work. In the language of the response_language block of the prompt.", StringOf("Risk."))),
            ("correctiveActions", ArrayOf("Required for ChangesRequested and Rejected. In the language of the response_language block of the prompt.", StringOf("Action."))),
            ("confidence", confidence),
            ("requiresHumanIntervention", BooleanOf("True for EscalateToHuman and Unresolved; false for Approved.")),
            ("decision", EnumOf("The verdict, decided after the analysis above.", Enum.GetNames<VerdictDecision>())),
            ("summary", StringOf("One or two sentences. In the language of the response_language block of the prompt.")),
            ("requirementSetHash", StringOf("Copy requirementSetHash from the dossier exactly.")),
            ("decisionLogHeadHash", StringOf("Copy decisionLogHeadHash from the dossier exactly.")));

        var text = schema.ToJsonString();
        var size = Encoding.UTF8.GetByteCount(text);
        if (size > MaxBytes)
        {
            throw new InvalidOperationException(
                $"The verdict schema for {requirementIds.Length} requirements takes {size} bytes, more than the {MaxBytes} bytes a judge on Claude accepts.");
        }

        return text;
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

    private static JsonObject IntegerOf() => new() { ["type"] = "integer" };

    private static JsonObject BooleanOf(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject EnumOf(string description, IEnumerable<string> values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v))]),
    };
}
