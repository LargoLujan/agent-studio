using AgentStudio.Application.Prompts;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Memory.Context;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Desktop.Orchestrator;
using AgentStudio.Tests.Domain.Orchestration;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// How much context a task would start with before any run (docs/17 D-13 and D-18, P49): the facade over what the engine uses to
/// launch it. The size is the prompt the engine builds, measured in the parts it records, so these tests move one piece of the
/// prompt at a time and look at the part that moves.
/// </summary>
public sealed class ExecutorContextPreviewTests
{
    private static readonly ProjectContext Project = new("p-1", @"C:\repo");

    private static FrozenRequirementSet Requirements(string mandatory = "Add Calc.Multiply") =>
        FrozenRequirementSet.Freeze(
            [
                Req("R1", RequirementPriority.Mandatory, mandatory, Crit("c1", VerificationMethod.AutomatedTest)),
                Req("R2", RequirementPriority.Desirable, "Document it", Crit("c2", VerificationMethod.HumanReview)),
            ],
            T0);

    private static TaskSpec Spec(params string[] covers) => SpecAbout("Do T1", covers);

    private static TaskSpec SpecAbout(string objective, params string[] covers) =>
        PlanFixtures.Spec("T1") with
        {
            Agent = Aid("ejecutor"),
            Objective = objective,
            Workspace = WorkspaceMode.Write,
            WriteScope = ["src/Calc/"],
            Covers = [.. covers.Select(Rid)],
        };

    private static AgentDefinition Executor(MemoryPolicy memory = MemoryPolicy.None) =>
        PlanFixtures.Agent("ejecutor", AgentRole.Executor, VendorId.Anthropic) with { Memory = memory };

    private static int Of(ExecutorContextSize size, ContextPartKind kind) => size.Parts.Where(p => p.Kind == kind).Sum(p => p.Characters);

    private static ContextPack Pack(int characters) =>
        new(
            [new ContextItem("m1", ContextTier.Decision, "Decision", "System", new string('x', characters), HashA)],
            [],
            characters,
            MetricValue<long>.EstimatedBySystem((characters + 3L) / 4, ContextPacker.TokenEstimateSource),
            HashB);

    private sealed class RecordingPacker(ContextPack? pack = null) : IContextPacker
    {
        public List<ContextPackRequest> Requests { get; } = [];

        public Task<ContextPack> PackAsync(ProjectContext project, ContextPackRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(pack ?? Pack(100));
        }
    }

    [Fact]
    public void The_size_is_the_prompt_of_the_first_attempt_in_the_parts_the_engine_records()
    {
        var size = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), Backends.Profile, Executor(), memory: null, brief: null);

        Assert.True(size.Characters > 0);
        Assert.Equal(size.Characters, size.Parts.Sum(p => p.Characters));
        Assert.Equal((size.Characters + 3L) / 4, size.Tokens);
        Assert.True(Of(size, ContextPartKind.Protocol) > 0);
        Assert.True(Of(size, ContextPartKind.Requirements) > 0);
        Assert.Equal(0, Of(size, ContextPartKind.Memory));
        Assert.Equal(0, Of(size, ContextPartKind.ProjectBrief));
        Assert.Equal(0, Of(size, ContextPartKind.Upstream));
        Assert.Equal(size.Parts.OrderBy(p => p.Kind).ToList(), size.Parts);
    }

    [Fact]
    public void The_objective_and_the_requirements_move_their_own_part_by_exactly_their_characters()
    {
        var baseline = ExecutorContextPreview.Measure(SpecAbout("Do T1", "R1"), Requirements(), null, Executor(), null, null);

        var longerObjective = ExecutorContextPreview.Measure(SpecAbout("Do T1" + new string('y', 50), "R1"), Requirements(), null, Executor(), null, null);
        var longerRequirement = ExecutorContextPreview.Measure(SpecAbout("Do T1", "R1"), Requirements("Add Calc.Multiply" + new string('z', 100)), null, Executor(), null, null);

        Assert.Equal(Of(baseline, ContextPartKind.Protocol) + 50, Of(longerObjective, ContextPartKind.Protocol));
        Assert.Equal(Of(baseline, ContextPartKind.Requirements), Of(longerObjective, ContextPartKind.Requirements));
        Assert.Equal(Of(baseline, ContextPartKind.Requirements) + 100, Of(longerRequirement, ContextPartKind.Requirements));
        Assert.Equal(Of(baseline, ContextPartKind.Protocol), Of(longerRequirement, ContextPartKind.Protocol));
    }

    [Fact]
    public void The_steps_of_the_profile_the_task_names_are_in_the_prompt()
    {
        var without = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), Backends.Profile, Executor(), null, null);
        var withSteps = ExecutorContextPreview.Measure(Spec("R1") with { VerificationSteps = ["build", "test"] }, Requirements(), Backends.Profile, Executor(), null, null);

        // «- build: Compilar» and «- test: Probar», one line each, in place of «- (ninguna)».
        var line = Environment.NewLine.Length;
        Assert.Equal(without.Characters + ("- build: Compilar".Length + line) + ("- test: Probar".Length + line) - ("- (ninguna)".Length + line), withSteps.Characters);
    }

    [Fact]
    public void The_mandatory_characters_are_those_of_the_mandatory_requirements_the_task_covers()
    {
        var one = ExecutorContextPreview.MandatoryCharacters(Spec("R1"), Requirements());
        var longer = ExecutorContextPreview.MandatoryCharacters(Spec("R1"), Requirements("Add Calc.Multiply" + new string('z', 100)));

        Assert.True(one > 0);
        Assert.Equal(one + 100, longer);
        Assert.Equal(0, ExecutorContextPreview.MandatoryCharacters(Spec("R2"), Requirements()));
        Assert.Equal(0, ExecutorContextPreview.MandatoryCharacters(Spec(), Requirements()));
        Assert.Equal(one, ExecutorContextPreview.MandatoryCharacters(Spec("R1", "R1"), Requirements()));
    }

    [Fact]
    public void The_brief_reaches_an_executor_and_never_a_judge_or_an_auditor()
    {
        var brief = new ProjectBrief("brief-1", new string('b', 300), HashA, T0);
        var none = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(), null, null);

        var executor = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(), null, brief);
        var judge = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, PlanFixtures.Agent("juez", AgentRole.Judge, VendorId.OpenAI), null, brief);

        Assert.True(Of(executor, ContextPartKind.ProjectBrief) > 300);
        Assert.Equal(0, Of(judge, ContextPartKind.ProjectBrief));
        Assert.Equal(0, Of(none, ContextPartKind.ProjectBrief));
        Assert.True(ExecutorContextPreview.ReceivesBrief(Executor()));
        Assert.False(ExecutorContextPreview.ReceivesBrief(PlanFixtures.Agent("auditor", AgentRole.Auditor, VendorId.OpenAI)));
    }

    [Fact]
    public void The_memory_of_the_pack_goes_in_as_the_prompt_puts_it()
    {
        var none = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(MemoryPolicy.ReadSelected), null, null);

        var packed = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(MemoryPolicy.ReadSelected), Pack(800), null);

        // The 800 characters of the entry and the block that holds them: tags, the sentence that says it is data.
        Assert.True(Of(packed, ContextPartKind.Memory) > 800);
        Assert.Equal(0, Of(none, ContextPartKind.Memory));
        Assert.True(packed.Tokens > none.Tokens + 200);
    }

    [Fact]
    public async Task Only_an_agent_that_reads_memory_asks_the_packer_and_it_asks_as_the_engine_does_without_a_focus()
    {
        var packer = new RecordingPacker();
        var ct = TestContext.Current.CancellationToken;

        var without = await ExecutorContextPreview.PackAsync(packer, Project, Executor(), Spec("R1"), Requirements(), ct);
        Assert.Null(without);
        Assert.Empty(packer.Requests);
        Assert.Null(await ExecutorContextPreview.PackAsync(null, Project, Executor(MemoryPolicy.ReadSelected), Spec("R1"), Requirements(), ct));

        var reading = await ExecutorContextPreview.PackAsync(packer, Project, Executor(MemoryPolicy.ReadSelected), SpecAbout("Do something", "R1", "R2"), Requirements(), ct);

        Assert.NotNull(reading);
        var request = Assert.Single(packer.Requests);
        Assert.Equal(string.Empty, request.Focus);
        Assert.Equal(ExecutorContextPreview.BudgetCharacters, request.BudgetCharacters);
        Assert.Equal(["R1"], request.Mandatory.Select(i => i.Id));
        Assert.Equal(["R2"], request.Desirable.Select(i => i.Id));
        Assert.True(ExecutorContextPreview.ReadsMemory(Executor(MemoryPolicy.ReadSelected)));
        Assert.False(ExecutorContextPreview.ReadsMemory(Executor()));
    }

    [Fact]
    public void The_skills_are_counted_as_the_engine_counts_them_the_name_and_the_description_of_each_skill_md()
    {
        using var root = new TempDirectory();
        root.WriteFile("pdf/SKILL.md", "---\nname: pdf\ndescription: Work with PDFs\n---\n# pdf\nA long body that is not what the CLI presents.\n");
        root.WriteFile("docx/SKILL.md", "---\r\nname: \"docx\"\r\ndescription: >\r\n  Word documents,\r\n  and more\r\n---\r\nBody\r\n");
        root.WriteFile("nofront/SKILL.md", "# no frontmatter, so nothing is presented\n");
        Directory.CreateDirectory(root.Combine("empty"));

        // «pdf» and «Work with PDFs»; «docx» and the two lines of its description, each with the space that joins it.
        Assert.Equal(3 + 14, ExecutorContextPreview.SkillCharacters([root.Combine("pdf")]));
        Assert.Equal(4 + ("Word documents,".Length + 1) + ("and more".Length + 1), ExecutorContextPreview.SkillCharacters([root.Combine("docx")]));
        Assert.Equal(0, ExecutorContextPreview.SkillCharacters([root.Combine("nofront"), root.Combine("empty"), root.Combine("missing")]));
        Assert.Equal(
            ExecutorContextPreview.SkillCharacters([root.Combine("pdf")]) + ExecutorContextPreview.SkillCharacters([root.Combine("docx")]),
            ExecutorContextPreview.SkillCharacters([root.Combine("pdf"), root.Combine("docx"), root.Combine("nofront")]));
    }

    [Fact]
    public void The_skills_of_the_agent_are_a_part_of_its_context_that_moves_the_size_by_exactly_their_characters()
    {
        var without = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(), null, null);

        var with = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(), null, null, skillCharacters: 400);

        Assert.Equal(0, Of(without, ContextPartKind.Skills));
        Assert.Equal(400, Of(with, ContextPartKind.Skills));
        Assert.Equal(without.Characters + 400, with.Characters);
        Assert.Equal(with.Parts.OrderBy(p => p.Kind).ToList(), with.Parts);
    }

    [Fact]
    public void The_budget_is_the_one_of_the_prompt_of_the_executor()
    {
        Assert.Equal(24_000, ExecutorContextPreview.BudgetCharacters);
    }

    [Fact]
    public void A_language_with_a_longer_name_makes_the_estimate_longer_by_exactly_the_difference()
    {
        var shorter = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(), null, null, responseLanguage: "en");
        var longer = ExecutorContextPreview.Measure(Spec("R1"), Requirements(), null, Executor(), null, null, responseLanguage: "en-GB");

        Assert.Equal(3, longer.Characters - shorter.Characters);
    }
}
