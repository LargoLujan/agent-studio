using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Questions;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Orchestration.Planning;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase13;

/// <summary>
/// V43 (docs/18, ADR-0051) end to end, with the modules composed as the app composes them (SQLite log, evidence store, provider
/// pipeline, planner, engine, judge dossier) over a temporary git repository, and scripted CLIs; no <c>App</c>, no real CLI.
/// The orchestrator asks the person before it plans, the person answers with an option, the orchestrator plans in the same run
/// and the run starts from that plan: the answer reaches the first prompt of the executor of every task as a
/// <c>&lt;human_answer … askedBy="orchestrator" …&gt;</c> block of data with no task and no attempt, with the rule that says not
/// to ask it again, and the judge's prompt carries neither the block nor a word of the question.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class OrchestratorQuestionReachesTheExecutorsTests
{
    // Text of the orchestrator that the judge must not get through the prompt of its review: each marker is unique.
    private const string QuestionText = "¿Pregunta-orq-OX1: qué hace Power(base, exponent) con un exponente negativo?";
    private const string Why = "Porque-orq-OX2: las dos cumplen RQ-01.";
    private const string LabelA = "Etiqueta-orq-OX3-A lanzar ArgumentOutOfRangeException";
    private const string LabelB = "Etiqueta-orq-OX4-B devolver el inverso";
    private const string Detail = "Detalle-orq-OX5.";
    private static readonly string[] Markers = [QuestionText, Why, LabelA, LabelB, Detail, "OX1", "OX2", "OX3", "OX4", "OX5"];

    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentScript Script(JsonObject json) => new() { StructuredOutput = json.ToJsonString() };

    private static JsonObject Notes() => new() { ["dependencies"] = new JsonArray(), ["parallelOk"] = new JsonArray(), ["harmful"] = new JsonArray() };

    /// <summary>The orchestrator asks instead of planning.</summary>
    private static JsonObject Asking()
    {
        var answer = PlanJson.Plan();
        answer["orderNotes"] = Notes();
        answer["questions"] = new JsonArray(
            new JsonObject
            {
                ["text"] = QuestionText,
                ["why"] = Why,
                ["options"] = new JsonArray(
                    new JsonObject { ["id"] = "a", ["label"] = LabelA, ["detail"] = Detail, ["effort"] = "small", ["recommended"] = true },
                    new JsonObject { ["id"] = "b", ["label"] = LabelB, ["detail"] = Detail, ["effort"] = "medium", ["recommended"] = false }),
                ["allowOther"] = true,
            });
        return answer;
    }

    /// <summary>Two executors, one after the other, and the review of the judge after both.</summary>
    private static JsonObject Planned()
    {
        var plan = PlanJson.Plan(
            PlanJson.Task("T1", covers: ["RQ-01"], writeScope: ["src/T1/"]),
            PlanJson.Task("T2", covers: ["RQ-01"], writeScope: ["src/T2/"], dependsOn: [PlanJson.Dependency("T1")]),
            PlanJson.Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [PlanJson.Dependency("T2")]));
        plan["orderNotes"] = Notes();
        plan["questions"] = new JsonArray();
        return plan;
    }

    [Fact]
    public async Task The_answer_to_a_question_of_the_orchestrator_is_in_the_first_prompt_of_every_executor_and_the_judge_never_gets_it()
    {
        using var repo = TempGitRepo.Create();
        var claude = ScriptedCli.Claude(TimeProvider.System);
        var codex = ScriptedCli.Codex(TimeProvider.System);
        claude.Script.Fallback = (request, _) => AgentScript.Completed(($"src/{request.TaskId}/a.txt", "a\n"));
        claude.Script.Script("orquestador", null, 1, Script(Asking()));

        // The judge answers without a verdict, so the review goes to the person, who approves it (as in the memory scenario);
        // when it starts, every file of the folders it may read besides its workspace is kept to be searched.
        var readable = new List<string>();
        codex.Script.Script("juez", "R1", 1, new AgentScript
        {
            StructuredOutput = "{}",
            FinalText = "Sin veredicto.",
            OnStart = request =>
            {
                lock (readable)
                {
                    foreach (var folder in request.Permissions.AdditionalDirectories.Where(Directory.Exists))
                    {
                        readable.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
                    }
                }
            },
        });
        var services = Phase4Composition.Services(Phase4Paths.Beside(repo.Root), new ScriptedApprovalPrompt(), Phase4ScriptedProviders.Over(claude, codex)).WithoutCliProbes();
        await using var app = await Phase4App.OpenAsync(repo.Root, services, Roster());
        var profile = new VerificationProfile("sin verificación", []);
        var planning = new PlanningRequest(app.Project, "Añade Power(base, exponent) con sus pruebas.", Requirements(), Roster(), new AgentId("orquestador"), profile, new PlanLimits());

        // The orchestrator asks, the person chooses an option, the orchestrator plans in the same run.
        var asked = await app.Orchestrator.PlanAsync(planning, null, Ct);
        var question = Assert.Single(asked.Questions!);
        Assert.Null(asked.Plan);
        claude.Script.Script("orquestador", null, 1, Script(Planned()));
        var answer = QuestionAnswer.Choosing(null, question.Id, "a", "usuario");
        var proposal = await app.Orchestrator.PlanAsync(planning with { Answers = [new AnsweredQuestion(asked.Run, question, answer)] }, null, Ct);
        Assert.Equal(asked.Run, proposal.Run);
        Assert.True(proposal.Plan is not null && proposal.Violations.Count == 0, proposal.Error ?? string.Join(" ", proposal.Violations.Select(v => v.Message)));

        // The run starts from that plan, in the run that asked.
        var request = await app.RunRequestAsync(planning.OriginalRequest,Requirements(), proposal.Plan!, profile) with { Run = proposal.Run };
        await using var run = await app.Orchestrator.StartAsync(request, Ct);
        var result = await run.Completion.WaitAsync(Guard, Ct);
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));

        // The log has the question and the answer of the orchestrator once, with no task: the run only read them.
        var (_, recordedQuestion) = Assert.Single(await app.PayloadsAsync<QuestionAskedPayload>(run.Id.Value));
        var (_, recordedAnswer) = Assert.Single(await app.PayloadsAsync<QuestionAnsweredPayload>(run.Id.Value));
        Assert.Null(recordedQuestion.Task);
        Assert.Equal("plan-1", recordedQuestion.Question);
        Assert.Null(recordedAnswer.Task);
        Assert.Equal(("plan-1", "a"), (recordedAnswer.Question, recordedAnswer.ChosenOption));

        // The first prompt of the executor of each task: the answer as data, marked as the orchestrator's, and the rule.
        foreach (var task in new[] { "T1", "T2" })
        {
            var (start, attempt) = Assert.Single(claude.Script.Starts, s => s.Request.TaskId == task);
            Assert.Equal(1, attempt);
            var (attributes, block) = Assert.Single(BlocksOf(start.Prompt));
            Assert.Equal($"question=\"plan-1\" askedBy=\"orchestrator\" evidence=\"{recordedQuestion.QuestionEvidence.Value}\" sha256=\"{recordedQuestion.QuestionSha256}\"", attributes);
            Assert.DoesNotContain("task=", attributes, StringComparison.Ordinal);
            Assert.DoesNotContain("attempt=", attributes, StringComparison.Ordinal);
            Assert.Equal((QuestionText, Why), (block.GetProperty("question").GetString(), block.GetProperty("why").GetString()));
            Assert.Equal(("a", LabelA), (block.GetProperty("answer").GetProperty("option").GetString(), block.GetProperty("answer").GetProperty("label").GetString()));
            Assert.Equal(("usuario", "ChosenByPerson"), (block.GetProperty("decidedBy").GetString(), block.GetProperty("method").GetString()));
            Assert.Contains("No vuelvas a preguntar lo que un bloque <human_answer> ya decide", start.Prompt, StringComparison.Ordinal);
        }

        // The judge: its review went to the person, and neither its prompt nor anything it could read carries the question.
        var judge = Assert.Single(codex.Script.Starts, s => s.Request.Agent.Id.Value == "juez").Request;
        Assert.DoesNotContain("<human_answer", judge.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("askedBy", judge.Prompt, StringComparison.Ordinal);
        foreach (var marker in Markers)
        {
            Assert.DoesNotContain(marker, judge.Prompt, StringComparison.Ordinal);
        }

        string[] material;
        lock (readable)
        {
            material = [.. readable];
        }

        // Nothing of the executors' prompts is in what it may read: V43 adds the block and the rule to them only, and the dossier
        // leaves those prompts out. (The prompts of the orchestrator's own planning are another matter: see the report of V43.)
        Assert.NotEmpty(material);
        Assert.DoesNotContain(material, text => text.Contains("askedBy", StringComparison.Ordinal));
        Assert.DoesNotContain(material, text => text.Contains("No vuelvas a preguntar", StringComparison.Ordinal));
        Assert.DoesNotContain(material, text => text.Contains("# Tarea T1 de Agent Studio", StringComparison.Ordinal) || text.Contains("# Tarea T2 de Agent Studio", StringComparison.Ordinal));
    }

    /// <summary>The attributes and the JSON of every <c>&lt;human_answer&gt;</c> block of a prompt, in order.</summary>
    private static IReadOnlyList<(string Attributes, JsonElement Json)> BlocksOf(string prompt)
    {
        var blocks = new List<(string, JsonElement)>();
        var from = 0;
        while (prompt.IndexOf("<human_answer ", from, StringComparison.Ordinal) is var start and >= 0)
        {
            var close = prompt.IndexOf('>', start);
            var end = prompt.IndexOf("</human_answer>", close, StringComparison.Ordinal);
            Assert.True(end > close, "A human_answer block is not closed.");
            blocks.Add((prompt[(start + "<human_answer ".Length)..close], JsonDocument.Parse(prompt[(close + 1)..end].Trim()).RootElement.Clone()));
            from = end + "</human_answer>".Length;
        }

        return blocks;
    }
}
