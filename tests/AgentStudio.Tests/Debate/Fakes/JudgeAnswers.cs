using System.Text.Json.Nodes;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>Answers a judge could give, in the JSON shape of the verdict schema.</summary>
internal static class JudgeAnswers
{
    /// <summary>
    /// A valid approval: each mandatory requirement satisfied by <paramref name="proof"/> (a green system test run), each
    /// desirable one not verifiable, and both hashes echoed from the dossier.
    /// </summary>
    public static JsonObject Approval(JudgeDossier dossier, EvidenceId proof) => new()
    {
        ["assessments"] = new JsonArray([.. dossier.Requirements.Requirements.Select(r => (JsonNode)new JsonObject
        {
            ["requirement"] = r.Id.Value,
            ["status"] = r.Priority == RequirementPriority.Mandatory ? "Satisfied" : "NotVerifiable",
            ["evidence"] = r.Priority == RequirementPriority.Mandatory ? new JsonArray(proof.Value) : new JsonArray(),
            ["rationale"] = r.Priority == RequirementPriority.Mandatory ? $"La ejecución {proof} del sistema lo prueba." : "No hay evidencia objetiva.",
        })]),
        ["evidenceExamined"] = new JsonArray(proof.Value),
        ["findings"] = new JsonArray(),
        ["pendingObjections"] = new JsonArray(),
        ["risks"] = new JsonArray(),
        ["correctiveActions"] = new JsonArray(),
        ["confidence"] = new JsonObject { ["level"] = "High", ["justification"] = "Tests del sistema en verde." },
        ["requiresHumanIntervention"] = false,
        ["decision"] = "Approved",
        ["summary"] = "Cumple los requisitos obligatorios.",
        ["requirementSetHash"] = dossier.Requirements.ContentHash,
        ["decisionLogHeadHash"] = dossier.DecisionLogHeadHash,
    };

    /// <summary>The same answer with another decision, and what that decision needs to stay valid.</summary>
    public static JsonObject Deciding(JudgeDossier dossier, EvidenceId proof, VerdictDecision decision)
    {
        var answer = Approval(dossier, proof);
        answer["decision"] = decision.ToString();
        if (decision is VerdictDecision.ChangesRequested or VerdictDecision.Rejected)
        {
            answer["correctiveActions"] = new JsonArray("Repetir la tarea T1");
        }

        answer["requiresHumanIntervention"] = decision is VerdictDecision.EscalateToHuman or VerdictDecision.Unresolved;
        return answer;
    }

    public static ScriptedRun Run(JsonNode answer, string sessionId = "thread-1", string? model = "gpt-5-codex") => new(
        new AgentRunOutcome(AgentRunStatus.Succeeded, 0, answer.ToJsonString(), answer.ToJsonString(), sessionId, null),
        new SessionStarted(DateTimeOffset.UnixEpoch, sessionId, model));
}
