using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Debate.Judging;

/// <summary>
/// The dossier as the judge reads it: one JSON document, written to <c>dossier.json</c> and placed in the prompt
/// between delimiters. It is data to evaluate, never instructions: '&lt;' and '&gt;' are always escaped, so no text of
/// the dossier (a requirement, a commit message, an agent's report) can close the delimiter it travels in.
/// </summary>
public static class DossierDocument
{
    public const string Format = "agent-studio/judge-dossier/1";

    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    internal static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <param name="dossier">What the judge evaluates.</param>
    /// <param name="files">The package of the dossier, if there is one: where each evidence was copied and the system checks.</param>
    public static string ToJson(JudgeDossier dossier, DossierFiles? files)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        var copies = (files?.Evidence ?? []).GroupBy(f => f.Evidence).ToDictionary(g => g.Key, g => g.First());
        var root = new JsonObject
        {
            ["format"] = Format,
            ["projectId"] = files?.ProjectId,
            ["runId"] = files?.RunId,
            ["requirementSetHash"] = dossier.Requirements.ContentHash,
            ["decisionLogHeadHash"] = dossier.DecisionLogHeadHash,
            ["candidateCommit"] = dossier.CandidateCommit,
            ["excludedVendors"] = Texts(dossier.ExcludedVendors.Select(v => v.Value).Order(StringComparer.Ordinal)),
            ["originalRequest"] = dossier.OriginalRequest,
            ["requirements"] = new JsonArray([.. dossier.Requirements.Requirements.Select(Requirement)]),
            ["plan"] = JsonOrText(dossier.OrchestratorPlan),
            ["tasks"] = new JsonArray([.. dossier.Tasks.Select(Task)]),
            ["evidence"] = new JsonArray([.. dossier.Evidence.Select(e => Evidence(e, copies.GetValueOrDefault(e.Id)))]),
            ["decisions"] = new JsonArray([.. dossier.Decisions.Select(Decision)]),
            ["systemChecks"] = Texts(dossier.IntegrityProblems.Union(files?.SystemChecks ?? [], StringComparer.Ordinal)),
        };
        return root.ToJsonString(Indented);
    }

    private static JsonObject Requirement(Requirement requirement) => new()
    {
        ["id"] = requirement.Id.Value,
        ["priority"] = requirement.Priority.ToString(),
        ["statement"] = requirement.Statement,
        ["criteria"] = new JsonArray([.. requirement.Criteria.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["method"] = c.Method.ToString(),
            ["description"] = c.Description,
        })]),
    };

    private static JsonObject Task(WorkTaskSnapshot task) => new()
    {
        ["id"] = task.Id.Value,
        ["title"] = task.Title,
        ["kind"] = task.Kind.ToString(),
        ["status"] = task.Status.ToString(),
        ["assignedTo"] = task.AssignedTo?.Value,
        ["retryCount"] = task.RetryCount,
        ["completionEvidence"] = Texts(task.CompletionEvidence.Select(id => id.Value)),
    };

    private static JsonObject Evidence(EvidenceRecord evidence, DossierEvidenceFile? copy) => new()
    {
        ["id"] = evidence.Id.Value,
        ["kind"] = evidence.Kind.ToString(),
        ["origin"] = evidence.Origin.ToString(),
        ["objective"] = evidence.IsObjective,
        ["title"] = evidence.Title,
        ["producedBy"] = evidence.ProducedBy,
        ["capturedAt"] = evidence.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
        ["sha256"] = evidence.ContentSha256,
        ["subjectCommit"] = evidence.SubjectCommit,
        ["testRun"] = evidence.TestRun is not { } run ? null : new JsonObject
        {
            ["passed"] = run.Passed,
            ["failed"] = run.Failed,
            ["skipped"] = run.Skipped,
            ["exitCode"] = run.ExitCode,
            ["succeeded"] = run.Succeeded,
        },
        ["covers"] = Texts(evidence.Covers.Select(r => r.Value)),
        ["file"] = copy?.RelativePath,
        ["contentVerified"] = copy?.ContentVerified,
        ["truncated"] = copy?.Truncated,
        ["sizeBytes"] = copy?.SizeBytes,
        ["saidByAgent"] = copy is { SaidByAgent.Count: > 0 } ? Texts(copy.SaidByAgent) : null,
    };

    private static JsonObject Decision(DecisionEntry entry) => new()
    {
        ["sequence"] = entry.Sequence,
        ["recordedAt"] = entry.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
        ["actor"] = new JsonObject { ["kind"] = entry.Actor.Kind.ToString(), ["id"] = entry.Actor.Id },
        ["kind"] = entry.Kind.ToString(),
        ["summary"] = entry.Summary,
        ["payload"] = JsonOrText(entry.PayloadJson),
        ["corrects"] = entry.Corrects,
        ["hash"] = entry.Hash,
    };

    private static JsonArray Texts(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    /// <summary>JSON text is embedded as JSON; anything else as a string.</summary>
    private static JsonNode? JsonOrText(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }
}

/// <summary>The plan of a run as the dossier shows it (<see cref="JudgeDossier.OrchestratorPlan"/>): every field of every task.</summary>
public static class PlanJson
{
    public static string Render(PlanDefinition plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = new JsonObject
        {
            ["contentHash"] = plan.ContentHash,
            ["summary"] = plan.Summary,
            ["source"] = plan.Source.ToString(),
            ["author"] = plan.Author?.Value,
            ["risks"] = new JsonArray([.. plan.Risks.Select(r => (JsonNode)JsonValue.Create(r))]),
            ["tasks"] = new JsonArray([.. plan.Tasks.Select(t => (JsonNode)Task(t))]),
        };
        return root.ToJsonString(DossierDocument.Compact);
    }

    private static JsonObject Task(TaskSpec spec) => new()
    {
        ["id"] = spec.Id.Value,
        ["title"] = spec.Title,
        ["kind"] = spec.Kind.ToString(),
        ["agent"] = spec.Agent?.Value,
        ["objective"] = spec.Objective,
        ["instructions"] = spec.Instructions,
        ["dependsOn"] = new JsonArray([.. spec.DependsOn.Select(d => (JsonNode)new JsonObject { ["prerequisite"] = d.Prerequisite.Value, ["branch"] = d.Branch })]),
        ["join"] = spec.Join.ToString(),
        ["covers"] = Texts(spec.Covers.Select(r => r.Value)),
        ["writeScope"] = Texts(spec.WriteScope),
        ["verificationSteps"] = Texts(spec.VerificationSteps),
        ["condition"] = Describe(spec.Condition),
        ["toolId"] = spec.ToolId,
        ["maxRetries"] = spec.MaxRetries,
        ["timeout"] = spec.Timeout?.ToString("c", CultureInfo.InvariantCulture),
        ["workspace"] = spec.Workspace.ToString(),
    };

    private static JsonArray Texts(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    private static string? Describe(ConditionPredicate? predicate) => predicate switch
    {
        null => null,
        TaskSucceeded p => $"TaskSucceeded({p.Task})",
        TestsPassed p => $"TestsPassed({p.Task})",
        OutputEquals p => $"OutputEquals({p.Task}, {p.Key}, {p.Value})",
        PathsChanged p => $"PathsChanged({p.Task}, {p.PathPrefix})",
        _ => predicate.ToString(),
    };
}
