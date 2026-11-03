using AgentStudio.Application.Decisions;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Orchestration;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Orchestration.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Orchestration.Engine;

public sealed class CompositionAndReportTests
{
    [Fact]
    public async Task The_orchestrator_resolves_from_the_ports_and_plans_through_them()
    {
        using var harness = new EngineHarness();
        harness.Claude.Script("orquestador", null, 1, new AgentScript { StructuredOutput = PlanJson.Plan(PlanJson.Task("T1", covers: ["RQ-01"])).ToJsonString() });
        var services = new ServiceCollection()
            .AddSingleton<IDecisionRecorder>(harness.Decisions)
            .AddSingleton<IEvidenceStore>(harness.Evidence)
            .AddSingleton<IPlanStore>(harness.Plans)
            .AddSingleton<IRunRegistry>(harness.Runs)
            .AddSingleton<IRequirementStore>(harness.RequirementStore)
            .AddSingleton<IAgentRoster>(harness.Roster)
            .AddSingleton<IProjectAgentProviders>(harness.Providers)
            .AddSingleton<IWorktreeManager>(harness.Worktrees)
            .AddSingleton<IVerificationRunner>(harness.Verification)
            .AddSingleton<IIntegrator>(harness.Integrator)
            .AddSingleton<IIndependentReview>(harness.Review)
            .AddSingleton<IApprovalGate>(harness.Approvals)
            .AddSingleton(harness.Skills)
            .AddSingleton(harness.Paths)
            .AddSingleton<TimeProvider>(harness.Time)
            .AddAgentStudioOrchestration();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var orchestrator = provider.GetRequiredService<IOrchestrator>();
        var proposal = await orchestrator.PlanAsync(harness.PlanningRequest(), null, TestContext.Current.CancellationToken);

        Assert.IsType<Orchestrator>(orchestrator);
        Assert.Same(orchestrator, provider.GetRequiredService<IOrchestrator>());
        Assert.Equal(AgentRunStatus.Succeeded, proposal.Status);
        Assert.Contains(harness.RepositoryRoot, harness.Providers.Roots);
    }

    [Fact]
    public void The_registration_adds_a_clock_only_when_there_is_none()
    {
        var withClock = new ServiceCollection().AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()).AddAgentStudioOrchestration();
        var withoutClock = new ServiceCollection().AddAgentStudioOrchestration();

        Assert.Single(withClock, d => d.ServiceType == typeof(TimeProvider));
        Assert.Same(TimeProvider.System, Assert.Single(withoutClock, d => d.ServiceType == typeof(TimeProvider)).ImplementationInstance);
        Assert.Single(withoutClock, d => d.ServiceType == typeof(IOrchestrator));
    }

    [Fact]
    public void The_report_schema_is_portable_to_strict_mode()
    {
        Assert.Empty(StrictSchemaChecker.Problems(ExecutorReport.Schema));
        Assert.DoesNotContain('\n', ExecutorReport.Schema);
    }

    [Fact]
    public void A_report_in_the_schema_is_read_and_its_outputs_mapped()
    {
        var report = ExecutorReport.Parse(AgentScript.Report(outputs: [("k", "1"), ("k", "2"), ("otra", "x")]), out var problem);

        Assert.Null(problem);
        Assert.NotNull(report);
        Assert.Equal(ExecutorReport.Completed, report.Status);
        Assert.Equal("2", report.OutputMap()["k"]);
        Assert.Equal("x", report.OutputMap()["otra"]);
    }

    [Theory]
    [InlineData("""{"status":"completed","summary":"x","filesChanged":[],"claims":[],"openIssues":[],"outputs":[],"extra":1}""")]
    [InlineData("""{"status":"completed","summary":"x","filesChanged":[],"claims":[],"openIssues":[]}""")]
    [InlineData("""{"status":"done","summary":"x","filesChanged":[],"claims":[],"openIssues":[],"outputs":[]}""")]
    [InlineData("""{"status":"completed","summary":null,"filesChanged":[],"claims":[],"openIssues":[],"outputs":[]}""")]
    [InlineData("""{"status":"completed","summary":"x","filesChanged":[null],"claims":[],"openIssues":[],"outputs":[]}""")]
    [InlineData("""{"status":"completed","status":"failed","summary":"x","filesChanged":[],"claims":[],"openIssues":[],"outputs":[]}""")]
    [InlineData("""{"status":"completed","summary":"x","filesChanged":[],"claims":[],"openIssues":[],"outputs":[{"key":"k"}]}""")]
    [InlineData("no es json")]
    [InlineData("")]
    public void A_report_outside_the_schema_is_refused(string json)
    {
        var report = ExecutorReport.Parse(json, out var problem);

        Assert.Null(report);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }
}
