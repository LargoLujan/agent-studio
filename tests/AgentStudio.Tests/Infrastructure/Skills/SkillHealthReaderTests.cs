using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Infrastructure.Skills;

/// <summary>
/// docs/14 D-26: the health the catalog screen shows is local only, with no network: the frontmatter of the SKILL.md
/// (<see cref="SkillFrontmatter.Validate"/>), the license and the pinning. Upstream, archived and scripts come back
/// «sin comprobar» (they need «Comprobar en GitHub», or the reader of scripts), which weighs more than «sana». Nothing here leaves a temporary folder.
/// </summary>
public sealed class SkillHealthReaderTests : IDisposable
{
    private const string Sha = "8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "as salud " + Guid.NewGuid().ToString("N")[..12]);

    public SkillHealthReaderTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The junction a test plants is removed as a link, never walked into (<see cref="TempFolders"/>).</summary>
    public void Dispose() => TempFolders.Delete(_root);

    private string Folder(string name, string? skillMarkdown)
    {
        var folder = Path.Combine(_root, "domains", "ingenieria", "skills", name);
        Directory.CreateDirectory(folder);
        if (skillMarkdown is not null)
        {
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), skillMarkdown);
        }

        return folder;
    }

    private static CatalogSkill Skill(string name, string? localPath, string? license = "MIT", string? pinned = Sha, SkillDistribution mode = SkillDistribution.Vendored) =>
        new(new SkillEntry(new SkillId(name), name, "Resumen.", SkillSectors.IngenieriaDatos, [], new SkillSource("owner/repo", "skills/" + name, "main", pinned, license, mode)), localPath);

    private static string Markdown(string name, string description = "Optimiza trabajos de Spark.") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\nCuerpo.\n";

    private static SkillHealthCheck Check(SkillHealthReport report, string code) => Assert.Single(report.Checks, c => c.Code == code);

    [Fact]
    public async Task A_healthy_vendored_skill_passes_every_local_check_and_is_still_unverified_overall()
    {
        var skill = Skill("spark-optimization", Folder("spark-optimization", Markdown("spark-optimization")));

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        Assert.Equal(SkillHealthStatus.Healthy, Check(report, SkillHealthCodes.Frontmatter).Status);
        Assert.Equal(SkillHealthStatus.Healthy, Check(report, SkillHealthCodes.Size).Status);
        Assert.Equal(SkillHealthStatus.Healthy, Check(report, SkillHealthCodes.License).Status);
        Assert.Equal(SkillHealthStatus.Healthy, Check(report, SkillHealthCodes.Pinned).Status);

        // What needs the network or a review waits for Phase 6, and «sin comprobar» weighs more than «sana».
        foreach (var code in new[] { SkillHealthCodes.Upstream, SkillHealthCodes.Archived, SkillHealthCodes.Scripts })
        {
            var check = Check(report, code);
            Assert.Equal(SkillHealthStatus.Unknown, check.Status);
            Assert.NotNull(check.TextCode);
            Assert.Equal(code switch
            {
                SkillHealthCodes.Upstream => SkillsUserTexts.ReaderUpstreamUnchecked,
                SkillHealthCodes.Archived => SkillsUserTexts.ReaderArchivedUnchecked,
                _ => SkillsUserTexts.ReaderScriptsNoReader,
            }, check.TextCode);
        }

        Assert.Equal(SkillHealthStatus.Unknown, report.Status);
    }

    /// <summary>P65: the cost of the whole SKILL.md in context comes back next to the size, as a number of the coded text (not as a piece of sentence).</summary>
    [Fact]
    public async Task The_size_check_carries_the_cost_in_context_of_the_whole_skill_file()
    {
        var markdown = Markdown("spark-optimization");
        var skill = Skill("spark-optimization", Folder("spark-optimization", markdown));

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        var size = Check(report, SkillHealthCodes.Size);
        Assert.Equal(SkillHealthTextCodes.SizeOkCost, size.TextCode);
        Assert.Equal(SkillContextCost.OfText(markdown.Length), Assert.IsType<long>(size.TextArguments![1]));
        Assert.True((long)size.TextArguments[1] > 0);
    }

    [Fact]
    public async Task A_frontmatter_without_description_is_critical()
    {
        var skill = Skill("sin-descripcion", Folder("sin-descripcion", "---\nname: sin-descripcion\n---\n\nCuerpo.\n"));

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        Assert.Contains(report.Checks, c => c.Code == SkillHealthCodes.Frontmatter && c.Status == SkillHealthStatus.Critical);
        Assert.Equal(SkillHealthStatus.Critical, report.Status);
    }

    [Fact]
    public async Task A_name_that_differs_from_its_folder_is_a_warning()
    {
        var skill = Skill("carpeta", Folder("carpeta", Markdown("otro-nombre")));

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        Assert.Contains(report.Checks, c => c.Code == SkillHealthCodes.Frontmatter && c.Status == SkillHealthStatus.Warning && c.TextCode == SkillHealthTextCodes.FrontmatterNameFolderMismatch && c.TextArguments is ["otro-nombre", "carpeta"]);
        Assert.Equal(SkillHealthStatus.Warning, report.Status);
    }

    [Fact]
    public async Task No_license_and_no_pinned_commit_are_warnings()
    {
        var skill = Skill("libre", Folder("libre", Markdown("libre")), license: null, pinned: null);

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        Assert.Equal(SkillHealthStatus.Warning, Check(report, SkillHealthCodes.License).Status);
        Assert.Equal(SkillHealthStatus.Warning, Check(report, SkillHealthCodes.Pinned).Status);
    }

    [Fact]
    public async Task A_linked_skill_has_no_local_copy_to_check_but_keeps_its_license_and_pinning()
    {
        var skill = Skill("enlazada", localPath: null, license: "Apache-2.0", pinned: null, mode: SkillDistribution.Linked);

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        var frontmatter = Check(report, SkillHealthCodes.Frontmatter);
        Assert.Equal(SkillHealthStatus.Unknown, frontmatter.Status);
        Assert.Equal(SkillsUserTexts.ReaderUnreadNoCopy, frontmatter.TextCode);
        Assert.Equal(SkillHealthStatus.Healthy, Check(report, SkillHealthCodes.License).Status);
        Assert.Equal(SkillHealthStatus.Warning, Check(report, SkillHealthCodes.Pinned).Status);
    }

    [Fact]
    public async Task A_skill_file_that_disappeared_is_not_checked_rather_than_reported_healthy()
    {
        var skill = Skill("borrada", Folder("borrada", skillMarkdown: null));

        var report = await new SkillHealthReader().ReadLocalAsync(skill, Ct);

        var frontmatter = Check(report, SkillHealthCodes.Frontmatter);
        Assert.Equal(SkillHealthStatus.Unknown, frontmatter.Status);
        Assert.Equal(SkillsUserTexts.ReaderUnreadFileMissing, frontmatter.TextCode);
    }

    [Fact]
    public async Task A_skill_file_too_large_to_be_a_skill_is_not_read()
    {
        var folder = Folder("enorme", skillMarkdown: null);
        await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), Markdown("enorme") + new string('x', (int)SkillHealthReader.MaxSkillFileBytes), Ct);

        var report = await new SkillHealthReader().ReadLocalAsync(Skill("enorme", folder), Ct);

        var frontmatter = Check(report, SkillHealthCodes.Frontmatter);
        Assert.Equal(SkillHealthStatus.Warning, frontmatter.Status);
        Assert.Equal(SkillsUserTexts.ReaderTooLargeFrontmatter, frontmatter.TextCode);
        Assert.IsType<long>(Assert.Single(frontmatter.TextArguments!));
    }

    [Fact]
    public async Task A_skill_folder_that_became_a_junction_is_not_followed()
    {
        // The catalog checked the folder when it listed it; a junction planted afterwards could lead out of the index.
        var elsewhere = Path.Combine(_root, "fuera", "plantada");
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "SKILL.md"), Markdown("plantada"), Ct);
        var folder = Path.Combine(_root, "domains", "ingenieria", "skills", "plantada");
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        MakeJunction(folder, elsewhere);

        var report = await new SkillHealthReader().ReadLocalAsync(Skill("plantada", folder), Ct);

        var frontmatter = Check(report, SkillHealthCodes.Frontmatter);
        Assert.Equal(SkillHealthStatus.Unknown, frontmatter.Status);
        Assert.Equal(SkillsUserTexts.ReaderUnreadFolderLink, frontmatter.TextCode);
    }

    [Fact]
    public void The_skills_module_registers_the_local_health_reader()
    {
        var services = new ServiceCollection().AddAgentStudioSkills(new SkillCatalogOptions(null), new SkillActivationStoreOptions(Path.Combine(_root, "skill-activations.jsonl")));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SkillHealthReader>(provider.GetRequiredService<ISkillHealthReader>());
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
}
