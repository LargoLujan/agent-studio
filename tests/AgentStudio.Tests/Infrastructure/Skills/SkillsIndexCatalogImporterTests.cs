using AgentStudio.Application.Skills;
using System.IO;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Skills;

public sealed class SkillsIndexCatalogImporterTests : IDisposable
{
    // Same shape as the real catalog: vendored entries carry repo/path/commit/license, linked ones a url.
    private const string FixtureJson = """
    {
      "marketplace": "test",
      "domains": { "data-engineering": { "title": "DE", "scope": "x" } },
      "skills": [
        { "domain": "data-engineering", "name": "spark-performance", "repo": "o/data", "path": "skills/spark-performance", "ref": "main", "license": "MIT", "mode": "vendor", "summary": "Tuning de PySpark y Delta.", "commit": "8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4" },
        { "domain": "microsoft-data", "name": "dax-model-review", "repo": "o/ms", "path": "dax", "license": "MIT", "mode": "vendor", "summary": "Revisa el modelo semántico y las medidas DAX.", "commit": "6c11ad58c25992e5d1435ce7cd80d217d5598a31" },
        { "domain": "microsoft-data", "name": "fabric-lakehouse", "repo": "o/ms", "path": "lh", "license": "MIT", "mode": "vendor", "summary": "Lakehouses en Fabric." },
        { "domain": "microsoft-data", "name": "fabric-medallion", "repo": "o/ms", "path": "med", "mode": "vendor", "summary": "Arquitectura medallion bronce/plata/oro." },
        { "domain": "web-development", "name": "nextjs-app-router", "repo": "o/web", "path": "next", "license": "MIT", "mode": "vendor", "summary": "App Router de Next.js." },
        { "domain": "web-development", "name": "fastapi-patterns", "repo": "o/web", "path": "fastapi", "license": "Apache-2.0", "mode": "vendor", "summary": "Servicios con FastAPI." },
        { "domain": "software-engineering", "name": "clean-design", "repo": "o/se", "path": "cd", "license": "MIT", "mode": "vendor", "summary": "Software architecture y límites de módulos." },
        { "domain": "software-engineering", "name": "technical-writing", "repo": "o/se", "path": "tw", "license": "MIT", "mode": "vendor", "summary": "Escribir docs claras." },
        { "domain": "ux-ui-design", "name": "design-critique", "repo": "o/ux", "path": "dc", "license": "MIT", "mode": "vendor", "summary": "Crítica de diseño." },
        { "domain": "mystery-domain", "name": "mystery", "repo": "o/x", "path": "m", "mode": "vendor", "summary": "Sin dominio conocido." },
        { "domain": "desktop-development", "name": "tauri (guía)", "mode": "link", "summary": "Apps con Tauri.", "url": "https://github.com/acme/tauri-skills/tree/dev/skills/tauri" },
        { "domain": "desktop-development", "name": "nameless-repo", "mode": "link", "summary": "Sin repo ni url." },
        { "summary": "no name", "repo": "o/x", "mode": "vendor", "domain": "testing-qa" },
        { "domain": "microsoft-data", "name": "fabric-common", "repo": "o/ms", "path": "common", "mode": "support", "summary": "Shared references." }
      ],
      "mcp": []
    }
    """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agentstudio-skills-" + Guid.NewGuid().ToString("N"));

    public SkillsIndexCatalogImporterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TempFolders.Delete(_dir);

    private async Task<(IReadOnlyList<SkillEntry> Entries, IReadOnlyList<string> Warnings)> ImportFixtureAsync()
    {
        var path = Path.Combine(_dir, "catalog.json");
        await File.WriteAllTextAsync(path, FixtureJson, TestContext.Current.CancellationToken);
        var result = await new SkillsIndexCatalogImporter().ImportWithReportAsync(path, TestContext.Current.CancellationToken);
        return (result.Entries, result.Warnings);
    }

    private static SkillEntry Get(IReadOnlyList<SkillEntry> entries, string name) => entries.Single(e => e.Name == name);

    [Fact]
    public async Task Import_MapsDomainsToSectors()
    {
        var (entries, _) = await ImportFixtureAsync();

        Assert.Equal("ingenieria-datos", Get(entries, "spark-performance").Sector.Id);
        Assert.Equal("analitica-bi", Get(entries, "dax-model-review").Sector.Id);
        Assert.Equal("ingenieria-datos", Get(entries, "fabric-lakehouse").Sector.Id);
        Assert.Equal("arquitectura-datos", Get(entries, "fabric-medallion").Sector.Id);
        Assert.Equal("frontend", Get(entries, "nextjs-app-router").Sector.Id);
        Assert.Equal("backend", Get(entries, "fastapi-patterns").Sector.Id);
        Assert.Equal("arquitectura-software", Get(entries, "clean-design").Sector.Id);
        Assert.Equal("documentacion", Get(entries, "technical-writing").Sector.Id);
        Assert.Equal("ux-ui", Get(entries, "design-critique").Sector.Id);
        Assert.Equal("escritorio", Get(entries, "tauri (guía)").Sector.Id);
    }

    [Fact]
    public async Task Import_UnknownDomain_FallsBackAndTagsSinClasificar()
    {
        var (entries, _) = await ImportFixtureAsync();
        var mystery = Get(entries, "mystery");
        Assert.Equal("ingenieria-software", mystery.Sector.Id);
        Assert.Contains("sin-clasificar", mystery.Tags);
        Assert.DoesNotContain("sin-clasificar", Get(entries, "spark-performance").Tags);
    }

    [Fact]
    public async Task Import_KeepsSourceMetadata()
    {
        var (entries, _) = await ImportFixtureAsync();
        var spark = Get(entries, "spark-performance");
        Assert.Equal(new SkillId("spark-performance"), spark.Id);
        Assert.Equal("o/data", spark.Source.Repository);
        Assert.Equal("skills/spark-performance", spark.Source.Path);
        Assert.Equal("main", spark.Source.Ref);
        Assert.Equal("8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4", spark.Source.PinnedCommit);
        Assert.Equal("MIT", spark.Source.License);
        Assert.Equal(SkillDistribution.Vendored, spark.Source.Mode);
        Assert.Contains("pyspark", spark.Tags);
        Assert.Contains("delta", spark.Tags);
        Assert.Contains("ingenieria-datos", spark.Tags);
    }

    [Fact]
    public async Task Import_LinkedEntry_DerivesRepositoryRefAndPathFromUrl()
    {
        var (entries, _) = await ImportFixtureAsync();
        var tauri = Get(entries, "tauri (guía)");
        Assert.Equal(new SkillId("tauri-guia"), tauri.Id);
        Assert.Equal(SkillDistribution.Linked, tauri.Source.Mode);
        Assert.Equal("acme/tauri-skills", tauri.Source.Repository);
        Assert.Equal("dev", tauri.Source.Ref);
        Assert.Equal("skills/tauri", tauri.Source.Path);
        Assert.Null(tauri.Source.PinnedCommit);
        Assert.Contains("tauri", tauri.Tags);
    }

    [Fact]
    public async Task Import_OmitsInvalidEntriesAndReportsWarnings()
    {
        var (entries, warnings) = await ImportFixtureAsync();

        Assert.Equal(11, entries.Count);
        Assert.Equal(4, warnings.Count); // + "ref ambigua" for the tauri tree URL
        Assert.Contains(warnings, w => w.Contains("tauri") && w.Contains("ref ambigua"));
        Assert.Contains(warnings, w => w.Contains("nameless-repo") && w.Contains("repositorio"));
        Assert.Contains(warnings, w => w.Contains("sin nombre"));
        Assert.Contains(warnings, w => w.Contains("fabric-common"));
        Assert.All(entries, e => Assert.NotEmpty(e.Tags));
    }

    [Fact]
    public async Task ImportAsync_ReturnsTheSameEntries()
    {
        var path = Path.Combine(_dir, "catalog.json");
        await File.WriteAllTextAsync(path, FixtureJson, TestContext.Current.CancellationToken);
        var entries = await new SkillsIndexCatalogImporter().ImportAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(11, entries.Count);
    }

    [Fact]
    public async Task Import_CatalogWithoutSkills_WarnsInsteadOfThrowing()
    {
        var path = Path.Combine(_dir, "empty.json");
        await File.WriteAllTextAsync(path, "{ \"marketplace\": \"x\" }", TestContext.Current.CancellationToken);
        var result = await new SkillsIndexCatalogImporter().ImportWithReportAsync(path, TestContext.Current.CancellationToken);
        Assert.Empty(result.Entries);
        Assert.Single(result.Warnings);
    }

    // ---- review fixes ----

    private async Task<SkillImportResult> ImportJsonAsync(string skillsJson)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{ \"skills\": [" + skillsJson + "] }", TestContext.Current.CancellationToken);
        return await new SkillsIndexCatalogImporter().ImportWithReportAsync(path, TestContext.Current.CancellationToken);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string Vendor(string name, string repo, string path, string? reference = null) =>
        "{ \"domain\": \"testing-qa\", \"name\": " + Json(name) + ", \"repo\": " + Json(repo) + ", \"path\": " + Json(path)
        + (reference is null ? string.Empty : ", \"ref\": " + Json(reference))
        + ", \"mode\": \"vendor\", \"summary\": \"s\" }";

    private static string Link(string name, string url, string? reference = null) =>
        "{ \"domain\": \"testing-qa\", \"name\": " + Json(name) + ", \"url\": " + Json(url)
        + (reference is null ? string.Empty : ", \"ref\": " + Json(reference))
        + ", \"mode\": \"link\", \"summary\": \"s\" }";

    [Fact]
    public async Task Import_AnInvalidSourceDomain_DoesNotAbortTheImport()
    {
        var result = await ImportJsonAsync(
            "{ \"domain\": \"Web Dev!\", \"name\": \"odd-domain\", \"repo\": \"o/r\", \"path\": \"p\", \"mode\": \"vendor\", \"summary\": \"s\" },"
            + Vendor("good", "o/r", "g"));

        Assert.Contains(result.Entries, e => e.Name == "good");
        Assert.Contains(result.Entries, e => e.Name == "odd-domain");
    }

    [Fact]
    public async Task Import_WrongTypedFields_DoNotAbortTheImport()
    {
        var result = await ImportJsonAsync(
            "{ \"name\": \"bad\", \"repo\": \"o/r\", \"path\": \"p\", \"mode\": \"vendor\", \"summary\": 5, \"domain\": 7 },"
            + Vendor("good", "o/r", "g"));

        Assert.Contains(result.Entries, e => e.Name == "good");
    }

    [Theory]
    [InlineData("-owner/repo")]
    [InlineData("owner/-repo")]
    [InlineData(".owner/repo")]
    [InlineData("owner/.repo")]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/re po")]
    [InlineData("../repo")]
    public async Task Import_InvalidRepo_IsOmittedWithAWarning(string repo)
    {
        var result = await ImportJsonAsync(Vendor("bad", repo, "p") + "," + Vendor("good", "o/r", "g"));

        Assert.Equal("good", Assert.Single(result.Entries).Name);
        Assert.Contains(result.Warnings, w => w.Contains("bad") && w.Contains("repositorio"));
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    [InlineData("C:/x")]
    [InlineData("a//b")]
    public async Task Import_InvalidPath_IsOmittedWithAWarning(string path)
    {
        var result = await ImportJsonAsync(Vendor("bad", "o/r", path) + "," + Vendor("good", "o/r", "g"));

        Assert.Equal("good", Assert.Single(result.Entries).Name);
        Assert.Contains(result.Warnings, w => w.Contains("bad") && w.Contains("ruta"));
    }

    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("http://github.com/o/r")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://github.com.evil.example/o/r")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/-o/r")]
    [InlineData("https://github.com/o/r/tree/main/../../x")]
    public async Task Import_InvalidUrl_IsOmittedWithAWarning(string url)
    {
        var result = await ImportJsonAsync(Link("bad", url) + "," + Link("good", "https://github.com/o/r"));

        Assert.Equal("good", Assert.Single(result.Entries).Name);
        Assert.Contains(result.Warnings, w => w.Contains("bad"));
    }

    [Fact]
    public async Task Import_InvalidRef_IsOmittedWithAWarning()
    {
        var result = await ImportJsonAsync(Vendor("bad", "o/r", "p", "../main") + "," + Vendor("good", "o/r", "g", "feature/x"));

        Assert.Equal("good", Assert.Single(result.Entries).Name);
        Assert.Equal("feature/x", result.Entries[0].Source.Ref);
        Assert.Contains(result.Warnings, w => w.Contains("bad") && w.Contains("ref"));
    }

    [Fact]
    public async Task Import_TreeUrlWithACatalogRefContainingASlash_SplitsRefAndPathUsingIt()
    {
        var result = await ImportJsonAsync(Link("slashy", "https://github.com/o/r/tree/feature/x/skills/foo", "feature/x"));

        var source = Assert.Single(result.Entries).Source;
        Assert.Equal("o/r", source.Repository);
        Assert.Equal("feature/x", source.Ref);
        Assert.Equal("skills/foo", source.Path);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Import_TreeUrlWithoutACatalogRef_TakesTheFirstSegmentAndWarnsAmbiguousRef()
    {
        var result = await ImportJsonAsync(Link("guess", "https://github.com/o/r/tree/feature/x/skills/foo"));

        var source = Assert.Single(result.Entries).Source;
        Assert.Equal("feature", source.Ref);
        Assert.Equal("x/skills/foo", source.Path);
        Assert.Contains(result.Warnings, w => w.Contains("guess") && w.Contains("ref ambigua"));
    }

    [Fact]
    public async Task Import_TreeUrlWhoseRefDoesNotMatchTheCatalogRef_IsOmitted()
    {
        var result = await ImportJsonAsync(Link("mismatch", "https://github.com/o/r/tree/main/skills/foo", "dev"));

        Assert.Empty(result.Entries);
        Assert.Contains(result.Warnings, w => w.Contains("mismatch") && w.Contains("ref"));
    }

    // ---- real catalog (local tools only) ----

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task RealCatalog_ImportsAllSkillsAndParsesVendoredFrontmatter()
    {
        const string root = "C:/skills-index";
        var catalog = Path.Combine(root, "catalog.json");
        Assert.SkipUnless(File.Exists(catalog), "The local skills index is not available.");

        var result = await new SkillsIndexCatalogImporter().ImportWithReportAsync(catalog, TestContext.Current.CancellationToken);
        var entries = result.Entries;

        Assert.True(entries.Count >= 119, $"Expected at least 119 entries, got {entries.Count}.");
        Assert.All(entries, e => Assert.NotNull(e.Sector));
        Assert.All(entries, e => Assert.NotEmpty(e.Tags));

        var perSector = string.Join(", ", entries
            .GroupBy(e => e.Sector.Id)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}"));
        TestContext.Current.SendDiagnosticMessage($"Imported {entries.Count} skills. Per sector: {perSector}. Warnings: {result.Warnings.Count}.");

        var parsed = 0;
        var valid = 0;
        var invalid = new List<string>();
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(catalog, TestContext.Current.CancellationToken));
        foreach (var item in doc.RootElement.GetProperty("skills").EnumerateArray())
        {
            if (!item.TryGetProperty("mode", out var mode) || mode.GetString() != "vendor")
            {
                continue;
            }

            var name = item.GetProperty("name").GetString()!;
            var domain = item.GetProperty("domain").GetString()!;
            var file = Path.Combine(root, "domains", domain, "skills", name, "SKILL.md");
            if (!File.Exists(file))
            {
                continue;
            }

            var frontmatter = SkillFrontmatter.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.True(frontmatter.HasHeader, $"{name}: SKILL.md has no frontmatter header.");
            parsed++;
            var report = new SkillHealthReport(frontmatter.Validate(name));
            if (report.Status == SkillHealthStatus.Healthy)
            {
                valid++;
            }
            else
            {
                invalid.Add($"{name}:{report.Status}");
            }
        }

        TestContext.Current.SendDiagnosticMessage($"Vendored SKILL.md parsed: {parsed}; fully valid: {valid}; others: {string.Join(", ", invalid)}.");
        Assert.True(parsed > 0, "No vendored SKILL.md was found locally.");
    }
}
