using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>
/// The dossier of a debate as its judge reads it (docs/15 D-24, format <see cref="Format"/>): one JSON document, written to
/// the package and placed in the prompt between the delimiters of <see cref="JudgePrompt"/>. It is data to evaluate, never
/// instructions: it is written with the escaping encoder of <see cref="DossierDocument"/>, so no text in it (a
/// contribution, a quote, the question) can close the delimiter it travels in.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Blind (D-15): the participants by label and role, the contributions by label, the protocol of the debate by
/// sequence and kind with a reference the system writes. No agent, CLI, vendor or model of a participant, no excluded
/// vendors and no disposition of the judgment: the system keeps them, and the card shows them to the user.</item>
/// <item>The second judge of a panel reads the proposals in reverse (D-14): the proposals, the participants and the
/// contributions of round 1 are listed backwards; the rounds that follow keep their order, which is that of the debate.</item>
/// <item>A run the debate is about goes in whole, as its dossier of Phase 3 (<see cref="DossierDocument"/>), with the files
/// of this package. A document larger than <see cref="MaxBytes"/> keeps the text of the contributions and the quotes, and
/// gives the rest as an index with hashes: the plan and the payloads of the run are then in <see cref="DebateDossierBuilder.SubjectRunFile"/> (R8).</item>
/// </list>
/// </remarks>
public static class DebateDossierDocument
{
    public const string Format = "agent-studio/debate-dossier/1";

    /// <summary>Beyond this size the document gives the subject run as an index (D-24 and R8).</summary>
    public const int MaxBytes = 256 * 1024;

    /// <param name="reversed">The order of the second judge of a panel: the proposals reversed.</param>
    public static string ToJson(DebateDossier dossier, DossierFiles files, bool reversed)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ArgumentNullException.ThrowIfNull(files);
        var full = Render(dossier, files, reversed, index: false).ToJsonString(DossierDocument.Indented);
        return Encoding.UTF8.GetByteCount(full) <= MaxBytes ? full : Render(dossier, files, reversed, index: true).ToJsonString(DossierDocument.Indented);
    }

    /// <summary>The run whose dossier of Phase 3 goes in the package: the subject, or the object of a challenge.</summary>
    public static RunSubject? SubjectRun(DebateDossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        return dossier.Run ?? dossier.Challenge?.Run ?? dossier.Challenge?.DebateRun;
    }

    /// <summary>The whole dossier of Phase 3 of <paramref name="run"/>, pointing at the copies of its evidence in this package.</summary>
    public static string RunJson(RunSubject run, DossierFiles files)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(files);
        var node = JsonNode.Parse(DossierDocument.ToJson(run.Dossier, RunFiles(run, files)))!.AsObject();
        KeepOnlyAgentsThatWorked(node);
        return node.ToJsonString(DossierDocument.Indented);
    }

    private static JsonObject Render(DebateDossier dossier, DossierFiles files, bool reversed, bool index)
    {
        var copies = files.Evidence.GroupBy(f => f.Evidence).ToDictionary(g => g.Key, g => g.First());
        var judged = dossier.Dossier;
        return new JsonObject
        {
            ["format"] = Format,
            ["projectId"] = files.ProjectId,
            ["debateId"] = dossier.Debate.Debate.Value,
            ["requirementSetHash"] = judged.Requirements.ContentHash,
            ["decisionLogHeadHash"] = judged.DecisionLogHeadHash,
            ["candidateCommit"] = judged.CandidateCommit,
            ["family"] = judged.Debate?.Choice == true ? "choice" : "review",
            ["mode"] = (judged.Debate?.Mode ?? dossier.Debate.Mode).ToString(),
            ["proposals"] = Texts(Ordered(dossier.Proposals, reversed)),
            ["question"] = dossier.Debate.Question,
            ["requirements"] = new JsonArray([.. judged.Requirements.Requirements.Select(Requirement)]),
            ["debate"] = Debate(dossier.Debate, copies, reversed),
            ["subject"] = Subject(dossier, files, copies, reversed, index),
            ["systemChecks"] = Texts(judged.IntegrityProblems.Union(files.SystemChecks, StringComparer.Ordinal)),
        };
    }

    private static JsonObject Debate(DebateMaterial debate, Dictionary<EvidenceId, DossierEvidenceFile> copies, bool reversed)
    {
        var judgment = debate.Judgment;
        var roundOne = debate.Contributions.Where(c => c.Round == 1).ToList();
        var later = debate.Contributions.Where(c => c.Round != 1);
        return new JsonObject
        {
            ["id"] = debate.Debate.Value,
            ["mode"] = debate.Mode.ToString(),
            ["subject"] = new JsonObject
            {
                ["kind"] = debate.Subject.ToString(),
                ["reference"] = debate.SubjectReference,
                ["snapshotCommit"] = debate.SnapshotCommit,
            },
            ["questionEvidence"] = debate.QuestionEvidence.Value,
            ["participants"] = new JsonArray([.. Ordered(debate.Participants, reversed).Select(p => (JsonNode)new JsonObject { ["label"] = p.Label, ["role"] = p.Role.ToString() })]),
            ["proposals"] = Texts(Ordered(debate.Proposals, reversed)),
            ["limits"] = new JsonObject
            {
                ["maxRounds"] = debate.Limits.MaxRounds,
                ["maxDurationSeconds"] = (long)debate.Limits.MaxDuration.TotalSeconds,
                ["maxTotalTokens"] = debate.Limits.MaxTotalTokens,
                ["maxAgentRuns"] = debate.MaxAgentRuns,
            },
            ["judgment"] = judgment is null ? null : new JsonObject
            {
                ["round"] = judgment.Round,
                ["reason"] = judgment.Judgment?.ToString(),
                ["limit"] = judgment.Limit.ToString(),
            },
            ["rounds"] = new JsonArray([.. debate.Rounds.Select(r => (JsonNode)new JsonObject
            {
                ["sequence"] = r.Sequence,
                ["recordedAt"] = r.At.ToString("O", CultureInfo.InvariantCulture),
                ["round"] = r.Round,
                ["next"] = r.Next.ToString(),
                ["judgment"] = r.Judgment?.ToString(),
                ["limit"] = r.Limit.ToString(),
                ["elapsedSeconds"] = r.ElapsedSeconds,
            })]),
            ["contributions"] = new JsonArray([.. (reversed ? Enumerable.Reverse(roundOne).Concat(later) : debate.Contributions).Select(c => (JsonNode)Contribution(c, copies.GetValueOrDefault(c.Evidence)))]),
            ["evidence"] = new JsonArray([.. debate.Evidence.Select(e => (JsonNode)Evidence(e, copies.GetValueOrDefault(e.Id), debate.Quotes.GetValueOrDefault(e.Id)))]),
            ["protocol"] = new JsonArray([.. debate.Protocol.Select(p => (JsonNode)new JsonObject
            {
                ["sequence"] = p.Sequence,
                ["recordedAt"] = p.At.ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = p.Kind.ToString(),
                ["reference"] = p.Reference,
            })]),
        };
    }

    private static JsonObject Subject(DebateDossier dossier, DossierFiles files, Dictionary<EvidenceId, DossierEvidenceFile> copies, bool reversed, bool index)
    {
        var subject = new JsonObject { ["kind"] = dossier.Debate.Subject.ToString() };
        if (dossier.Run is { } run)
        {
            subject["run"] = RunNode(run, files, index);
        }

        if (dossier.Challenge is { } challenge)
        {
            subject["challenge"] = Challenge(challenge, files, copies, reversed, index);
        }

        return subject;
    }

    private static JsonObject Challenge(ChallengeSubject challenge, DossierFiles files, Dictionary<EvidenceId, DossierEvidenceFile> copies, bool reversed, bool index)
    {
        var stored = challenge.Stored;
        var node = new JsonObject
        {
            ["verdict"] = challenge.Verdict,
            ["inStore"] = stored is not null,
            ["unit"] = stored?.RunId,
            ["scope"] = stored?.Scope.ToString(),
            ["valid"] = stored?.Valid,
            ["issues"] = stored is null ? null : Texts((stored.Issues ?? []).Select(i => i.Code.ToString())),
            ["supersedes"] = stored?.Supersedes,
            ["challengedVerdict"] = stored is null ? null : JsonNode.Parse(VerdictJson.ToJson(stored.Verdict)),
            ["challengedBy"] = challenge.ChallengedBy,
            ["reasons"] = new JsonObject
            {
                ["evidence"] = challenge.Reasons?.Value,
                ["text"] = challenge.ReasonsText,
            },
        };

        var target = new JsonObject { ["kind"] = challenge.Object.ToString() };
        switch (challenge.Object)
        {
            case ChallengedObject.Run when challenge.Run is { } run:
                target["run"] = RunNode(run, files, index);
                break;
            case ChallengedObject.Debate when challenge.Debate is { } debate:
                target["question"] = debate.Question;
                target["debate"] = Debate(debate, copies, reversed);
                target["run"] = challenge.DebateRun is { } debateRun ? RunNode(debateRun, files, index) : null;
                break;
            case ChallengedObject.Audit when challenge.Audit is { } audit:
                target["audit"] = new JsonObject
                {
                    ["id"] = audit.Audit.Value,
                    ["headCommit"] = audit.HeadCommit,
                    ["requirementSetHash"] = audit.RequirementSetHash,
                    ["evidence"] = new JsonArray([.. audit.Evidence.Select(e => (JsonNode)Evidence(e, copies.GetValueOrDefault(e.Id), null))]),
                };
                break;
        }

        node["object"] = target;
        return node;
    }

    /// <summary>
    /// The dossier of Phase 3 of a run, in the dossier of the debate: whole, but without the hashes the verdict copies (those
    /// at the top are the ones that count) and, as an index, without its plan and the payloads of its entries.
    /// </summary>
    private static JsonObject RunNode(RunSubject run, DossierFiles files, bool index)
    {
        var node = JsonNode.Parse(DossierDocument.ToJson(run.Dossier, RunFiles(run, files)))!.AsObject();
        KeepOnlyAgentsThatWorked(node);
        node.Remove("decisionLogHeadHash");
        node.Remove("requirementSetHash");
        if (index)
        {
            node["plan"] = new JsonObject { ["file"] = DebateDossierBuilder.SubjectRunFile };
            foreach (var decision in node["decisions"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                decision.Remove("payload");
                decision.Remove("summary");
            }

            node["index"] = $"Index only: the plan and the payloads of the entries of this run are in {DebateDossierBuilder.SubjectRunFile}, in the package folder.";
        }

        return node;
    }

    /// <summary>
    /// The roster a run started with (<c>RunStarted</c>) names every agent of the project's template, the participants of a
    /// debate about the run among them, with their role, CLI and vendor. The dossier is blind (ADR-0029, section 7: only
    /// the producers are visible), so the roster keeps only the agents that worked in the run: those it launched or
    /// assigned a task to.
    /// </summary>
    private static void KeepOnlyAgentsThatWorked(JsonObject runNode)
    {
        if (runNode["decisions"] is not JsonArray decisions)
        {
            return;
        }

        var worked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions.OfType<JsonObject>())
        {
            if (KindOf(decision) is "AgentRunStarted" or "TaskAssigned" && decision["payload"] is JsonObject payload && TextOf(payload["agent"]) is { } agent)
            {
                worked.Add(agent);
            }
        }

        foreach (var decision in decisions.OfType<JsonObject>())
        {
            if (KindOf(decision) == "RunStarted" && decision["payload"] is JsonObject payload && payload["roster"] is JsonArray roster)
            {
                foreach (var entry in roster.OfType<JsonObject>().Where(e => TextOf(e["agent"]) is not { } agent || !worked.Contains(agent)).ToList())
                {
                    roster.Remove(entry);
                }
            }
        }
    }

    private static string? KindOf(JsonObject decision) => TextOf(decision["kind"]);

    private static string? TextOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DossierFiles RunFiles(RunSubject run, DossierFiles files) => new(files.Folder, files.ProjectId, run.Run.Value, files.Evidence, []);

    private static JsonObject Contribution(DebateContributionMaterial contribution, DossierEvidenceFile? copy) => new()
    {
        ["label"] = contribution.Label,
        ["sequence"] = contribution.Sequence,
        ["round"] = contribution.Round,
        ["author"] = contribution.Author,
        ["kind"] = contribution.Kind.ToString(),
        ["repliesTo"] = contribution.RepliesTo,
        ["evidence"] = contribution.Evidence.Value,
        ["sha256"] = contribution.Sha256,
        ["file"] = copy?.RelativePath,
        ["contentVerified"] = contribution.Text is not null && copy?.ContentVerified == true,
        ["answeredAt"] = contribution.AnsweredAt.ToString("O", CultureInfo.InvariantCulture),
        ["finished"] = contribution.Finished,
        ["redactions"] = contribution.Redactions,
        ["cites"] = Texts(contribution.Cites.Select(c => c.Value)),
        ["unresolvedCitations"] = Texts(contribution.UnresolvedCitations),
        ["content"] = contribution.Text is null ? null : JsonOrText(contribution.Text),
    };

    private static JsonObject Evidence(EvidenceRecord evidence, DossierEvidenceFile? copy, string? content) => new()
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
        ["content"] = content,
    };

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

    private static IEnumerable<T> Ordered<T>(IReadOnlyList<T> items, bool reversed) => reversed ? Enumerable.Reverse(items) : items;

    private static JsonArray Texts(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    /// <summary>A contribution is the JSON of its schema, embedded as JSON; anything else as a string.</summary>
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
