using System.IO;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Audit;
using AgentStudio.Debate.Challenges;
using AgentStudio.Debate.Judging;
using AgentStudio.Debate.Judging.Debates;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Orchestrator.Monitor;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Memory.Derivation;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Orchestration.Integration;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Integration.Phase4;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Integration.Phase5;

/// <summary>
/// The composition of Phase 5 (docs/15 section 9, «Integración»; D-05, D-44 and D-45): the container of Phase 4 with the arena
/// of debates and the audits, each port the real implementation of its module and one <see cref="IProjectActivity"/> for the
/// whole app, which is also the <see cref="IRunActivity"/> the memory waits on. On a real project (SQLite and a temporary git
/// repository) every turn and every judge of a debate goes through the prompt journal and the user's quota record; a debate,
/// an audit and a run of the same project never go on at once, and while a debate holds the project the memory derives
/// nothing, exports nothing and a write of it waits until the debate ends. The actions the room, the challenges, the audits
/// and the monitor write are the ones the delivery reads.
/// </summary>
public sealed class Phase5CompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_module_of_phase_5_composes_into_one_container_with_one_project_activity_and_writes_nothing()
    {
        using var temp = new TempDirectory();
        var paths = Phase4Paths.Under(temp.Path);
        var clis = new Phase5Clis();
        await using var container = Phase5Composition.Services(paths, new ScriptedApprovalPrompt(), clis.Providers)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // Each port of Phase 5 is the real implementation of its module.
        Assert.IsType<DebateCoordinator>(container.GetRequiredService<IDebateCoordinator>());
        Assert.IsType<DebateJudgment>(container.GetRequiredService<IDebateJudgment>());
        Assert.IsType<ProjectAuditService>(container.GetRequiredService<IProjectAuditService>());
        Assert.IsType<OrchestratorAuditService>(container.GetRequiredService<IOrchestratorAuditService>());
        Assert.IsType<VerdictChallengeService>(container.GetRequiredService<IVerdictChallenges>());
        Assert.IsType<SqliteDebateRegistry>(container.GetRequiredService<IDebateRegistry>());
        Assert.IsType<SqliteProjectAuditRegistry>(container.GetRequiredService<IProjectAuditRegistry>());
        Assert.IsType<DeliveryService>(container.GetRequiredService<IDeliveryService>());
        Assert.IsType<FileVerificationProfileSource>(container.GetRequiredService<IVerificationProfileSource>());

        // One activity with agents for the whole app (D-05): the orchestrator's, the arena's and the memory's are one instance.
        var activity = Assert.IsType<OrchestrationActivity>(container.GetRequiredService<IProjectActivity>());
        Assert.Same(activity, container.GetRequiredService<IRunActivity>());
        Assert.Same(activity, Assert.IsType<Orchestrator>(container.GetRequiredService<IOrchestrator>()).Activity);
        var ports = container.GetRequiredService<DebateArenaPorts>();
        Assert.Same(activity, ports.Activity);

        // The arena uses the providers, the judgment and the stores of the app, never its own.
        Assert.Same(container.GetRequiredService<IProjectAgentProviders>(), ports.Providers);
        Assert.Same(container.GetRequiredService<IDebateJudgment>(), ports.Judgment);
        Assert.Same(container.GetRequiredService<IDecisionRecorder>(), ports.Decisions);
        Assert.Same(container.GetRequiredService<IDebateRegistry>(), ports.Debates);
        Assert.Same(container.GetRequiredService<IProjectAuditRegistry>(), ports.Audits);
        Assert.Same(container.GetRequiredService<IJudgeDossierPackageBuilder>(), ports.Dossiers);

        // The folders are the composition's, next to the worktrees and the dossiers: never the real %LOCALAPPDATA%.
        Assert.Equal(Path.Combine(paths.Root, "debates"), container.GetRequiredService<DebateArenaOptions>().DebatesRoot);
        Assert.Equal(Path.Combine(paths.Root, "audits"), container.GetRequiredService<AuditOptions>().SnapshotRoot);
        Assert.Equal(paths.Engine.DossierRoot, container.GetRequiredService<JudgingOptions>().DossierRoot);

        // Every provider of a project, the ones the debates use, goes through the journal and the quota record.
        var root = temp.CreateDirectory("proyecto");
        var providers = container.GetRequiredService<IProjectAgentProviders>().For(root);
        Assert.Equal([ProviderId.ClaudeCode, ProviderId.CodexCli], providers.Select(p => p.Provider));
        Assert.All(providers, p => Assert.True(AgentProviderPipeline.IsJournaled(p) && AgentProviderPipeline.IsQuotaObserved(p), $"The provider {p.Provider} skips the journal or the quota record."));

        // Composing wrote nothing and launched nothing.
        Assert.False(Directory.Exists(Path.Combine(root, ".agentstudio")), "Composing the app wrote into the project.");
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "debates")));
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "audits")));
        Assert.Empty(clis.Launches);
    }

    /// <summary>Stands for the executable locator and both process runners: nothing may be looked up nor started.</summary>
    private sealed class NoProcesses : IExecutableLocator, IProcessRunner, IStreamingProcessRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? Find(string command) => Refuse<string?>("find " + command);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) => Refuse<Task<ProcessResult>>("run " + spec.FileName);

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken) => Refuse<Task<IProcessSession>>("start " + spec.FileName);

        private T Refuse<T>(string what)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException($"Nothing may be looked up nor launched here ({what}).");
        }
    }

    /// <summary>
    /// The same in the app as it composes itself (<see cref="AppComposition.AddAgentStudioApp"/>): the arena, the audits and
    /// the challenges are the real ones, and the arena enters the very activity the orchestrator reports to and the memory
    /// waits on; their folders live under the app's local folder, and composing launches nothing.
    /// </summary>
    [Fact]
    public async Task In_the_app_as_it_composes_itself_the_arena_the_audits_and_the_orchestrator_share_one_activity()
    {
        using var temp = new TempDirectory();
        var folders = new AppFolders(new AppDataPaths(temp.Combine("roaming")), new LocalDataPaths(temp.Combine("local")), null);
        var processes = new NoProcesses();
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddTestLocalData(temp.Combine("local"));
        services.AddAgentStudioApp(folders);
        services.Replace(ServiceDescriptor.Singleton<IExecutableLocator>(processes));
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(processes));
        services.Replace(ServiceDescriptor.Singleton<IStreamingProcessRunner>(processes));
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var activity = Assert.IsType<OrchestrationActivity>(container.GetRequiredService<IProjectActivity>());
        Assert.Same(activity, container.GetRequiredService<IRunActivity>());
        Assert.Same(activity, Assert.IsType<Orchestrator>(container.GetRequiredService<IOrchestrator>()).Activity);
        var ports = container.GetRequiredService<DebateArenaPorts>();
        Assert.Same(activity, ports.Activity);
        Assert.Same(container.GetRequiredService<IProjectAgentProviders>(), ports.Providers);
        Assert.Same(container.GetRequiredService<IDebateJudgment>(), ports.Judgment);
        Assert.IsType<DebateCoordinator>(container.GetRequiredService<IDebateCoordinator>());
        Assert.IsType<DebateJudgment>(container.GetRequiredService<IDebateJudgment>());
        Assert.IsType<ProjectAuditService>(container.GetRequiredService<IProjectAuditService>());
        Assert.IsType<OrchestratorAuditService>(container.GetRequiredService<IOrchestratorAuditService>());
        Assert.IsType<VerdictChallengeService>(container.GetRequiredService<IVerdictChallenges>());
        Assert.IsType<SqliteDebateRegistry>(container.GetRequiredService<IDebateRegistry>());
        Assert.IsType<SqliteProjectAuditRegistry>(container.GetRequiredService<IProjectAuditRegistry>());
        Assert.Same(container.GetRequiredService<IProjectAuditRegistry>(), ports.Audits);

        // «Ejecutar pruebas» and the audit read the one trusted profile the user saved, through the same adapter.
        Assert.Same((object)container.GetRequiredService<IDebateTestProfiles>(), container.GetRequiredService<IAuditVerificationProfiles>());
        Assert.Same((object)container.GetRequiredService<IDebateTestProfiles>(), ports.TestProfiles);

        // Their folders are the app's, never the real %LOCALAPPDATA%; composing launched nothing.
        var local = Path.GetFullPath(folders.Local.BaseDirectory);
        Assert.StartsWith(local, Path.GetFullPath(container.GetRequiredService<DebateArenaOptions>().DebatesRoot), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(local, Path.GetFullPath(container.GetRequiredService<AuditOptions>().SnapshotRoot), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, processes.Calls);
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task Every_turn_and_every_judge_of_a_debate_go_through_the_prompt_journal_and_the_quota_record()
    {
        using var repo = Phase5Runs.Repo();
        var paths = Phase4Paths.Beside(repo.Root);
        var clis = new Phase5Clis();
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(paths, new ScriptedApprovalPrompt(), clis.Providers), Phase5Roster.Agents());

        var request = new DebateRequest(
            app.Project,
            DebateMode.ArchitectureReview,
            "¿Dónde guardamos la caché de las respuestas del servicio?",
            Phase5Checks.ChoiceRequirements(),
            new DebateSubject(DebateSubjectKind.None, null),
            [new DebateParticipantRequest(Phase5Roster.Architect, DebateRole.Proponent), new DebateParticipantRequest(Phase5Roster.GreenCritic, DebateRole.Critic)],
            [Phase5Roster.ClaudeJudge, Phase5Roster.CodexJudge],
            DebateJudgingKind.CrossVendorPanel,
            new DebateLimits(2, TimeSpan.FromMinutes(30), 400_000));
        await using var debate = await Phase5Checks.OpenAsync(app, request);
        var result = await debate.Completion.WaitAsync(Phase5Runs.Guard, Ct);
        var log = await app.LoadLogAsync();
        Assert.True(result is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved }, result.Summary + "\n" + Phase4Checks.Describe(log));

        // Every launch of the debate, the four turns and the two judges, is in the log and reached a CLI once.
        var unit = debate.Id.Value;
        var started = (await app.PayloadsAsync<AgentRunStartedPayload>(unit)).Select(p => p.Payload).ToList();
        Assert.Equal(
            ["juez-juez-claude#1", "juez-juez-codex#1", "r1-arquitecta-azul#1", "r1-critico-verde#1", "r2-arquitecta-azul#1", "r2-critico-verde#1"],
            started.Select(s => $"{s.Task}#{s.Attempt}").Order(StringComparer.Ordinal));
        Assert.Equal(
            started.Select(s => s.Task!.Value.Value).Order(StringComparer.Ordinal),
            clis.LaunchesOf(unit).Select(r => r.TaskId!).Order(StringComparer.Ordinal));

        // The prompt journal of the project has each of them, tagged #debate and naming the debate (D-45), on its CLI; an
        // anchored launch also names its entry with its day (docs/17 D-02, addendum of 2026-11-03).
        var journal = Phase5Checks.JournalLines(repo.Root);
        foreach (var launch in started)
        {
            var line = Assert.Single(journal, l => l.Contains($"(tarea {launch.Task} · debate {unit} · entrada ", StringComparison.Ordinal));
            Assert.Contains("#debate", line, StringComparison.Ordinal);
            Assert.Contains($" · {launch.Provider.Value}/", line, StringComparison.Ordinal);
        }

        // The user's quota record has the window each launch announced, under the provider that ran it (D-14).
        var quota = await app.Get<IQuotaObservations>().LatestAsync(Ct);
        foreach (var launch in started)
        {
            var window = Phase5Clis.QuotaWindowOf(unit, launch.Task!.Value.Value);
            var observed = Assert.Single(quota, o => o.Window == window);
            Assert.Equal(launch.Provider, observed.Provider);
        }

        Assert.True(File.Exists(paths.QuotaLog));
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
    }

    [Fact]
    [Trait("Category", "LocalTools")]
    public async Task A_debate_an_audit_and_a_run_of_one_project_never_go_on_at_once_and_the_memory_waits_for_the_debate()
    {
        // HEAD already has the feature, so the audit's own verification of HEAD passes; the run adds another file.
        using var repo = Phase5Runs.Repo();
        repo.Write("src/feature.txt", "función de la base\n");
        repo.Run("add", "-A");
        repo.Run("commit", "-q", "-m", "La función ya existe");
        var paths = Phase4Paths.Beside(repo.Root);
        var clis = new Phase5Clis { Executor = (_, _) => AgentScript.Completed(("src/otra.txt", "otra función\n")) };
        await using var app = await Phase4App.OpenAsync(repo.Root, Phase5Composition.Services(paths, new ScriptedApprovalPrompt(), clis.Providers), Phase5Roster.Agents());
        var project = app.Project;
        await app.Get<IVerificationProfileSource>().SaveAsync(project, Phase5Runs.Profile(), Ct);
        await app.Get<IRequirementStore>().SaveAsync(project.ProjectId, Requirements(), Ct);
        var activity = app.Get<IProjectActivity>();
        var coordinator = app.Get<IDebateCoordinator>();
        var audits = app.Get<IProjectAuditService>();
        var auditRequest = new ProjectAuditRequest(project, Requirements().ContentHash, Phase5Roster.CodexJudge, [VendorId.Anthropic]);
        var runRequest = await app.RunRequestAsync("Añade otra función y que el juez la revise.", Requirements(), Phase5Runs.Reviewed(), Phase5Runs.Profile());
        DebateRequest Debate(string question) => new(
            project,
            DebateMode.Free,
            question,
            Phase5Checks.ChoiceRequirements(),
            new DebateSubject(DebateSubjectKind.None, null),
            [new DebateParticipantRequest(Phase5Roster.Architect, DebateRole.Proponent), new DebateParticipantRequest(Phase5Roster.GreyReviewer, DebateRole.Critic)],
            [Phase5Roster.CodexJudge],
            DebateJudgingKind.IndependentJudge,
            new DebateLimits(1, TimeSpan.FromMinutes(30), 400_000));

        // ---- A debate holds the project: its turns are running.
        var turns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clis.Hold = r => r.Agent.Role != AgentRole.Judge && DebateIds.IsDebate(r.DebateId) ? turns : null;
        await using var debate = await Phase5Checks.OpenAsync(app, Debate("¿Cacheamos en memoria o en disco?"));
        await Phase3Checks.WaitUntilAsync(() => clis.LaunchesOf(debate.Id.Value).Count == 2, "the two turns of round 1 to be running", Phase5Runs.Guard);
        Assert.Equal(ProjectActivityKind.Debate, activity.Current(project.ProjectId));
        Assert.True(activity.IsBusy(project.ProjectId));
        var entries = (await app.LoadLogAsync()).Entries.Count;

        // Nothing else with agents starts, and nothing is written for trying: the run, the audit and a second debate are refused.
        var run = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.Orchestrator.StartAsync(runRequest, Ct));
        Assert.Contains(nameof(ProjectActivityKind.Debate), run.Message, StringComparison.Ordinal);
        var proposal = await audits.PrepareAsync(auditRequest, Ct);
        Assert.True(proposal.CanJudge, string.Join(" | ", proposal.Problems));
        var audit = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => audits.AuditAsync(proposal, null, Ct));
        Assert.Contains(nameof(ProjectActivityKind.Debate), audit.Message, StringComparison.Ordinal);
        var second = await coordinator.PrepareAsync(Debate("¿Y la caché de las sesiones?"), Ct);
        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => coordinator.OpenAsync(second, Ct));
        Assert.Contains("ocupado", refused.Message, StringComparison.Ordinal);

        // The memory waits (D-05): it derives nothing, exports nothing, and a write of a person waits for the debate to end.
        var derived = await app.Get<IMemoryDerivation>().DeriveAsync(project, Ct);
        Assert.False(derived.Completed);
        Assert.Contains("debate", derived.Reason, StringComparison.Ordinal);
        Assert.Empty(derived.Recorded);
        var export = await app.Get<IMemoryExporter>().ExportAsync(project, "es-ES", Ct);
        Assert.False(export.Exported);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".agentstudio", "memory")));
        var write = app.Get<IProjectMemory>().RecordAsync(project, new MemoryDraft(MemoryKind.Convention, "Pruebas", "Las pruebas usan solo Assert de xUnit.", MemoryOrigin.User, "usuario:nota-1"), Ct);
        Assert.False(write.IsCompleted, "A write of the memory did not wait for the debate.");
        Assert.Equal(entries, (await app.LoadLogAsync()).Entries.Count);

        turns.SetResult();
        var closed = await debate.Completion.WaitAsync(Phase5Runs.Guard, Ct);
        Assert.True(closed is { Phase: DebatePhase.Closed, Outcome: DebateOutcome.Resolved }, closed.Summary);
        var written = await write.WaitAsync(Phase5Runs.Guard, Ct);
        Assert.Null(activity.Current(project.ProjectId));

        // The write waited: it is announced after the debate closed.
        var closedEntry = Assert.Single(await app.PayloadsAsync<DebateClosedPayload>(debate.Id.Value)).Entry;
        var announced = Assert.Single(await app.PayloadsAsync<MemoryRecordedPayload>(), p => p.Payload.Entry == written.Id).Entry;
        Assert.True(announced.Sequence > closedEntry.Sequence, $"The memory was written at entry {announced.Sequence}, before the debate closed at {closedEntry.Sequence}.");

        // Once the project is idle the memory derives the summary of the debate (D-46).
        derived = await app.Get<IMemoryDerivation>().DeriveAsync(project, Ct);
        Assert.True(derived.Completed, derived.Reason);
        Assert.Contains(derived.Recorded, e => e.Kind == MemoryKind.DebateSummary && e.Source == MemorySources.LogEntry(debate.Id.Value, closedEntry.Sequence));

        // ---- A run holds the project: its executor is working.
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clis.Hold = r => r.Agent.Role == AgentRole.Executor ? work : null;
        await using var running = await app.Orchestrator.StartAsync(runRequest, Ct);
        await Phase3Checks.WaitUntilAsync(() => clis.Launches.Any(r => r.Agent.Role == AgentRole.Executor), "the executor of T1 to be working", Phase5Runs.Guard);
        Assert.Equal(ProjectActivityKind.Run, activity.Current(project.ProjectId));
        var third = await coordinator.PrepareAsync(Debate("¿Y la caché de las imágenes?"), Ct);
        refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => coordinator.OpenAsync(third, Ct));
        Assert.Contains(nameof(ProjectActivityKind.Run), refused.Message, StringComparison.Ordinal);

        // Prepared now, with the run in the log (its producers count): only the project being busy can refuse it.
        proposal = await audits.PrepareAsync(auditRequest, Ct);
        Assert.True(proposal.CanJudge, string.Join(" | ", proposal.Problems));
        audit = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => audits.AuditAsync(proposal, null, Ct));
        Assert.Contains(nameof(ProjectActivityKind.Run), audit.Message, StringComparison.Ordinal);
        Assert.False((await app.Get<IMemoryDerivation>().DeriveAsync(project, Ct)).Completed);

        work.SetResult();
        var ran = await running.Completion.WaitAsync(Phase5Runs.Guard, Ct);
        Assert.True(ran.Phase == RunPhase.Succeeded, Phase4Checks.Describe(ran) + "\n" + Phase4Checks.Describe(await app.LoadLogAsync()));
        Assert.Null(activity.Current(project.ProjectId));

        // ---- An audit holds the project: its judge is reading the code.
        var judging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clis.Hold = r => r.DebateId?.StartsWith(AuditIds.Prefix, StringComparison.Ordinal) == true ? judging : null;
        proposal = await audits.PrepareAsync(auditRequest, Ct);
        Assert.True(proposal.CanJudge, string.Join(" | ", proposal.Problems));
        var auditing = audits.AuditAsync(proposal, null, Ct);
        await Phase3Checks.WaitUntilAsync(() => clis.Launches.Any(r => r.DebateId?.StartsWith(AuditIds.Prefix, StringComparison.Ordinal) == true) || auditing.IsCompleted, "the judge of the audit to be launched", Phase5Runs.Guard);
        Assert.False(auditing.IsCompleted, auditing.IsCompletedSuccessfully ? "The audit ended without launching its judge." : auditing.Exception?.Message);
        Assert.Equal(ProjectActivityKind.Audit, activity.Current(project.ProjectId));
        run = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.Orchestrator.StartAsync(runRequest, Ct));
        Assert.Contains(nameof(ProjectActivityKind.Audit), run.Message, StringComparison.Ordinal);
        var fourth = await coordinator.PrepareAsync(Debate("¿Y la caché de los informes?"), Ct);
        refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => coordinator.OpenAsync(fourth, Ct));
        Assert.Contains(nameof(ProjectActivityKind.Audit), refused.Message, StringComparison.Ordinal);

        judging.SetResult();
        var report = await auditing.WaitAsync(Phase5Runs.Guard, Ct);
        Assert.True(report is { VerdictId: not null, IsTrustworthy: true }, report.Verdict.Summary);
        Assert.Equal(VerdictDecision.Approved, report.Verdict.Decision);
        Assert.Null(activity.Current(project.ProjectId));

        // Whatever was refused wrote nothing: each refusal left the log as it was, and it verifies.
        var final = await app.LoadLogAsync();
        Assert.True(final.Verification.IsValid, final.Verification.Problem);
        Assert.Single(await app.PayloadsAsync<RunStartedPayload>());
        Assert.Single(await app.PayloadsAsync<DebateOpenedPayload>());
        Assert.Single(await app.PayloadsAsync<ProjectAuditStartedPayload>());
    }

    /// <summary>
    /// The actions the room (<c>debate.open</c>), the challenges (<c>verdict.revision.accept</c>, and the opening of their
    /// revision), the audits (the pending problems) and the monitor (<c>replan.dismiss</c>) write, each kept in its own module
    /// because ADR-0002 does not let one module see another, are the very values the delivery reads; so are the copies the
    /// memory and the auditor keep of the actions of the engine.
    /// </summary>
    [Fact]
    public void The_actions_the_room_the_challenges_the_audits_and_the_monitor_write_are_the_ones_the_delivery_reads()
    {
        Assert.Equal(DeliveryService.DebateOpenAction, DebateCoordinator.OpenAction);
        Assert.Equal(DeliveryService.DebateOpenAction, ChallengeLog.DebateOpenAction);
        Assert.Equal(DeliveryService.RevisionAcceptanceAction, ChallengeLog.RevisionAcceptanceAction);
        Assert.Equal(DeliveryService.ReplanDismissalAction, RunMonitorViewModel.ReplanDismissAction);
        Assert.Equal(DeliveryService.ReplanDismissalAction, PendingProblems.ReplanDismissalAction);
        Assert.Equal(DeliveryService.ReplanDismissalAction, MemoryDerivation.ReplanDismissalAction);
        Assert.Equal(DeliveryService.RevisionAcceptanceAction, MemoryDerivation.RevisionAcceptanceAction);
        Assert.Equal(DeliveryService.ApprovalAction, MemoryDerivation.DeliveryApprovalAction);
        Assert.Equal(DeliveryService.ApprovalAction, OrchestratorAudit.DeliveryApprovalAction);
        Assert.Equal(WorkspaceCleanup.ApprovalAction, OrchestratorAudit.DiscardApprovalAction);
        Assert.Equal(Orchestrator.PlanApprovalAction, OrchestratorAudit.PlanApprovalAction);
        Assert.Equal(Orchestrator.PlanApprovalAction, MemoryDerivation.PlanApprovalAction);

        // The approvals of the room and of the audits are actions of their own, never one the delivery would read as its own.
        string[] delivery = [DeliveryService.ApprovalAction, DeliveryService.RevisionAcceptanceAction, DeliveryService.ReplanDismissalAction];
        Assert.DoesNotContain(DebateCoordinator.ResumeAction, delivery);
        Assert.DoesNotContain(DebateRun.HumanResolutionAction, delivery);
        Assert.DoesNotContain(ProjectAuditService.ApprovalAction, delivery);
    }
}
