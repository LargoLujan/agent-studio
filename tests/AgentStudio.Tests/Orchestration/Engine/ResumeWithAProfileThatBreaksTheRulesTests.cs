using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Orchestration.Engine;

/// <summary>
/// A run frozen with a verification profile before a rule of this version existed (docs/24: no invisible or direction character, no
/// double quote in a program) is never resumed with it: the resume ends as a clear refusal, with the alert of a run that cannot be
/// resumed and a text the person reads in their language, and nothing more is launched. Never an exception of the verifier at every
/// attempt.
/// </summary>
public sealed class ResumeWithAProfileThatBreaksTheRulesTests
{
    [Fact]
    public async Task A_run_frozen_with_a_profile_that_now_breaks_a_rule_is_not_resumed_and_says_why_in_plain_words()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("ejecutor", "T1", 1, new AgentScript { Hang = true });
        var steps = Profile().Steps;

        // What an older version let through: a mark of direction inside an argument (given by code point, so no invisible character sits here).
        var profile = Profile() with { Steps = [steps[0] with { Arguments = ["build" + char.ConvertFromUtf32(0x202E) + "nls.exe"] }, .. steps.Skip(1)] };
        var first = await harness.CreateOrchestrator().StartAsync(harness.Request(Plan(Work("T1")), profile: profile), TestContext.Current.CancellationToken);
        await harness.WaitForStatusAsync(first, "T1", WorkTaskStatus.Running);
        await first.DisposeAsync();
        Assert.Equal(RunPhase.Interrupted, (await first.CompleteAsync(harness)).Phase);

        var error = await Assert.ThrowsAsync<UserTextException>(() => harness.CreateOrchestrator().ResumeAsync(harness.Project, first.Id, TestContext.Current.CancellationToken));

        Assert.Equal(OrchestrationUserTexts.ResumeProfileBreaksRules, error.Text.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        var (_, alert) = Assert.Single(harness.Alerts("run.not-resumable"));
        Assert.Contains("invisible or direction character", alert.Message, StringComparison.Ordinal);
        Assert.Single(harness.Claude.Starts);
    }
}
