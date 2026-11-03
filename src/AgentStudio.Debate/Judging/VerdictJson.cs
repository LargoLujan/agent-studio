using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// Reads the judge's JSON answer (the shape of <see cref="VerdictSchema"/>) into a domain <see cref="Verdict"/>, strictly.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A member the judge left null, left out or wrote with another JSON type stays null, so
/// <see cref="VerdictValidator"/> reports it (<c>MalformedVerdict</c>) instead of the parser filling it in. An item that
/// cannot be read (an assessment without a valid requirement or status, a finding without a valid category or severity)
/// stays as a null item, and a list with an unreadable element (a blank evidence id) stays null: nothing is dropped.</item>
/// <item>Enum values are matched by their exact name, never by number.</item>
/// <item>Unknown members are rejected anywhere, and so are duplicated ones: the judge has no way to state who it is or
/// when it judged. <see cref="Verdict.Judge"/> and <see cref="Verdict.IssuedAt"/> are given by the system.</item>
/// <item>Only the decision and the human-intervention flag must be there, because the domain record cannot hold them
/// as null. Without them, or when the text is not one JSON object, the answer is not a verdict
/// (<see cref="JudgeOutputException"/>).</item>
/// <item>The judge of a debate also answers with a resolution (docs/15 D-25), read by <see cref="ParseDebate"/>: the
/// proposals it compares and the one it chose, by label, or <see cref="NoProposal"/>. <see cref="Parse"/> reads the answer
/// of the review of a run, whose schema has no resolution, exactly as in Phase 3: a <c>resolution</c> there is an unknown
/// member. Inside a resolution the chosen proposal must be there, for the same reason as the decision: the domain record
/// reads null as «ninguna», so a chosen proposal the judge left null cannot be told from one it did not choose.</item>
/// </list>
/// </remarks>
public static class VerdictJson
{
    /// <summary>The sentinel of the schema of a debate for «no proposal chosen» (docs/15 D-25), read as a null <see cref="DebateResolution.ChosenProposal"/>.</summary>
    public const string NoProposal = "ninguna";

    private static readonly string[] VerdictMembers =
    [
        "assessments", "evidenceExamined", "findings", "pendingObjections", "risks", "correctiveActions", "confidence",
        "requiresHumanIntervention", "decision", "summary", "requirementSetHash", "decisionLogHeadHash",
    ];

    private static readonly string[] DebateVerdictMembers = [.. VerdictMembers, "resolution"];
    private static readonly string[] AssessmentMembers = ["requirement", "status", "evidence", "rationale"];
    private static readonly string[] FindingMembers = ["category", "severity", "description", "evidence", "decisionRefs"];
    private static readonly string[] ConfidenceMembers = ["level", "justification"];
    private static readonly string[] ResolutionMembers = ["comparison", "chosenProposal"];
    private static readonly string[] ComparisonMembers = ["proposal", "strengths", "weaknesses"];

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowDuplicateProperties = false };

    /// <summary>Writes data that goes inside a prompt: '&lt;' and '&gt;' are always escaped, so the text cannot close a delimiter.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Reads the answer of the judge of the review of a run (<see cref="VerdictSchema"/>), whose verdicts have no resolution.</summary>
    /// <exception cref="JudgeOutputException">The text is not one JSON object, or it has no decision or human flag, or it has unknown or duplicated members.</exception>
    public static Verdict Parse(string json, JudgeIdentity judge, DateTimeOffset issuedAt) => Read(json, judge, issuedAt, debate: false);

    /// <summary>
    /// Reads the answer of the judge of a debate: the members of <see cref="Parse"/> plus the resolution
    /// (<see cref="Verdict.Resolution"/>). A resolution left out, null or of another JSON type stays null, and the validator
    /// decides (<c>DebateResolutionMissing</c>); a comparison left null stays null and one that cannot be read stays as a
    /// null item (<c>MalformedVerdict</c>).
    /// </summary>
    /// <exception cref="JudgeOutputException">As <see cref="Parse"/>, and also when a resolution has no chosen proposal that is a string.</exception>
    public static Verdict ParseDebate(string json, JudgeIdentity judge, DateTimeOffset issuedAt) => Read(json, judge, issuedAt, debate: true);

    private static Verdict Read(string json, JudgeIdentity judge, DateTimeOffset issuedAt, bool debate)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(judge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new JudgeOutputException("The judge's answer is not valid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JudgeOutputException($"The judge's answer is a JSON {root.ValueKind}, not an object.");
            }

            RejectUnknown(root, debate ? DebateVerdictMembers : VerdictMembers, "the verdict");
            var decision = EnumOrNull<VerdictDecision>(root, "decision")
                ?? throw new JudgeOutputException("The judge's answer has no valid 'decision', so it is not a verdict.");
            var requiresHuman = BooleanOrNull(root, "requiresHumanIntervention")
                ?? throw new JudgeOutputException("The judge's answer has no 'requiresHumanIntervention', so it is not a verdict.");
            var resolution = debate ? ResolutionOrNull(root) : null;

            // Null members are deliberate: the validator reports them (MalformedVerdict). Nothing is filled in.
            return new Verdict(
                decision,
                StringOrNull(root, "summary")!,
                ItemsOrNull(root, "assessments", AssessmentMembers, "an assessment", Assessment)!,
                EvidenceIdsOrNull(root, "evidenceExamined")!,
                StringsOrNull(root, "pendingObjections")!,
                StringsOrNull(root, "risks")!,
                ItemsOrNull(root, "findings", FindingMembers, "a finding", Finding)!,
                ConfidenceOrNull(root)!,
                StringsOrNull(root, "correctiveActions")!,
                requiresHuman,
                judge,
                StringOrNull(root, "requirementSetHash")!,
                StringOrNull(root, "decisionLogHeadHash")!,
                issuedAt)
            {
                Resolution = resolution,
            };
        }
    }

    /// <summary>
    /// The verdict in the schema's shape, nulls included, without identity or time: what the judge is shown in the
    /// correction round. Safe to put inside a prompt (see <see cref="WriteOptions"/>). The resolution of a debate is written
    /// after the analysis and before the decision, as its schema asks; a verdict without one (the review of a run, every
    /// verdict stored before Phase 5) is written exactly as in Phase 3.
    /// </summary>
    public static string ToJson(Verdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var members = new List<KeyValuePair<string, JsonNode?>>
        {
            new("assessments", Array(verdict.Assessments, a => a is null ? null : new JsonObject
            {
                ["requirement"] = a.Requirement.Value,
                ["status"] = a.Status.ToString(),
                ["evidence"] = Array(a.Evidence, id => JsonValue.Create(id.Value)),
                ["rationale"] = a.Rationale,
            })),
            new("evidenceExamined", Array(verdict.EvidenceExamined, id => JsonValue.Create(id.Value))),
            new("findings", Array(verdict.Findings, f => f is null ? null : new JsonObject
            {
                ["category"] = f.Category.ToString(),
                ["severity"] = f.Severity.ToString(),
                ["description"] = f.Description,
                ["evidence"] = Array(f.Evidence, id => JsonValue.Create(id.Value)),
                ["decisionRefs"] = Array(f.DecisionRefs, r => JsonValue.Create(r)),
            })),
            new("pendingObjections", Array(verdict.PendingObjections, s => JsonValue.Create(s))),
            new("risks", Array(verdict.Risks, s => JsonValue.Create(s))),
            new("correctiveActions", Array(verdict.CorrectiveActions, s => JsonValue.Create(s))),
            new("confidence", verdict.Confidence is null ? null : new JsonObject
            {
                ["level"] = verdict.Confidence.Level.ToString(),
                ["justification"] = verdict.Confidence.Justification,
            }),
        };

        if (verdict.Resolution is { } resolution)
        {
            members.Add(new("resolution", new JsonObject
            {
                ["comparison"] = Array(resolution.Comparison, c => c is null ? null : new JsonObject
                {
                    ["proposal"] = c.Proposal,
                    ["strengths"] = c.Strengths,
                    ["weaknesses"] = c.Weaknesses,
                }),
                ["chosenProposal"] = resolution.ChosenProposal ?? NoProposal,
            }));
        }

        members.Add(new("requiresHumanIntervention", verdict.RequiresHumanIntervention));
        members.Add(new("decision", verdict.Decision.ToString()));
        members.Add(new("summary", verdict.Summary));
        members.Add(new("requirementSetHash", verdict.RequirementSetHash));
        members.Add(new("decisionLogHeadHash", verdict.DecisionLogHeadHash));
        return new JsonObject(members).ToJsonString(WriteOptions);
    }

    private static JsonArray? Array<T>(IReadOnlyList<T>? items, Func<T, JsonNode?> map) =>
        items is null ? null : new JsonArray([.. items.Select(map)]);

    /// <summary>
    /// The resolution of a debate, or null when the judge left it out, null or of another JSON type. Its chosen proposal
    /// must be a string («ninguna» is no proposal); a comparison that cannot be read stays as a null item.
    /// </summary>
    /// <exception cref="JudgeOutputException">The resolution has unknown members, or no chosen proposal that is a string.</exception>
    private static DebateResolution? ResolutionOrNull(JsonElement root)
    {
        if (!root.TryGetProperty("resolution", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        RejectUnknown(value, ResolutionMembers, "the resolution");
        var chosen = StringOrNull(value, "chosenProposal")
            ?? throw new JudgeOutputException("The judge's answer has a resolution without a 'chosenProposal' string: null would read as «ninguna», which the judge did not say.");
        var comparison = ItemsOrNull(value, "comparison", ComparisonMembers, "a comparison", Comparison);
        return new DebateResolution(string.Equals(chosen, NoProposal, StringComparison.Ordinal) ? null : chosen, comparison!);
    }

    private static ProposalComparison? Comparison(JsonElement item)
    {
        var proposal = StringOrNull(item, "proposal");
        var strengths = StringOrNull(item, "strengths");
        var weaknesses = StringOrNull(item, "weaknesses");
        return proposal is null || strengths is null || weaknesses is null ? null : new ProposalComparison(proposal, strengths, weaknesses);
    }

    private static RequirementAssessment? Assessment(JsonElement item)
    {
        var requirement = StringOrNull(item, "requirement");
        var status = EnumOrNull<AssessmentStatus>(item, "status");
        if (string.IsNullOrWhiteSpace(requirement) || status is null)
        {
            return null;
        }

        return new RequirementAssessment(new RequirementId(requirement), status.Value, EvidenceIdsOrNull(item, "evidence")!, StringOrNull(item, "rationale")!);
    }

    private static Finding? Finding(JsonElement item)
    {
        var category = EnumOrNull<FindingCategory>(item, "category");
        var severity = EnumOrNull<Severity>(item, "severity");
        if (category is null || severity is null)
        {
            return null;
        }

        return new Finding(category.Value, severity.Value, StringOrNull(item, "description")!, EvidenceIdsOrNull(item, "evidence")!, IntegersOrNull(item, "decisionRefs")!);
    }

    private static Confidence? ConfidenceOrNull(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        RejectUnknown(value, ConfidenceMembers, "the confidence");
        var level = EnumOrNull<ConfidenceLevel>(value, "level");
        return level is null ? null : new Confidence(level.Value, StringOrNull(value, "justification")!);
    }

    private static List<T?>? ItemsOrNull<T>(JsonElement parent, string name, string[] members, string what, Func<JsonElement, T?> read)
        where T : class
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<T?>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                items.Add(null);
                continue;
            }

            RejectUnknown(element, members, what);
            items.Add(read(element));
        }

        return items;
    }

    private static List<EvidenceId>? EvidenceIdsOrNull(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var ids = new List<EvidenceId>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                return null;
            }

            ids.Add(new EvidenceId(element.GetString()!));
        }

        return ids;
    }

    private static List<string?>? StringsOrNull(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var texts = new List<string?>();
        foreach (var element in value.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    texts.Add(element.GetString());
                    break;
                case JsonValueKind.Null:
                    texts.Add(null);
                    break;
                default:
                    return null;
            }
        }

        return texts;
    }

    private static List<long>? IntegersOrNull(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var numbers = new List<long>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers;
    }

    private static string? StringOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? BooleanOrNull(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Only the exact name of a member counts: <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> would also accept "0".</summary>
    private static TEnum? EnumOrNull<TEnum>(JsonElement parent, string name)
        where TEnum : struct, Enum
    {
        var text = StringOrNull(parent, name);
        return text is not null && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal) ? Enum.Parse<TEnum>(text) : null;
    }

    private static void RejectUnknown(JsonElement element, string[] allowed, string what)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JudgeOutputException($"The judge's answer has the member '{property.Name}' in {what}, which the verdict schema does not allow.");
            }
        }
    }
}

/// <summary>
/// The judge answered, but not with a verdict: the text is not one JSON object, or it lacks the decision or the human
/// flag, or it has members the schema does not allow. Nothing can be validated or stored for it.
/// </summary>
public sealed class JudgeOutputException : Exception
{
    public JudgeOutputException(string message)
        : base(message)
    {
    }

    public JudgeOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
