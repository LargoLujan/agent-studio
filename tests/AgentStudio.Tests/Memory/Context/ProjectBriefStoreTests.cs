using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Providers;
using AgentStudio.Memory.Context;
using AgentStudio.Tests.Memory.Store;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Memory.Context;

/// <summary>
/// docs/17 D-10 (ADR-0052): the brief of the project is a memory entry of the person, anchored like every entry; each version
/// supersedes the one in force; it is never part of the pack of memory, and the packer gives it to every agent but the judge.
/// </summary>
public sealed class ProjectBriefStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentDefinition Agent(AgentRole role, MemoryPolicy memory) => new(
        Aid(role.ToString().ToLowerInvariant()),
        role.ToString(),
        role,
        new ModelBinding(ProviderId.ClaudeCode, VendorId.Anthropic),
        "instructions",
        [],
        [],
        [],
        new ExecutionBudget(10, TimeSpan.FromMinutes(5)),
        1,
        memory,
        new ReviewPolicy(false, false));

    [Fact]
    public async Task A_project_without_a_brief_reads_none()
    {
        using var world = await MemoryWorld.CreateAsync();

        Assert.Null(await new ProjectBriefStore(world.Memory).ReadAsync(world.Project, Ct));
    }

    [Fact]
    public async Task The_brief_is_an_anchored_entry_of_the_person_and_each_version_supersedes_the_one_in_force()
    {
        using var world = await MemoryWorld.CreateAsync();
        var briefs = new ProjectBriefStore(world.Memory);

        var first = await briefs.SaveAsync(world.Project, "Agent Studio: un IDE de escritorio.", "persona", Ct);
        var second = await briefs.SaveAsync(world.Project, "Agent Studio: un IDE de escritorio. Código en inglés.", "persona", Ct);

        var announced = (await world.EntriesAsync(DecisionKind.MemoryRecorded)).Select(e => DecisionPayloadSerializer.Deserialize<MemoryRecordedPayload>(e.PayloadJson)).ToList();
        Assert.Equal(2, announced.Count);
        Assert.All(announced, a => Assert.Equal((MemoryKind.ProjectBrief, MemoryOrigin.User, ProjectBriefs.Source), (a.Kind, a.Origin, a.Source)));
        Assert.Equal([null, first.EntryId], announced.Select(a => a.Supersedes));
        Assert.Equal(MemoryContent.Sha256(MemoryKind.ProjectBrief, ProjectBriefs.Topic, second.Text), second.Sha256);
        Assert.Equal(announced[1].Sha256, second.Sha256);
        Assert.Equal(second, await briefs.ReadAsync(world.Project, Ct));
    }

    [Fact]
    public async Task Saving_the_text_in_force_again_writes_nothing()
    {
        using var world = await MemoryWorld.CreateAsync();
        var briefs = new ProjectBriefStore(world.Memory);
        var saved = await briefs.SaveAsync(world.Project, "Ficha.", "persona", Ct);

        var again = await briefs.SaveAsync(world.Project, "Ficha.", "persona", Ct);

        Assert.Equal(saved, again);
        Assert.Single(await world.EntriesAsync(DecisionKind.MemoryRecorded));
    }

    [Fact]
    public async Task A_blank_or_too_long_brief_is_refused_before_anything_is_written()
    {
        using var world = await MemoryWorld.CreateAsync();
        var briefs = new ProjectBriefStore(world.Memory);

        await Assert.ThrowsAsync<ArgumentException>(() => briefs.SaveAsync(world.Project, "  ", "persona", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => briefs.SaveAsync(world.Project, new string('x', ProjectBriefs.MaxCharacters + 1), "persona", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => briefs.SaveAsync(world.Project, "Ficha.", " ", Ct));
        Assert.Empty(await world.EntriesAsync(DecisionKind.MemoryRecorded));
    }

    [Fact]
    public async Task The_packer_gives_the_brief_in_force_to_an_executor_without_memory_and_never_to_the_judge_or_in_the_pack()
    {
        using var world = await MemoryWorld.CreateAsync();
        var briefs = new ProjectBriefStore(world.Memory);
        var brief = await briefs.SaveAsync(world.Project, "Ficha del proyecto.", "persona", Ct);
        await world.Memory.RecordAsync(world.Project, new MemoryDraft(MemoryKind.Decision, "Despliegue", "Blue green.", MemoryOrigin.User, "nota:persona"), Ct);
        var packer = new ContextPacker(world.Memory, brief: briefs);

        var executor = await packer.PackAsync(world.Project, new ContextPackRequest(Agent(AgentRole.Executor, MemoryPolicy.None), "Ficha", [], 10_000), Ct);
        var withMemory = await packer.PackAsync(world.Project, new ContextPackRequest(Agent(AgentRole.Executor, MemoryPolicy.ReadSelected), "Ficha del proyecto", [], 10_000), Ct);
        var judge = await packer.PackAsync(world.Project, new ContextPackRequest(Agent(AgentRole.Judge, MemoryPolicy.None), "Ficha", [], 10_000), Ct);

        Assert.Equal(brief, executor.Brief);
        Assert.Equal(brief, withMemory.Brief);
        Assert.DoesNotContain(withMemory.Items, i => i.Id == brief.EntryId);
        Assert.Contains(withMemory.Items, i => i.Kind == nameof(MemoryKind.Decision));
        Assert.Null(judge.Brief);
    }
}
