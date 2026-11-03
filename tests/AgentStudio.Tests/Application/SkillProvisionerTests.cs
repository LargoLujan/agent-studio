using System.IO;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Application;

public sealed class SkillProvisionerTests
{
    private const string Project = "p1";
    private const string BaseDirectory = "C:/AgentStudio/skills";
    private const string WorkDirectory = "C:/AgentStudio/wt/T1";

    private sealed class FakeCatalog(params CatalogSkill[] skills) : ISkillCatalog
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<CatalogSkill>> ListAsync(CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CatalogSkill>>(skills);
        }
    }

    private sealed class FakeActivations(params SkillActivation[] activations) : ISkillActivationStore
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct)
        {
            Asked.Add(projectId);
            return Task.FromResult<IReadOnlyList<SkillActivation>>(activations);
        }

        public Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct) =>
            throw new NotSupportedException("The provisioner only reads activations.");
    }

    private sealed class RecordingMaterializer : ISkillMaterializer
    {
        public List<(ProviderId Cli, IReadOnlyList<ActivatedSkill> Skills, SkillSessionContext Session, CancellationToken Token)> Materialized { get; } = [];

        public List<string> Cleaned { get; } = [];

        public Exception? FailMaterialize { get; set; }

        public Exception? FailCleanup { get; set; }

        public Task<string?> MaterializeAsync(ProviderId cli, IReadOnlyList<ActivatedSkill> activatedSkills, SkillSessionContext session, CancellationToken ct)
        {
            if (FailMaterialize is { } failure)
            {
                throw failure;
            }

            Materialized.Add((cli, activatedSkills, session, ct));
            return Task.FromResult<string?>(activatedSkills.Count == 0 ? null : Path.Combine(session.SessionBaseDirectory, "claude-plugin-" + session.RunId));
        }

        public void Cleanup(string materializedDirectory)
        {
            if (FailCleanup is { } failure)
            {
                throw failure;
            }

            Cleaned.Add(materializedDirectory);
        }
    }

    private static CatalogSkill Skill(string id, string? localPath = null) => new(
        new SkillEntry(
            new SkillId(id),
            id,
            "summary",
            SkillSectors.IngenieriaSoftware,
            ["testing"],
            new SkillSource("repo", id, null, null, "MIT", localPath is null ? SkillDistribution.Linked : SkillDistribution.Vendored)),
        localPath);

    private static CatalogSkill Vendored(string id) => Skill(id, "C:/skills-index/" + id);

    private static SkillActivation On(string skill, SkillScopeKind scope, string? scopeId = null) => new(new SkillId(skill), scope, scopeId, true);

    private static SkillActivation Off(string skill, SkillScopeKind scope, string? scopeId = null) => new(new SkillId(skill), scope, scopeId, false);

    private static AgentDefinition Agent(string id = "ejecutor", ProviderId? provider = null) => new(
        Aid(id),
        id,
        AgentRole.Executor,
        new ModelBinding(provider ?? ProviderId.ClaudeCode, VendorId.Anthropic),
        "system",
        [],
        [],
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
        3,
        MemoryPolicy.None,
        new ReviewPolicy(false, false));

    private static (SkillProvisioner Sut, RecordingMaterializer Materializer) Build(FakeCatalog catalog, FakeActivations activations)
    {
        var materializer = new RecordingMaterializer();
        return (new SkillProvisioner(catalog, activations, materializer, new SkillProvisioningOptions(BaseDirectory)), materializer);
    }

    private static Task<ProvisionedSkills> Provision(SkillProvisioner sut, AgentDefinition? agent = null, string runKey = "T1-1", CancellationToken? ct = null) =>
        sut.ProvisionAsync(Project, agent ?? Agent(), runKey, WorkDirectory, ct ?? TestContext.Current.CancellationToken);

    // ---- which skills ----

    [Fact]
    public async Task Nothing_is_provisioned_unless_it_was_activated()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a"), Vendored("b")), new FakeActivations());

        var result = await Provision(sut);

        Assert.Empty(result.Skills);
        Assert.Null(result.Directory);
        Assert.Empty(result.Warnings);
        Assert.Empty(materializer.Materialized);
    }

    [Fact]
    public async Task The_agent_wins_over_the_project_and_the_project_over_the_global_setting()
    {
        var catalog = new FakeCatalog(Vendored("global-only"), Vendored("off-in-project"), Vendored("on-for-agent"), Vendored("off-for-agent"), Vendored("other-agent-only"));
        var activations = new FakeActivations(
            On("global-only", SkillScopeKind.Global),
            On("off-in-project", SkillScopeKind.Global),
            Off("off-in-project", SkillScopeKind.Project, Project),
            Off("on-for-agent", SkillScopeKind.Project, Project),
            On("on-for-agent", SkillScopeKind.Agent, "ejecutor"),
            On("off-for-agent", SkillScopeKind.Project, Project),
            Off("off-for-agent", SkillScopeKind.Agent, "ejecutor"),
            On("other-agent-only", SkillScopeKind.Agent, "someone-else"));
        var (sut, materializer) = Build(catalog, activations);

        var result = await Provision(sut);

        Assert.Equal(["global-only", "on-for-agent"], result.Skills.Select(s => s.Value));
        Assert.Equal(["global-only", "on-for-agent"], materializer.Materialized.Single().Skills.Select(s => s.Skill.Id.Value));
    }

    [Fact]
    public async Task Two_agents_of_the_same_project_get_different_skills()
    {
        var catalog = new FakeCatalog(Vendored("tdd"), Vendored("security"));
        var activations = new FakeActivations(On("tdd", SkillScopeKind.Agent, "ejecutor"), On("security", SkillScopeKind.Agent, "juez"));
        var (sut, _) = Build(catalog, activations);

        var executor = await Provision(sut, Agent("ejecutor"));
        var judge = await Provision(sut, Agent("juez"));

        Assert.Equal(["tdd"], executor.Skills.Select(s => s.Value));
        Assert.Equal(["security"], judge.Skills.Select(s => s.Value));
    }

    [Fact]
    public async Task The_last_decision_of_a_scope_wins()
    {
        var activations = new FakeActivations(On("a", SkillScopeKind.Project, Project), Off("a", SkillScopeKind.Project, Project), On("b", SkillScopeKind.Project, Project));
        var (sut, _) = Build(new FakeCatalog(Vendored("a"), Vendored("b")), activations);

        var result = await Provision(sut);

        Assert.Equal(["b"], result.Skills.Select(s => s.Value));
    }

    [Fact]
    public async Task Activations_of_another_project_do_not_apply()
    {
        var (sut, _) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Project, "p2")));

        var result = await Provision(sut);

        Assert.Empty(result.Skills);
    }

    [Fact]
    public async Task Skills_are_materialized_in_a_deterministic_order_whatever_the_catalog_order()
    {
        var catalog = new FakeCatalog(Vendored("zeta"), Vendored("alpha"), Vendored("mid"));
        var activations = new FakeActivations(On("zeta", SkillScopeKind.Global), On("alpha", SkillScopeKind.Global), On("mid", SkillScopeKind.Global));
        var (sut, materializer) = Build(catalog, activations);

        var result = await Provision(sut);

        Assert.Equal(["alpha", "mid", "zeta"], result.Skills.Select(s => s.Value));
        Assert.Equal(["alpha", "mid", "zeta"], materializer.Materialized.Single().Skills.Select(s => s.Skill.Id.Value));
    }

    [Fact]
    public async Task A_skill_listed_twice_in_the_catalog_is_provisioned_once()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a"), Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));

        var result = await Provision(sut);

        Assert.Equal(["a"], result.Skills.Select(s => s.Value));
        Assert.Single(materializer.Materialized.Single().Skills);
    }

    // ---- what is skipped ----

    [Fact]
    public async Task A_linked_skill_cannot_be_provisioned_and_the_user_is_told()
    {
        var catalog = new FakeCatalog(Vendored("local"), Skill("linked"));
        var activations = new FakeActivations(On("local", SkillScopeKind.Global), On("linked", SkillScopeKind.Global));
        var (sut, materializer) = Build(catalog, activations);

        var result = await Provision(sut);

        Assert.Equal(["local"], result.Skills.Select(s => s.Value));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("linked", warning, StringComparison.Ordinal);
        Assert.Contains("enlazada", warning, StringComparison.Ordinal);
        Assert.Equal(["local"], materializer.Materialized.Single().Skills.Select(s => s.Skill.Id.Value));
    }

    [Fact]
    public async Task A_skill_that_is_active_but_missing_from_the_catalog_is_reported()
    {
        var activations = new FakeActivations(On("gone", SkillScopeKind.Project, Project), On("gone-but-off", SkillScopeKind.Project, Project), Off("gone-but-off", SkillScopeKind.Project, Project));
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), activations);

        var result = await Provision(sut);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("gone", warning, StringComparison.Ordinal);
        Assert.Contains("catálogo", warning, StringComparison.Ordinal);
        Assert.Empty(result.Skills);
        Assert.Empty(materializer.Materialized);
    }

    [Fact]
    public async Task When_only_a_linked_skill_is_active_nothing_is_materialized_but_the_warning_remains()
    {
        var (sut, materializer) = Build(new FakeCatalog(Skill("linked")), new FakeActivations(On("linked", SkillScopeKind.Global)));

        var result = await Provision(sut);

        Assert.Null(result.Directory);
        Assert.Empty(result.Skills);
        Assert.Single(result.Warnings);
        Assert.Empty(materializer.Materialized);
    }

    // ---- how it materializes ----

    [Fact]
    public async Task The_materializer_gets_the_cli_of_the_agent_and_the_session_of_the_run()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));
        using var source = new CancellationTokenSource();

        var result = await Provision(sut, Agent(provider: ProviderId.CodexCli), "run_1-T2-1", source.Token);

        var call = Assert.Single(materializer.Materialized);
        Assert.Equal(ProviderId.CodexCli, call.Cli);
        Assert.Equal(new SkillSessionContext("run_1-T2-1", BaseDirectory, WorkDirectory), call.Session);
        Assert.Equal(source.Token, call.Token);
        Assert.Equal(ProviderId.CodexCli, result.Cli);
        Assert.Equal(Path.Combine(BaseDirectory, "claude-plugin-run_1-T2-1"), result.Directory);
        Assert.Equal("C:/skills-index/a", call.Skills.Single().LocalPath);
    }

    [Fact]
    public async Task The_catalog_and_the_activations_of_the_right_project_are_read_once_per_call()
    {
        var catalog = new FakeCatalog(Vendored("a"));
        var activations = new FakeActivations(On("a", SkillScopeKind.Global));
        var (sut, _) = Build(catalog, activations);

        await Provision(sut);

        Assert.Equal(1, catalog.Calls);
        Assert.Equal([Project], activations.Asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("../x")]
    [InlineData("a.b")]
    public async Task A_run_key_that_is_not_safe_for_a_folder_name_is_rejected_before_anything_is_read(string runKey)
    {
        var catalog = new FakeCatalog(Vendored("a"));
        var (sut, materializer) = Build(catalog, new FakeActivations(On("a", SkillScopeKind.Global)));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Provision(sut, runKey: runKey));

        Assert.Equal(0, catalog.Calls);
        Assert.Empty(materializer.Materialized);
    }

    [Fact]
    public async Task Other_arguments_are_checked()
    {
        var (sut, _) = Build(new FakeCatalog(), new FakeActivations());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.ProvisionAsync(" ", Agent(), "k", WorkDirectory, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.ProvisionAsync(Project, null!, "k", WorkDirectory, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.ProvisionAsync(Project, Agent(), "k", " ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_call_provisions_nothing()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provision(sut, ct: source.Token));

        Assert.Empty(materializer.Materialized);
    }

    [Fact]
    public async Task A_materializer_failure_reaches_the_caller_and_leaves_nothing_to_release()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));
        materializer.FailMaterialize = new IOException("already exists");

        await Assert.ThrowsAsync<IOException>(() => Provision(sut));

        Assert.Empty(materializer.Cleaned);
    }

    [Fact]
    public void Options_need_a_session_folder()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillProvisioningOptions(" "));
    }

    // ---- releasing ----

    [Fact]
    public async Task Release_cleans_what_was_created()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));
        var result = await Provision(sut);

        sut.Release(result);

        Assert.Equal([result.Directory!], materializer.Cleaned);
    }

    [Fact]
    public void Release_with_nothing_provisioned_does_nothing()
    {
        var (sut, materializer) = Build(new FakeCatalog(), new FakeActivations());

        sut.Release(new ProvisionedSkills(ProviderId.ClaudeCode, null, [], []));

        Assert.Empty(materializer.Cleaned);
    }

    [Fact]
    public async Task Release_does_not_let_a_file_system_error_hide_the_failure_that_made_the_caller_release()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));
        var result = await Provision(sut);

        materializer.FailCleanup = new IOException("file in use");
        sut.Release(result);
        materializer.FailCleanup = new UnauthorizedAccessException("denied");
        sut.Release(result);

        Assert.Empty(materializer.Cleaned);
    }

    [Fact]
    public async Task Release_does_not_swallow_a_programming_error()
    {
        var (sut, materializer) = Build(new FakeCatalog(Vendored("a")), new FakeActivations(On("a", SkillScopeKind.Global)));
        var result = await Provision(sut);
        materializer.FailCleanup = new ArgumentException("not canonical");

        Assert.Throws<ArgumentException>(() => sut.Release(result));
    }

    [Fact]
    public void Release_rejects_null()
    {
        var (sut, _) = Build(new FakeCatalog(), new FakeActivations());

        Assert.Throws<ArgumentNullException>(() => sut.Release(null!));
    }
}
