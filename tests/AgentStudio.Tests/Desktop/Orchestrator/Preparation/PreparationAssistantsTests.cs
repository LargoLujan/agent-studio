using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Desktop.Orchestrator.Preparation;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Tests.Domain.Orchestration;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Preparation;

/// <summary>
/// F13-10 (the screen's side) and the chat part of F13-18 (docs/17 D-20 and D-44): «Revisar el orden» and the chat of preparation
/// are auxiliary runs that spend quota, so each use asks the person first, with the estimate in front; what they suggest is
/// checked again by the system and is never applied by itself. The services are scripted agents: nothing here launches a CLI.
/// </summary>
public sealed class PreparationAssistantsTests
{
    private static readonly RunId PlanningRun = new("20261109-abc123");

    private sealed class FakeOrderReview(OrderReviewResult result) : IOrderReview
    {
        public List<OrderReviewRequest> Requests { get; } = [];

        public Task<OrderReviewResult> ReviewAsync(OrderReviewRequest request, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeChat(params PreparationChatReply[] replies) : IPreparationChat
    {
        private int _next;

        public List<PreparationChatRequest> Requests { get; } = [];

        public List<PreparationChatSession> Ended { get; } = [];

        public Task<PreparationChatReply> SendAsync(PreparationChatRequest request, IProgress<AgentEvent>? progress, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }

        public Task EndAsync(ProjectContext project, PreparationChatSession session, CancellationToken ct)
        {
            Ended.Add(session);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEstimator(UsageEstimate estimate) : IUsageEstimator
    {
        public List<UsageEstimateRequest> Requests { get; } = [];

        public Task<UsageEstimate> EstimateAsync(UsageEstimateRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(estimate);
        }

        public Task<PlanEstimate> EstimatePlanAsync(PlanEstimateRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static UsageEstimate Known() => new(
        5,
        EstimateInterval<long>.Unknown("x"),
        EstimateInterval<long>.Unknown("x"),
        EstimateInterval<TimeSpan>.Unknown("x"),
        EstimateInterval<decimal>.Unknown("x"),
        [],
        new EstimateInterval<long>(MetricValue<long>.EstimatedBySystem(8_000, "log"), MetricValue<long>.EstimatedBySystem(21_500, "log")));

    private static OrderReviewSubject Subject()
    {
        var roster = PlanFixtures.Roster();
        return new OrderReviewSubject(Project(), PlanningRun, PlanFixtures.Plan(), roster, roster[0].Id);
    }

    private static OrderReviewResult Result(params OrderSuggestion[] suggestions) =>
        new(PlanningRun, AgentRunStatus.Succeeded, null, OrderNotes.None, suggestions, ["una nota que no se pudo comprobar"], null);

    private static OrderReviewViewModel Review(BuilderServices services, List<Fix> applied, OrderReviewSubject? subject = null) =>
        new(services, () => subject ?? Subject(), fix =>
        {
            applied.Add(fix);
            return Task.CompletedTask;
        }, Text(), new FakeTimeProvider(T0));

    /// <summary>docs/18 P47: the estimate of the review is asked with the effort of the orchestrator, whose attempts it is estimated from.</summary>
    [Fact]
    public async Task The_estimate_of_the_review_is_asked_with_the_effort_of_the_orchestrator()
    {
        var roster = PlanFixtures.Roster();
        var subject = new OrderReviewSubject(Project(), PlanningRun, PlanFixtures.Plan(), [roster[0] with { Effort = ReasoningEffort.High }, .. roster.Skip(1)], roster[0].Id);
        var estimator = new FakeEstimator(Known());
        var review = Review(new BuilderServices(OrderReview: new FakeOrderReview(Result()), Estimator: estimator), [], subject);

        await review.AskCommand.ExecuteAsync(null);

        Assert.Equal(ReasoningEffort.High, estimator.Requests.Single().Effort);
    }

    [Fact]
    public async Task Asking_to_review_the_order_opens_the_card_with_the_estimate_and_launches_nothing()
    {
        var service = new FakeOrderReview(Result());
        var estimator = new FakeEstimator(Known());
        var review = Review(new BuilderServices(OrderReview: service, Estimator: estimator), []);

        Assert.True(review.IsAvailable);
        await review.AskCommand.ExecuteAsync(null);

        Assert.True(review.Consent.IsOpen);
        Assert.Equal("Estimación: entre 8.000 y 21.500 tokens en total (estimado por Agent Studio con 5 referencias).", review.Consent.EstimateText);
        Assert.Equal(AgentRunPurpose.OrderReview, estimator.Requests.Single().Purpose);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task Without_enough_references_the_estimate_says_so_and_never_a_number_made_up()
    {
        var unknown = UsageEstimate.Unknown(1, "unknown: 1 of 3 references");
        var review = Review(new BuilderServices(OrderReview: new FakeOrderReview(Result()), Estimator: new FakeEstimator(unknown)), []);

        await review.AskCommand.ExecuteAsync(null);
        Assert.Equal("Sin estimación todavía: hay 1 de 3 referencias necesarias.", review.Consent.EstimateText);

        var noEstimator = Review(new BuilderServices(OrderReview: new FakeOrderReview(Result())), []);
        await noEstimator.AskCommand.ExecuteAsync(null);
        Assert.StartsWith("Sin estimación: Agent Studio todavía no sabe", noEstimator.Consent.EstimateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_review_runs_only_after_the_OK_and_its_suggestions_are_never_applied_by_themselves()
    {
        var applied = new List<Fix>();
        var fix = new AddDependencyFix(new TaskId("T2"), new TaskId("T1"));
        var service = new FakeOrderReview(Result(
            new OrderSuggestion(OrderSuggestionKind.Dependency, new TaskId("T1"), new TaskId("T2"), "T2 usa lo que T1 escribe", Kept: true, fix),
            new OrderSuggestion(OrderSuggestionKind.ParallelOk, new TaskId("T1"), new TaskId("T6"), "no comparten ficheros", Kept: true, null)));
        var review = Review(new BuilderServices(OrderReview: service), applied);

        await review.AskCommand.ExecuteAsync(null);
        Assert.Empty(service.Requests);
        await review.ConfirmCommand.ExecuteAsync(null);

        var request = Assert.Single(service.Requests);
        Assert.Equal(PlanningRun, request.PlanningRun);
        Assert.Equal(AgentStudio.Desktop.Orchestrator.Approvals.ApprovalPromptService.DecidedBy, request.Consent.DecidedBy);
        Assert.Equal(T0, request.Consent.DecidedAt);
        Assert.False(review.Consent.IsOpen);
        Assert.Equal(2, review.Suggestions.Count);
        Assert.Empty(applied);
        Assert.Equal("T2 necesita T1", review.Suggestions[0].Text);
        Assert.Equal("T2 usa lo que T1 escribe", review.Suggestions[0].Reason);
        Assert.Equal("T2 después de T1", review.Suggestions[0].ButtonText);
        Assert.Equal("T1 y T6 pueden ir a la vez", review.Suggestions[1].Text);
        Assert.False(review.Suggestions[1].CanApply);
        Assert.Equal(["una nota que no se pudo comprobar"], review.Discarded);

        await review.Suggestions[0].ApplyCommand.ExecuteAsync(null);
        Assert.Equal([fix], applied);
    }

    [Fact]
    public async Task Cancelling_the_card_spends_nothing_and_a_second_use_asks_again()
    {
        var service = new FakeOrderReview(Result());
        var review = Review(new BuilderServices(OrderReview: service), []);

        await review.AskCommand.ExecuteAsync(null);
        review.CancelCommand.Execute(null);
        await review.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(service.Requests);

        await review.AskCommand.ExecuteAsync(null);
        await review.ConfirmCommand.ExecuteAsync(null);
        await review.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(service.Requests);
    }

    [Fact]
    public async Task The_notes_are_offered_as_old_when_the_plan_changed_and_not_available_without_the_service_or_the_planning_run()
    {
        var review = Review(new BuilderServices(OrderReview: new FakeOrderReview(Result())), []);

        Assert.Equal("Pide al orquestador que revise el orden del plan.", review.OfferText);
        review.Refresh(stale: true);
        Assert.Equal("Cambiaste el plan o el equipo: las notas de orden del orquestador pueden estar viejas.", review.OfferText);
        Assert.True(review.NotesAreStale);

        var withoutService = Review(BuilderServices.None, []);
        Assert.False(withoutService.IsAvailable);
        Assert.Equal("Revisar el orden no está disponible: falta el servicio.", withoutService.UnavailableText);
        Assert.False(withoutService.AskCommand.CanExecute(null));

        var withoutRun = new OrderReviewViewModel(new BuilderServices(OrderReview: new FakeOrderReview(Result())), () => null, _ => Task.CompletedTask, Text(), new FakeTimeProvider(T0));
        Assert.False(withoutRun.IsAvailable);
        await withoutRun.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(withoutRun.Suggestions);
        await Task.CompletedTask;
    }

    // ---------- The chat of preparation ----------

    private static PreparationChatReply Reply(string text, PreparationChatSession session, params object[] extras) => new(
        PlanningRun,
        AgentRunStatus.Succeeded,
        null,
        text,
        [.. extras.OfType<RequirementDraft>()],
        [.. extras.OfType<TeamSuggestion>()],
        ["el repositorio tiene cambios sin confirmar"],
        ["¿Quieres también la documentación?"],
        [],
        session,
        null);

    private static PreparationChatSubject ChatSubject()
    {
        var roster = PlanFixtures.Roster();
        return new PreparationChatSubject(Project(), roster, roster[0], null);
    }

    private static PreparationChatViewModel Chat(BuilderServices services, List<RequirementDraft> copied, List<Fix> applied) =>
        new(services, ChatSubject, draft =>
        {
            copied.Add(draft);
            return true;
        }, fix =>
        {
            applied.Add(fix);
            return Task.CompletedTask;
        }, Text(), new FakeTimeProvider(T0));

    /// <summary>docs/18 P47: the estimate of the chat is asked with the effort of the orchestrator, whose attempts it is estimated from.</summary>
    [Fact]
    public async Task The_estimate_of_the_chat_is_asked_with_the_effort_of_the_orchestrator()
    {
        var roster = PlanFixtures.Roster();
        var orchestrator = roster[0] with { Effort = ReasoningEffort.Low };
        var estimator = new FakeEstimator(Known());
        var chat = new PreparationChatViewModel(
            new BuilderServices(Chat: new FakeChat(), Estimator: estimator),
            () => new PreparationChatSubject(Project(), roster, orchestrator, null),
            _ => true,
            _ => Task.CompletedTask,
            Text(),
            new FakeTimeProvider(T0));

        chat.Draft = "Quiero añadir Calc.Multiply";
        await chat.AskCommand.ExecuteAsync(null);

        Assert.Equal(ReasoningEffort.Low, estimator.Requests.Single().Effort);
    }

    [Fact]
    public async Task Each_message_asks_for_its_own_OK_and_the_same_conversation_is_resumed()
    {
        var first = new PreparationChatSession(new RunId("chat-20261109-aaaaaa"), "sesion-1", 1);
        var second = first with { Messages = 2 };
        var service = new FakeChat(Reply("Primero.", first), Reply("Segundo.", second));
        var estimator = new FakeEstimator(Known());
        var chat = Chat(new BuilderServices(Chat: service, Estimator: estimator), [], []);

        chat.Draft = "Quiero añadir Calc.Multiply";
        await chat.AskCommand.ExecuteAsync(null);
        Assert.Empty(service.Requests);
        Assert.Contains("entre 8.000 y 21.500 tokens", chat.Consent.EstimateText, StringComparison.Ordinal);
        await chat.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(service.Requests);
        Assert.Null(service.Requests[0].Session);
        Assert.Equal("Quiero añadir Calc.Multiply", service.Requests[0].Message);
        Assert.Equal(AgentRunPurpose.PreparationChat, estimator.Requests.Single().Purpose);
        Assert.Equal(string.Empty, chat.Draft);
        Assert.True(chat.HasConversation);

        // A second message is another OK: writing it sends nothing until the person says yes again.
        chat.Draft = "Y los tests";
        Assert.True(chat.AskCommand.CanExecute(null));
        await chat.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(service.Requests);
        await chat.AskCommand.ExecuteAsync(null);
        await chat.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(2, service.Requests.Count);
        Assert.Equal(first, service.Requests[1].Session);
        Assert.Equal(["Tú", "Orquestador (texto de un agente)", "Tú", "Orquestador (texto de un agente)"], chat.Turns.Select(t => t.Author));
    }

    [Fact]
    public async Task What_the_orchestrator_suggests_is_never_applied_by_itself()
    {
        var copied = new List<RequirementDraft>();
        var applied = new List<Fix>();
        var draft = new RequirementDraft("Añadir Calc.Multiply", RequirementPriority.Mandatory, ["Multiply(2,3) devuelve 6"]);
        var fix = new ChangeModelFix(new AgentId("ejecutor"), "sonnet");
        var session = new PreparationChatSession(new RunId("chat-20261109-aaaaaa"), null, 1);
        var service = new FakeChat(Reply("Te propongo esto.", session, draft, new TeamSuggestion("el ejecutor no necesita tanto", fix)));
        var chat = Chat(new BuilderServices(Chat: service), copied, applied);

        chat.Draft = "ayuda";
        await chat.AskCommand.ExecuteAsync(null);
        await chat.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(copied);
        Assert.Empty(applied);
        var requirement = Assert.Single(chat.Requirements);
        Assert.Equal("Obligatorio: Añadir Calc.Multiply", requirement.AutomationName);
        var team = Assert.Single(chat.Team);
        Assert.Equal("Cambiar ejecutor a sonnet", team.ButtonText);
        Assert.Equal("el ejecutor no necesita tanto", team.Reason);
        Assert.True(team.WithdrawsApproval);
        Assert.Equal(["el repositorio tiene cambios sin confirmar"], chat.Risks);
        Assert.Equal(["¿Quieres también la documentación?"], chat.Questions);

        requirement.CopyCommand.Execute(null);
        await team.ApplyCommand.ExecuteAsync(null);
        Assert.Equal([draft], copied);
        Assert.Equal([fix], applied);
        Assert.True(requirement.IsCopied);
    }

    [Fact]
    public async Task Ending_the_conversation_deletes_its_copy_and_the_next_message_starts_another()
    {
        var session = new PreparationChatSession(new RunId("chat-20261109-aaaaaa"), "s", 1);
        var service = new FakeChat(Reply("Hola.", session));
        var chat = Chat(new BuilderServices(Chat: service), [], []);
        chat.Draft = "hola";
        await chat.AskCommand.ExecuteAsync(null);
        await chat.ConfirmCommand.ExecuteAsync(null);

        await chat.EndCommand.ExecuteAsync(null);

        Assert.Equal([session], service.Ended);
        Assert.False(chat.HasConversation);
        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task Without_the_service_or_an_orchestrator_the_chat_says_so_and_sends_nothing()
    {
        var withoutService = Chat(BuilderServices.None, [], []);
        withoutService.Draft = "hola";
        Assert.False(withoutService.IsAvailable);
        Assert.Equal("El chat no está disponible: falta el servicio.", withoutService.UnavailableText);
        Assert.False(withoutService.AskCommand.CanExecute(null));

        var service = new FakeChat(Reply("x", new PreparationChatSession(new RunId("chat-20261109-aaaaaa"), null, 1)));
        var withoutOrchestrator = new PreparationChatViewModel(new BuilderServices(Chat: service), () => null, _ => true, _ => Task.CompletedTask, Text(), new FakeTimeProvider(T0));
        withoutOrchestrator.Draft = "hola";
        Assert.Equal("El chat necesita un orquestador en el equipo.", withoutOrchestrator.UnavailableText);
        await withoutOrchestrator.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(service.Requests);
    }
}
