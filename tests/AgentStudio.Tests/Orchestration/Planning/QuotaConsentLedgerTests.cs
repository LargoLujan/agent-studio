using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Planning;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Planning;

/// <summary>
/// One register of the OKs spent for the whole app (docs/17 D-20 and D-44): an OK given for one use is spent once, atomically,
/// and a service never takes an OK another one already spent.
/// </summary>
public sealed class QuotaConsentLedgerTests
{
    private static readonly QuotaConsent Consent = new("miveritas", Start, "Revisar el orden: ~3.000 tokens (estimado)");

    [Fact]
    public void An_OK_is_spent_once()
    {
        var ledger = new QuotaConsentLedger();

        Assert.True(ledger.TrySpend(Consent));
        Assert.False(ledger.TrySpend(Consent));
        Assert.False(ledger.TrySpend(Consent with { }));
        Assert.True(ledger.TrySpend(Consent with { DecidedAt = Start.AddSeconds(1) }));
    }

    [Fact]
    public async Task Spending_is_atomic_when_many_try_at_once()
    {
        var ledger = new QuotaConsentLedger();

        var spent = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => ledger.TrySpend(Consent), TestContext.Current.CancellationToken)));

        Assert.Equal(1, spent.Count(s => s));
    }

    [Fact]
    public async Task An_OK_spent_by_one_service_is_refused_by_another_and_launches_nothing()
    {
        using var harness = new EngineHarness();
        var ledger = new QuotaConsentLedger();
        var runner = new AuxiliaryAgentRunner(harness.Decisions, harness.Evidence, harness.Providers, harness.Time);
        var review = new OrderReviewService(runner, harness.Worktrees, ledger, harness.Time);
        var chat = new PreparationChatService(runner, harness.Worktrees, new NoSecrets(), ledger, harness.Time);
        var consent = new QuotaConsent("miveritas", harness.Time.GetUtcNow(), "Un uso");
        harness.Claude.Fallback = (_, _) => new AgentScript { StructuredOutput = "{\"dependencies\":[],\"parallelOk\":[],\"harmful\":[]}" };
        var plan = Plan(Work("T1").Covering("RQ-01"));

        await review.ReviewAsync(new OrderReviewRequest(harness.Project, new RunId("20261015-a1b2c3"), plan, Roster(), new AgentId("orquestador"), consent), null, TestContext.Current.CancellationToken);
        var starts = harness.Claude.Starts.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync(new PreparationChatRequest(harness.Project, Roster(), new AgentId("orquestador"), "Hola", null, consent), null, TestContext.Current.CancellationToken));
        Assert.Equal(starts, harness.Claude.Starts.Count);
        Assert.False(ledger.TrySpend(consent));
    }

    private sealed class NoSecrets : AgentStudio.Application.Security.ISecretRedactor
    {
        public string Redact(string text) => text;
    }
}
