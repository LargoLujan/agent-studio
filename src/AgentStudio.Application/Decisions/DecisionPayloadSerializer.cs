using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Application.Decisions;

/// <summary>
/// The one way decision payloads are written and read. The text of a payload is part of the hash of its entry, so the
/// format never varies: compact, camelCase, enums by name, ids as plain strings, null members left out, text not
/// escaped beyond what JSON requires, and no dependence on the culture of the machine.
/// </summary>
public static class DecisionPayloadSerializer
{
    private static readonly Dictionary<DecisionKind, Type> PayloadTypes = new()
    {
        [DecisionKind.PlanCreated] = typeof(PlanCreatedPayload),
        [DecisionKind.PlanRevised] = typeof(PlanRevisedPayload),
        [DecisionKind.TaskCreated] = typeof(TaskCreatedPayload),
        [DecisionKind.TaskAssigned] = typeof(TaskAssignedPayload),
        [DecisionKind.TaskStatusChanged] = typeof(TaskStatusChangedPayload),
        [DecisionKind.InstructionsIssued] = typeof(InstructionsIssuedPayload),
        [DecisionKind.ReviewRequested] = typeof(ReviewRequestedPayload),
        [DecisionKind.ApprovalRequested] = typeof(ApprovalRequestedPayload),
        [DecisionKind.ApprovalGranted] = typeof(ApprovalAnsweredPayload),
        [DecisionKind.ApprovalDenied] = typeof(ApprovalAnsweredPayload),
        [DecisionKind.EvidenceRegistered] = typeof(EvidenceRegisteredPayload),
        [DecisionKind.IntegrationPerformed] = typeof(IntegrationPerformedPayload),
        [DecisionKind.VerdictIssued] = typeof(VerdictIssuedPayload),
        [DecisionKind.AuditAlertRaised] = typeof(AuditAlertPayload),
        [DecisionKind.RunStarted] = typeof(RunStartedPayload),
        [DecisionKind.RunFinished] = typeof(RunFinishedPayload),
        [DecisionKind.AgentRunStarted] = typeof(AgentRunStartedPayload),
        [DecisionKind.AgentRunFinished] = typeof(AgentRunFinishedPayload),
        [DecisionKind.ConditionEvaluated] = typeof(ConditionEvaluatedPayload),
        [DecisionKind.WorktreeCreated] = typeof(WorktreeCreatedPayload),
        [DecisionKind.WorktreeRemoved] = typeof(WorktreeRemovedPayload),
        [DecisionKind.MemoryRecorded] = typeof(MemoryRecordedPayload),
        [DecisionKind.MemoryReviewed] = typeof(MemoryReviewedPayload),

        // Phase 5 (docs/15 D-03). ReplanRequested existed with no payload, and nothing ever wrote it: it gains one.
        [DecisionKind.ReplanRequested] = typeof(ReplanRequestedPayload),
        [DecisionKind.DebateOpened] = typeof(DebateOpenedPayload),
        [DecisionKind.DebateContributionRecorded] = typeof(DebateContributionRecordedPayload),
        [DecisionKind.DebateRoundClosed] = typeof(DebateRoundClosedPayload),
        [DecisionKind.DebateClosed] = typeof(DebateClosedPayload),
        [DecisionKind.VerdictChallenged] = typeof(VerdictChallengedPayload),
        [DecisionKind.ProjectAuditStarted] = typeof(ProjectAuditStartedPayload),
        [DecisionKind.ProjectAuditFinished] = typeof(ProjectAuditFinishedPayload),

        // Phase 8 (docs/16 D-16, D-38 and D-39). AgentRunFinished (version 3) and the answers to an approval (version 2) keep their types.
        [DecisionKind.RunExitChosen] = typeof(RunExitChosenPayload),
        [DecisionKind.RunDispatchChanged] = typeof(RunDispatchChangedPayload),
        [DecisionKind.AccessChecked] = typeof(AccessCheckedPayload),
        [DecisionKind.RunResumed] = typeof(RunResumedPayload),

        // Phase 12 (docs/17 D-02): the trust of the journal, outside every run.
        [DecisionKind.JournalTrustChanged] = typeof(JournalTrustChangedPayload),
    };

    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The payload type recorded with a kind of entry, or null for the kinds whose payload is free-form.</summary>
    public static Type? PayloadTypeFor(DecisionKind kind) => PayloadTypes.GetValueOrDefault(kind);

    /// <summary>Writes the payload as its own type: serializing through the base type would drop every field but the version and the run.</summary>
    public static string Serialize(DecisionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, payload.GetType(), Options);
    }

    public static T Deserialize<T>(string json)
        where T : DecisionPayload => (T)Deserialize(typeof(T), json);

    public static DecisionPayload Deserialize(Type type, string json)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(json);
        return (DecisionPayload?)JsonSerializer.Deserialize(json, type, Options)
            ?? throw new JsonException($"The payload is null, not a {type.Name}.");
    }

    /// <summary>Reads the payload of an entry of the given kind, or returns null if that kind has no typed payload.</summary>
    public static DecisionPayload? Deserialize(DecisionKind kind, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return PayloadTypeFor(kind) is { } type ? Deserialize(type, json) : null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new DomainIdConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The ids of the domain are structs around a string; in a payload they are that string.</summary>
    private sealed class DomainIdConverterFactory : JsonConverterFactory
    {
        private static readonly Dictionary<Type, JsonConverter> Converters = new()
        {
            [typeof(TaskId)] = new IdConverter<TaskId>(v => new TaskId(v), id => id.Value),
            [typeof(AgentId)] = new IdConverter<AgentId>(v => new AgentId(v), id => id.Value),
            [typeof(EvidenceId)] = new IdConverter<EvidenceId>(v => new EvidenceId(v), id => id.Value),
            [typeof(RequirementId)] = new IdConverter<RequirementId>(v => new RequirementId(v), id => id.Value),
            [typeof(RunId)] = new IdConverter<RunId>(v => new RunId(v), id => id.Value),
            [typeof(ProviderId)] = new IdConverter<ProviderId>(v => new ProviderId(v), id => id.Value),
            [typeof(VendorId)] = new IdConverter<VendorId>(v => new VendorId(v), id => id.Value),
            [typeof(SkillId)] = new IdConverter<SkillId>(v => new SkillId(v), id => id.Value),
        };

        public override bool CanConvert(Type typeToConvert) => Converters.ContainsKey(typeToConvert);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => Converters[typeToConvert];
    }

    private sealed class IdConverter<T>(Func<string, T> create, Func<T, string?> value) : JsonConverter<T>
        where T : struct
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException($"A {typeof(T).Name} cannot be empty.");
            }

            try
            {
                return create(text);
            }
            catch (ArgumentException ex)
            {
                throw new JsonException($"'{text}' is not a valid {typeof(T).Name}.", ex);
            }
        }

        public override void Write(Utf8JsonWriter writer, T id, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value(id) ?? throw new JsonException($"A default {typeof(T).Name} cannot be written."));
        }
    }
}
