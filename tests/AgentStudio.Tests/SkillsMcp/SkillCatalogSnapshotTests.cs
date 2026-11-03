using System.IO;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Skills;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// docs/14 D-30: what the local MCP server reads is a snapshot of the catalog that Agent Studio writes for one run: catalog
/// metadata only (name, summary, sector, tags, local health, activation and estimated cost), never the body of a SKILL.md
/// nor a path. The writer (the service) and the reader (the server) share one JSON format.
/// </summary>
public sealed class SkillCatalogSnapshotTests
{
    private const string ProjectId = "6f1c2b0e-0000-4000-8000-0000000000bb";
    private const string Sha = "8a1541c4a3ffa5a20a5a91de0dcf3f0bab1d1ef4";

    private static readonly IReadOnlyList<AgentId> Roster = [Aid("orquestador"), Aid("ejecutor"), Aid("juez")];

    internal static CatalogSkill Skill(string name, string summary, SkillSector sector, string[] tags, string? localPath = "C:\\index\\domains\\x\\skills\\n", SkillDistribution mode = SkillDistribution.Vendored) =>
        new(
            new SkillEntry(new SkillId(name), name, summary, sector, tags, new SkillSource("owner/repo", "skills/" + name, "main", Sha, "MIT", mode)),
            localPath);

    private static readonly CatalogSkill Spark = Skill("spark-optimization", "Optimiza trabajos de Spark y Delta.", SkillSectors.IngenieriaDatos, ["spark", "delta", "ingenieria-datos"]);
    private static readonly CatalogSkill Wpf = Skill("wpf-mvvm", "Patrones MVVM para aplicaciones de escritorio en WPF.", SkillSectors.Escritorio, ["wpf", "dotnet", "escritorio"]);
    private static readonly CatalogSkill Linked = Skill("diseño-accesible", "Accesibilidad y contraste en interfaces.", SkillSectors.UxUi, ["accessibility", "ux-ui"], localPath: null, mode: SkillDistribution.Linked);

    private static SkillCatalogSnapshot Build(IReadOnlyList<SkillActivation>? activations = null, IReadOnlyDictionary<SkillId, SkillHealthStatus>? health = null) =>
        SkillCatalogSnapshotBuilder.Build(T0, ProjectId, [Wpf, Linked, Spark], activations ?? [], Roster, health ?? new Dictionary<SkillId, SkillHealthStatus>());

    [Fact]
    public void Every_skill_keeps_its_catalog_metadata_with_sorted_tags_and_its_estimated_cost()
    {
        var snapshot = Build();

        var spark = Assert.Single(snapshot.Skills, s => s.Id == "spark-optimization");
        Assert.Equal(("spark-optimization", "Optimiza trabajos de Spark y Delta.", "ingenieria-datos", "Ingeniería de datos"), (spark.Name, spark.Summary, spark.SectorId, spark.SectorTitle));
        Assert.Equal(["delta", "ingenieria-datos", "spark"], spark.Tags);
        Assert.Equal(SkillContextCost.Of(Spark.Entry).Value, spark.EstimatedContextTokens);
        Assert.Equal(T0, snapshot.At);
    }

    [Fact]
    public void The_skills_go_in_the_order_of_the_sectors_and_then_by_name()
    {
        var snapshot = Build();

        // Ingeniería de datos, UX/UI and Escritorio, in the fixed order of SkillSectors.All.
        Assert.Equal(["spark-optimization", "diseño-accesible", "wpf-mvvm"], snapshot.Skills.Select(s => s.Id));
    }

    [Fact]
    public void A_skill_without_a_local_copy_is_marked_as_linked()
    {
        var snapshot = Build();

        Assert.True(Assert.Single(snapshot.Skills, s => s.Id == "diseño-accesible").Linked);
        Assert.False(Assert.Single(snapshot.Skills, s => s.Id == "wpf-mvvm").Linked);
    }

    [Fact]
    public void The_active_agents_are_the_roster_agents_the_resolver_enables_it_for()
    {
        // Global on, the judge off at agent scope: every agent but the judge has it.
        var activations = new[]
        {
            new SkillActivation(Spark.Entry.Id, SkillScopeKind.Global, null, true),
            new SkillActivation(Spark.Entry.Id, SkillScopeKind.Agent, "juez", false),
            new SkillActivation(Wpf.Entry.Id, SkillScopeKind.Agent, "ejecutor", true),
        };

        var snapshot = Build(activations);

        Assert.Equal(["ejecutor", "orquestador"], Assert.Single(snapshot.Skills, s => s.Id == "spark-optimization").ActiveAgents);
        Assert.Equal(["ejecutor"], Assert.Single(snapshot.Skills, s => s.Id == "wpf-mvvm").ActiveAgents);
        Assert.Empty(Assert.Single(snapshot.Skills, s => s.Id == "diseño-accesible").ActiveAgents);
    }

    [Fact]
    public void Without_a_project_the_project_decisions_do_not_apply_and_the_global_ones_do()
    {
        var activations = new[]
        {
            new SkillActivation(Spark.Entry.Id, SkillScopeKind.Project, ProjectId, true),
            new SkillActivation(Wpf.Entry.Id, SkillScopeKind.Global, null, true),
        };

        var snapshot = SkillCatalogSnapshotBuilder.Build(T0, projectId: null, [Wpf, Spark], activations, Roster, new Dictionary<SkillId, SkillHealthStatus>());

        Assert.Empty(Assert.Single(snapshot.Skills, s => s.Id == "spark-optimization").ActiveAgents);
        Assert.Equal(["ejecutor", "juez", "orquestador"], Assert.Single(snapshot.Skills, s => s.Id == "wpf-mvvm").ActiveAgents);
    }

    [Fact]
    public void The_local_health_comes_from_what_was_read_and_is_unknown_otherwise()
    {
        var snapshot = Build(health: new Dictionary<SkillId, SkillHealthStatus> { [Wpf.Entry.Id] = SkillHealthStatus.Warning });

        Assert.Equal(SkillHealthStatus.Warning, Assert.Single(snapshot.Skills, s => s.Id == "wpf-mvvm").Health);
        Assert.Equal(SkillHealthStatus.Unknown, Assert.Single(snapshot.Skills, s => s.Id == "spark-optimization").Health);
    }

    [Fact]
    public void The_json_goes_and_comes_back_unchanged()
    {
        var snapshot = Build([new SkillActivation(Spark.Entry.Id, SkillScopeKind.Project, ProjectId, true)], new Dictionary<SkillId, SkillHealthStatus> { [Spark.Entry.Id] = SkillHealthStatus.Healthy });

        var json = SkillCatalogSnapshotJson.Write(snapshot);
        var read = SkillCatalogSnapshotJson.Read(json);

        Assert.Equal(snapshot.At, read.At);
        Assert.Equal(snapshot.Skills.Count, read.Skills.Count);
        for (var i = 0; i < snapshot.Skills.Count; i++)
        {
            var (expected, actual) = (snapshot.Skills[i], read.Skills[i]);
            Assert.Equal((expected.Id, expected.Name, expected.Summary, expected.SectorId, expected.SectorTitle, expected.Health, expected.Linked, expected.EstimatedContextTokens), (actual.Id, actual.Name, actual.Summary, actual.SectorId, actual.SectorTitle, actual.Health, actual.Linked, actual.EstimatedContextTokens));
            Assert.Equal(expected.Tags, actual.Tags);
            Assert.Equal(expected.ActiveAgents, actual.ActiveAgents);
        }
    }

    [Fact]
    public void The_json_names_its_format_and_writes_the_health_as_text()
    {
        var json = SkillCatalogSnapshotJson.Write(Build());

        Assert.Contains("\"schema\":\"agentstudio-skills-snapshot/1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"health\":\"Unknown\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_holds_no_path_nor_source_of_the_catalog()
    {
        var json = SkillCatalogSnapshotJson.Write(Build());

        Assert.DoesNotContain("C:\\\\index", json, StringComparison.Ordinal);
        Assert.DoesNotContain("owner/repo", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha, json, StringComparison.Ordinal);
        Assert.DoesNotContain("SKILL.md", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"schema\":\"otra-cosa/1\",\"at\":\"2026-01-01T10:00:00+00:00\",\"skills\":[]}")]
    [InlineData("{\"at\":\"2026-01-01T10:00:00+00:00\",\"skills\":[]}")]
    [InlineData("{\"schema\":\"agentstudio-skills-snapshot/1\",\"at\":\"2026-01-01T10:00:00+00:00\"}")]
    [InlineData("{\"schema\":\"agentstudio-skills-snapshot/1\",\"at\":\"2026-01-01T10:00:00+00:00\",\"skills\":[{\"id\":\"a\",\"name\":\"a\",\"summary\":\"s\",\"sectorId\":\"frontend\",\"sectorTitle\":\"Frontend\",\"tags\":null,\"health\":\"Healthy\",\"activeAgents\":[],\"linked\":false,\"estimatedContextTokens\":1}]}")]
    [InlineData("{\"schema\":\"agentstudio-skills-snapshot/1\",\"at\":\"2026-01-01T10:00:00+00:00\",\"skills\":[{\"id\":\"a\",\"name\":\"a\",\"summary\":\"s\",\"sectorId\":\"frontend\",\"sectorTitle\":\"Frontend\",\"tags\":[],\"health\":\"Sana\",\"activeAgents\":[],\"linked\":false,\"estimatedContextTokens\":1}]}")]
    [InlineData("{\"schema\":\"agentstudio-skills-snapshot/1\",\"at\":\"2026-01-01T10:00:00+00:00\",\"skills\":[{\"id\":\" \",\"name\":\"a\",\"summary\":\"s\",\"sectorId\":\"frontend\",\"sectorTitle\":\"Frontend\",\"tags\":[],\"health\":\"Healthy\",\"activeAgents\":[],\"linked\":false,\"estimatedContextTokens\":1}]}")]
    [InlineData("{\"schema\":\"agentstudio-skills-snapshot/1\",\"at\":\"2026-01-01T10:00:00+00:00\",\"skills\":[{\"id\":\"a\",\"name\":\"a\",\"summary\":\"s\",\"sectorId\":\"frontend\",\"sectorTitle\":\"Frontend\",\"tags\":[],\"health\":\"Healthy\",\"activeAgents\":[],\"linked\":false,\"estimatedContextTokens\":-1}]}")]
    public void A_snapshot_that_is_not_one_is_refused(string json)
    {
        Assert.Throws<InvalidDataException>(() => SkillCatalogSnapshotJson.Read(json));
    }

    [Fact]
    public void Two_skills_with_the_same_id_are_refused()
    {
        var skill = Build().Skills[0];
        var json = SkillCatalogSnapshotJson.Write(new SkillCatalogSnapshot(T0, [skill, skill]));

        Assert.Throws<InvalidDataException>(() => SkillCatalogSnapshotJson.Read(json));
    }

    // ---- search (the screen and search_skills share it) ----

    [Theory]
    [InlineData("spark", "spark-optimization")]
    [InlineData("SPARK", "spark-optimization")]
    [InlineData("escritorio wpf", "wpf-mvvm")]
    [InlineData("diseno", "diseño-accesible")]
    [InlineData("contraste", "diseño-accesible")]
    [InlineData("accessibility", "diseño-accesible")]
    public void The_search_finds_by_name_summary_and_tags_whatever_the_case_and_accents(string query, string expected)
    {
        Assert.Equal([expected], SkillSearch.Search(Build().Skills, query, null, null, 20).Select(s => s.Id));
    }

    [Fact]
    public void Every_word_of_the_search_must_match()
    {
        Assert.Empty(SkillSearch.Search(Build().Skills, "spark wpf", null, null, 20));
    }

    [Fact]
    public void An_empty_search_lists_everything_and_the_filters_narrow_it()
    {
        var skills = Build().Skills;

        Assert.Equal(3, SkillSearch.Search(skills, "  ", null, null, 20).Count);
        Assert.Equal(["wpf-mvvm"], SkillSearch.Search(skills, null, "escritorio", null, 20).Select(s => s.Id));
        Assert.Equal(["spark-optimization"], SkillSearch.Search(skills, null, null, ["Delta", "spark"], 20).Select(s => s.Id));
        Assert.Empty(SkillSearch.Search(skills, null, null, ["delta", "wpf"], 20));
    }

    [Fact]
    public void A_name_hit_ranks_before_a_summary_hit_and_the_top_cuts_the_list()
    {
        CatalogSkill[] catalog =
        [
            Skill("guia-delta", "Guía de tablas.", SkillSectors.Documentacion, []),
            Skill("tablas", "Tablas Delta en el lago.", SkillSectors.IngenieriaDatos, []),
        ];
        var skills = SkillCatalogSnapshotBuilder.Build(T0, ProjectId, catalog, [], Roster, new Dictionary<SkillId, SkillHealthStatus>()).Skills;

        Assert.Equal(["guia-delta", "tablas"], SkillSearch.Search(skills, "delta", null, null, 20).Select(s => s.Id));
        Assert.Equal(["guia-delta"], SkillSearch.Search(skills, "delta", null, null, 1).Select(s => s.Id));
    }

    [Fact]
    public void Matches_says_whether_one_skill_passes_the_search()
    {
        var spark = Build().Skills.Single(s => s.Id == "spark-optimization");

        Assert.True(SkillSearch.Matches(spark, "delta"));
        Assert.True(SkillSearch.Matches(spark, null));
        Assert.False(SkillSearch.Matches(spark, "wpf"));
    }
}
