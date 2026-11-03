using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Projects;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Tests.Debate.Fakes;

/// <summary>A judge whose verdicts are scripted from the dossier it receives; it remembers what it was asked.</summary>
internal sealed class ScriptedJudge(JudgeIdentity identity, Func<JudgeDossier, Verdict> evaluate, Func<JudgeDossier, Verdict, IReadOnlyList<VerdictViolation>, Verdict>? revise = null) : IJudge
{
    public List<JudgeDossier> Evaluated { get; } = [];

    public List<IReadOnlyList<VerdictViolation>> Revisions { get; } = [];

    public List<Verdict> Given { get; } = [];

    public Exception? ReviseFailure { get; init; }

    public JudgeIdentity Identity => identity;

    public Task<Verdict> EvaluateAsync(JudgeDossier dossier, CancellationToken cancellationToken)
    {
        Evaluated.Add(dossier);
        var verdict = evaluate(dossier);
        Given.Add(verdict);
        return Task.FromResult(verdict);
    }

    public Task<Verdict> ReviseAsync(JudgeDossier dossier, Verdict previous, IReadOnlyList<VerdictViolation> violations, CancellationToken cancellationToken)
    {
        Revisions.Add(violations);
        if (ReviseFailure is { } failure)
        {
            throw failure;
        }

        var verdict = (revise ?? throw new InvalidOperationException("No correction was scripted."))(dossier, previous, violations);
        Given.Add(verdict);
        return Task.FromResult(verdict);
    }

    /// <summary>A verdict parsed from a judge's JSON answer, as the real judge would build it.</summary>
    public static Verdict From(JsonNode answer, JudgeIdentity identity) => VerdictJson.Parse(answer.ToJsonString(), identity, DateTimeOffset.UnixEpoch);
}

internal sealed class FakeJudgeFactory(Func<JudgeLaunch, IJudge> create) : IAgentJudgeFactory
{
    public List<(ProjectContext Project, JudgeLaunch Launch)> Created { get; } = [];

    public IJudge Create(ProjectContext project, JudgeLaunch launch)
    {
        Created.Add((project, launch));
        return create(launch);
    }
}

/// <summary>Reads back what the judge was shown: the dossier JSON between the delimiters of the prompt.</summary>
internal static class PromptData
{
    public static JsonElement Dossier(string prompt)
    {
        var start = prompt.IndexOf(JudgePrompt.DataOpen, StringComparison.Ordinal) + JudgePrompt.DataOpen.Length;
        var end = prompt.IndexOf(JudgePrompt.DataClose, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(prompt[start..end]);
        return document.RootElement.Clone();
    }

    /// <summary>An approval that echoes the hashes the prompt carries, like a judge that read its dossier.</summary>
    public static JsonObject EchoingApproval(string prompt, EvidenceId proof)
    {
        var data = Dossier(prompt);
        var assessments = new JsonArray();
        foreach (var requirement in data.GetProperty("requirements").EnumerateArray())
        {
            var mandatory = requirement.GetProperty("priority").GetString() == "Mandatory";
            assessments.Add(new JsonObject
            {
                ["requirement"] = requirement.GetProperty("id").GetString(),
                ["status"] = mandatory ? "Satisfied" : "NotVerifiable",
                ["evidence"] = mandatory ? new JsonArray(proof.Value) : new JsonArray(),
                ["rationale"] = mandatory ? "La ejecución del sistema lo prueba." : "Sin evidencia objetiva.",
            });
        }

        return new JsonObject
        {
            ["assessments"] = assessments,
            ["evidenceExamined"] = new JsonArray(proof.Value),
            ["findings"] = new JsonArray(),
            ["pendingObjections"] = new JsonArray(),
            ["risks"] = new JsonArray(),
            ["correctiveActions"] = new JsonArray(),
            ["confidence"] = new JsonObject { ["level"] = "High", ["justification"] = "Tests del sistema en verde." },
            ["requiresHumanIntervention"] = false,
            ["decision"] = "Approved",
            ["summary"] = "Cumple.",
            ["requirementSetHash"] = data.GetProperty("requirementSetHash").GetString(),
            ["decisionLogHeadHash"] = data.GetProperty("decisionLogHeadHash").GetString(),
        };
    }
}
