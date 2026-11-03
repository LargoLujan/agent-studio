using System.IO;
using System.Text.Json;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Providers.Skills;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Providers.Skills;

public sealed class SkillMaterializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentstudio-mat-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly SkillMaterializer _sut;

    public SkillMaterializerTests()
    {
        _profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(_profile);
        _sut = new SkillMaterializer(_profile);
    }

    // Junctions are removed as links, so the delete never touches (or trips over) their targets.
    public void Dispose() => TempFolders.Delete(_root);

    private ActivatedSkill MakeSkill(string name, bool withNested = true)
    {
        var dir = Path.Combine(_root, "catalog", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: d\n---\nbody");
        if (withNested)
        {
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            File.WriteAllText(Path.Combine(dir, "scripts", "run.ps1"), "echo hi");
        }

        var entry = new SkillEntry(
            new SkillId(name), name, "s", SkillSectors.IngenieriaSoftware, ["x"],
            new SkillSource("o/r", name, null, null, null, SkillDistribution.Vendored));
        return new ActivatedSkill(entry, dir);
    }

    private SkillSessionContext Session(string? workdir = null) =>
        new("run-1", Path.Combine(_root, "sessions"), workdir ?? Path.Combine(_root, "work"));

    [Fact]
    public async Task Claude_BuildsAValidPluginInTheSessionDirectory()
    {
        var dir = await _sut.MaterializeAsync(ProviderId.ClaudeCode, [MakeSkill("alpha"), MakeSkill("beta", false)], Session(), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "sessions", "claude-plugin-run-1"), dir);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, ".claude-plugin", "plugin.json")));
        var name = manifest.RootElement.GetProperty("name").GetString()!;
        Assert.Equal("agent-studio-session", name);
        Assert.False(name.StartsWith("claude-", StringComparison.Ordinal) || name.StartsWith("anthropic-", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("description").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("version").GetString()));
        Assert.True(File.Exists(Path.Combine(dir!, "skills", "alpha", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(dir!, "skills", "alpha", "scripts", "run.ps1")));
        Assert.True(File.Exists(Path.Combine(dir!, "skills", "beta", "SKILL.md")));
    }

    /// <summary>A skill whose source grants tools at the top and in a nested skill file, and carries a plain reference.</summary>
    private (ActivatedSkill Skill, Dictionary<string, byte[]> Source) MakeGrantingSkill(string name)
    {
        var skill = MakeSkill(name, withNested: false);
        var dir = skill.LocalPath;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: Probe.\nallowed-tools: Write, Bash\nhooks:\n  PreToolUse: []\n---\nTop body.\n");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "skill.md"), "---\nname: inner\ndescription: Inner.\nallowed-tools:\n  - Edit\n---\nInner body.\n");
        Directory.CreateDirectory(Path.Combine(dir, "references"));
        File.WriteAllText(Path.Combine(dir, "references", "notes.md"), "---\nallowed-tools: Write\n---\nA reference, not a skill.\n");
        var source = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        return (skill, source);
    }

    private static void AssertGrantsNothing(string materialized, ActivatedSkill skill, Dictionary<string, byte[]> source)
    {
        // Every skill file, at any depth and in any letter case, gets the session header; anything else is copied as is.
        foreach (var (relative, bytes) in source)
        {
            var target = Path.Combine(materialized, relative);
            var original = System.Text.Encoding.UTF8.GetString(bytes);
            if (string.Equals(Path.GetFileName(relative), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(SkillFrontmatter.ForSession(original), File.ReadAllText(target));
                Assert.DoesNotContain("allowed-tools", SkillFrontmatter.Parse(File.ReadAllText(target)).Fields.Keys);
            }
            else
            {
                Assert.Equal(bytes, File.ReadAllBytes(target));
            }

            // The source is never touched.
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(skill.LocalPath, relative)));
        }
    }

    [Fact]
    public async Task Claude_GivesTheSessionEverySkillFileWithAHeaderThatGrantsNothing()
    {
        var (skill, source) = MakeGrantingSkill("granting");

        var dir = await _sut.MaterializeAsync(ProviderId.ClaudeCode, [skill], Session(), TestContext.Current.CancellationToken);

        AssertGrantsNothing(Path.Combine(dir!, "skills", "granting"), skill, source);
        Assert.Contains("Top body.", File.ReadAllText(Path.Combine(dir!, "skills", "granting", "SKILL.md")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("gemini-cli")]
    public async Task CodexAndGemini_GetEverySkillFileWithAHeaderThatGrantsNothingToo(string provider)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var (skill, source) = MakeGrantingSkill("granting");

        var dir = await _sut.MaterializeAsync(new ProviderId(provider), [skill], Session(work), TestContext.Current.CancellationToken);

        AssertGrantsNothing(Path.Combine(dir!, "granting"), skill, source);
    }

    [Fact]
    public async Task Claude_CleanupRemovesTheSessionDirectory()
    {
        var dir = await _sut.MaterializeAsync(ProviderId.ClaudeCode, [MakeSkill("alpha")], Session(), TestContext.Current.CancellationToken);
        _sut.Cleanup(dir!);
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(Path.Combine(_root, "catalog", "alpha")));
    }

    [Fact]
    public async Task EmptyList_ReturnsNullAndWritesNothing()
    {
        var dir = await _sut.MaterializeAsync(ProviderId.ClaudeCode, [], Session(), TestContext.Current.CancellationToken);
        Assert.Null(dir);
        Assert.False(Directory.Exists(Path.Combine(_root, "sessions")));
    }

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("gemini-cli")]
    public async Task CodexAndGemini_CopyIntoAgentsSkillsAndExcludeItFromGit(string provider)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(work, ".git", "info"));
        File.WriteAllText(Path.Combine(work, ".gitignore"), "bin/\n");
        File.WriteAllText(Path.Combine(work, ".git", "info", "exclude"), "# comment");

        var dir = await _sut.MaterializeAsync(new ProviderId(provider), [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(work, ".agents", "skills"), dir);
        Assert.True(File.Exists(Path.Combine(work, ".agents", "skills", "alpha", "SKILL.md")));
        var exclude = File.ReadAllText(Path.Combine(work, ".git", "info", "exclude"));
        Assert.Contains(".agents/", exclude.Split('\n'));
        Assert.StartsWith("# comment\n", exclude, StringComparison.Ordinal);
        Assert.Equal("bin/\n", File.ReadAllText(Path.Combine(work, ".gitignore")));

        // A second materialization of other skills does not duplicate the exclude line.
        await _sut.MaterializeAsync(new ProviderId(provider), [MakeSkill("beta")], Session(work), TestContext.Current.CancellationToken);
        Assert.Single(File.ReadAllText(Path.Combine(work, ".git", "info", "exclude")).Split('\n'), l => l == ".agents/");
    }

    [Fact]
    public async Task Codex_WithoutGitRepoDoesNotCreateGitFolder()
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);

        await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(work, ".git")));
    }

    [Fact]
    public async Task Codex_WorktreeGitFileUsesTheCommonDirExclude()
    {
        var common = Path.Combine(_root, "main", ".git");
        var wtGit = Path.Combine(common, "worktrees", "wt");
        Directory.CreateDirectory(wtGit);
        File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..");
        var work = Path.Combine(_root, "wt");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, ".git"), "gitdir: " + wtGit + "\n");

        await _sut.MaterializeAsync(ProviderId.GeminiCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);

        Assert.Contains(".agents/", File.ReadAllText(Path.Combine(common, "info", "exclude")).Split('\n'));
    }

    [Fact]
    public async Task Codex_CleanupRemovesOnlyWhatWasCreated()
    {
        var work = Path.Combine(_root, "work");
        var own = Path.Combine(work, ".agents", "skills", "mine");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "SKILL.md"), "x");

        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);
        _sut.Cleanup(dir!);

        Assert.False(Directory.Exists(Path.Combine(dir!, "alpha")));
        Assert.True(File.Exists(Path.Combine(own, "SKILL.md")));
    }

    [Fact]
    public async Task Codex_CleanupRemovesEmptyAgentsFolder()
    {
        var work = Path.Combine(_root, "work");
        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);
        _sut.Cleanup(dir!);
        Assert.False(Directory.Exists(Path.Combine(work, ".agents")));
    }

    [Fact]
    public async Task Codex_DoesNotOverwriteAnExistingSkillFolder()
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(work, ".agents", "skills", "alpha"));

        await Assert.ThrowsAsync<IOException>(() =>
            _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex-cli")]
    [InlineData("gemini-cli")]
    public async Task NeverWritesUnderTheUserProfileCliFolders(string provider)
    {
        foreach (var folder in new[] { ".claude", ".codex", ".gemini", ".agents" })
        {
            var target = Path.Combine(_profile, folder, "sub");
            var session = new SkillSessionContext("run-1", target, target);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _sut.MaterializeAsync(new ProviderId(provider), [MakeSkill("alpha")], session, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(_profile, folder)));
        }
    }

    [Fact]
    public async Task ProfileCheckAlsoCatchesDotDotEscapes()
    {
        var session = new SkillSessionContext("run-1", Path.Combine(_root, "sessions", "..", "profile", ".claude"), Path.Combine(_root, "work"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [MakeSkill("alpha")], session, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../evil")]
    [InlineData("Upper")]
    [InlineData("under_score")]
    [InlineData("-lead")]
    public async Task RejectsInvalidSkillNamesWithoutWritingAnything(string bad)
    {
        var skill = MakeSkill("good");
        var evil = skill with { Skill = new SkillEntry(new SkillId(bad), "x", "s", SkillSectors.IngenieriaSoftware, ["x"], skill.Skill.Source) };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [skill, evil], Session(), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_root, "sessions")));
    }

    [Fact]
    public async Task RejectsUnsafeRunId()
    {
        var session = new SkillSessionContext("../x", Path.Combine(_root, "sessions"), Path.Combine(_root, "work"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [MakeSkill("alpha")], session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsUnknownProvider()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.MaterializeAsync(new ProviderId("other"), [MakeSkill("alpha")], Session(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DoesNotFollowSymbolicLinks()
    {
        var skill = MakeSkill("alpha");
        var secret = Path.Combine(_root, "secret");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "token.txt"), "s3cret");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "s3cret");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(skill.LocalPath, "linkdir"), secret);
            File.CreateSymbolicLink(Path.Combine(skill.LocalPath, "linkfile.txt"), Path.Combine(_root, "secret.txt"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic links are not available in this environment.");
        }

        var dir = await _sut.MaterializeAsync(ProviderId.ClaudeCode, [skill], Session(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(dir!, "skills", "alpha", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(dir!, "skills", "alpha", "linkdir")));
        Assert.False(File.Exists(Path.Combine(dir!, "skills", "alpha", "linkfile.txt")));

        // Cleanup never touches the link targets.
        _sut.Cleanup(dir!);
        Assert.True(File.Exists(Path.Combine(secret, "token.txt")));
    }

    private static void MakeJunction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "mklink /J failed");
    }

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("gemini-cli")]
    public async Task RefusesAWorkdirAgentsFolderThatIsAJunctionToTheUserProfile(string provider)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var realAgents = Path.Combine(_profile, ".agents");
        Directory.CreateDirectory(realAgents);
        MakeJunction(Path.Combine(work, ".agents"), realAgents);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(new ProviderId(provider), [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(realAgents));
    }

    [Fact]
    public async Task RefusesASessionBaseThatIsAJunctionToTheUserProfile()
    {
        var realClaude = Path.Combine(_profile, ".claude");
        Directory.CreateDirectory(realClaude);
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        MakeJunction(Path.Combine(_root, "sessions", "link"), realClaude);
        var session = new SkillSessionContext("run-1", Path.Combine(_root, "sessions", "link"), Path.Combine(_root, "work"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [MakeSkill("alpha")], session, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFileSystemEntries(realClaude));
    }

    [Theory]
    [InlineData("claude-code", ".claude")]
    [InlineData("codex-cli", ".codex")]
    [InlineData("gemini-cli", ".gemini")]
    public async Task ADotDotPrefixedFolderInsideAProtectedFolderIsStillProtected(string provider, string folder)
    {
        var inside = Path.Combine(_profile, folder, "..cache");
        var session = new SkillSessionContext("run-1", inside, inside);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(new ProviderId(provider), [MakeSkill("alpha")], session, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(_profile, folder)));
    }

    [Fact]
    public async Task RefusesAnExistingPluginRootThatIsAJunctionToAProtectedFolder()
    {
        var realClaude = Path.Combine(_profile, ".claude");
        Directory.CreateDirectory(realClaude);
        File.WriteAllText(Path.Combine(realClaude, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        var root = Path.Combine(_root, "sessions", "claude-plugin-run-1");
        MakeJunction(root, realClaude);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [MakeSkill("alpha")], Session(), TestContext.Current.CancellationToken));

        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(realClaude).Select(Path.GetFileName));
        Assert.Throws<InvalidOperationException>(() => _sut.Cleanup(root));
        Assert.True(File.Exists(Path.Combine(realClaude, "settings.json")));
    }

    [Fact]
    public async Task AGitFileRedirectingToAProtectedFolderDoesNotGetAnExcludeWritten()
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var protectedGit = Path.Combine(_profile, ".codex");
        Directory.CreateDirectory(protectedGit);
        File.WriteAllText(Path.Combine(work, ".git"), "gitdir: " + protectedGit + "\n");

        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(dir!, "alpha", "SKILL.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(protectedGit));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex-cli")]
    public async Task ExtendedPrefixUncAndTrailingDotOrSpaceSpellingsAreRefusedForDestinations(string provider)
    {
        var real = Path.Combine(_profile, ".codex");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(Path.Combine(_root, "w"));
        MakeJunction(Path.Combine(_root, "w", "j"), real);
        var spellings = new[]
        {
            @"\\?\" + Path.Combine(_root, "w", "j"),
            @"\\?\" + real,
            @"\\localhost\C$\x",
            Path.Combine(_profile, ".codex.", "x"),
            Path.Combine(_profile, ".codex ", "x"),
        };

        foreach (var spelling in spellings)
        {
            var session = new SkillSessionContext("run-1", spelling, spelling);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _sut.MaterializeAsync(new ProviderId(provider), [MakeSkill("alpha")], session, TestContext.Current.CancellationToken));
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public async Task ASkillSourceReachedThroughAnExtendedPrefixIsRefused()
    {
        var inside = Path.Combine(_profile, ".codex", "skills", "alpha");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "SKILL.md"), "x");
        var skill = MakeSkill("alpha") with { LocalPath = @"\\?\" + inside };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(ProviderId.CodexCli, [skill], Session(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("gitdir")]
    [InlineData("commondir")]
    public async Task GitRedirectionsThroughExtendedPrefixesDoNotWriteIntoProtectedFolders(string vector)
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var protectedGit = Path.Combine(_profile, ".codex");
        Directory.CreateDirectory(protectedGit);
        if (vector == "gitdir")
        {
            File.WriteAllText(Path.Combine(work, ".git"), "gitdir: \\\\?\\" + protectedGit + "\n");
        }
        else
        {
            var wt = Path.Combine(_root, "gitdata", "worktrees", "w");
            Directory.CreateDirectory(wt);
            File.WriteAllText(Path.Combine(work, ".git"), "gitdir: " + wt + "\n");
            File.WriteAllText(Path.Combine(wt, "commondir"), "\\\\?\\" + protectedGit + "\n");
        }

        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(dir!, "alpha", "SKILL.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(protectedGit));
    }

    [Fact]
    public async Task CleanupAlsoRemovesMarkedStagingFolders()
    {
        var work = Path.Combine(_root, "work");
        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);
        var staging = Path.Combine(dir!, ".staging-leftover");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, ".agent-studio-managed"), "Created by Agent Studio.\n");
        File.WriteAllText(Path.Combine(staging, "partial.txt"), "x");
        var foreign = Path.Combine(dir!, ".staging-foreign");
        Directory.CreateDirectory(foreign);

        _sut.Cleanup(dir!);

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public async Task ASkillThatContainsTheMarkerFileNameStillCopies()
    {
        var work = Path.Combine(_root, "work");
        var skill = MakeSkill("alpha");
        File.WriteAllText(Path.Combine(skill.LocalPath, ".agent-studio-managed"), "from the source");

        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [skill], Session(work), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(dir!, "alpha", "SKILL.md")));
        Assert.Equal("Created by Agent Studio.\n", File.ReadAllText(Path.Combine(dir!, "alpha", ".agent-studio-managed")));
    }

    [Fact]
    public async Task CleanupIgnoresForgedMarkersAndImplausibleNames()
    {
        var work = Path.Combine(_root, "work");
        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);
        var forged = Path.Combine(dir!, "beta");
        Directory.CreateDirectory(forged);
        File.WriteAllText(Path.Combine(forged, ".agent-studio-managed"), "not ours");
        var oddName = Path.Combine(dir!, "Not A Skill");
        Directory.CreateDirectory(oddName);
        File.WriteAllText(Path.Combine(oddName, ".agent-studio-managed"), "Created by Agent Studio.\n");

        _sut.Cleanup(dir!);

        Assert.False(Directory.Exists(Path.Combine(dir!, "alpha")));
        Assert.True(Directory.Exists(forged));
        Assert.True(Directory.Exists(oddName));
    }

    [Fact]
    public async Task AHardLinkedExcludeFileIsNeverModified()
    {
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(work, ".git", "info"));
        var protectedFile = Path.Combine(_profile, ".codex", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(protectedFile)!);
        File.WriteAllText(protectedFile, "original");
        var exclude = Path.Combine(work, ".git", "info", "exclude");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{exclude}\" \"{protectedFile}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var process = System.Diagnostics.Process.Start(info)!)
        {
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, "mklink /H failed");
        }

        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [MakeSkill("alpha")], Session(work), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(dir!, "alpha", "SKILL.md")));
        Assert.Equal("original", File.ReadAllText(protectedFile));
    }

    [Fact]
    public async Task APluginRootCreatedByAnotherProcessIsNeverDeleted()
    {
        var bad = MakeSkill("beta");
        var root = Path.Combine(_root, "sessions", "claude-plugin-run-1");
        var foreignFile = Path.Combine(root, "theirs.txt");
        // Simulate the race: the folder appears (with someone else's files) right after the exists-check.
        // The materializer must notice it does not own the folder and leave it alone.
        Directory.CreateDirectory(root);
        File.WriteAllText(foreignFile, "keep");

        await Assert.ThrowsAsync<IOException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [bad], Session(), TestContext.Current.CancellationToken));

        Assert.True(File.Exists(foreignFile));
    }

    [Fact]
    public async Task ASkillSourceInsideAProtectedFolderIsRefused()
    {
        var skill = MakeSkill("alpha");
        var inside = Path.Combine(_profile, ".claude", "skills", "alpha");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "SKILL.md"), "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.MaterializeAsync(ProviderId.CodexCli, [skill with { LocalPath = inside }], Session(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedCopyLeavesNoPartialFolderAndALaterRunSucceeds()
    {
        var work = Path.Combine(_root, "work");
        var good = MakeSkill("alpha");
        var bad = MakeSkill("beta");
        var locked = Path.Combine(bad.LocalPath, "scripts", "run.ps1");
        var cts = TestContext.Current.CancellationToken;

        await using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => _sut.MaterializeAsync(ProviderId.CodexCli, [good, bad], Session(work), cts));
        }

        // Neither the partial skill nor the one that had been copied before it remains, and no staging folder is left.
        Assert.False(Directory.Exists(Path.Combine(work, ".agents")));

        var dir = await _sut.MaterializeAsync(ProviderId.CodexCli, [good, bad], Session(work), cts);
        Assert.True(File.Exists(Path.Combine(dir!, "beta", "scripts", "run.ps1")));
        Assert.DoesNotContain(Directory.GetDirectories(dir!), d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledOrFailedClaudePluginIsRemovedEntirely()
    {
        var bad = MakeSkill("beta");
        var locked = Path.Combine(bad.LocalPath, "scripts", "run.ps1");

        await using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                _sut.MaterializeAsync(ProviderId.ClaudeCode, [bad], Session(), TestContext.Current.CancellationToken));
        }

        Assert.False(Directory.Exists(Path.Combine(_root, "sessions", "claude-plugin-run-1")));
        var dir = await _sut.MaterializeAsync(ProviderId.ClaudeCode, [bad], Session(), TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task RejectsAMissingSkillFolderOrSkillMd()
    {
        var skill = MakeSkill("alpha");
        File.Delete(Path.Combine(skill.LocalPath, "SKILL.md"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [skill], Session(), TestContext.Current.CancellationToken));

        var gone = skill with { LocalPath = Path.Combine(_root, "nope") };
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _sut.MaterializeAsync(ProviderId.ClaudeCode, [gone], Session(), TestContext.Current.CancellationToken));
    }
}
