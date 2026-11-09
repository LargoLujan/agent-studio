using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase13;

/// <summary>
/// F13-16 (ADR-0053, decision 3, «Comprobaciones de respaldo») with the real engine composed over SQLite: whatever a screen
/// did, the engine does not start a plan whose writers run at medium integrity unless the approval confirmed the person with
/// Windows Hello or the verified lock of Windows. The dialog alone, a missing confirmation and a claim that confirms nothing are
/// refused before anything is written. Nothing is launched: the scripted CLIs are never started.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class EngineBacksTheBarrierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Phase4App App, RunRequest Request)> OpenAsync(TempGitRepo repo)
    {
        var services = Phase4Composition.Services(
                Phase4Paths.Beside(repo.Root),
                new ScriptedApprovalPrompt(),
                Phase4ScriptedProviders.Over(ScriptedCli.Claude(TimeProvider.System), ScriptedCli.Codex(TimeProvider.System)))
            .WithoutCliProbes();
        var app = await Phase4App.OpenAsync(repo.Root, services, Roster());

        // Codex writing is a writer at medium integrity (ADR-0018): this plan is on the list of the fixed barrier.
        var request = await app.RunRequestAsync("Cambiar con Codex.", Requirements(), Plan(Work("T1", "ejecutorCodex", scope: ["src/T1/"])), new VerificationProfile("sin verificación", []));
        Assert.NotEmpty(request.ApprovedWarning ?? string.Empty);
        return (app, request);
    }

    [Theory]
    [InlineData(ApprovalVerification.Dialog)]
    [InlineData(ApprovalVerification.WindowsHelloUnavailable)]
    public async Task A_plan_with_writers_approved_without_confirming_the_person_does_not_start_and_nothing_is_written(ApprovalVerification method)
    {
        using var repo = TempGitRepo.Create();
        var (app, request) = await OpenAsync(repo);
        await using (app)
        {
            var before = (await app.LoadLogAsync()).Entries.Count;

            await Assert.ThrowsAsync<InvalidOperationException>(() => app.Orchestrator.StartAsync(request with { PlanApproval = request.PlanApproval with { Verification = method } }, Ct));

            Assert.Empty(await app.Orchestrator.ListRunsAsync(app.Project, Ct));
            Assert.Equal(before, (await app.LoadLogAsync()).Entries.Count);
        }
    }

    [Theory]
    [InlineData(ApprovalVerification.WindowsHello)]
    [InlineData(ApprovalVerification.WindowsLock)]
    public async Task A_plan_with_writers_approved_with_hello_or_the_verified_lock_starts(ApprovalVerification method)
    {
        using var repo = TempGitRepo.Create();
        var (app, request) = await OpenAsync(repo);
        await using (app)
        {
            await using var run = await app.Orchestrator.StartAsync(request with { PlanApproval = request.PlanApproval with { Verification = method } }, Ct);

            Assert.Single(await app.Orchestrator.ListRunsAsync(app.Project, Ct));
            await run.CancelAsync("fin de la prueba");
            await run.Completion.WaitAsync(TimeSpan.FromMinutes(2), Ct);
        }
    }
}
