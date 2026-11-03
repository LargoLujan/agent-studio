using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Application.Skills;

/// <summary>
/// What the local MCP server of the assisted recommendation reads (docs/14 D-30), built from the catalog, the activation
/// decisions that apply to the project, the agents of its roster and the local health of each skill. Catalog metadata
/// only: never the body of a SKILL.md, its folder or its source. Pure: the caller reads the catalog and the health.
/// </summary>
public static class SkillCatalogSnapshotBuilder
{
    /// <summary>
    /// The snapshot of <paramref name="catalog"/>: the skills in the order of <see cref="SkillSectors.All"/> (a sector it
    /// does not know goes last), then by name and id; the first entry of an id wins.
    /// </summary>
    /// <param name="at">When it was taken.</param>
    /// <param name="projectId">The project whose project-scope decisions apply; null when no project is open (only the global and agent ones do).</param>
    /// <param name="catalog">The catalog, with the local folder of each skill (null for one that cannot be given to an agent).</param>
    /// <param name="activations">Every decision that applies to the project, oldest first (<see cref="ISkillActivationStore.ListAsync"/>).</param>
    /// <param name="roster">The agents of the project's roster: <see cref="SnapshotSkill.ActiveAgents"/> lists those it is active for.</param>
    /// <param name="health">The local health of each skill read so far; a skill missing here is <see cref="SkillHealthStatus.Unknown"/>.</param>
    public static SkillCatalogSnapshot Build(
        DateTimeOffset at,
        string? projectId,
        IReadOnlyList<CatalogSkill> catalog,
        IReadOnlyList<SkillActivation> activations,
        IReadOnlyList<AgentId> roster,
        IReadOnlyDictionary<SkillId, SkillHealthStatus> health)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(activations);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(health);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skills = catalog
            .Where(skill => skill is not null && seen.Add(skill.Entry.Id.Value))
            .OrderBy(skill => SectorOrder(skill.Entry.Sector))
            .ThenBy(skill => skill.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.Entry.Id.Value, StringComparer.Ordinal)
            .Select(skill => Describe(skill, projectId, roster, activations, health.GetValueOrDefault(skill.Entry.Id, SkillHealthStatus.Unknown)))
            .ToList();
        return new SkillCatalogSnapshot(at, skills);
    }

    /// <summary>
    /// One skill as the server sees it: its tags in ordinal order, the agents of <paramref name="roster"/> that
    /// <see cref="SkillActivationResolver"/> enables it for (in ordinal order), whether it lacks a local copy (then it
    /// cannot be given to an agent) and its estimated context cost (<see cref="SkillContextCost"/>).
    /// </summary>
    public static SnapshotSkill Describe(CatalogSkill skill, string? projectId, IReadOnlyList<AgentId> roster, IReadOnlyList<SkillActivation> activations, SkillHealthStatus health)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(activations);

        var entry = skill.Entry;
        var active = roster
            .Select(agent => agent.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(agent => SkillActivationResolver.IsEnabled(entry.Id, projectId, agent, activations))
            .ToList();
        return new SnapshotSkill(
            entry.Id.Value,
            entry.Name,
            entry.Summary,
            entry.Sector.Id,
            entry.Sector.Title,
            [.. entry.Tags.Order(StringComparer.Ordinal)],
            health,
            active,
            skill.LocalPath is null,
            SkillContextCost.Of(entry).Value ?? 0);
    }

    private static int SectorOrder(SkillSector sector)
    {
        for (var i = 0; i < SkillSectors.All.Count; i++)
        {
            if (string.Equals(SkillSectors.All[i].Id, sector.Id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return SkillSectors.All.Count;
    }
}

/// <summary>
/// The file format of a <see cref="SkillCatalogSnapshot"/>, shared by its writer (Agent Studio, with <c>CreateNew</c> in
/// the folder of the run) and its reader (the MCP server): one JSON object with <c>schema</c> set to <see cref="Schema"/>,
/// <c>at</c> and <c>skills</c>, names in camel case and the health as text. Reading is strict: anything that is not a
/// complete snapshot is an <see cref="InvalidDataException"/>.
/// </summary>
public static class SkillCatalogSnapshotJson
{
    /// <summary>The format and its version.</summary>
    public const string Schema = "agentstudio-skills-snapshot/1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static string Write(SkillCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(new SnapshotFile(Schema, snapshot.At, snapshot.Skills), Options);
    }

    /// <exception cref="InvalidDataException">The text is not a complete snapshot of this format.</exception>
    public static SkillCatalogSnapshot Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        SnapshotFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SnapshotFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The skills snapshot is not valid JSON of its format: " + ex.Message, ex);
        }

        if (file is null || !string.Equals(file.Schema, Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The skills snapshot does not declare the format '{Schema}'.");
        }

        if (file.At == default || file.Skills is null)
        {
            throw new InvalidDataException("The skills snapshot has no time or no list of skills.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < file.Skills.Count; i++)
        {
            var skill = file.Skills[i];
            if (!IsComplete(skill))
            {
                throw new InvalidDataException($"Skill {i} of the snapshot is incomplete or out of range.");
            }

            if (!ids.Add(skill!.Id))
            {
                throw new InvalidDataException($"The snapshot lists the skill '{skill.Id}' twice.");
            }
        }

        return new SkillCatalogSnapshot(file.At, file.Skills);
    }

    private static bool IsComplete(SnapshotSkill? skill) =>
        skill is not null
        && !string.IsNullOrWhiteSpace(skill.Id)
        && !string.IsNullOrWhiteSpace(skill.Name)
        && skill.Summary is not null
        && !string.IsNullOrWhiteSpace(skill.SectorId)
        && !string.IsNullOrWhiteSpace(skill.SectorTitle)
        && skill.Tags is not null
        && skill.Tags.All(tag => !string.IsNullOrWhiteSpace(tag))
        && skill.ActiveAgents is not null
        && skill.ActiveAgents.All(agent => !string.IsNullOrWhiteSpace(agent))
        && Enum.IsDefined(skill.Health)
        && skill.EstimatedContextTokens >= 0;

    private sealed record SnapshotFile(string? Schema, DateTimeOffset At, IReadOnlyList<SnapshotSkill>? Skills);
}

/// <summary>
/// The deterministic search over the catalog metadata, shared by the catalog screen and the <c>search_skills</c> tool of
/// the MCP server (docs/14 D-26 and D-30). Every word of the query (lower case, without accents, a leading <c>#</c>
/// dropped) must appear in the name, the summary or a tag. Ranking: 3 points per word found in a tag, 2 in the name and 1
/// in the summary; ties by name and then id. The optional filters keep one sector and the skills that have every tag.
/// </summary>
public static class SkillSearch
{
    /// <summary>The longest query the search takes; the MCP tool refuses a longer one.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>True when every word of <paramref name="query"/> appears in the name, summary or tags; a blank query matches all.</summary>
    public static bool Matches(SnapshotSkill skill, string? query)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return Score(skill, Terms(query)) is not null;
    }

    /// <summary>The skills that pass the query and the filters, best first, at most <paramref name="top"/>.</summary>
    /// <param name="sectorId">A sector id (<see cref="SkillSector.Id"/>) to keep, or null for every sector.</param>
    /// <param name="tags">Tags a skill must all have (normalized here; one that cannot be a tag matches nothing), or null.</param>
    public static IReadOnlyList<SnapshotSkill> Search(IEnumerable<SnapshotSkill> skills, string? query, string? sectorId, IReadOnlyCollection<string>? tags, int top)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (top <= 0)
        {
            return [];
        }

        var required = new List<string>();
        foreach (var raw in tags ?? [])
        {
            if (!SkillTags.TryNormalize(raw, out var tag))
            {
                return [];
            }

            required.Add(tag);
        }

        var terms = Terms(query);
        return skills
            .Where(skill => sectorId is null || string.Equals(skill.SectorId, sectorId, StringComparison.Ordinal))
            .Where(skill => required.All(tag => skill.Tags.Contains(tag, StringComparer.Ordinal)))
            .Select(skill => (Skill: skill, Score: Score(skill, terms)))
            .Where(hit => hit.Score is not null)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(hit => hit.Skill.Id, StringComparer.Ordinal)
            .Take(top)
            .Select(hit => hit.Skill)
            .ToList();
    }

    /// <summary>The points of a skill for the words, or null when one of them appears nowhere.</summary>
    private static int? Score(SnapshotSkill skill, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return 0;
        }

        var name = Fold(skill.Name);
        var summary = Fold(skill.Summary);
        var tags = skill.Tags.Select(Fold).ToList();
        var score = 0;
        foreach (var term in terms)
        {
            var inTag = tags.Any(tag => tag.Contains(term, StringComparison.Ordinal));
            var inName = name.Contains(term, StringComparison.Ordinal);
            var inSummary = summary.Contains(term, StringComparison.Ordinal);
            if (!inTag && !inName && !inSummary)
            {
                return null;
            }

            score += (inTag ? 3 : 0) + (inName ? 2 : 0) + (inSummary ? 1 : 0);
        }

        return score;
    }

    private static List<string> Terms(string? query) =>
        [.. Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.TrimStart('#'))
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Lower case without diacritics; anything but a letter, a digit, <c>-</c> or <c>#</c> becomes a space.</summary>
    private static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(char.IsLetterOrDigit(c) || c is '-' or '#' ? char.ToLowerInvariant(c) : ' ');
        }

        return folded.ToString();
    }
}
