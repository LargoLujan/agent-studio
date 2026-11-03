using System.IO;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Skills;

public sealed class SkillsIndexExplicitFieldsTests : IDisposable
{
    private const string Json = """
    {
      "skills": [
        { "domain": "web-development", "name": "explicit-sector", "repo": "o/x", "path": "a", "mode": "vendor", "summary": "App Router de Next.js.", "sector": "documentacion", "tags": ["Mi Tag", "otro", "  ", "no válido!", 7] },
        { "domain": "web-development", "name": "bad-sector", "repo": "o/x", "path": "b", "mode": "vendor", "summary": "App Router de Next.js.", "sector": "no-existe" },
        { "domain": "mystery", "name": "mystery-explicit", "repo": "o/x", "path": "c", "mode": "vendor", "summary": "Nada.", "sector": "ia-llm" },
        { "domain": "web-development", "name": "tags-not-list", "repo": "o/x", "path": "d", "mode": "vendor", "summary": "App Router de Next.js.", "tags": "oops" },
        { "domain": "web-development", "name": "plain", "repo": "o/x", "path": "e", "mode": "vendor", "summary": "App Router de Next.js." }
      ]
    }
    """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agentstudio-skills-" + Guid.NewGuid().ToString("N"));

    public SkillsIndexExplicitFieldsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TempFolders.Delete(_dir);

    private async Task<AgentStudio.Application.Skills.SkillImportResult> ImportAsync()
    {
        var path = Path.Combine(_dir, "catalog.json");
        await File.WriteAllTextAsync(path, Json, TestContext.Current.CancellationToken);
        return await new SkillsIndexCatalogImporter().ImportWithReportAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitSector_OverridesTheDomainMapping()
    {
        var result = await ImportAsync();
        Assert.Equal("documentacion", result.Entries.Single(e => e.Name == "explicit-sector").Sector.Id);
        Assert.Equal("frontend", result.Entries.Single(e => e.Name == "plain").Sector.Id);
    }

    [Fact]
    public async Task ExplicitSector_OnUnknownDomainIsClassified()
    {
        var result = await ImportAsync();
        var entry = result.Entries.Single(e => e.Name == "mystery-explicit");
        Assert.Equal("ia-llm", entry.Sector.Id);
        Assert.DoesNotContain("sin-clasificar", entry.Tags);
    }

    [Fact]
    public async Task InvalidSector_FallsBackToDomainWithWarning()
    {
        var result = await ImportAsync();
        Assert.Equal("frontend", result.Entries.Single(e => e.Name == "bad-sector").Sector.Id);
        Assert.Contains(result.Warnings, w => w.Contains("bad-sector") && w.Contains("no-existe"));
    }

    [Fact]
    public async Task ExplicitTags_AreNormalizedAndInvalidOnesDiscardedWithWarning()
    {
        var result = await ImportAsync();
        var entry = result.Entries.Single(e => e.Name == "explicit-sector");
        Assert.Contains("mi-tag", entry.Tags);
        Assert.Contains("otro", entry.Tags);
        Assert.DoesNotContain(entry.Tags, t => t.Contains('!') || t.Contains(' '));
        Assert.Equal(3, result.Warnings.Count(w => w.Contains("explicit-sector") && w.Contains("etiqueta inválida")));
    }

    [Fact]
    public async Task TagsThatAreNotAList_AreIgnoredWithWarning()
    {
        var result = await ImportAsync();
        Assert.Contains(result.Entries, e => e.Name == "tags-not-list");
        Assert.Contains(result.Warnings, w => w.Contains("tags-not-list") && w.Contains("no es una lista"));
    }
}
