using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentStudio.Application.Prompts;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;

namespace AgentStudio.Debate.Arena;

/// <summary>A contribution published before the turn: its label, index, round, author label, kind, the label it replies to, its SHA-256 and its text (the canonical JSON, read against that hash).</summary>
public sealed record PublishedContribution(string Label, int Index, int Round, string Author, ContributionKind Kind, string? RepliesTo, string Sha256, string Text);

/// <summary>A piece of evidence of the material, with its content when it could be read against the hash the log anchored.</summary>
public sealed record MaterialEvidence(EvidenceRecord Record, byte[]? Content);

/// <summary>
/// What one turn is given (docs/15 D-08 and D-17): its debate, mode and round, its own label and role, the question and the
/// frozen requirements, what the debate is about, the kinds the round admits, the contributions published before the round
/// and the evidence of the material. <paramref name="Participants"/> are the labels and roles of everyone, which round 1
/// leaves out: a proposal of round 1 knows nothing of the others.
/// </summary>
public sealed record DebateMaterialInput(
    RunId Debate,
    DebateMode Mode,
    int Round,
    string Label,
    DebateRole Role,
    IReadOnlyList<(string Label, DebateRole Role)> Participants,
    string Question,
    FrozenRequirementSet Requirements,
    DebateSubjectKind Subject,
    string? SubjectReference,
    string? SnapshotCommit,
    string? WorkingCopy,
    string? SubjectFolder,
    IReadOnlyList<ContributionKind> Kinds,
    IReadOnlyList<PublishedContribution> Contributions,
    IReadOnlyList<MaterialEvidence> Evidence);

/// <summary>
/// The material of one turn (docs/15 D-08): <c>debate.json</c> (format <c>agent-studio/debate-material/1</c>) in the folder of
/// the turn, with a file for each published contribution (<c>contributions\C1.json</c>, its canonical JSON, whose SHA-256 is
/// the one of its evidence) and for each evidence of the material that could be read (<c>evidence\001-….txt</c>). The same
/// JSON goes in the prompt, and the text of each contribution in its own block (<see cref="ParticipantPrompt"/>).
/// </summary>
/// <remarks>
/// Every file is new and nothing is written through a link (<see cref="DebateFolders"/>). The JSON is written as prompt data
/// (<see cref="PromptData"/>), so no text in it can close the block it travels in. It names no agent, CLI, vendor or model:
/// only labels and roles (D-15).
/// </remarks>
public static class DebateMaterial
{
    public const string Format = "agent-studio/debate-material/1";
    public const string FileName = "debate.json";

    /// <summary>Writes the files of the material in <paramref name="turnFolder"/> and returns the JSON of <c>debate.json</c>.</summary>
    public static async Task<string> WriteAsync(DebateFolders folders, string turnFolder, DebateMaterialInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnFolder);
        ArgumentNullException.ThrowIfNull(input);

        var contributionFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var contribution in input.Contributions)
        {
            var relative = "contributions/" + contribution.Label + ".json";
            await folders.WriteNewFileAsync(Path.Combine(turnFolder, "contributions", contribution.Label + ".json"), Encoding.UTF8.GetBytes(contribution.Text), cancellationToken).ConfigureAwait(false);
            contributionFiles[contribution.Label] = relative;
        }

        var evidenceFiles = new Dictionary<EvidenceId, string>();
        for (var i = 0; i < input.Evidence.Count; i++)
        {
            var (record, content) = (input.Evidence[i].Record, input.Evidence[i].Content);
            if (content is null)
            {
                continue;
            }

            var name = string.Create(CultureInfo.InvariantCulture, $"{i + 1:D3}-{Safe(record.Id.Value)}.txt");
            await folders.WriteNewFileAsync(Path.Combine(turnFolder, "evidence", name), content, cancellationToken).ConfigureAwait(false);
            evidenceFiles[record.Id] = "evidence/" + name;
        }

        var json = Json(input, contributionFiles, evidenceFiles);
        await folders.WriteNewFileAsync(Path.Combine(turnFolder, FileName), Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
        return json;
    }

    /// <summary>The JSON of the material, as prompt data.</summary>
    public static string Json(DebateMaterialInput input, IReadOnlyDictionary<string, string> contributionFiles, IReadOnlyDictionary<EvidenceId, string> evidenceFiles)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(contributionFiles);
        ArgumentNullException.ThrowIfNull(evidenceFiles);

        var material = new JsonObject
        {
            ["format"] = Format,
            ["debate"] = input.Debate.Value,
            ["mode"] = input.Mode.ToString(),
            ["purpose"] = DebateModes.Purpose(input.Mode),
            ["round"] = input.Round,
            ["you"] = new JsonObject { ["label"] = input.Label, ["role"] = input.Role.ToString() },
        };

        if (input.Round > 1)
        {
            material["participants"] = new JsonArray([.. input.Participants.Select(p => (JsonNode)new JsonObject { ["label"] = p.Label, ["role"] = p.Role.ToString() })]);
        }

        material["question"] = input.Question;
        material["requirementSetHash"] = input.Requirements.ContentHash;
        material["requirements"] = new JsonArray([.. input.Requirements.Requirements.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Id.Value,
            ["priority"] = r.Priority.ToString(),
            ["statement"] = r.Statement,
            ["criteria"] = new JsonArray([.. r.Criteria.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["method"] = c.Method.ToString(),
                ["description"] = c.Description,
            })]),
        })]);
        material["subject"] = new JsonObject
        {
            ["kind"] = input.Subject.ToString(),
            ["reference"] = input.SubjectReference,
            ["snapshotCommit"] = input.SnapshotCommit,
            ["workingCopy"] = input.WorkingCopy,
            ["folder"] = input.SubjectFolder,
        };
        material["kinds"] = new JsonArray([.. input.Kinds.Select(k => (JsonNode)JsonValue.Create(k.ToString()))]);
        material["contributions"] = new JsonArray([.. input.Contributions.Select(c => (JsonNode)new JsonObject
        {
            ["label"] = c.Label,
            ["author"] = c.Author,
            ["round"] = c.Round,
            ["kind"] = c.Kind.ToString(),
            ["repliesTo"] = c.RepliesTo,
            ["sha256"] = c.Sha256,
            ["file"] = contributionFiles.GetValueOrDefault(c.Label),
        })]);
        material["evidence"] = new JsonArray([.. input.Evidence.Select(e => (JsonNode)new JsonObject
        {
            ["id"] = e.Record.Id.Value,
            ["kind"] = e.Record.Kind.ToString(),
            ["origin"] = e.Record.Origin.ToString(),
            ["title"] = e.Record.Title,
            ["subjectCommit"] = e.Record.SubjectCommit,
            ["file"] = evidenceFiles.GetValueOrDefault(e.Record.Id),
        })]);
        return PromptData.Json(material);
    }

    private static string Safe(string text)
    {
        var safe = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return safe.Length > 48 ? safe[..48] : safe;
    }
}
