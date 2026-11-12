using System.Text.Json.Nodes;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Questions;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Planning.PlanJson;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// docs/17 D-28 to D-30 (decision 4 (a), «quién pregunta»): before planning, the orchestrator may ask the person instead of
/// proposing a plan, once and three questions at most. The questions are evidence and <c>QuestionAsked</c> with no task; the
/// planning that follows gets the answers, goes on in the same run, records <c>QuestionAnswered</c> and gives them to the
/// orchestrator as data, with no way to ask again. With a scripted orchestrator: no CLI is launched.
/// </summary>
public sealed class PlannerQuestionsTests
{
    private static AgentScript Answer(JsonObject json) => new() { StructuredOutput = json.ToJsonString() };

    private static JsonObject Asking()
    {
        var answer = PlanJson.Plan();
        answer["orderNotes"] = new JsonObject { ["dependencies"] = new JsonArray(), ["parallelOk"] = new JsonArray(), ["harmful"] = new JsonArray() };
        answer["questions"] = new JsonArray(
            new JsonObject
            {
                ["text"] = "¿Guardo las sesiones en SQLite o en ficheros JSON?",
                ["why"] = "Las dos cumplen RQ-01; cambia cómo se migran los datos.",
                ["options"] = new JsonArray(
                    new JsonObject { ["id"] = "a", ["label"] = "SQLite", ["detail"] = "Una base local.", ["effort"] = "small", ["recommended"] = true },
                    new JsonObject { ["id"] = "b", ["label"] = "Ficheros JSON", ["detail"] = "Un fichero por sesión.", ["effort"] = "medium", ["recommended"] = false }),
                ["allowOther"] = true,
            });
        return answer;
    }

    /// <summary>The valid plan of PlannerTests, with a note of order that explains its dependency.</summary>
    private static JsonObject ValidPlanWithNotes()
    {
        var plan = Plan(
            Task("T1", covers: ["RQ-01"], writeScope: ["src/Calc/"], steps: ["build", "test"]),
            Task("I1", kind: "Integration", agent: null, workspace: "None", dependsOn: [Dependency("T1")], steps: ["build", "test"]),
            Task("R1", kind: "Review", agent: "juez", workspace: "ReadOnly", maxRetries: 0, dependsOn: [Dependency("I1")]));
        plan["orderNotes"] = new JsonObject
        {
            ["dependencies"] = new JsonArray(new JsonObject { ["from"] = "T1", ["to"] = "I1", ["reason"] = "I1 integra lo que escribe T1" }),
            ["parallelOk"] = new JsonArray(),
            ["harmful"] = new JsonArray(new JsonObject { ["first"] = "R1", ["then"] = "I1", ["reason"] = "Revisar antes de integrar no revisa nada" }),
        };
        plan["questions"] = new JsonArray();
        return plan;
    }

    private static Task<PlanProposal> PlanAsync(EngineHarness harness, PlanningRequest? request = null) =>
        harness.CreateOrchestrator().PlanAsync(request ?? harness.PlanningRequest(), null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_plan_carries_its_notes_of_order_for_the_canvas()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));

        var proposal = await PlanAsync(harness);

        Assert.NotNull(proposal.Plan);
        Assert.Empty(proposal.Violations);
        var notes = Assert.IsType<OrderNotes>(proposal.OrderNotes);
        Assert.Equal(new OrderDependencyNote(new TaskId("T1"), new TaskId("I1"), "I1 integra lo que escribe T1"), Assert.Single(notes.Dependencies));
        Assert.Single(notes.Harmful);
        Assert.Single(harness.Claude.Starts); // no run more for the notes
    }

    [Fact]
    public async Task The_orchestrator_asks_instead_of_planning_and_its_questions_are_recorded_without_a_task()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));

        var proposal = await PlanAsync(harness);

        Assert.Null(proposal.Plan);
        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.Null(proposal.Error);
        var pending = Assert.Single(proposal.Questions!);
        Assert.Equal(("plan-1", (TaskId?)null, new AgentId("orquestador"), 1), (pending.Id, pending.Task, pending.Agent, pending.Attempt));
        Assert.Equal("¿Guardo las sesiones en SQLite o en ficheros JSON?", pending.Question.Text);
        Assert.Empty(pending.Estimates);
        Assert.Single(harness.Claude.Starts); // no correction for a question
        Assert.Empty(harness.Payloads<PlanCreatedPayload>());

        var (entry, asked) = Assert.Single(harness.Payloads<QuestionAskedPayload>());
        Assert.Equal(new Actor(ActorKind.Orchestrator, "orquestador"), entry.Actor);
        Assert.Null(asked.Task);
        Assert.Equal((proposal.Run.Value, "plan-1", 1, "a"), (asked.RunId, asked.Question, asked.Attempt, asked.RecommendedByAgent));
        Assert.Equal(["a", "b"], asked.Options);
        Assert.Equal(pending.Evidence, asked.QuestionEvidence);
        var evidence = harness.Evidence.Record(asked.QuestionEvidence);
        Assert.Equal((EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent), (evidence.Kind, evidence.Origin));
        Assert.Contains("SQLite", harness.Evidence.Text(evidence.Id), StringComparison.Ordinal);
        Assert.DoesNotContain("SQLite", entry.PayloadJson, StringComparison.Ordinal); // no text of an agent in a payload

        var prompt = Assert.Single(harness.Claude.Starts).Request.Prompt;
        Assert.Contains("como mucho 3 preguntas", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_answers_it_plans_in_the_same_run_records_them_and_cannot_ask_again()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);

        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));
        var answer = QuestionAnswer.Choosing(null, question.Id, "a", "ana");
        var proposal = await PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, answer)] });

        Assert.Equal(asked.Run, proposal.Run);
        Assert.NotNull(proposal.Plan);
        Assert.Null(proposal.Questions);
        var (entry, answered) = Assert.Single(harness.Payloads<QuestionAnsweredPayload>());
        Assert.Equal(new Actor(ActorKind.User, "ana"), entry.Actor);
        Assert.Equal((asked.Run.Value, (TaskId?)null, "plan-1", "a", QuestionAnswerMethod.ChosenByPerson), (answered.RunId, answered.Task, answered.Question, answered.ChosenOption, answered.Method));
        Assert.Single(harness.Payloads<WorktreeCreatedPayload>()); // the _base of the run that asked, reused

        var prompt = harness.Claude.Starts[^1].Request.Prompt;
        Assert.Contains("<human_answer question=\"plan-1\">", prompt, StringComparison.Ordinal);
        Assert.Contains("\"Label\":\"SQLite\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Ya no puedes preguntar", prompt, StringComparison.Ordinal);
        Assert.True(entry.Sequence < harness.Payloads<PlanCreatedPayload>().Single().Entry.Sequence);
    }

    [Fact]
    public async Task Secrets_in_the_questions_of_the_orchestrator_and_in_the_own_answer_are_redacted_before_they_are_recorded_or_sent()
    {
        using var harness = new EngineHarness { Redactor = new MarkingRedactor("sk-secreto") };
        var asking = Asking();
        asking["questions"]![0]!["text"] = "¿Uso la clave sk-secreto para la API?";
        harness.Claude.Script("orquestador", null, 1, Answer(asking));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);
        Assert.Equal("¿Uso la clave [REDACTED] para la API?", question.Question.Text);

        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));
        var answer = QuestionAnswer.Writing(null, question.Id, "Usa sk-secreto solo en local", "ana");
        var request = harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, answer)] };
        var proposal = await PlanAsync(harness, request);

        Assert.NotNull(proposal.Plan);
        var (_, answered) = Assert.Single(harness.Payloads<QuestionAnsweredPayload>());
        Assert.Equal("Usa [REDACTED] solo en local", harness.Evidence.Text(answered.AnswerEvidence!.Value));
        var prompt = harness.Claude.Starts[^1].Request.Prompt;
        Assert.Contains("Usa [REDACTED] solo en local", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secreto", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secreto", harness.Evidence.Text(question.Evidence), StringComparison.Ordinal);
        // What the system writes (the prompts, the text of the person) has no secret; the raw output of the agent is kept as it came.
        var evidence = await harness.Evidence.ListAsync(harness.Project.ProjectId, TestContext.Current.CancellationToken);
        Assert.All(evidence.Where(e => e.Origin != EvidenceOrigin.ReportedByAgent), e => Assert.DoesNotContain("sk-secreto", harness.Evidence.Text(e.Id), StringComparison.Ordinal));

        // A failed planning tried again with the same answer finds it recorded: the redacted text is the same each time.
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));
        var again = await PlanAsync(harness, request);
        Assert.Equal(asked.Run, again.Run);
        Assert.Single(harness.Payloads<QuestionAnsweredPayload>());
    }

    private sealed class MarkingRedactor(string secret) : AgentStudio.Application.Security.ISecretRedactor
    {
        public string Redact(string text) => text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_in_the_persons_own_words_is_evidence_they_provided()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));

        await PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, QuestionAnswer.Writing(null, question.Id, "Usa LiteDB", "ana"))] });

        var (_, answered) = Assert.Single(harness.Payloads<QuestionAnsweredPayload>());
        Assert.Null(answered.ChosenOption);
        var text = harness.Evidence.Record(answered.AnswerEvidence!.Value);
        Assert.Equal(EvidenceOrigin.ProvidedByHuman, text.Origin);
        Assert.Equal(text.ContentSha256, answered.AnswerSha256);
        Assert.Equal("Usa LiteDB", harness.Evidence.Text(text.Id));
        Assert.Contains("\"OwnText\":\"Usa LiteDB\"", harness.Claude.Starts[^1].Request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_again_after_the_answers_is_a_problem_for_the_one_correction()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        harness.Claude.Script("orquestador", null, 2, Answer(ValidPlanWithNotes()));

        var proposal = await PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, QuestionAnswer.Choosing(null, question.Id, "b", "ana"))] });

        Assert.True(proposal.Corrected);
        Assert.NotNull(proposal.Plan);
        Assert.Null(proposal.Questions);
        Assert.Single(harness.Payloads<QuestionAskedPayload>());
        Assert.Contains("asks only once", harness.Claude.Starts[^1].Request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_to_questions_nobody_asked_or_already_answered_are_refused_before_anything_runs()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);
        var starts = harness.Claude.Starts.Count;

        var ghost = question with { Id = "plan-9" };
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, ghost, QuestionAnswer.Choosing(null, "plan-9", "a", "ana"))] }));
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, QuestionAnswer.Choosing(null, question.Id, "z", "ana"))] }));
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, QuestionAnswer.RecommendedByConsent(null, question.Id, "a"))] }));
        var closed = question with { Question = question.Question with { AllowOther = false } };
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, closed, QuestionAnswer.Writing(null, question.Id, "Otra cosa", "ana"))] }));
        Assert.Equal(starts, harness.Claude.Starts.Count);
        Assert.Empty(harness.Payloads<QuestionAnsweredPayload>());

        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));
        var answers = new[] { new AnsweredQuestion(asked.Run, question, QuestionAnswer.Choosing(null, question.Id, "a", "ana")) };
        await PlanAsync(harness, harness.PlanningRequest() with { Answers = answers });
        var other = new[] { new AnsweredQuestion(asked.Run, question, QuestionAnswer.Choosing(null, question.Id, "b", "ana")) };
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(harness, harness.PlanningRequest() with { Answers = other })); // answered already, otherwise
    }

    [Fact]
    public async Task A_planning_with_the_answers_that_failed_is_tried_again_without_recording_them_twice()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);
        var answers = new[] { new AnsweredQuestion(asked.Run, question, QuestionAnswer.Writing(null, question.Id, "Usa LiteDB", "ana")) };
        harness.Claude.Script("orquestador", null, 1, AgentScript.Failing("Límite de uso alcanzado."));

        var failed = await PlanAsync(harness, harness.PlanningRequest() with { Answers = answers });
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));
        var retried = await PlanAsync(harness, harness.PlanningRequest() with { Answers = answers });

        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal((asked.Run, asked.Run), (failed.Run, retried.Run));
        Assert.NotNull(retried.Plan);
        Assert.Single(harness.Payloads<QuestionAnsweredPayload>());
        Assert.Contains("Usa LiteDB", harness.Claude.Starts[^1].Request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_its_copy_removed_and_HEAD_moved_the_planning_with_the_answers_plans_on_the_commit_it_asked_from()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, Answer(Asking()));
        var asked = await PlanAsync(harness);
        var question = Assert.Single(asked.Questions!);
        var lease = Assert.Single(await harness.Worktrees.ListOwnedAsync(harness.Project, TestContext.Current.CancellationToken), l => l.Run == asked.Run && l.Name == "_base");
        harness.Worktrees.AllowRemove = true;
        await harness.Worktrees.RemoveAsync(lease, false, TestContext.Current.CancellationToken);
        harness.Worktrees.UserState = harness.Worktrees.UserState with { HeadCommit = new string('f', 40) }; // the person committed since
        harness.Claude.Script("orquestador", null, 1, Answer(ValidPlanWithNotes()));

        var proposal = await PlanAsync(harness, harness.PlanningRequest() with { Answers = [new AnsweredQuestion(asked.Run, question, QuestionAnswer.Choosing(null, question.Id, "a", "ana"))] });

        Assert.NotNull(proposal.Plan);
        var bases = harness.Payloads<WorktreeCreatedPayload>().Select(p => p.Payload.BaseCommit).ToList();
        Assert.Equal([harness.Worktrees.InitialCommit, harness.Worktrees.InitialCommit], bases); // made again, on the same base the run builds on
    }

    [Fact]
    public async Task Every_question_is_answered_at_once()
    {
        using var harness = new EngineHarness();
        var two = Asking();
        ((JsonArray)two["questions"]!).Add(JsonNode.Parse(((JsonArray)two["questions"]!)[0]!.ToJsonString())!.AsObject().Also(q => q["text"] = "¿Y el formato de fecha?"));
        harness.Claude.Script("orquestador", null, 1, Answer(two));
        var asked = await PlanAsync(harness);
        Assert.Equal(["plan-1", "plan-2"], asked.Questions!.Select(q => q.Id));
        var starts = harness.Claude.Starts.Count;

        var partial = new[] { new AnsweredQuestion(asked.Run, asked.Questions![0], QuestionAnswer.Choosing(null, "plan-1", "a", "ana")) };
        await Assert.ThrowsAsync<ArgumentException>(() => PlanAsync(harness, harness.PlanningRequest() with { Answers = partial }));

        Assert.Equal(starts, harness.Claude.Starts.Count);
        Assert.Empty(harness.Payloads<QuestionAnsweredPayload>());
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject node, Action<JsonObject> change)
    {
        change(node);
        return node;
    }
}
