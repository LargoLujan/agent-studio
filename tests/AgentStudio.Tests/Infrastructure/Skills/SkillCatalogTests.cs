using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Skills;

/// <summary>
/// The catalog over a fake skills index laid out like the real one (<c>domains/&lt;domain&gt;/skills/&lt;name&gt;/SKILL.md</c>).
/// Nothing here reads the user's index or writes outside a temporary folder.
/// </summary>
public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _container = Path.Combine(Path.GetTempPath(), "as catálogo " + Guid.NewGuid().ToString("N")[..12]);

    public SkillCatalogTests()
    {
        IndexRoot = Path.Combine(_container, "índice");
        Directory.CreateDirectory(IndexRoot);
    }

    private string IndexRoot { get; }

    private string CatalogPath => Path.Combine(IndexRoot, "catalog.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        RemoveJunctions(_container);
        Persistence.TempProjectFolder.ForceDelete(_container);
    }

    private static void RemoveJunctions(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                child.Delete();
            }
            else
            {
                RemoveJunctions(child.FullName);
            }
        }
    }

    private static void MakeJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed");
    }

    private string SkillFolder(string domain, string name, bool withSkillMd = true)
    {
        var folder = Path.Combine(IndexRoot, "domains", domain, "skills", name);
        Directory.CreateDirectory(folder);
        if (withSkillMd)
        {
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: prueba\n---\n# {name}\n");
        }

        return folder;
    }

    private static string Vendored(string name, string domain = "software-engineering") =>
        $$"""{ "domain": "{{domain}}", "name": "{{name.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "repo": "o/r", "path": "skills/x", "license": "MIT", "mode": "vendor", "summary": "Skill de prueba." }""";

    private void WriteCatalog(params string[] entries) =>
        File.WriteAllText(CatalogPath, $$"""{ "marketplace": "test", "domains": {}, "skills": [ {{string.Join(",\n", entries)}} ], "mcp": [] }""");

    private Task<SkillCatalogReport> ListAsync() =>
        new SkillCatalog(new SkillsIndexCatalogImporter(), new SkillCatalogOptions(CatalogPath)).ListWithReportAsync(Ct);

    [Fact]
    public async Task A_vendored_skill_resolves_to_its_folder_inside_the_index_with_its_skill_md()
    {
        var folder = SkillFolder("software-engineering", "tdd-lite");
        WriteCatalog(Vendored("tdd-lite"));

        var report = await ListAsync();

        var skill = Assert.Single(report.Skills);
        Assert.Equal(new SkillId("tdd-lite"), skill.Entry.Id);
        Assert.Equal(Path.GetFullPath(folder), skill.LocalPath);
        Assert.StartsWith(Path.GetFullPath(IndexRoot) + Path.DirectorySeparatorChar, skill.LocalPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(skill.LocalPath!, "SKILL.md")));
        Assert.Empty(report.Warnings);
        var listed = await new SkillCatalog(new SkillsIndexCatalogImporter(), new SkillCatalogOptions(CatalogPath)).ListAsync(Ct);
        Assert.Equal(report.Skills.Select(s => (s.Entry.Id, s.LocalPath)), listed.Select(s => (s.Entry.Id, s.LocalPath)));
    }

    [Fact]
    public async Task A_linked_skill_has_no_local_path_and_a_warning()
    {
        WriteCatalog("""{ "domain": "desktop-development", "name": "enlazada", "mode": "link", "summary": "Solo enlace.", "url": "https://github.com/acme/skills/tree/main/skills/enlazada", "ref": "main" }""");

        var report = await ListAsync();

        var skill = Assert.Single(report.Skills);
        Assert.Equal(SkillDistribution.Linked, skill.Entry.Source.Mode);
        Assert.Null(skill.LocalPath);
        Assert.Contains(report.Warnings, w => w.Contains("enlazada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_vendored_skill_whose_folder_is_missing_or_has_no_skill_md_has_no_local_path_and_a_warning()
    {
        SkillFolder("software-engineering", "sin-skill-md", withSkillMd: false);
        WriteCatalog(Vendored("sin-copia"), Vendored("sin-skill-md"));

        var report = await ListAsync();

        Assert.All(report.Skills, s => Assert.Null(s.LocalPath));
        Assert.Contains(report.Warnings, w => w.Contains("sin-copia", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("sin-skill-md", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../../../../fuera", "software-engineering")]
    [InlineData("fuera", "../../..")]
    [InlineData("fuera", "C:")]
    public async Task A_name_or_domain_that_would_leave_the_index_is_never_resolved(string name, string domain)
    {
        // The escape works on disk: an unchecked path would find this SKILL.md outside the index.
        var outside = Path.Combine(_container, "fuera");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), "---\nname: fuera\ndescription: x\n---\n");
        WriteCatalog(Vendored(name, domain));

        var report = await ListAsync();

        Assert.All(report.Skills, s => Assert.Null(s.LocalPath));
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task A_skill_folder_that_is_a_junction_out_of_the_index_is_not_resolved()
    {
        var outside = Path.Combine(_container, "fuera");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), "---\nname: saltada\ndescription: x\n---\n");
        var skills = Path.Combine(IndexRoot, "domains", "software-engineering", "skills");
        Directory.CreateDirectory(skills);
        MakeJunction(Path.Combine(skills, "saltada"), outside);
        WriteCatalog(Vendored("saltada"));

        var report = await ListAsync();

        Assert.Null(Assert.Single(report.Skills).LocalPath);
        Assert.Contains(report.Warnings, w => w.Contains("saltada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_warnings_of_the_importer_are_kept()
    {
        SkillFolder("software-engineering", "tdd-lite");
        WriteCatalog(Vendored("tdd-lite"), """{ "domain": "microsoft-data", "name": "fabric-common", "repo": "o/ms", "path": "common", "mode": "support", "summary": "Shared." }""");

        var report = await ListAsync();

        Assert.Single(report.Skills);
        Assert.Contains(report.Warnings, w => w.Contains("fabric-common", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_index_the_catalog_is_empty_and_says_why()
    {
        var notConfigured = await new SkillCatalog(new SkillsIndexCatalogImporter(), new SkillCatalogOptions(null)).ListWithReportAsync(Ct);
        var missing = await new SkillCatalog(new SkillsIndexCatalogImporter(), new SkillCatalogOptions(Path.Combine(IndexRoot, "no-existe.json"))).ListWithReportAsync(Ct);

        Assert.Empty(notConfigured.Skills);
        Assert.Single(notConfigured.Warnings);
        Assert.Empty(missing.Skills);
        Assert.Contains(missing.Warnings, w => w.Contains("no-existe.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_registrations_compose_a_provisioner_that_gives_an_activated_skill_to_a_codex_agent()
    {
        SkillFolder("software-engineering", "tdd-lite");
        WriteCatalog(Vendored("tdd-lite"));
        var project = Path.Combine(_container, "proyecto");
        var work = Path.Combine(_container, "wt", "T1");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(work);
        var services = new ServiceCollection()
            .AddAgentStudioPersistence()
            .AddAgentStudioSkills(new SkillCatalogOptions(CatalogPath), new SkillActivationStoreOptions(Path.Combine(_container, "perfil", "skill-activations.jsonl")))
            .AddAgentStudioProviders();
        await using var provider = services.BuildServiceProvider();

        var context = await provider.GetRequiredService<IProjectWorkspace>().OpenAsync(project, Ct);
        var activations = provider.GetRequiredService<ISkillActivationStore>();
        await activations.AppendAsync(context.ProjectId, new SkillActivation(new SkillId("tdd-lite"), SkillScopeKind.Project, context.ProjectId, true), "manuel", Ct);
        var provisioner = new SkillProvisioner(
            provider.GetRequiredService<ISkillCatalog>(),
            activations,
            provider.GetRequiredService<ISkillMaterializer>(),
            new SkillProvisioningOptions(Path.Combine(_container, "sesiones")));
        var agent = new AgentDefinition(
            new AgentId("ejecutor"), "ejecutor", AgentRole.Executor, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI), "system", [], [], [],
            new ExecutionBudget(10, TimeSpan.FromMinutes(5)), 1, MemoryPolicy.None, new ReviewPolicy(false, false));

        var provisioned = await provisioner.ProvisionAsync(context.ProjectId, agent, "run-1", work, Ct);

        Assert.Equal([new SkillId("tdd-lite")], provisioned.Skills);
        Assert.Empty(provisioned.Warnings);
        Assert.True(File.Exists(Path.Combine(work, ".agents", "skills", "tdd-lite", "SKILL.md")));
        provisioner.Release(provisioned);
        Assert.False(Directory.Exists(Path.Combine(work, ".agents")));
        Assert.Same(provider.GetRequiredService<SkillCatalog>(), provider.GetRequiredService<ISkillCatalog>());
    }
}
