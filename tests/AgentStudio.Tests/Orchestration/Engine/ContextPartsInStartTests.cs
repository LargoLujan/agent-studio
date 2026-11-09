using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Skills;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// docs/17 D-13 and F13-6 (ADR-0052): every launch of the engine records the parts of its prompt, in characters, in
/// <c>AgentRunStarted</c> version 3. The parts of the text add up to the exact prompt the agent got; the skills add what
/// presenting them costs (their names and descriptions), and the instructions of the agent are their own part: a block of data
/// of the prompt of an executor (docs/17 D-42), and what the adapter adds outside the prompt of the planner. No text is
/// recorded, only sizes.
/// </summary>
public sealed class ContextPartsInStartTests
{
    private static int SectionLength(string prompt, string heading)
    {
        var start = prompt.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The prompt has no section «{heading}».");
        var next = prompt.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return (next < 0 ? prompt.Length : next + 1) - start;
    }

    /// <summary>The instructions of the agent of the fixtures, which the adapter adds outside the prompt of the planner.</summary>
    private static int Instructions(string agent) => ("Instrucciones de sistema de " + agent).Length;

    /// <summary>
    /// The parts of the text of the prompt of an executor add up to it: its instructions reach it only in a section of the
    /// prompt, as a block of data (docs/17 D-42), never also as a system prompt of the CLI, and that section is their part.
    /// </summary>
    private static void AddsUp(string prompt, Dictionary<ContextPartKind, int> parts)
    {
        Assert.Equal(SectionLength(prompt, "## Instrucciones del agente"), parts[ContextPartKind.AgentInstructions]);
        Assert.Equal(prompt.Length, parts.Where(p => p.Key is not ContextPartKind.Skills).Sum(p => p.Value));
    }

    /// <summary>The parts of the text of the prompt of the planner add up to it; its instructions are their own part, outside it.</summary>
    private static void AddsUpWithInstructionsOutside(string prompt, Dictionary<ContextPartKind, int> parts, string agent)
    {
        Assert.Equal(Instructions(agent), parts[ContextPartKind.AgentInstructions]);
        Assert.Equal(prompt.Length, parts.Where(p => p.Key is not (ContextPartKind.AgentInstructions or ContextPartKind.Skills)).Sum(p => p.Value));
    }

    private static Dictionary<ContextPartKind, int> PartsOf(AgentRunStartedPayload started)
    {
        Assert.True(started.RecordsContextParts());
        Assert.Equal(AgentRunStartedPayload.ContextPartsVersion, started.V);
        var parts = Assert.IsAssignableFrom<IReadOnlyList<ContextPartSnapshot>>(started.ContextParts);
        Assert.All(parts, p => Assert.True(p.Characters > 0, $"{p.Kind} has no characters and is recorded."));
        Assert.Equal(parts.Select(p => p.Kind).Order(), parts.Select(p => p.Kind));
        return parts.ToDictionary(p => p.Kind, p => p.Characters);
    }

    [Fact]
    public async Task An_executor_records_its_protocol_and_its_requirements_and_they_add_up_to_its_prompt()
    {
        using var harness = new EngineHarness();

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        var prompt = Assert.Single(harness.Claude.Starts).Request.Prompt;
        var parts = PartsOf(Assert.Single(harness.Payloads<AgentRunStartedPayload>()).Payload);
        Assert.Equal([ContextPartKind.Protocol, ContextPartKind.Requirements, ContextPartKind.AgentInstructions], parts.Keys);
        AddsUp(prompt, parts);
        Assert.Equal(SectionLength(prompt, "## Requisitos que cubre"), parts[ContextPartKind.Requirements]);
    }

    [Fact]
    public async Task The_memory_of_an_agent_with_ReadSelected_is_its_own_part()
    {
        using var harness = new EngineHarness();
        await harness.GiveMemoryAsync("ejecutor");

        await using var run = await harness.WithPacker(new RecordingContextPacker()).StartAsync(harness.Request(Plan(Work("T1"))), TestContext.Current.CancellationToken);
        await run.CompleteAsync(harness);

        var prompt = Assert.Single(harness.Claude.Starts).Request.Prompt;
        var parts = PartsOf(Assert.Single(harness.Payloads<AgentRunStartedPayload>()).Payload);
        Assert.Equal([ContextPartKind.Protocol, ContextPartKind.Requirements, ContextPartKind.AgentInstructions, ContextPartKind.Memory], parts.Keys);
        AddsUp(prompt, parts);
        Assert.Equal(SectionLength(prompt, "## Memoria del proyecto"), parts[ContextPartKind.Memory]);
    }

    [Fact]
    public async Task What_comes_from_an_earlier_task_is_the_upstream_part_of_the_next()
    {
        using var harness = new EngineHarness();

        await using var run = await harness.StartAsync(Plan(Work("T1"), Work("T2").After(On("T1"))));
        await run.CompleteAsync(harness);

        var second = harness.Claude.Starts.Single(s => s.Request.TaskId == "T2").Request.Prompt;
        var started = harness.Payloads<AgentRunStartedPayload>().Single(p => p.Payload.Task?.Value == "T2").Payload;
        var parts = PartsOf(started);
        Assert.Contains("<upstream_output task=\"T1\"", second, StringComparison.Ordinal);
        Assert.Equal(SectionLength(second, "## Datos de tareas anteriores"), parts[ContextPartKind.Upstream]);
        AddsUp(second, parts);
        Assert.False(PartsOf(harness.Payloads<AgentRunStartedPayload>().Single(p => p.Payload.Task?.Value == "T1").Payload).ContainsKey(ContextPartKind.Upstream));
    }

    [Fact]
    public async Task A_retry_carries_its_previous_attempt_as_a_part()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, AgentScript.Failing("Se rompió la compilación."));
        harness.Claude.Script("ejecutor", "T1", 2, AgentScript.Completed());

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        var result = await run.CompleteAsync(harness);

        Assert.Equal(RunPhase.Succeeded, result.Phase);
        var retry = harness.Claude.Starts.Single(s => s.Request.Purpose!.Contains("intento 2", StringComparison.Ordinal)).Request.Prompt;
        var parts = PartsOf(harness.Payloads<AgentRunStartedPayload>().Single(p => p.Payload.Attempt == 2).Payload);
        Assert.Equal(SectionLength(retry, "## Intento anterior"), parts[ContextPartKind.PreviousAttempt]);
        AddsUp(retry, parts);
    }

    [Fact]
    public async Task The_planner_records_its_parts_too()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = Planning.PlanJson.Plan(Planning.PlanJson.Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"])).ToJsonString() });

        await harness.CreateOrchestrator().PlanAsync(harness.PlanningRequest(), null, TestContext.Current.CancellationToken);

        var prompt = Assert.Single(harness.Claude.Starts).Request.Prompt;
        var started = Assert.Single(harness.Payloads<AgentRunStartedPayload>()).Payload;
        Assert.Equal(AgentRunPurpose.Planner, started.Purpose);
        var parts = PartsOf(started);
        AddsUpWithInstructionsOutside(prompt, parts, "orquestador");
        Assert.Equal(SectionLength(prompt, "## Requisitos congelados"), parts[ContextPartKind.Requirements]);
    }

    [Fact]
    public async Task The_skills_count_their_names_and_descriptions_and_nothing_else()
    {
        using var harness = new EngineHarness();
        using var temp = new TempDirectory();
        var skills = temp.CreateDirectory("skills");
        var first = Path.Combine(skills, ".claude", "skills", "docs");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "SKILL.md"), "---\nname: docs\ndescription: \"Escribe documentación.\"\nlicense: MIT\n---\n# Cuerpo largo que no se presenta\n" + new string('x', 5000));
        var second = Path.Combine(skills, ".claude", "skills", "tests");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "SKILL.md"), "---\r\nname: tests\r\ndescription: >\r\n  Escribe pruebas\r\n  con xUnit.\r\n---\r\nCuerpo.");
        File.WriteAllText(Path.Combine(skills, "README.md"), "---\nname: no-es-una-skill\n---\n");
        harness.Skills = new FixedSkills(skills);

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        var prompt = Assert.Single(harness.Claude.Starts).Request.Prompt;
        var parts = PartsOf(Assert.Single(harness.Payloads<AgentRunStartedPayload>()).Payload);
        var expected = "docs".Length + "Escribe documentación.".Length + "tests".Length + ("Escribe pruebas".Length + 1) + ("con xUnit.".Length + 1);
        Assert.Equal(expected, parts[ContextPartKind.Skills]);
        AddsUp(prompt, parts);
    }

    [Fact]
    public async Task No_text_of_the_prompt_is_recorded_in_the_start()
    {
        using var harness = new EngineHarness();

        await using var run = await harness.StartAsync(Plan(Work("T1")));
        await run.CompleteAsync(harness);

        var entry = Assert.Single(harness.Payloads<AgentRunStartedPayload>()).Entry;
        Assert.DoesNotContain("Objetivo de T1", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"contextParts\":[{\"kind\":\"Protocol\",\"characters\":", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"v\":3", entry.PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>Gives every agent the same folder of materialized skills, as the provisioner would.</summary>
    private sealed class FixedSkills(string directory) : ISkillProvisioner
    {
        public Task<ProvisionedSkills> ProvisionAsync(string projectId, AgentDefinition agent, string runKey, string workingDirectory, CancellationToken ct) =>
            Task.FromResult(new ProvisionedSkills(agent.Model.Provider, directory, [new SkillId("docs"), new SkillId("tests")], []));

        public void Release(ProvisionedSkills skills)
        {
        }
    }
}
