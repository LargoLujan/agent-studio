using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Debate.Audit;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// What the judge reads of an attempt record (ADR-0051, addendum). The engine captures the record as system evidence, but
/// copies into it the <c>outputs</c> of the executor's report, which the agent wrote. The stored record does not change (a
/// resumed run reads those outputs); the copy of it that goes into a dossier does: <c>outputs</c> leaves the system-captured
/// fields and travels under <see cref="SaidByAgentKey"/>, marked as a claim of the agent, like any other report of an agent.
/// </summary>
internal static class AgentSaidFields
{
    /// <summary>The key, in the copy of an attempt record, under which the outputs the agent reported travel.</summary>
    public const string SaidByAgentKey = "outputsSaidByAgent";

    /// <summary>The field of the stored record that the agent wrote (<c>AttemptRecord.Outputs</c>).</summary>
    public const string OutputsKey = "outputs";

    private const string RecordKind = OrchestratorAudit.AttemptRecordKind;
    private const string TitlePrefix = OrchestratorAudit.AttemptRecordTitlePrefix + " ";

    private const string Note =
        "Said by the agent, not captured by the system: the outputs the executor wrote in its report. " +
        "A claim to check against the evidence, never proof.";

    /// <summary>Whether <paramref name="evidence"/> may be an attempt record, as the auditor tells it (a document the system captured, titled as one).</summary>
    public static bool MayBeAttemptRecord(EvidenceRecord evidence) =>
        evidence is { Kind: EvidenceKind.Document, Origin: EvidenceOrigin.CapturedBySystem }
        && evidence.Title?.StartsWith(TitlePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Moves <c>outputs</c> of an attempt record under <see cref="SaidByAgentKey"/>. Returns false, and no text, when
    /// <paramref name="json"/> is not an attempt record of version 1 with outputs: such a copy stays as it was.
    /// </summary>
    public static bool TryMark(string json, out string marked)
    {
        marked = string.Empty;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || root["v"]?.GetValueKind() != JsonValueKind.Number || root["v"]!.GetValue<int>() != 1
                || root["kind"]?.GetValueKind() != JsonValueKind.String || root["kind"]!.GetValue<string>() != RecordKind
                || root[OutputsKey] is not JsonArray outputs)
            {
                return false;
            }

            var copy = new JsonObject();
            foreach (var (name, value) in root.ToList())
            {
                root.Remove(name);
                if (name == OutputsKey)
                {
                    copy[SaidByAgentKey] = new JsonObject
                    {
                        ["origin"] = nameof(EvidenceOrigin.ReportedByAgent),
                        ["note"] = Note,
                        [OutputsKey] = outputs.DeepClone(),
                    };
                }
                else
                {
                    copy[name] = value;
                }
            }

            marked = copy.ToJsonString(DossierDocument.Compact);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
