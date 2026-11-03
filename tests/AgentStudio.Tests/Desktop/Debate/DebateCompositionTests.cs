using AgentStudio.Application.Coordination;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Security;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Debate;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Domain.Debate;
using AgentStudio.Tests.Desktop.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Desktop.Debate.DebateSamples;

namespace AgentStudio.Tests.Desktop.Debate;

/// <summary>
/// The composition of the Phase 5 screens (<see cref="DebateUiServices.AddDebateUiServices"/>): it registers no port of its
/// own, takes each one the composition root registered, and without them the screens say what is missing. Nothing is run
/// here: the services are only resolved.
/// </summary>
public sealed class DebateCompositionTests
{
    [Fact]
    public async Task Alone_the_screens_are_composed_over_no_port_and_say_what_is_missing()
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddDebateUiServices();
        await using var provider = services.BuildServiceProvider();

        Assert.Equal(DebateScreenBackend.None, provider.GetRequiredService<DebateScreenBackend>());
        Assert.Equal(new RunReviewServices(), provider.GetRequiredService<RunReviewServices>());
        var screens = provider.GetRequiredService<DebateScreens>();
        Assert.False(screens.CanNavigate);

        var document = screens.Debates(Root);
        await document.OpenAsync();
        Assert.Contains(document.Notices, n => n.StartsWith("Falta el coordinador de debates", StringComparison.Ordinal));
        Assert.Contains(document.Notices, n => n.StartsWith("Falta el almacén del proyecto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_port_the_composition_root_registered_reaches_the_screens_and_the_review_of_a_run()
    {
        var coordinator = new FakeDebateCoordinator();
        var challenges = new FakeVerdictChallenges();
        var audits = new FakeProjectAuditService();
        var auditor = new FakeOrchestratorAuditService();
        var gate = new FakeApprovalGate();
        var verdicts = new FakeVerdictStore();
        var evidence = new FakeEvidenceStore();
        var requirements = new FakeRequirementStore(Requirements);
        var runs = new FakeRunRegistry();
        var navigation = new FakeReviewNavigation();
        DebateModeRequirements fixedRequirements = _ => Requirements;
        var services = new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IDebateCoordinator>(coordinator)
            .AddSingleton<IVerdictChallenges>(challenges)
            .AddSingleton<IProjectAuditService>(audits)
            .AddSingleton<IOrchestratorAuditService>(auditor)
            .AddSingleton<IApprovalGate>(gate)
            .AddSingleton<IProjectWorkspace>(new FakeWorkspace())
            .AddSingleton<IAgentRoster>(new FakeRoster(() => Roster))
            .AddSingleton<IRequirementStore>(requirements)
            .AddSingleton<IRunRegistry>(runs)
            .AddSingleton<IVerdictStore>(verdicts)
            .AddSingleton<IEvidenceStore>(evidence)
            .AddSingleton(fixedRequirements)
            .AddSingleton<IReviewNavigation>(navigation)
            .AddDebateUiServices();
        await using var provider = services.BuildServiceProvider();

        var backend = provider.GetRequiredService<DebateScreenBackend>();
        Assert.Same(coordinator, backend.Coordinator);
        Assert.Same(challenges, backend.Challenges);
        Assert.Same(audits, backend.Audits);
        Assert.Same(requirements, backend.Requirements);
        Assert.Same(runs, backend.Runs);
        Assert.Same(verdicts, backend.Verdicts);
        Assert.Same(evidence, backend.Evidence);
        Assert.Same(fixedRequirements, backend.FixedRequirements);
        Assert.NotNull(backend.Workspace);
        Assert.NotNull(backend.Roster);

        var review = provider.GetRequiredService<RunReviewServices>();
        Assert.Same(auditor, review.Auditor);
        Assert.Same(verdicts, review.Verdicts);
        Assert.Same(challenges, review.Challenges);
        Assert.Same(gate, review.Approvals);
        Assert.Same(requirements, review.Requirements);
        Assert.Null(review.Challenge);
        Assert.Null(review.CorrectiveTasks);

        var screens = provider.GetRequiredService<DebateScreens>();
        Assert.True(screens.CanNavigate);
        Assert.Same(screens.Debates(Root), screens.Debates(Root));
        await screens.CreateCorrectiveTasksAsync(Root, "Tareas correctivas.");
        Assert.Equal([(Root, "Tareas correctivas.")], navigation.CorrectiveTasks);
    }

    [Fact]
    public void A_composition_root_that_registered_the_screens_first_keeps_its_own()
    {
        var own = new DebateScreenBackend(new FakeDebateCoordinator());
        var services = new ServiceCollection().AddSingleton(own).AddDebateUiServices();
        using var provider = services.BuildServiceProvider();

        Assert.Same(own, provider.GetRequiredService<DebateScreenBackend>());
        Assert.Same(own, provider.GetRequiredService<DebateScreens>().Backend);
    }
}
