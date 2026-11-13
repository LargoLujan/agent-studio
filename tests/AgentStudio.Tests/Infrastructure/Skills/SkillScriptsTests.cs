using System.IO;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;

namespace AgentStudio.Tests.Infrastructure.Skills;

/// <summary>The local checks of docs/20 D-14: scripts without review, changed after the review, and the review store. Nothing runs, nothing leaves a temporary folder.</summary>
public sealed class SkillScriptsTests : IDisposable
{
    private const string Sha = "6dcb09b5b57875f334f61aebed695e2e4193db5e";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "as scripts " + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the temporary folder.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SkillFolder(string name, params (string Path, string Content)[] scripts)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: Una skill.\n---\n\n# {name}\n\nCuerpo.\n");
        foreach (var (path, content) in scripts)
        {
            var file = Path.Combine(folder, "scripts", path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        return folder;
    }

    private static CatalogSkill Skill(string name, string folder) =>
        new(new SkillEntry(new SkillId(name), name, "Resumen.", SkillSectors.IngenieriaSoftware, [], new SkillSource("owner/repo", "skills/" + name, "main", Sha, "MIT", SkillDistribution.Vendored)), folder);

    private SkillScriptReviewStore Store() => new(new SkillScriptReviewOptions(Path.Combine(_root, "profile", "reviews.json")));

    [Fact]
    public async Task A_skill_without_scripts_has_none_and_is_healthy()
    {
        var folder = SkillFolder("plain");
        var reader = new SkillHealthReader(new SkillScriptsInspector(), Store());

        Assert.Null(await new SkillScriptsInspector().InspectAsync(folder, Ct));
        var report = await reader.ReadLocalAsync(Skill("plain", folder), Ct);

        var scripts = Assert.Single(report.Checks, c => c.Code == SkillHealthCodes.Scripts);
        Assert.Equal(SkillHealthStatus.Healthy, scripts.Status);
    }

    [Fact]
    public async Task Scripts_nobody_reviewed_are_a_warning_until_the_person_marks_them_and_again_when_one_changes()
    {
        var folder = SkillFolder("with-scripts", ("run.ps1", "Write-Host one"), ("sub/helper.py", "print(1)"));
        var inspector = new SkillScriptsInspector();
        var store = Store();
        var reader = new SkillHealthReader(inspector, store);
        var skill = Skill("with-scripts", folder);

        var first = Assert.Single((await reader.ReadLocalAsync(skill, Ct)).Checks, c => c.Code == SkillHealthCodes.Scripts);
        Assert.Equal(SkillHealthStatus.Warning, first.Status);
        Assert.Contains("sin revisar", first.Message, StringComparison.Ordinal);

        var info = (await inspector.InspectAsync(folder, Ct))!;
        Assert.Equal(["run.ps1", "sub/helper.py"], info.RelativePaths);
        await store.MarkReviewedAsync(skill.Entry.Id, info.Sha256, Ct);

        var reviewed = Assert.Single((await reader.ReadLocalAsync(skill, Ct)).Checks, c => c.Code == SkillHealthCodes.Scripts);
        Assert.Equal(SkillHealthStatus.Healthy, reviewed.Status);

        File.WriteAllText(Path.Combine(folder, "scripts", "run.ps1"), "Write-Host two");

        var changed = Assert.Single((await reader.ReadLocalAsync(skill, Ct)).Checks, c => c.Code == SkillHealthCodes.Scripts);
        Assert.Equal(SkillHealthStatus.Warning, changed.Status);
        Assert.Contains("cambiaron", changed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_script_or_a_renamed_one_changes_the_fingerprint()
    {
        var folder = SkillFolder("fp", ("a.sh", "x"));
        var inspector = new SkillScriptsInspector();
        var before = (await inspector.InspectAsync(folder, Ct))!.Sha256;

        File.WriteAllText(Path.Combine(folder, "scripts", "b.sh"), "y");
        var added = (await inspector.InspectAsync(folder, Ct))!.Sha256;
        File.Move(Path.Combine(folder, "scripts", "b.sh"), Path.Combine(folder, "scripts", "c.sh"));
        var renamed = (await inspector.InspectAsync(folder, Ct))!.Sha256;

        Assert.Equal(3, new[] { before, added, renamed }.Distinct().Count());
        Assert.Equal(64, before.Length);
    }

    [Fact]
    public async Task A_skill_with_no_local_copy_or_no_reader_is_unknown_never_healthy()
    {
        var folder = SkillFolder("x", ("a.sh", "x"));

        var noCopy = await new SkillHealthReader(new SkillScriptsInspector(), Store()).ReadLocalAsync(Skill("x", folder) with { LocalPath = null }, Ct);
        var noReader = await new SkillHealthReader().ReadLocalAsync(Skill("x", folder), Ct);

        Assert.Equal(SkillHealthStatus.Unknown, Assert.Single(noCopy.Checks, c => c.Code == SkillHealthCodes.Scripts).Status);
        Assert.Equal(SkillHealthStatus.Unknown, Assert.Single(noReader.Checks, c => c.Code == SkillHealthCodes.Scripts).Status);
    }

    [Fact]
    public async Task The_size_check_says_what_the_skill_costs_in_context()
    {
        var folder = SkillFolder("size");

        var report = await new SkillHealthReader().ReadLocalAsync(Skill("size", folder), Ct);

        var size = Assert.Single(report.Checks, c => c.Code == SkillHealthCodes.Size);
        Assert.Contains("tokens (estimado por Agent Studio)", size.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_review_store_keeps_the_last_hash_per_skill_and_reads_a_hostile_file_as_empty()
    {
        var store = Store();
        var file = Path.Combine(_root, "profile", "reviews.json");
        var one = new string('a', 64);
        var two = new string('b', 64);

        await store.MarkReviewedAsync(new SkillId("s"), one, Ct);
        await store.MarkReviewedAsync(new SkillId("s"), two, Ct);
        await store.MarkReviewedAsync(new SkillId("t"), one, Ct);

        Assert.Equal(two, await store.ReviewedSha256Async(new SkillId("s"), Ct));
        Assert.Equal(one, await store.ReviewedSha256Async(new SkillId("t"), Ct));
        Assert.Null(await store.ReviewedSha256Async(new SkillId("never"), Ct));

        File.WriteAllText(file, "{\"Version\":1,\"Reviews\":{\"s\":\"not-a-hash\",\"t\":\"" + one + "\"}}");
        Assert.Null(await store.ReviewedSha256Async(new SkillId("s"), Ct));
        Assert.Equal(one, await store.ReviewedSha256Async(new SkillId("t"), Ct));

        File.WriteAllText(file, "{broken");
        Assert.Null(await store.ReviewedSha256Async(new SkillId("t"), Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task A_review_must_be_a_lowercase_sha_256(string hash) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Store().MarkReviewedAsync(new SkillId("s"), hash, Ct));
}
