using System.Text.Json;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>
/// Where the user's skills index is: the path of its <c>catalog.json</c>. Null means no index is configured, and the
/// catalog is then empty (nothing is active by default anyway).
/// </summary>
public sealed record SkillCatalogOptions(string? CatalogPath);

/// <summary>The catalog, and what the user should know about it (in Spanish): what was skipped and why.</summary>
public sealed record SkillCatalogReport(IReadOnlyList<CatalogSkill> Skills, IReadOnlyList<string> Warnings);

/// <summary>
/// The skills index as a catalog of skills with their local folders (docs/13 section 3.6). The entries come from
/// <see cref="SkillsIndexCatalogImporter"/>; the folder of a vendored skill is where the index keeps its copy,
/// <c>&lt;index&gt;/domains/&lt;domain&gt;/skills/&lt;name&gt;/</c>, and it is only given when:
/// <list type="bullet">
/// <item>the domain and the name are plain folder names (no separators, no <c>..</c>);</item>
/// <item>the folder lies inside the index, and nothing on the way from the index to it is a link (junction or symbolic
/// link), so it cannot lead somewhere else;</item>
/// <item>it holds a <c>SKILL.md</c> that is a regular file.</item>
/// </list>
/// Otherwise, and for every linked skill (only a reference upstream), <see cref="CatalogSkill.LocalPath"/> is null and
/// the report says why: such a skill cannot be materialized for an agent.
/// </summary>
/// <remarks>
/// <see cref="ISkillCatalog"/> has no channel for warnings; <see cref="ListWithReportAsync"/> gives them, as
/// <see cref="ISkillCatalogImporter.ImportWithReportAsync"/> does for the import. The index is read on every call, so
/// a change to it is seen by the next run.
/// </remarks>
public sealed class SkillCatalog(ISkillCatalogImporter importer, SkillCatalogOptions options) : ISkillCatalog
{
    private const string SkillFile = "SKILL.md";

    public async Task<IReadOnlyList<CatalogSkill>> ListAsync(CancellationToken ct) =>
        (await ListWithReportAsync(ct).ConfigureAwait(false)).Skills;

    public async Task<SkillCatalogReport> ListWithReportAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.CatalogPath))
        {
            return new SkillCatalogReport([], ["No hay ningún índice de skills configurado: el catálogo está vacío."]);
        }

        var catalogPath = Path.GetFullPath(options.CatalogPath);
        if (!File.Exists(catalogPath))
        {
            return new SkillCatalogReport([], [$"No se encuentra el índice de skills «{catalogPath}»: el catálogo está vacío."]);
        }

        var root = Path.GetDirectoryName(catalogPath)!;
        var imported = await importer.ImportWithReportAsync(catalogPath, ct).ConfigureAwait(false);
        var domains = await ReadVendoredDomainsAsync(catalogPath, ct).ConfigureAwait(false);

        var warnings = new List<string>(imported.Warnings);
        var skills = new List<CatalogSkill>();
        foreach (var entry in imported.Entries)
        {
            if (entry.Source.Mode == SkillDistribution.Linked)
            {
                warnings.Add($"«{entry.Name}» está enlazada: el índice no guarda una copia local y no se puede dar a un agente.");
                skills.Add(new CatalogSkill(entry, null));
                continue;
            }

            var localPath = ResolveLocalPath(root, domains.GetValueOrDefault(entry.Name), entry.Name, out var problem);
            if (localPath is null)
            {
                warnings.Add($"«{entry.Name}»: {problem}; no se puede dar a un agente.");
            }

            skills.Add(new CatalogSkill(entry, localPath));
        }

        return new SkillCatalogReport(skills, warnings);
    }

    /// <summary>The domain of each vendored entry by its name, as the index wrote it (the first entry wins, as in the importer).</summary>
    private static async Task<Dictionary<string, string>> ReadVendoredDomainsAsync(string catalogPath, CancellationToken ct)
    {
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var stream = File.OpenRead(catalogPath);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("skills", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return domains;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "mode") == "vendor" && Text(item, "name") is { } name && Text(item, "domain") is { } domain)
                {
                    domains.TryAdd(name, domain);
                }
            }
        }

        return domains;
    }

    private static string? ResolveLocalPath(string root, string? domain, string name, out string problem)
    {
        if (domain is null)
        {
            problem = "el índice no dice en qué dominio guarda su copia";
            return null;
        }

        if (!IsPlainFolderName(domain) || !IsPlainFolderName(name))
        {
            problem = $"su dominio («{domain}») o su nombre no son un nombre de carpeta simple";
            return null;
        }

        string[] chain =
        [
            Path.Combine(root, "domains"),
            Path.Combine(root, "domains", domain),
            Path.Combine(root, "domains", domain, "skills"),
            Path.Combine(root, "domains", domain, "skills", name),
        ];
        var folder = Path.GetFullPath(chain[^1]);
        if (!IsInside(folder, root))
        {
            problem = "su carpeta queda fuera del índice";
            return null;
        }

        foreach (var step in chain)
        {
            var directory = new DirectoryInfo(step);
            if (!directory.Exists)
            {
                problem = $"no existe su copia local «{folder}»";
                return null;
            }

            if (IsLink(directory))
            {
                problem = $"«{step}» es un enlace y podría llevar fuera del índice";
                return null;
            }
        }

        var skillFile = new FileInfo(Path.Combine(folder, SkillFile));
        if (!skillFile.Exists || IsLink(skillFile))
        {
            problem = $"su copia local «{folder}» no tiene {SkillFile}";
            return null;
        }

        problem = string.Empty;
        return folder;
    }

    private static bool IsPlainFolderName(string segment) =>
        segment.Length > 0
        && segment is not ("." or "..")
        && segment.Trim() == segment
        && !segment.EndsWith('.')
        && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
