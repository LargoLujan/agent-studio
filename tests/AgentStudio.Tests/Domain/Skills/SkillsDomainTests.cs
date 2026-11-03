using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Domain.Skills;

public sealed class SkillsDomainTests
{
    private const string Sha = "8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4";

    private static SkillEntry Entry(string name, string summary, string sectorId, params string[] tags) =>
        new(
            new SkillId(name),
            name,
            summary,
            SkillSectors.Get(sectorId),
            tags,
            new SkillSource("owner/repo", "skills/" + name, "main", Sha, "MIT", SkillDistribution.Vendored));

    // ---- taxonomy, ids, tags ----

    [Fact]
    public void Taxonomy_HasTheSixteenSectorsWithKebabIdsAndSpanishTitles()
    {
        Assert.Equal(16, SkillSectors.All.Count);
        Assert.All(SkillSectors.All, s =>
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", s.Id);
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
        });
        Assert.Equal(SkillSectors.All.Count, SkillSectors.All.Select(s => s.Id).Distinct().Count());
        Assert.Equal("ux-ui", SkillSectors.Get("ux-ui").Id);
        Assert.Null(SkillSectors.Find("nope"));
    }

    [Fact]
    public void SkillId_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new SkillId(" "));
    }

    [Fact]
    public void Entry_NormalizesTagsAndRejectsInvalidOnes()
    {
        var entry = Entry("x", "y", "frontend", "Spark", " Power BI ", "spark");
        Assert.Equal(new[] { "power-bi", "spark" }, entry.Tags.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Throws<ArgumentException>(() => Entry("x", "y", "frontend", "bad/tag!"));
    }

    // ---- activation ----

    [Fact]
    public void Resolver_WithoutActivations_IsDisabled()
    {
        Assert.False(SkillActivationResolver.IsEnabled(new SkillId("a"), "p", "ag", []));
    }

    [Fact]
    public void Resolver_AgentBeatsProjectBeatsGlobal()
    {
        var id = new SkillId("a");
        var global = new SkillActivation(id, SkillScopeKind.Global, null, true);
        var project = new SkillActivation(id, SkillScopeKind.Project, "p1", false);
        var agent = new SkillActivation(id, SkillScopeKind.Agent, "ag1", true);

        Assert.True(SkillActivationResolver.IsEnabled(id, "p2", null, [global, project]));
        Assert.False(SkillActivationResolver.IsEnabled(id, "p1", null, [global, project]));
        Assert.True(SkillActivationResolver.IsEnabled(id, "p1", "ag1", [global, project, agent]));
        Assert.False(SkillActivationResolver.IsEnabled(id, "p1", "ag2", [global, project, agent]));
    }

    [Fact]
    public void Resolver_IgnoresOtherSkillsAndOtherScopes()
    {
        var id = new SkillId("a");
        var other = new SkillActivation(new SkillId("b"), SkillScopeKind.Global, null, true);
        var otherProject = new SkillActivation(id, SkillScopeKind.Project, "p9", true);
        Assert.False(SkillActivationResolver.IsEnabled(id, "p1", null, [other, otherProject]));
    }

    // ---- health ----

    [Fact]
    public void Report_StatusIsTheWorstCheck_AndUnknownWhenEmpty()
    {
        Assert.Equal(SkillHealthStatus.Unknown, new SkillHealthReport([]).Status);
        var ok = new SkillHealthCheck(SkillHealthCodes.License, SkillHealthStatus.Healthy, "ok");
        var warn = new SkillHealthCheck(SkillHealthCodes.Pinned, SkillHealthStatus.Warning, "w");
        var bad = new SkillHealthCheck(SkillHealthCodes.Frontmatter, SkillHealthStatus.Critical, "c");
        Assert.Equal(SkillHealthStatus.Healthy, new SkillHealthReport([ok]).Status);
        Assert.Equal(SkillHealthStatus.Warning, new SkillHealthReport([ok, warn]).Status);
        Assert.Equal(SkillHealthStatus.Critical, new SkillHealthReport([warn, bad, ok]).Status);
    }

    [Theory]
    [InlineData("MIT", SkillHealthStatus.Healthy)]
    [InlineData("Apache-2.0", SkillHealthStatus.Healthy)]
    [InlineData("BSD-3-Clause", SkillHealthStatus.Healthy)]
    [InlineData("MPL-2.0", SkillHealthStatus.Healthy)]
    [InlineData(null, SkillHealthStatus.Warning)]
    [InlineData("", SkillHealthStatus.Warning)]
    [InlineData("source-available", SkillHealthStatus.Warning)]
    [InlineData("BUSL-1.1", SkillHealthStatus.Warning)]
    [InlineData("CC-BY-SA-4.0", SkillHealthStatus.Warning)]
    public void LicenseCheck_ClassifiesLicenses(string? license, SkillHealthStatus expected)
    {
        var check = SkillHealthChecks.CheckLicense(license);
        Assert.Equal(SkillHealthCodes.License, check.Code);
        Assert.Equal(expected, check.Status);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Fact]
    public void PinnedCheck_RequiresA40HexCommit()
    {
        Assert.Equal(SkillHealthStatus.Healthy, SkillHealthChecks.CheckPinned(Sha).Status);
        Assert.Equal(SkillHealthStatus.Warning, SkillHealthChecks.CheckPinned(null).Status);
        Assert.Equal(SkillHealthStatus.Warning, SkillHealthChecks.CheckPinned("main").Status);
        Assert.Equal(SkillHealthStatus.Warning, SkillHealthChecks.CheckPinned(Sha[..39]).Status);
    }

    // ---- frontmatter ----

    private const string ValidSkill = "---\nname: my-skill\ndescription: Does a thing. Use when needed.\n---\n\n# Body\nline\n";

    [Fact]
    public void Parse_ReadsPlainQuotedAndBlockValues()
    {
        var md = "﻿---\r\nname: \"my-skill\"\r\ndescription: >\r\n  First line\r\n  second line\r\nnotes: |\r\n  a\r\n  b\r\nmetadata:\r\n  author: x\r\nlicense: 'MIT'\r\n---\r\nBody\r\n";
        var fm = SkillFrontmatter.Parse(md);
        Assert.True(fm.HasHeader);
        Assert.Equal("my-skill", fm.Name);
        Assert.Equal("First line second line", fm.Description);
        Assert.Equal("a\nb", fm.Fields["notes"]);
        Assert.Equal("MIT", fm.Fields["license"]);
        Assert.False(fm.Fields.ContainsKey("author"));
        Assert.Equal(1, fm.BodyLineCount);
    }

    [Fact]
    public void Parse_WithoutHeader_ReportsNoHeader()
    {
        var fm = SkillFrontmatter.Parse("# Just markdown\ntext");
        Assert.False(fm.HasHeader);
        Assert.Null(fm.Name);
        Assert.Equal(2, fm.BodyLineCount);
    }

    [Fact]
    public void Validate_ValidSkill_IsHealthy()
    {
        var checks = SkillFrontmatter.Parse(ValidSkill).Validate("my-skill");
        Assert.NotEmpty(checks);
        Assert.All(checks, c => Assert.Equal(SkillHealthStatus.Healthy, c.Status));
        Assert.Contains(checks, c => c.Code == SkillHealthCodes.Frontmatter);
        Assert.Contains(checks, c => c.Code == SkillHealthCodes.Size);
    }

    [Theory]
    [InlineData("---\ndescription: d\n---\nb")]
    [InlineData("---\nname: my-skill\n---\nb")]
    [InlineData("---\nname: My_Skill\ndescription: d\n---\nb")]
    [InlineData("---\nname: -my-skill\ndescription: d\n---\nb")]
    [InlineData("---\nname: my--skill\ndescription: d\n---\nb")]
    [InlineData("---\nname: my-skill-\ndescription: d\n---\nb")]
    [InlineData("# no header")]
    public void Validate_InvalidFrontmatter_IsCritical(string md)
    {
        var report = new SkillHealthReport(SkillFrontmatter.Parse(md).Validate("my-skill"));
        Assert.Equal(SkillHealthStatus.Critical, report.Status);
        Assert.Contains(report.Checks, c => c.Code == SkillHealthCodes.Frontmatter && c.Status == SkillHealthStatus.Critical);
    }

    [Fact]
    public void Validate_NameLongerThan64_OrDescriptionLongerThan1024_IsCritical()
    {
        var longName = new string('a', 65);
        var r1 = new SkillHealthReport(SkillFrontmatter.Parse($"---\nname: {longName}\ndescription: d\n---\nb").Validate(longName));
        Assert.Equal(SkillHealthStatus.Critical, r1.Status);
        var r2 = new SkillHealthReport(SkillFrontmatter.Parse($"---\nname: my-skill\ndescription: {new string('x', 1025)}\n---\nb").Validate("my-skill"));
        Assert.Equal(SkillHealthStatus.Critical, r2.Status);
    }

    [Fact]
    public void Validate_FolderMismatch_IsWarning()
    {
        var report = new SkillHealthReport(SkillFrontmatter.Parse(ValidSkill).Validate("other-folder"));
        Assert.Equal(SkillHealthStatus.Warning, report.Status);
    }

    [Fact]
    public void Validate_BodyOver500Lines_WarnsOnSize()
    {
        var body = string.Join('\n', Enumerable.Repeat("line", 501));
        var checks = SkillFrontmatter.Parse("---\nname: my-skill\ndescription: d\n---\n" + body).Validate("my-skill");
        Assert.Contains(checks, c => c.Code == SkillHealthCodes.Size && c.Status == SkillHealthStatus.Warning);
    }

    // ---- recommender ----

    [Fact]
    public void Recommender_RanksByTagTextAndSector_WithSpanishReasons()
    {
        var spark = Entry("spark-tuning", "Optimiza trabajos de Spark", "ingenieria-datos", "spark", "ingenieria-datos");
        var react = Entry("react-patterns", "Patrones de React", "frontend", "react", "frontend");
        var sql = Entry("sql-style", "Estilo SQL", "ingenieria-datos", "sql", "ingenieria-datos");

        var result = SkillRecommender.Recommend(
            new SkillQuery("optimizar spark", new HashSet<string> { "sql" }, SkillSectors.Get("ingenieria-datos")),
            [react, sql, spark]);

        Assert.Equal(new[] { "spark-tuning", "sql-style" }, result.Select(r => r.Skill.Name));
        Assert.Contains("coincide con #spark", result[0].Reasons);
        Assert.Contains(result[0].Reasons, r => r.Contains("sector", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("coincide con #sql", result[1].Reasons);
        Assert.True(result[0].Score > result[1].Score);
    }

    [Fact]
    public void Recommender_MatchesMultiWordTagsFromText()
    {
        var pbi = Entry("model-review", "Revisa modelos", "analitica-bi", "power-bi");
        var result = SkillRecommender.Recommend(new SkillQuery("revisar un modelo de Power BI", null, null), [pbi]);
        Assert.Single(result);
        Assert.Contains("coincide con #power-bi", result[0].Reasons);
    }

    [Fact]
    public void Recommender_NeverReturnsZeroScore_AndRespectsTop()
    {
        var a = Entry("a-skill", "alpha", "frontend", "react");
        var b = Entry("b-skill", "beta", "frontend", "react");
        var c = Entry("c-skill", "gamma", "backend", "node");

        Assert.Empty(SkillRecommender.Recommend(new SkillQuery("zzz", null, null), [a, b, c]));
        Assert.Empty(SkillRecommender.Recommend(new SkillQuery(null, null, null), [a, b, c]));
        Assert.Single(SkillRecommender.Recommend(new SkillQuery("react", null, null), [a, b, c], top: 1));
    }

    [Fact]
    public void Recommender_TiesAreStableByName()
    {
        var b = Entry("b-skill", "same", "frontend", "react");
        var a = Entry("a-skill", "same", "frontend", "react");
        var first = SkillRecommender.Recommend(new SkillQuery("react", null, null), [b, a]);
        var second = SkillRecommender.Recommend(new SkillQuery("react", null, null), [a, b]);
        Assert.Equal(new[] { "a-skill", "b-skill" }, first.Select(r => r.Skill.Name));
        Assert.Equal(first.Select(r => r.Skill.Name), second.Select(r => r.Skill.Name));
    }

    [Fact]
    public void Recommender_NameAndSummaryTermsScore()
    {
        var a = Entry("migraciones-helper", "Ayuda con migraciones de bases de datos", "backend", "backend");
        var result = SkillRecommender.Recommend(new SkillQuery("migraciones", null, null), [a]);
        Assert.Single(result);
        Assert.Contains(result[0].Reasons, r => r.Contains("nombre"));
        Assert.Contains(result[0].Reasons, r => r.Contains("resumen"));
    }

    // ---- tagger ----

    [Fact]
    public void Tagger_DerivesTechnologyTagsPlusSectorAndDomain()
    {
        var tags = SkillTagger.Derive(
            "pyspark-tuning",
            "Optimiza notebooks de PySpark y Delta Lake con T-SQL y CI/CD sobre Power BI",
            SkillSectors.Get("ingenieria-datos"),
            "data-engineering");
        foreach (var expected in new[] { "pyspark", "delta", "t-sql", "ci-cd", "power-bi", "ingenieria-datos", "data-engineering" })
        {
            Assert.Contains(expected, tags);
        }

        Assert.DoesNotContain("react", tags);
    }

    [Fact]
    public void Tagger_UsesWordBoundaries_AndAlwaysReturnsTheSector()
    {
        var tags = SkillTagger.Derive("reactive-streams", "Dockerfile and goal", SkillSectors.Get("backend"), null);
        Assert.DoesNotContain("react", tags);
        Assert.DoesNotContain("docker", tags);
        Assert.Contains("backend", tags);
    }

    // ---- review fixes ----

    [Theory]
    [InlineData("Web Dev!")]
    [InlineData("C#")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_RejectsInvalidTags(string raw)
    {
        Assert.False(SkillTags.TryNormalize(raw, out var tag));
        Assert.Equal(string.Empty, tag);
    }

    [Fact]
    public void TryNormalize_AcceptsAndNormalizesValidTags_AndNull()
    {
        Assert.True(SkillTags.TryNormalize(" Power BI ", out var tag));
        Assert.Equal("power-bi", tag);
        Assert.False(SkillTags.TryNormalize(null, out _));
    }

    [Fact]
    public void Tagger_IgnoresAnInvalidSourceDomain_InsteadOfThrowing()
    {
        var tags = SkillTagger.Derive("react-patterns", "Patrones de React", SkillSectors.Get("frontend"), "Web Dev!");
        Assert.Contains("react", tags);
        Assert.Contains("frontend", tags);
    }

    [Fact]
    public void Recommender_DiscardsInvalidOrNullContextTags()
    {
        var spark = Entry("spark-tuning", "Optimiza Spark", "ingenieria-datos", "spark");
        var tags = new HashSet<string>(StringComparer.Ordinal) { "C#", "spark", null!, "Web Dev!" };

        var result = SkillRecommender.Recommend(new SkillQuery(null, tags, null), [spark]);

        Assert.Equal("spark-tuning", Assert.Single(result).Skill.Name);
    }

    [Fact]
    public void Parse_FoldsAPlainMultilineScalarWhoseValueStartsOnTheNextLine()
    {
        var md = "---\nname: my-skill\ndescription:\n  Does a thing.\n  Use when needed.\n---\nbody\n";
        var fm = SkillFrontmatter.Parse(md);
        Assert.Equal("Does a thing. Use when needed.", fm.Description);
        Assert.DoesNotContain(fm.Validate("my-skill"), c => c.Status == SkillHealthStatus.Critical);
    }

    [Fact]
    public void Parse_NestedMapOrListKeepsAnEmptyValue()
    {
        var fm = SkillFrontmatter.Parse("---\nname: my-skill\nmetadata:\n  author: x\ntags:\n  - a\n  - b\n---\nbody\n");
        Assert.Equal(string.Empty, fm.Fields["metadata"]);
        Assert.Equal(string.Empty, fm.Fields["tags"]);
    }

    [Fact]
    public void Validate_WarnsAboutDuplicateKeys_AndTheLastOneWins()
    {
        var md = "---\nname: my-skill\ndescription: first\ndescription: second\n---\nbody\n";
        var fm = SkillFrontmatter.Parse(md);
        Assert.Equal("second", fm.Description);
        var checks = fm.Validate("my-skill");
        Assert.Contains(checks, c => c.Code == SkillHealthCodes.Frontmatter
            && c.Status == SkillHealthStatus.Warning && c.Message.Contains("description", StringComparison.Ordinal));
        Assert.DoesNotContain(checks, c => c.Status == SkillHealthStatus.Critical);
    }

    [Fact]
    public void PinnedCheck_RejectsATrailingNewline()
    {
        Assert.Equal(SkillHealthStatus.Warning, SkillHealthChecks.CheckPinned(Sha + "\n").Status);
    }

    [Theory]
    [InlineData("MIT", "permisiva")]
    [InlineData("Apache-2.0", "permisiva")]
    [InlineData("BSD-3-Clause", "permisiva")]
    [InlineData("ISC", "permisiva")]
    [InlineData("0BSD", "permisiva")]
    [InlineData("Unlicense", "permisiva")]
    [InlineData("Zlib", "permisiva")]
    [InlineData("MPL-2.0", "copyleft")]
    [InlineData("GPL-3.0-only", "copyleft")]
    [InlineData("AGPL-3.0", "copyleft")]
    [InlineData("LGPL-2.1", "copyleft")]
    [InlineData("EPL-2.0", "copyleft")]
    public void LicenseCheck_DistinguishesPermissiveFromCopyleft(string license, string wording)
    {
        var check = SkillHealthChecks.CheckLicense(license);
        Assert.Equal(SkillHealthStatus.Healthy, check.Status);
        Assert.Contains(wording, check.Message, StringComparison.Ordinal);
        if (wording == "copyleft")
        {
            Assert.Contains("uso personal", check.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("copyleft", check.Message, StringComparison.Ordinal);
        }
    }
}
