using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>
/// Imports the user's skills index (<c>catalog.json</c>: marketplace, domains, skills, mcp). Only <c>skills</c> is read.
/// Entries with mode <c>link</c> carry a GitHub <c>url</c> instead of repo/path; the repository, ref and path are
/// derived from it (only <c>https://github.com/...</c> is accepted; when the catalog also gives <c>ref</c> it is
/// used to split <c>.../tree/&lt;ref&gt;/&lt;path&gt;</c>, otherwise the first segment is taken as the ref and a
/// "ref ambigua" warning is added). Entries of mode <c>support</c> (shared references, not skills), entries without
/// name or repository, entries whose repo/path/ref/url fail validation, and entries that throw while being
/// processed are omitted and reported as warnings.
/// </summary>
public sealed partial class SkillsIndexCatalogImporter : ISkillCatalogImporter
{
    private const string Unclassified = "sin-clasificar";

    private static readonly HashSet<string> DataDomains = new(StringComparer.Ordinal)
    {
        "data-engineering", "microsoft-data", "data-analysis",
    };

    [GeneratedRegex(@"\b(power ?bi|dax|tmdl|semantic model|modelo sem[aá]ntico)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PowerBiRegex();

    [GeneratedRegex(@"\b(fastapi|node(js)?|backend|api)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackendRegex();

    [GeneratedRegex(@"\b(arquitectura|architecture|medallion)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();

    [GeneratedRegex(@"\b(documentation|documentaci[oó]n|docs?|writing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentationRegex();

    // owner/name with [A-Za-z0-9._-]; neither part starts with '-' or '.'.
    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._-]*/[A-Za-z0-9_][A-Za-z0-9._-]*\z")]
    private static partial Regex ValidRepo();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9._/+-]*\z")]
    private static partial Regex ValidRef();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public async Task<IReadOnlyList<SkillEntry>> ImportAsync(string catalogPath, CancellationToken ct) =>
        (await ImportWithReportAsync(catalogPath, ct).ConfigureAwait(false)).Entries;

    public async Task<SkillImportResult> ImportWithReportAsync(string catalogPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        var json = await File.ReadAllTextAsync(catalogPath, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var entries = new List<SkillEntry>();
        var warnings = new List<string>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("skills", out var skills)
            || skills.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("El catálogo no contiene la lista 'skills'.");
            return new SkillImportResult(entries, warnings);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in skills.EnumerateArray())
        {
            index++;
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = ImportItem(item, index, ids, warnings);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Entrada #{index} omitida: error inesperado ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        return new SkillImportResult(entries, warnings);
    }

    private static SkillEntry? ImportItem(JsonElement item, int index, HashSet<string> ids, List<string> warnings)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"Entrada #{index} omitida: no es un objeto.");
            return null;
        }

        var name = Str(item, "name");
        if (name is null)
        {
            warnings.Add($"Entrada #{index} omitida: sin nombre.");
            return null;
        }

        var mode = Str(item, "mode");
        if (mode is "support")
        {
            warnings.Add($"«{name}» omitida: es una referencia compartida, no una skill.");
            return null;
        }

        if (mode is not ("vendor" or "link"))
        {
            warnings.Add($"«{name}» omitida: modo desconocido '{mode}'.");
            return null;
        }

        var source = BuildSource(item, mode == "vendor" ? SkillDistribution.Vendored : SkillDistribution.Linked, name, warnings);
        if (source is null)
        {
            return null;
        }

        var slug = Slugify(name);
        if (slug.Length == 0)
        {
            warnings.Add($"«{name}» omitida: no se puede derivar un identificador.");
            return null;
        }

        if (!ids.Add(slug))
        {
            warnings.Add($"«{name}» omitida: identificador duplicado '{slug}'.");
            return null;
        }

        var summary = Str(item, "summary") ?? Str(item, "description") ?? string.Empty;
        var domain = Str(item, "domain");
        var explicitSector = ReadExplicitSector(item, name, warnings);
        var classified = true;
        var sector = explicitSector ?? MapSector(domain, name, summary, out classified);
        var tags = new HashSet<string>(SkillTagger.Derive(name, summary, sector, domain), StringComparer.Ordinal);
        if (!classified)
        {
            tags.Add(Unclassified);
        }

        AddExplicitTags(item, name, tags, warnings);

        return new SkillEntry(new SkillId(slug), name, summary, sector, tags, source);
    }

    // An explicit "sector" must be a valid sector id; otherwise it is reported and the domain mapping is used.
    private static SkillSector? ReadExplicitSector(JsonElement item, string name, List<string> warnings)
    {
        if (!item.TryGetProperty("sector", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var id = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        var sector = id is null ? null : SkillSectors.Find(id);
        if (sector is null)
        {
            warnings.Add($"«{name}»: sector inválido '{(id ?? value.ToString())}'; se usa el mapeo por dominio.");
        }

        return sector;
    }

    // Explicit "tags" (list of text) are added normalized; invalid ones are dropped with a warning.
    private static void AddExplicitTags(JsonElement item, string name, HashSet<string> tags, List<string> warnings)
    {
        if (!item.TryGetProperty("tags", out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"«{name}»: 'tags' no es una lista; se ignora.");
            return;
        }

        foreach (var raw in list.EnumerateArray())
        {
            var text = raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
            if (SkillTags.TryNormalize(text, out var tag))
            {
                tags.Add(tag);
            }
            else
            {
                warnings.Add($"«{name}»: etiqueta inválida descartada '{(text ?? raw.ToString())}'.");
            }
        }
    }

    /// <summary>Maps an index domain to a sector; <paramref name="classified"/> is false for unknown domains.</summary>
    internal static SkillSector MapSector(string? domain, string name, string summary, out bool classified)
    {
        classified = true;
        var text = name + " " + summary;
        var isData = domain is not null && DataDomains.Contains(domain);

        SkillSector baseSector;
        switch (domain)
        {
            case "data-engineering": baseSector = SkillSectors.IngenieriaDatos; break;
            case "microsoft-data":
                baseSector = PowerBiRegex().IsMatch(text) ? SkillSectors.AnaliticaBi : SkillSectors.IngenieriaDatos;
                break;
            case "data-analysis": baseSector = SkillSectors.CienciaDatos; break;
            case "web-development":
                baseSector = BackendRegex().IsMatch(text) ? SkillSectors.Backend : SkillSectors.Frontend;
                break;
            case "mobile-development": baseSector = SkillSectors.Movil; break;
            case "desktop-development": baseSector = SkillSectors.Escritorio; break;
            case "ux-ui-design": baseSector = SkillSectors.UxUi; break;
            case "cybersecurity": baseSector = SkillSectors.Ciberseguridad; break;
            case "devops-cloud": baseSector = SkillSectors.DevopsCloud; break;
            case "testing-qa": baseSector = SkillSectors.TestingQa; break;
            case "software-engineering": baseSector = SkillSectors.IngenieriaSoftware; break;
            case "ai-engineering": baseSector = SkillSectors.IaLlm; break;
            default:
                classified = false;
                return SkillSectors.IngenieriaSoftware;
        }

        if (ArchitectureRegex().IsMatch(text))
        {
            return isData ? SkillSectors.ArquitecturaDatos : SkillSectors.ArquitecturaSoftware;
        }

        if (domain is "software-engineering" or "ai-engineering" && DocumentationRegex().IsMatch(text))
        {
            return SkillSectors.Documentacion;
        }

        return baseSector;
    }

    // Returns null (after adding a warning) when the entry has no usable or valid source.
    private static SkillSource? BuildSource(JsonElement item, SkillDistribution mode, string name, List<string> warnings)
    {
        var repo = Str(item, "repo");
        var path = Str(item, "path") ?? string.Empty;
        var reference = Str(item, "ref");

        if (repo is null && Str(item, "url") is { } url)
        {
            var parsed = ParseGitHubUrl(url, reference, path, out var error, out var warning);
            if (parsed is null)
            {
                warnings.Add($"«{name}» omitida: {error}");
                return null;
            }

            if (warning is not null)
            {
                warnings.Add($"«{name}»: {warning}");
            }

            (repo, reference, path) = parsed.Value;
        }

        if (repo is null)
        {
            warnings.Add($"«{name}» omitida: sin repositorio.");
            return null;
        }

        if (!IsValidRepo(repo))
        {
            warnings.Add($"«{name}» omitida: repositorio inválido '{repo}' (se espera owner/nombre).");
            return null;
        }

        if (!IsValidPath(path))
        {
            warnings.Add($"«{name}» omitida: ruta inválida '{path}' (debe ser relativa, con '/' y sin '.' ni '..').");
            return null;
        }

        if (reference is not null && !IsValidRef(reference))
        {
            warnings.Add($"«{name}» omitida: ref inválida '{reference}'.");
            return null;
        }

        return new SkillSource(repo, path, reference, Str(item, "commit"), Str(item, "license"), mode);
    }

    // https://github.com/owner/repo[/tree/<ref>/<path>] -> (owner/repo, ref, path). Anything else is rejected.
    // A ref may contain '/', so with a catalog ref the URL must start with it; without one the first segment is
    // taken as the ref (ambiguous when a path follows) and a warning is returned.
    private static (string Repo, string? Ref, string Path)? ParseGitHubUrl(
        string url, string? reference, string path, out string error, out string? warning)
    {
        const string prefix = "https://github.com/";
        error = string.Empty;
        warning = null;
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"url no admitida '{url}' (solo https://github.com/...).";
            return null;
        }

        var parts = url[prefix.Length..].Trim('/').Split('/');
        if (parts.Length < 2)
        {
            error = $"url sin repositorio '{url}'.";
            return null;
        }

        var repo = parts[0] + "/" + parts[1];
        if (!IsValidRepo(repo))
        {
            error = $"repositorio inválido en la url '{url}'.";
            return null;
        }

        if (parts.Length < 4 || parts[2] != "tree")
        {
            return (repo, reference, path);
        }

        var rest = string.Join('/', parts.Skip(3));
        if (reference is not null)
        {
            if (rest != reference && !rest.StartsWith(reference + "/", StringComparison.Ordinal))
            {
                error = $"la ref '{reference}' del catálogo no coincide con la url '{url}'.";
                return null;
            }

            return (repo, reference, rest.Length > reference.Length ? rest[(reference.Length + 1)..] : string.Empty);
        }

        if (parts.Length > 4)
        {
            warning = $"ref ambigua en la url '{url}': se toma '{parts[3]}' como ref y el resto como ruta.";
        }

        return (repo, parts[3], string.Join('/', parts.Skip(4)));
    }

    private static bool IsValidRepo(string repo) => ValidRepo().IsMatch(repo);

    // Relative, '/'-separated, no empty/'.'/'..' segments; no root, drive, backslash, query/fragment, escapes or control chars.
    private static bool IsValidPath(string path)
    {
        if (path.Length == 0)
        {
            return true;
        }

        if (path.Any(c => char.IsControl(c) || c is '\\' or ':' or '%' or '?' or '#'))
        {
            return false;
        }

        return path.Split('/').All(seg => seg.Length > 0 && seg is not ("." or ".."));
    }

    private static bool IsValidRef(string reference) =>
        ValidRef().IsMatch(reference)
        && reference.Split('/').All(seg => seg.Length > 0 && seg is not ("." or "..") && !seg.Contains("..", StringComparison.Ordinal));

    private static string Slugify(string name)
    {
        var text = RemoveDiacritics(name);
        return NonSlug().Replace(text, "-").Trim('-');
    }

    private static string RemoveDiacritics(string name)
    {
        var decomposed = name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
    }

    private static string? Str(JsonElement item, string property) =>
        item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;
}
