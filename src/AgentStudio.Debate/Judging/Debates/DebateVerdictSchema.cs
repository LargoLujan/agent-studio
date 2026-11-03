using System.Text;
using System.Text.Json.Nodes;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>
/// The JSON schema the judge of a debate answers with (docs/15 D-25): the verdict of Phase 3 plus the resolution, built in
/// code for each judge from the frozen requirements and the labels of the proposals, in the order that judge reads them.
/// </summary>
/// <remarks>
/// <para>It keeps to the same portable subset as <see cref="VerdictSchema"/>: OpenAI strict mode (Codex
/// <c>--output-schema</c>) and Claude <c>--json-schema</c>, every object closed and with all its properties required, no
/// nullable unions, no format, pattern, length or range keywords, and at most <see cref="MaxBytes"/>. The validator, not the
/// schema, is the authority on what a verdict may say.</para>
/// <para>The resolution compares every proposal (an enum of their labels) and then names the one chosen, or
/// <see cref="VerdictJson.NoProposal"/>, the sentinel the portable subset needs instead of a null. It comes after the analysis
/// and before the decision, so the judge compares before it chooses and chooses before it decides. A debate with no proposal
/// to choose (one that reviews a run, a tribunal) answers with the members of Phase 3 alone: an enum cannot be empty.</para>
/// </remarks>
public static class DebateVerdictSchema
{
    /// <summary>The most Claude accepts inline on its command line, as for the review of a run.</summary>
    public const int MaxBytes = VerdictSchema.MaxBytes;

    /// <param name="proposals">The labels of the proposals that can be chosen (<c>P1</c>…), in the order this judge reads them; empty when there is nothing to choose.</param>
    /// <exception cref="ArgumentException">A label is blank, repeated or the sentinel.</exception>
    /// <exception cref="InvalidOperationException">The schema would exceed <see cref="MaxBytes"/>.</exception>
    public static string Build(FrozenRequirementSet requirements, IReadOnlyList<string> proposals)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Any(string.IsNullOrWhiteSpace)
            || proposals.Distinct(StringComparer.Ordinal).Count() != proposals.Count
            || proposals.Contains(VerdictJson.NoProposal, StringComparer.Ordinal))
        {
            throw new ArgumentException($"The proposals of a debate are distinct labels, never blank nor '{VerdictJson.NoProposal}': {string.Join(", ", proposals)}.", nameof(proposals));
        }

        var requirementIds = requirements.Requirements.Select(r => r.Id.Value).ToArray();
        var assessment = ObjectOf(
            ("requirement", EnumOf("Id of the frozen requirement.", requirementIds)),
            ("status", EnumOf("Satisfied only with evidence that fits every acceptance criterion; a JudgeAssessment criterion may rest on the contributions.", Enum.GetNames<AssessmentStatus>())),
            ("evidence", ArrayOf("Ids of dossier evidence behind this status (contributions, quotes, test runs, notes).", StringOf("Evidence id."))),
            ("rationale", StringOf("Why, citing the evidence. In Spanish.")));
        var finding = ObjectOf(
            ("category", EnumOf("Kind of problem.", Enum.GetNames<FindingCategory>())),
            ("severity", EnumOf("Critical makes approval unsafe.", Enum.GetNames<Severity>())),
            ("description", StringOf("What is wrong and where. In Spanish.")),
            ("evidence", ArrayOf("Ids of dossier evidence that show it.", StringOf("Evidence id."))),
            ("decisionRefs", ArrayOf("Sequence numbers of the decision log entries involved.", IntegerOf())));
        var confidence = ObjectOf(
            ("level", EnumOf("Confidence in the verdict.", Enum.GetNames<ConfidenceLevel>())),
            ("justification", StringOf("Why this confidence. In Spanish.")));

        var members = new List<(string Name, JsonObject Schema)>
        {
            ("assessments", ArrayOf("Exactly one per frozen requirement.", assessment)),
            ("evidenceExamined", ArrayOf("Every evidence id cited anywhere in the verdict.", StringOf("Evidence id."))),
            ("findings", ArrayOf("Problems found in the debate or in what it is about.", finding)),
            ("pendingObjections", ArrayOf("Objections still open. In Spanish.", StringOf("Objection."))),
            ("risks", ArrayOf("Risks of accepting the outcome. In Spanish.", StringOf("Risk."))),
            ("correctiveActions", ArrayOf("Required for ChangesRequested and Rejected. In Spanish.", StringOf("Action."))),
            ("confidence", confidence),
        };

        if (proposals.Count > 0)
        {
            var comparison = ObjectOf(
                ("proposal", EnumOf("Label of the proposal.", proposals)),
                ("strengths", StringOf("What speaks for it, citing evidence. In Spanish.")),
                ("weaknesses", StringOf("What speaks against it, citing evidence. In Spanish.")));
            members.Add(("resolution", ObjectOf(
                ("comparison", ArrayOf("One item per proposal, before choosing.", comparison)),
                ("chosenProposal", EnumOf($"The proposal chosen after the comparison, or {VerdictJson.NoProposal}.", [.. proposals, VerdictJson.NoProposal])))));
        }

        members.Add(("requiresHumanIntervention", BooleanOf("True for EscalateToHuman and Unresolved; false for Approved.")));
        members.Add(("decision", EnumOf("The verdict, decided after the analysis above.", Enum.GetNames<VerdictDecision>())));
        members.Add(("summary", StringOf("One or two sentences. In Spanish.")));
        members.Add(("requirementSetHash", StringOf("Copy requirementSetHash from the dossier exactly.")));
        members.Add(("decisionLogHeadHash", StringOf("Copy decisionLogHeadHash from the dossier exactly.")));

        var text = ObjectOf([.. members]).ToJsonString();
        var size = Encoding.UTF8.GetByteCount(text);
        if (size > MaxBytes)
        {
            throw new InvalidOperationException(
                $"The verdict schema of a debate with {requirementIds.Length} requirements and {proposals.Count} proposals takes {size} bytes, more than the {MaxBytes} bytes a judge on Claude accepts.");
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
