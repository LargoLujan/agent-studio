using System.Text.Json.Nodes;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Orchestration.Analysis;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// F13-18, the part of the chat (docs/17 D-44): the chat of preparation with the orchestrator is an auxiliary read-only run with
/// the person's OK for each message, resumes its session while the conversation lasts, keeps each message and answer as
/// evidence, sends what the person wrote with its secrets redacted, and its suggestions are checked against the template and
/// never applied. With a scripted orchestrator: no CLI is launched.
/// </summary>
public sealed class PreparationChatServiceTests
{
    private sealed class Redactor : ISecretRedactor
    {
        public string Redact(string text) => text.Replace("sk-SECRETO", "[secreto]", StringComparison.Ordinal);
    }

    private sealed class World : IDisposable
    {
        private int _consents;

        public World()
        {
            Service = new PreparationChatService(new AuxiliaryAgentRunner(Harness.Decisions, Harness.Evidence, Harness.Providers, Harness.Time), Harness.Worktrees, new Redactor(), Consents, Harness.Time);
        }

        public QuotaConsentLedger Consents { get; } = new();

        public EngineHarness Harness { get; } = new();

        public PreparationChatService Service { get; }

        public QuotaConsent Consent() => new("miveritas", Harness.Time.GetUtcNow().AddSeconds(-++_consents), "Un mensaje al orquestador: ~4.000 tokens (estimado)");

        public PreparationChatRequest Request(string message, PreparationChatSession? session = null, QuotaConsent? consent = null) =>
            new(Harness.Project, Roster(), new AgentId("orquestador"), message, session, consent ?? Consent()) { Catalog = Catalog };

        public Task<PreparationChatReply> SendAsync(PreparationChatRequest request) => Service.SendAsync(request, null, TestContext.Current.CancellationToken);

        public void Dispose() => Harness.Dispose();
    }

    /// <summary>A character that turns the direction of the text (bidi), written by its code so the source shows none.</summary>
    private static readonly char RightToLeftOverride = (char)0x202E;

    /// <summary>Claude Code knows sonnet; Codex knows gpt-6.</summary>
    private static readonly ModelCatalog Catalog = new(
        1,
        [
            new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-sonnet", "sonnet"], ModelTier.Balanced, []),
            new ModelCatalogEntry(ProviderId.CodexCli, "0.160.0", ["gpt-6"], ModelTier.Balanced, []),
        ],
        ModelRecommendationRules.Default);

    [Fact]
    public async Task A_change_of_model_is_offered_only_for_a_model_of_the_catalog_of_the_cli_of_the_agent()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            StructuredOutput = Answer(team:
            [
                Suggestion("changeModel", "ejecutor", "son" + RightToLeftOverride + "net"),
                Suggestion("changeModel", "ejecutor", "gpt-6"),
                Suggestion("changeModel", "ejecutor", "modelo-inventado"),
                Suggestion("changeModel", "ejecutorCodex", "gpt-6"),
            ]),
        });

        var reply = await world.SendAsync(world.Request("¿Qué modelos?"));

        Assert.Equal([new ChangeModelFix(new AgentId("ejecutor"), "sonnet"), new ChangeModelFix(new AgentId("ejecutorCodex"), "gpt-6")], reply.Team.Select(s => s.Fix));
        Assert.Equal(2, reply.Discarded.Count(d => d.Contains("changeModel", StringComparison.Ordinal)));
        Assert.Equal("Puedo ayudarte a preparar Calc.Multiply.", reply.Text); // the answer stays

        world.Harness.Claude.Script("orquestador", null, 2, new AgentScript { StructuredOutput = Answer(team: [Suggestion("changeModel", "ejecutor", "sonnet")]) });
        var withoutCatalog = await world.SendAsync(world.Request("Otra", reply.Session) with { Catalog = null });
        Assert.Empty(withoutCatalog.Team);
    }

    private static JsonObject Suggestion(string kind, string? agent = null, string? model = null, string? tool = null, string reason = "Porque sí") =>
        new() { ["kind"] = kind, ["agent"] = agent, ["model"] = model, ["tool"] = tool, ["reason"] = reason };

    private static string Answer(string text = "Puedo ayudarte a preparar Calc.Multiply.", JsonArray? requirements = null, JsonArray? team = null, JsonArray? risks = null, JsonArray? questions = null) =>
        new JsonObject
        {
            ["text"] = text,
            ["requirements"] = requirements ?? [],
            ["team"] = team ?? [],
            ["risks"] = risks ?? [],
            ["questions"] = questions ?? [],
        }.ToJsonString();

    [Fact]
    public async Task A_message_is_an_auxiliary_read_only_run_with_its_answer_checked_and_nothing_applied()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript
        {
            SessionId = "sesion-1",
            StructuredOutput = Answer(
                requirements: [new JsonObject { ["statement"] = "Añadir Calc.Multiply(a,b)", ["priority"] = "Mandatory", ["criteria"] = new JsonArray("Multiply(2,3) devuelve 6") }],
                team:
                [
                    Suggestion("memoryOn", "ejecutorCodex"),
                    Suggestion("changeModel", "ejecutor", "sonnet"),
                    Suggestion("removeCapability", "ejecutor", tool: "Write"),
                    Suggestion("removeCapability", "ejecutor", tool: "Bash"),
                    Suggestion("changeModel", "fantasma", "opus"),
                    Suggestion("addJudge"),
                ],
                risks: ["La división por cero"],
                questions: ["¿Quieres pruebas de propiedades?"]),
        });

        var reply = await world.SendAsync(world.Request("Quiero añadir Calc.Multiply; mi clave es sk-SECRETO"));

        var request = Assert.Single(world.Harness.Claude.Starts).Request;
        Assert.Equal(["Read", "Grep", "Glob"], request.Permissions.AllowedTools);
        Assert.False(request.Permissions.AllowFileWrites || request.Permissions.AllowShell || request.Permissions.AllowNetwork);
        Assert.Null(request.ResumeSessionId);
        Assert.DoesNotContain("sk-SECRETO", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("[secreto]", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(PreparationChatService.Schema(Roster().Select(a => a.Id.Value)), request.OutputJsonSchema);
        Assert.StartsWith(PreparationChatService.WorkspacePrefix, reply.Session.Workspace.Value, StringComparison.Ordinal);
        Assert.Contains("_base", request.WorkingDirectory, StringComparison.Ordinal);

        var (_, started) = Assert.Single(world.Harness.Payloads<AgentRunStartedPayload>());
        Assert.Equal((AgentRunPurpose.PreparationChat, reply.Run.Value), (started.Purpose, started.RunId));
        Assert.NotNull(reply.AnswerEvidence);
        Assert.Equal("Puedo ayudarte a preparar Calc.Multiply.", reply.Text);
        Assert.Equal(new RequirementDraft("Añadir Calc.Multiply(a,b)", RequirementPriority.Mandatory, ["Multiply(2,3) devuelve 6"]), Assert.Single(reply.Requirements), new DraftComparer());
        Assert.Equal(
            [new SetMemoryFix(new AgentId("ejecutorCodex"), MemoryPolicy.ReadSelected), new ChangeModelFix(new AgentId("ejecutor"), "sonnet"), new RemoveCapabilityFix(new AgentId("ejecutor"), "Write")],
            reply.Team.Select(s => s.Fix));
        Assert.Equal(3, reply.Discarded.Count); // a tool it does not have, an agent that does not exist, a judge when both vendors produce
        Assert.Equal(["La división por cero"], reply.Risks);
        Assert.Equal(["¿Quieres pruebas de propiedades?"], reply.Questions);
        Assert.Equal(new PreparationChatSession(reply.Session.Workspace, "sesion-1", 1), reply.Session);
        Assert.Empty(world.Harness.Plans.All());
    }

    [Fact]
    public async Task The_next_message_resumes_the_session_in_the_same_copy_and_needs_its_own_OK()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript { SessionId = "sesion-1", StructuredOutput = Answer() });
        world.Harness.Claude.Script("orquestador", null, 2, new AgentScript { SessionId = "sesion-1", StructuredOutput = Answer("Seguimos.") });
        var first = await world.SendAsync(world.Request("Hola"));
        var consent = world.Consent();

        var second = await world.SendAsync(world.Request("¿Y las pruebas?", first.Session, consent));

        var request = world.Harness.Claude.Starts[^1].Request;
        Assert.Equal("sesion-1", request.ResumeSessionId);
        Assert.Equal(world.Harness.Claude.Starts[0].Request.WorkingDirectory, request.WorkingDirectory);
        Assert.DoesNotContain("<team>", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("<person_message>", request.Prompt, StringComparison.Ordinal);
        Assert.Equal((first.Session.Workspace, 2), (second.Session.Workspace, second.Session.Messages));
        Assert.Equal("Seguimos.", second.Text);
        Assert.NotEqual(first.Run, second.Run);
        Assert.Equal(2, world.Harness.Payloads<AgentRunStartedPayload>().Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.SendAsync(world.Request("Otra vez", second.Session, consent)));
        Assert.Equal(2, world.Harness.Claude.Starts.Count);
    }

    [Fact]
    public async Task A_long_text_is_cut_its_control_characters_go_and_a_bad_answer_gives_nothing()
    {
        using var world = new World();
        world.Harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = Answer(new string('a', PreparationChatLimits.MaxTextLength + 50) + RightToLeftOverride) });
        world.Harness.Claude.Script("orquestador", null, 2, new AgentScript { StructuredOutput = "{\"text\":\"Sin el resto\"}" });

        var cut = await world.SendAsync(world.Request("Hola"));
        var broken = await world.SendAsync(world.Request("Hola otra vez", cut.Session));

        Assert.Equal(PreparationChatLimits.MaxTextLength, cut.Text.Length);
        Assert.DoesNotContain(RightToLeftOverride, cut.Text);
        Assert.Single(cut.Discarded);
        Assert.Empty(broken.Text);
        Assert.NotNull(broken.Error);
    }

    [Fact]
    public async Task A_blank_or_too_long_message_or_no_consent_launches_nothing()
    {
        using var world = new World();

        await Assert.ThrowsAsync<ArgumentException>(() => world.SendAsync(world.Request(" ")));
        await Assert.ThrowsAsync<ArgumentException>(() => world.SendAsync(world.Request(new string('x', PreparationChatLimits.MaxMessageLength + 1))));
        await Assert.ThrowsAsync<ArgumentNullException>(() => world.SendAsync(world.Request("Hola") with { Consent = null! }));
        await Assert.ThrowsAsync<ArgumentException>(() => world.SendAsync(world.Request("Hola") with { Orchestrator = new AgentId("juez") }));

        Assert.Empty(world.Harness.Claude.Starts);
        Assert.Empty(world.Harness.Payloads<AgentRunStartedPayload>());
    }

    [Fact]
    public void Adding_a_judge_is_offered_only_when_a_vendor_produces_nothing()
    {
        var onlyClaude = new[]
        {
            Agent("orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic),
            Agent("ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic),
        };

        Assert.Contains("addJudge", PreparationChatService.SuggestionKinds);
        Assert.Contains("\"agent\":{\"anyOf\":[{\"type\":\"string\",\"enum\":[\"ejecutor\",\"orquestador\"]}", PreparationChatService.Schema(onlyClaude.Select(a => a.Id.Value)), StringComparison.Ordinal);
        Assert.Empty(StrictSchemaChecker.Problems(PreparationChatService.Schema(onlyClaude.Select(a => a.Id.Value))));
    }

    private sealed class DraftComparer : IEqualityComparer<RequirementDraft>
    {
        public bool Equals(RequirementDraft? x, RequirementDraft? y) =>
            x is not null && y is not null && x.Statement == y.Statement && x.Priority == y.Priority && x.Criteria.SequenceEqual(y.Criteria);

        public int GetHashCode(RequirementDraft obj) => obj.Statement.GetHashCode(StringComparison.Ordinal);
    }
}
