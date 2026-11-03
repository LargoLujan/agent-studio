using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Challenges;
using AgentStudio.Debate.Judging;
using AgentStudio.Tests.Debate.Audit.Fixtures;
using AgentStudio.Tests.Debate.Challenges.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using static AgentStudio.Tests.Debate.Audit.Fixtures.AuditWorld;

namespace AgentStudio.Tests.Debate.Audit;

/// <summary>What <c>AddAgentStudioAudits</c> registers resolves over what the rest of the app registers, here as doubles.</summary>
public sealed class AuditCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceCollection Platform(ProjectAuditBench bench, ChallengeBench challenges)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDecisionRecorder>(bench.World.Recorder);
        services.AddSingleton<IRunRegistry>(bench.World.Runs);
        services.AddSingleton<IPlanStore>(bench.World.Plans);
        services.AddSingleton<IRequirementStore>(bench.World.Requirements);
        services.AddSingleton<IEvidenceStore>(bench.World.Evidence);
        services.AddSingleton<IVerdictStore>(bench.Verdicts);
        services.AddSingleton<IAgentRoster>(bench.Template);
        services.AddSingleton<IProjectAuditRegistry>(bench.Audits);
        services.AddSingleton<IWorktreeManager>(bench.Repository);
        services.AddSingleton<IVerificationRunner>(bench.Verifier);
        services.AddSingleton<IAgentJudgeFactory>(bench.Judges);
        services.AddSingleton<IApprovalGate>(bench.Gate);
        services.AddSingleton<IProjectActivity>(bench.Activity);
        services.AddSingleton<IAuditVerificationProfiles>(bench.Profiles);
        services.AddSingleton<IDebateCoordinator>(challenges.Coordinator);
        services.AddSingleton<TimeProvider>(bench.World.Clock);
        services.AddSingleton(new OrchestrationPaths(Path.Combine(bench.Root, "wt"), Path.Combine(bench.Root, "dossiers"), Path.Combine(bench.Root, "skills")));
        return services;
    }

    [Fact]
    public async Task The_audits_and_the_challenges_resolve_once_each_and_the_copies_go_beside_the_dossiers()
    {
        var run = await PreparedRun.CleanAsync();
        using var bench = new ProjectAuditBench(run);
        var challenges = new ChallengeBench(run);
        using var container = Platform(bench, challenges).AddAgentStudioAudits().BuildServiceProvider();

        Assert.IsType<OrchestratorAuditService>(container.GetRequiredService<IOrchestratorAuditService>());
        Assert.IsType<ProjectAuditService>(container.GetRequiredService<IProjectAuditService>());
        Assert.IsType<VerdictChallengeService>(container.GetRequiredService<IVerdictChallenges>());
        Assert.Same(container.GetRequiredService<IProjectAuditService>(), container.GetRequiredService<IProjectAuditService>());
        Assert.Equal(Path.Combine(bench.Root, "audits"), container.GetRequiredService<AuditOptions>().SnapshotRoot);
        Assert.Equal(Path.Combine(bench.Root, "dossiers"), container.GetRequiredService<JudgingOptions>().DossierRoot);

        var report = await container.GetRequiredService<IOrchestratorAuditService>().AuditRunAsync(bench.Project, Run, Ct);
        Assert.Empty(report.Findings);
        var audit = container.GetRequiredService<IProjectAuditService>();
        var outcome = await audit.AuditAsync(await audit.PrepareAsync(bench.Request(), Ct), null, Ct);
        Assert.True(outcome.IsTrustworthy);
    }

    [Fact]
    public async Task Options_registered_earlier_are_kept()
    {
        var run = await PreparedRun.CleanAsync();
        using var bench = new ProjectAuditBench(run);
        var services = Platform(bench, new ChallengeBench(run));
        var copies = Path.Combine(bench.Root, "otras-copias");
        services.AddSingleton(new AuditOptions(copies));

        using var container = services.AddAgentStudioAudits().BuildServiceProvider();

        Assert.Equal(copies, container.GetRequiredService<AuditOptions>().SnapshotRoot);
    }
}
