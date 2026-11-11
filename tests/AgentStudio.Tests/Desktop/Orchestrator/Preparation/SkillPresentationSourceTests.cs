using AgentStudio.Application.Projects;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Tests.Application.Setup;
using AgentStudio.Tests.Domain.Orchestration;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

/// <summary>
/// What the CLI would present of the skills an agent gets (docs/17 D-13, P49): the skills that are on for it as the provisioner
/// decides them, measured on the <c>SKILL.md</c> of each, without materializing anything. Temporary folders, never the catalog of
/// the person.
/// </summary>
public sealed class SkillPresentationSourceTests
{
    private static readonly ProjectContext Project = new("p-1", @"C:\repo");

    private static AgentDefinition Agent(string id) => PlanFixtures.Agent(id, AgentRole.Executor, VendorId.Anthropic);

    /// <summary>A skill whose presentation is its name and its description, with a body that is not counted.</summary>
    private static CatalogSkill Skill(TempDirectory root, string name, string description)
    {
        root.WriteFile($"{name}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n# {name}\nThe body is not what the CLI presents.\n");
        return FakeSkillCatalog.Skill(name, []) with { LocalPath = root.Combine(name) };
    }

    private static SkillActivation On(CatalogSkill skill, SkillScopeKind scope, string? scopeId = null) => new(skill.Entry.Id, scope, scopeId, Enabled: true);

    private static async Task<FakeActivations> ActivationsAsync(params SkillActivation[] decisions)
    {
        var store = new FakeActivations();
        foreach (var decision in decisions)
        {
            await store.AppendAsync(Project.ProjectId, decision, "test", TestContext.Current.CancellationToken);
        }

        return store;
    }

    [Fact]
    public async Task Counts_the_name_and_the_description_of_the_skills_that_are_on_for_the_agent_and_no_others()
    {
        using var root = new TempDirectory();
        var pdf = Skill(root, "pdf", "Work with PDFs");
        var docx = Skill(root, "docx", "Word documents");
        var off = Skill(root, "off", "Never turned on");
        var linked = FakeSkillCatalog.Skill("linked", [], linked: true);
        var source = new SkillPresentationSource(
            new FakeSkillCatalog(pdf, docx, off, linked),
            await ActivationsAsync(On(pdf, SkillScopeKind.Project, Project.ProjectId), On(docx, SkillScopeKind.Agent, "ejecutor"), On(linked, SkillScopeKind.Global)));

        var ejecutor = await source.CharactersAsync(Project, Agent("ejecutor"), TestContext.Current.CancellationToken);
        var other = await source.CharactersAsync(Project, Agent("ejecutor2"), TestContext.Current.CancellationToken);

        // The one of the project reaches every agent, the one of the agent only its own; a skill nobody turned on, and one that is
        // linked with no local copy, add nothing.
        Assert.Equal((3 + 14) + (4 + 14), ejecutor);
        Assert.Equal(3 + 14, other);
    }

    [Fact]
    public async Task A_skill_turned_off_for_the_agent_over_the_project_is_not_counted()
    {
        using var root = new TempDirectory();
        var pdf = Skill(root, "pdf", "Work with PDFs");
        var source = new SkillPresentationSource(
            new FakeSkillCatalog(pdf),
            await ActivationsAsync(On(pdf, SkillScopeKind.Project, Project.ProjectId), new SkillActivation(pdf.Entry.Id, SkillScopeKind.Agent, "ejecutor", Enabled: false)));

        Assert.Equal(0, await source.CharactersAsync(Project, Agent("ejecutor"), TestContext.Current.CancellationToken));
        Assert.Equal(3 + 14, await source.CharactersAsync(Project, Agent("ejecutor2"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Without_the_catalog_or_the_activations_there_is_no_service_and_with_both_there_is()
    {
        Assert.Null(SkillPresentationSource.For(null, new FakeActivations()));
        Assert.Null(SkillPresentationSource.For(new FakeSkillCatalog(), null));
        Assert.NotNull(SkillPresentationSource.For(new FakeSkillCatalog(), new FakeActivations()));
    }
}
