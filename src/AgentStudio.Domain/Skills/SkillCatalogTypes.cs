using System.Text.RegularExpressions;

namespace AgentStudio.Domain.Skills;

// Pure catalog model for third-party skills (no I/O). Skills are never active by default: activation is an
// explicit, scoped decision (see SkillActivationResolver).

/// <summary>Strongly typed skill identifier (a kebab-case slug). Rejects empty values.</summary>
public readonly record struct SkillId
{
    public SkillId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A folder of the catalog: a sector of the IT industry. <see cref="Title"/> is shown to the user.</summary>
public sealed record SkillSector(string Id, string Title);

/// <summary>The fixed taxonomy of sectors (catalog folders).</summary>
public static class SkillSectors
{
    public static readonly SkillSector ArquitecturaSoftware = new("arquitectura-software", "Arquitectura de software");
    public static readonly SkillSector ArquitecturaDatos = new("arquitectura-datos", "Arquitectura de datos");
    public static readonly SkillSector IngenieriaDatos = new("ingenieria-datos", "Ingeniería de datos");
    public static readonly SkillSector CienciaDatos = new("ciencia-datos", "Ciencia de datos");
    public static readonly SkillSector AnaliticaBi = new("analitica-bi", "Analítica y BI");
    public static readonly SkillSector UxUi = new("ux-ui", "UX/UI");
    public static readonly SkillSector Frontend = new("frontend", "Frontend");
    public static readonly SkillSector Backend = new("backend", "Backend");
    public static readonly SkillSector Movil = new("movil", "Desarrollo móvil");
    public static readonly SkillSector Escritorio = new("escritorio", "Desarrollo de escritorio");
    public static readonly SkillSector DevopsCloud = new("devops-cloud", "DevOps y cloud");
    public static readonly SkillSector Ciberseguridad = new("ciberseguridad", "Ciberseguridad");
    public static readonly SkillSector TestingQa = new("testing-qa", "Testing y QA");
    public static readonly SkillSector IaLlm = new("ia-llm", "IA y LLM");
    public static readonly SkillSector IngenieriaSoftware = new("ingenieria-software", "Ingeniería de software");
    public static readonly SkillSector Documentacion = new("documentacion", "Documentación");

    public static IReadOnlyList<SkillSector> All { get; } =
    [
        ArquitecturaSoftware, ArquitecturaDatos, IngenieriaDatos, CienciaDatos, AnaliticaBi, UxUi, Frontend, Backend,
        Movil, Escritorio, DevopsCloud, Ciberseguridad, TestingQa, IaLlm, IngenieriaSoftware, Documentacion,
    ];

    /// <summary>Returns the sector with that id, or null.</summary>
    public static SkillSector? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Returns the sector with that id or throws.</summary>
    public static SkillSector Get(string id) =>
        Find(id) ?? throw new ArgumentException($"Unknown skill sector '{id}'.", nameof(id));
}

/// <summary>Whether the skill's files are copied locally or only referenced upstream.</summary>
public enum SkillDistribution
{
    Vendored,
    Linked,
}

/// <summary>Where a skill comes from. <c>PinnedCommit</c> is the reviewed upstream commit (40 hex characters).</summary>
public sealed record SkillSource(
    string Repository,
    string Path,
    string? Ref,
    string? PinnedCommit,
    string? License,
    SkillDistribution Mode);

/// <summary>Tag normalization: lower-case kebab tokens matching <c>[a-z0-9]+(-[a-z0-9]+)*</c>.</summary>
public static partial class SkillTags
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Valid();

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Separators();

    /// <summary>Normalizes a raw tag; returns false (and an empty tag) when it is null, blank or cannot be valid.</summary>
    public static bool TryNormalize(string? raw, out string tag)
    {
        tag = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = Separators().Replace(raw.Trim().ToLowerInvariant(), "-");
        if (!Valid().IsMatch(candidate))
        {
            return false;
        }

        tag = candidate;
        return true;
    }

    /// <summary>Normalizes a raw tag or throws <see cref="ArgumentException"/> when it cannot be valid.</summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var tag = Separators().Replace(raw.Trim().ToLowerInvariant(), "-");
        if (!Valid().IsMatch(tag))
        {
            throw new ArgumentException($"Invalid skill tag '{raw}'.", nameof(raw));
        }

        return tag;
    }
}

/// <summary>A catalog entry. Tags are normalized on construction.</summary>
public sealed record SkillEntry
{
    public SkillEntry(
        SkillId id,
        string name,
        string summary,
        SkillSector sector,
        IEnumerable<string> tags,
        SkillSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(sector);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(source);
        Id = id;
        Name = name;
        Summary = summary;
        Sector = sector;
        Tags = tags.Select(SkillTags.Normalize).ToHashSet(StringComparer.Ordinal);
        Source = source;
    }

    public SkillId Id { get; }

    public string Name { get; }

    public string Summary { get; }

    public SkillSector Sector { get; }

    public IReadOnlySet<string> Tags { get; }

    public SkillSource Source { get; }
}
