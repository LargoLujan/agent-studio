using System.ComponentModel;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using ModelContextProtocol;

namespace AgentStudio.SkillsMcp;

/// <summary>
/// The two tools of the server (docs/14 D-30), pure functions over the snapshot it was given: no file, no network and no
/// state, so a call can only read. Their answers carry catalog metadata only (<see cref="SkillCard"/>): never the body of
/// a SKILL.md, a folder or a source. Every input has a limit; one out of it is a tool error (<see cref="McpException"/>)
/// whose message the model reads, in Spanish like the prompt that drives it.
/// </summary>
internal sealed class SkillCatalogTools
{
    public const int DefaultSearchTop = 10;
    public const int MaxSearchTop = 20;
    public const int MaxSearchTags = 10;
    public const int DefaultRecommendTop = 5;
    public const int MaxRecommendTop = 10;
    public const int MaxContextTags = 20;

    private readonly SkillCatalogSnapshot _snapshot;
    private readonly IReadOnlyList<SkillEntry> _entries;
    private readonly Dictionary<string, SnapshotSkill> _byId;

    /// <exception cref="ArgumentException">A skill of the snapshot is not a valid catalog entry (a tag that cannot be one).</exception>
    public SkillCatalogTools(SkillCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _byId = snapshot.Skills.ToDictionary(s => s.Id, StringComparer.Ordinal);
        _entries = [.. snapshot.Skills.Select(ToEntry)];
    }

    public SearchSkillsAnswer SearchSkills(
        [Description("Palabras a buscar en el nombre, el resumen y las etiquetas, sin distinguir mayúsculas ni acentos; todas deben aparecer. Vacío lista todo. Hasta 200 caracteres.")] string query,
        [Description("Id de un sector para quedarse solo con sus skills (por ejemplo ingenieria-datos, escritorio, testing-qa). Opcional.")] string? sector = null,
        [Description("Etiquetas que la skill debe tener todas, sin #, por ejemplo [\"spark\", \"delta\"]. Hasta 10. Opcional.")] string[]? tags = null,
        [Description("Cuántas devolver, de 1 a 20. Por defecto 10.")] int top = DefaultSearchTop)
    {
        if (query is null || query.Length > SkillSearch.MaxQueryLength)
        {
            throw new McpException($"«query» es obligatorio y admite hasta {SkillSearch.MaxQueryLength} caracteres.");
        }

        if (sector is not null && SkillSectors.Find(sector) is null)
        {
            throw new McpException($"Sector desconocido «{sector}». Los sectores son: {string.Join(", ", SkillSectors.All.Select(s => s.Id))}.");
        }

        var required = Tags(tags, MaxSearchTags, "tags");
        if (top is < 1 or > MaxSearchTop)
        {
            throw new McpException($"«top» va de 1 a {MaxSearchTop}.");
        }

        var hits = SkillSearch.Search(_snapshot.Skills, query, sector, required, int.MaxValue);
        return new SearchSkillsAnswer(hits.Count, [.. hits.Take(top).Select(Card)]);
    }

    public RecommendSkillsAnswer RecommendSkills(
        [Description("La tarea para la que se buscan skills, en lenguaje natural. Hasta 2000 caracteres.")] string task,
        [Description("Etiquetas del contexto, sin #, por ejemplo el stack del proyecto: [\"dotnet\", \"csharp\"]. Hasta 20. Opcional.")] string[]? contextTags = null,
        [Description("Cuántas devolver, de 1 a 10. Por defecto 5.")] int top = DefaultRecommendTop)
    {
        if (string.IsNullOrWhiteSpace(task) || task.Length > SkillsMcpServer.MaxTaskLength)
        {
            throw new McpException($"«task» es obligatorio, no puede estar vacío y admite hasta {SkillsMcpServer.MaxTaskLength} caracteres.");
        }

        var context = Tags(contextTags, MaxContextTags, "contextTags");
        if (top is < 1 or > MaxRecommendTop)
        {
            throw new McpException($"«top» va de 1 a {MaxRecommendTop}.");
        }

        var recommendations = SkillRecommender.Recommend(new SkillQuery(task, context.ToHashSet(StringComparer.Ordinal), null), _entries, top);
        return new RecommendSkillsAnswer([.. recommendations.Select(r => new RecommendedSkill(Card(_byId[r.Skill.Id.Value]), r.Score, r.Reasons))]);
    }

    /// <summary>The tags of an argument, normalized; a list longer than <paramref name="max"/> or a value that cannot be a tag is an error.</summary>
    private static List<string> Tags(string[]? raw, int max, string argument)
    {
        if (raw is null)
        {
            return [];
        }

        if (raw.Length > max)
        {
            throw new McpException($"«{argument}» admite hasta {max} etiquetas.");
        }

        var tags = new List<string>(raw.Length);
        foreach (var value in raw)
        {
            if (!SkillTags.TryNormalize(value?.TrimStart('#'), out var tag))
            {
                throw new McpException($"«{value}» no es una etiqueta: solo letras minúsculas, dígitos y guiones.");
            }

            tags.Add(tag);
        }

        return tags;
    }

    private static SkillCard Card(SnapshotSkill skill) =>
        new(skill.Id, skill.Name, skill.Summary, skill.SectorId, skill.SectorTitle, skill.Tags, skill.Health.ToString(), skill.ActiveAgents, skill.Linked, skill.EstimatedContextTokens);

    /// <summary>
    /// The catalog entry the deterministic recommender scores. It reads the id, name, summary, sector and tags only, so the
    /// source is a placeholder: the snapshot never carries the real one.
    /// </summary>
    private static SkillEntry ToEntry(SnapshotSkill skill) =>
        new(
            new SkillId(skill.Id),
            skill.Name,
            skill.Summary,
            SkillSectors.Find(skill.SectorId) ?? new SkillSector(skill.SectorId, skill.SectorTitle),
            skill.Tags,
            new SkillSource("agentstudio-snapshot", skill.Id, null, null, null, skill.Linked ? SkillDistribution.Linked : SkillDistribution.Vendored));
}

/// <summary>
/// One skill as the tools describe it: catalog metadata, its local health (<c>Healthy</c>, <c>Unknown</c>,
/// <c>Warning</c> or <c>Critical</c>), the agents of the roster that have it active, whether it is linked (no local copy,
/// so it cannot be given to an agent) and its estimated context cost in tokens (characters / 4, an estimate).
/// </summary>
internal sealed record SkillCard(
    string Id,
    string Name,
    string Summary,
    string Sector,
    string SectorTitle,
    IReadOnlyList<string> Tags,
    string Health,
    IReadOnlyList<string> ActiveAgents,
    bool Linked,
    long EstimatedContextTokens);

/// <summary>The answer of <c>search_skills</c>: how many skills match and the first <c>top</c> of them, best first.</summary>
internal sealed record SearchSkillsAnswer(int Total, IReadOnlyList<SkillCard> Skills);

/// <summary>A skill of the deterministic recommendation, with its score and its reasons (Spanish).</summary>
internal sealed record RecommendedSkill(SkillCard Skill, double Score, IReadOnlyList<string> Reasons);

/// <summary>The answer of <c>recommend_skills</c>: the deterministic recommendation, best first; empty when nothing scores.</summary>
internal sealed record RecommendSkillsAnswer(IReadOnlyList<RecommendedSkill> Recommendations);
