using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using AgentStudio.Application.Coordination;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Arena;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Integration.Phase3;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// The real demonstrations of the six modes of the arena (docs/15 F5-1, decision 2 a): each debate with a participant of
/// each vendor (Claude with haiku and Codex with its default model) and two rounds, judged by a cross-vendor panel in the four
/// modes that choose and by Codex in the two that review. The two review modes judge the run of F3-8 on the repository of the
/// demonstrations, which <see cref="Subject_run_where_Claude_plans_and_executes_and_Codex_reviews"/> makes (Claude produces it,
/// so Codex may judge it). Everything is real: the modules of Phases 3 to 5, the providers composed with the journal and
/// the quota record, SQLite and git in the workspace of the demonstrations (<see cref="DemoWorkspace"/>), which they keep
/// between runs so each can run alone.
/// </summary>
/// <remarks>
/// They spend subscription quota (<see cref="DemoPlan"/> gives each one's runs and estimate), so they need
/// <c>AGENTSTUDIO_PROVIDER_TESTS=1</c>, <c>AGENTSTUDIO_PHASE5_DEMOS=1</c> and <c>AGENTSTUDIO_CAPTURE_DIR</c>; run them one by
/// one with <c>--filter-method</c>. Each writes its capture (<c>fase5\&lt;block&gt;.json</c>) before asserting anything. The
/// opening of each debate is approved as the user approves it in «Abrir debate»; nothing the user alone decides is ever
/// approved (<see cref="DemoPerson"/>), and a debate that waits for a person is aborted with that reason (<see cref="DebateWatcher"/>).
/// </remarks>
[Trait("Category", "ProviderLive")]
public sealed class Phase5DebateDemoLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subject_run_where_Claude_plans_and_executes_and_Codex_reviews()
    {
        DemoSettings.RequireLiveDemo();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        var head = DemoRepository.EnsureCalc(workspace.Repository);
        var profile = DotNetVerificationProfile.Propose();
        await using var app = await DemoApp.OpenAsync(workspace.Repository, workspace, DemoRoster.Debates(), profile, Ct);
        var tools = await DemoFacts.ToolsAsync(app, Ct);
        var notes = new List<string> { $"Repositorio de las demostraciones en {head[..12]}: la solución .NET mínima de F3-8 (Calc.Add y su prueba)." };
        DemoCapture capture;
        try
        {
            capture = await SubjectRunCaptureAsync(app, profile, started, tools, notes);
        }
        catch (Exception ex)
        {
            // The quota may be spent already: whatever stopped the run, it leaves a capture that says so.
            DemoCaptures.WriteFailure(DemoPlan.SubjectRun, SubjectRunTitle, Test(), started, tools, null, notes, ex);
            throw;
        }

        var run = capture.Run!;
        Assert.True(capture.LogIntact, "The decision log does not verify.");
        Assert.Null(run.Stopped);
        Assert.True(run.Phase == nameof(RunPhase.Succeeded), $"{run.Phase}: {run.Summary}");
        Assert.NotNull(run.CandidateCommit);
        Assert.Equal([VendorId.Anthropic.Value], run.ProducerVendors);
        Assert.Contains(capture.Verdicts, v => v.Judge.Vendor == VendorId.OpenAI.Value);
        Assert.True(run.AuditorLogValid, "The auditor of the orchestrator did not audit the run on a log that verifies.");
    }

    private const string SubjectRunTitle = "Ejecución objeto de los modos de revisión";

    /// <summary>
    /// The run of F3-8 on the repository of the demonstrations, approved as the user approves it on the canvas, then the
    /// auditor of the orchestrator as the monitor runs it, and its capture.
    /// </summary>
    private static async Task<DemoCapture> SubjectRunCaptureAsync(DemoApp app, VerificationProfile profile, DateTimeOffset started, IReadOnlyList<DemoTool> tools, List<string> notes)
    {
        var requirements = F38EndToEndLiveTests.Requirements();
        var orchestrator = app.Get<IOrchestrator>();
        var roster = await app.Get<IAgentRoster>().ListAsync(app.Project.ProjectId, Ct);

        // The planner sees only the three agents of F3-8 (as the template defines them), so the producers of the run are
        // Anthropic's alone (ADR-0014) and Codex may judge it in the review modes.
        var proposal = await orchestrator.PlanAsync(
            new PlanningRequest(app.Project, F38EndToEndLiveTests.UserRequest, requirements, DemoRoster.Planning(roster), DemoRoster.Orchestrator, profile, new PlanLimits(MaxTasks: 6, MaxRetriesPerTask: 1)),
            null,
            Ct);
        var fromOrchestrator = proposal.Status == AgentRunStatus.Succeeded && proposal.Plan is { } proposed && proposal.Violations.Count == 0 && F38EndToEndLiveTests.HasExpectedShape(proposed);
        var plan = fromOrchestrator ? proposal.Plan! : F38EndToEndLiveTests.UserPlan();
        notes.Add(fromOrchestrator
            ? "El plan es el del orquestador, sin cambios."
            : $"El plan del orquestador no sirvió ({proposal.Status}, {proposal.Violations.Count} violaciones{(proposal.Error is null ? string.Empty : ", " + proposal.Error)}): se usa el del lienzo de F3-8 (el usuario lo completa).");
        var confined = await app.Get<IWriteConfinementProbe>().IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct);
        var approval = new ApprovalDecision(true, DemoPerson.Name, DateTimeOffset.UtcNow, $"Plan {plan.ContentHash[..12]} aprobado en el lienzo para la demostración.");

        RunResult result;
        string? stopped;
        RunId runId;
        await using (var run = await orchestrator.StartAsync(
            new RunRequest(
                app.Project,
                fromOrchestrator ? proposal.Run : null,
                F38EndToEndLiveTests.UserRequest,
                requirements,
                plan,
                approval,
                profile,
                new RunOptions(MaxAgentRuns: 4, DefaultTaskTimeout: TimeSpan.FromMinutes(15)),
                ValidatedRoster: roster,
                ApprovedWarning: VerificationStepText.PlanWarning(plan, roster, profile, confined) ?? string.Empty),
            Ct))
        {
            runId = run.Id;
            stopped = await WaitOrStopAsync(run, TimeSpan.FromMinutes(40));
            result = await run.Completion.WaitAsync(TimeSpan.FromMinutes(2), Ct);
        }

        // The auditor of the orchestrator, as the monitor runs it when a run ends: deterministic and free (D-34).
        OrchestratorAuditReport? audit = null;
        try
        {
            audit = await app.Get<IOrchestratorAuditService>().AuditRunAsync(app.Project, runId, Ct);
        }
        catch (InvalidOperationException ex)
        {
            notes.Add("El auditor del orquestador no pudo auditar la ejecución: " + ex.Message);
        }

        var log = await app.LoadLogAsync(Ct);
        var entriesOfRun = RunLog.EntriesOf(log.Entries, runId.Value);
        var units = new HashSet<string>(StringComparer.Ordinal) { runId.Value, proposal.Run.Value };
        var (entries, alerts) = DemoFacts.Log(log.Entries, units);
        var producers = RunLog.ProducerVendors(entriesOfRun).Select(v => v.Value).Order(StringComparer.Ordinal).ToList();
        var candidate = RunLog.CandidateCommit(entriesOfRun);
        notes.Add($"Vendors productores según el registro: {string.Join(", ", producers)}; commit candidato {candidate ?? "ninguno"}.");
        var facts = new DemoRunFacts
        {
            Run = runId.Value,
            PlanningRun = proposal.Run.Value,
            Request = F38EndToEndLiveTests.UserRequest,
            Requirements = DemoFacts.Requirements(requirements),
            RequirementSetHash = requirements.ContentHash,
            PlanFromOrchestrator = fromOrchestrator,
            PlanCorrected = proposal.Corrected,
            PlanViolations = [.. proposal.Violations.Select(v => $"{v.Code}: {v.Message}")],
            Tasks = [.. plan.Tasks.Select(t => new DemoTask(t.Id.Value, t.Kind.ToString(), t.Agent?.Value, [.. t.DependsOn.Select(d => d.Prerequisite.Value)]))],
            Phase = result.Phase.ToString(),
            Summary = result.Summary,
            BaseCommit = RunLog.RunStarted(entriesOfRun)?.BaseCommit,
            IntegrationCommit = result.IntegrationCommit,
            CandidateCommit = candidate,
            ProducerVendors = producers,
            VerdictId = result.VerdictId,
            AuditorLogValid = audit?.Log.IsValid ?? false,
            AuditorFindings = [.. (audit?.Findings ?? []).Select(f => new DemoAuditorFinding(f.Code, f.Severity.ToString(), f.Message))],
            ReplanRequested = audit?.ReplanRequested ?? false,
            Stopped = stopped,
        };
        var capture = new DemoCapture
        {
            Block = DemoPlan.SubjectRun,
            Title = SubjectRunTitle,
            Test = Test(nameof(Subject_run_where_Claude_plans_and_executes_and_Codex_reviews)),
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
            Result = $"{result.Phase}: {result.Summary}{(stopped is null ? string.Empty : " " + stopped)}",
            Completed = result.Phase == RunPhase.Succeeded && candidate is not null,
            Tools = tools,
            Notes = notes,
            Run = facts,
            Verdicts = await DemoFacts.VerdictsAsync(app, log.Entries, runId.Value, null, Ct),
            Attempts = DemoFacts.Attempts(app.Project.ProjectId, log.Entries, units),
            Quota = await DemoFacts.QuotaAsync(app, Ct),
            Journal = DemoFacts.JournalSince(app.Project.RootPath, started),
            Log = entries,
            LogIntact = log.Verification.IsValid,
            HeadHash = log.HeadHash,
            Alerts = alerts,
            Approvals = DemoFacts.Approvals(app.Person),
        };
        DemoCaptures.Write(DemoSettings.CaptureDirectory!, capture);
        return capture;
    }

    [Fact]
    public Task Free_debate_judged_by_a_cross_vendor_panel() => ChoiceAsync(DebateDemo.Free);

    [Fact]
    public Task Adversarial_debate_judged_by_a_cross_vendor_panel() => ChoiceAsync(DebateDemo.Adversarial);

    [Fact]
    public Task Architecture_review_judged_by_a_cross_vendor_panel() => ChoiceAsync(DebateDemo.Architecture);

    [Fact]
    public Task Tribunal_judged_by_a_cross_vendor_panel() => ChoiceAsync(DebateDemo.Tribunal);

    [Fact]
    public Task Implementation_audit_of_the_subject_run_judged_by_Codex() => ReviewAsync(DebateDemo.Implementation);

    [Fact]
    public Task Orchestrator_review_of_the_subject_run_judged_by_Codex() => ReviewAsync(DebateDemo.Orchestrator);

    /// <summary>A mode that chooses, about the commit HEAD of the repository of the demonstrations, with a cross-vendor panel.</summary>
    private static async Task ChoiceAsync(DebateDemo demo, [CallerMemberName] string method = "")
    {
        DemoSettings.RequireLiveDemo();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        var head = DemoRepository.EnsureCalc(workspace.Repository);
        await using var app = await DemoApp.OpenAsync(workspace.Repository, workspace, DemoRoster.Debates(), DotNetVerificationProfile.Propose(), Ct);
        var tools = await DemoFacts.ToolsAsync(app, Ct);
        var request = Request(app, demo, demo.Requirements!(DateTimeOffset.UtcNow), new DebateSubject(DebateSubjectKind.Commit, null));
        var coordinator = app.Get<IDebateCoordinator>();

        // What «Abrir debate» shows: nothing is spent until it is approved, and a problem stops it here.
        var proposal = await coordinator.PrepareAsync(request, Ct);
        Assert.True(proposal.Problems.Count == 0, "The debate cannot open: " + string.Join(" ", proposal.Problems));
        Assert.Equal(DebateJudgingKind.CrossVendorPanel, proposal.Judging.Kind);
        Assert.Equal([VendorId.Anthropic.Value, VendorId.OpenAI.Value], proposal.Judging.Judges.Select(j => j.Model.Vendor.Value).Order(StringComparer.Ordinal));
        Assert.True(proposal.Judging.WaivedParticipantVendors.SetEquals([VendorId.Anthropic, VendorId.OpenAI]));

        var outcome = await DebateDemoRunner.RunAsync(
            app,
            new DemoStart(demo.Block, demo.Title, Test(method), started, tools),
            proposal,
            () => coordinator.OpenAsync(proposal, Ct),
            runTests: false,
            challengedUnit: null,
            challenge: null,
            [$"Objeto: el commit HEAD {head[..12]} del repositorio de las demostraciones."],
            Ct);
        DebateDemoRunner.AssertDemonstrated(outcome, demo.Participants.Count, expectWaiver: true);
        Assert.Equal(2, outcome.Capture.Verdicts.Select(v => v.Judge.Agent).Distinct().Count());
    }

    /// <summary>A mode that reviews the run of F3-8, judged by Codex, which did not produce it (rule P and a waiver of Q for OpenAI).</summary>
    private static async Task ReviewAsync(DebateDemo demo, [CallerMemberName] string method = "")
    {
        DemoSettings.RequireLiveDemo();
        var started = DateTimeOffset.UtcNow;
        var workspace = DemoWorkspace.Resolve();
        DemoRepository.EnsureCalc(workspace.Repository);
        await using var app = await DemoApp.OpenAsync(workspace.Repository, workspace, DemoRoster.Debates(), DotNetVerificationProfile.Propose(), Ct);
        var subject = await SubjectRunAsync(app);
        Assert.SkipWhen(subject is null, $"There is no finished run with a candidate commit in the workspace of the demonstrations: run {nameof(Subject_run_where_Claude_plans_and_executes_and_Codex_reviews)} first.");
        var tools = await DemoFacts.ToolsAsync(app, Ct);
        var notes = new List<string> { $"Objeto: la ejecución {subject!.Run} (commit candidato {subject.Candidate[..12]}), que produjo Anthropic." };

        FrozenRequirementSet requirements;
        if (demo.Mode == DebateMode.OrchestratorReview)
        {
            // The auditor's report is part of what the reviewers read (D-36): it runs again, idempotent and free.
            var audit = await app.Get<IOrchestratorAuditService>().AuditRunAsync(app.Project, new RunId(subject.Run), Ct);
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"El auditor del orquestador revisó la ejecución: {audit.Findings.Count} hallazgos, {audit.Recorded.Count} alertas nuevas, replanificación {(audit.ReplanRequested ? "pedida" : "no pedida")}."));
            requirements = DebateModes.OrchestratorReview(DateTimeOffset.UtcNow);
        }
        else
        {
            requirements = await app.Get<IRequirementStore>().FindAsync(app.Project.ProjectId, subject.RequirementSetHash, Ct)
                ?? throw new InvalidOperationException($"The frozen requirements {subject.RequirementSetHash} of the run are not stored.");
        }

        var request = Request(app, demo, requirements, new DebateSubject(DebateSubjectKind.Run, subject.Run));
        var coordinator = app.Get<IDebateCoordinator>();
        var proposal = await coordinator.PrepareAsync(request, Ct);
        Assert.True(proposal.Problems.Count == 0, "The debate cannot open: " + string.Join(" ", proposal.Problems));
        Assert.Equal(DebateJudgingKind.NonProducerJudge, proposal.Judging.Kind);
        Assert.Equal(DemoRoster.Judge, Assert.Single(proposal.Judging.Judges).Agent);
        Assert.True(proposal.Judging.ProducerVendors.SetEquals([VendorId.Anthropic]));
        Assert.True(proposal.Judging.WaivedParticipantVendors.SetEquals([VendorId.OpenAI]));

        var outcome = await DebateDemoRunner.RunAsync(
            app,
            new DemoStart(demo.Block, demo.Title, Test(method), started, tools),
            proposal,
            () => coordinator.OpenAsync(proposal, Ct),
            runTests: demo.Mode == DebateMode.ImplementationAudit,
            challengedUnit: null,
            challenge: null,
            notes,
            Ct);
        DebateDemoRunner.AssertDemonstrated(outcome, demo.Participants.Count, expectWaiver: true);
        Assert.All(outcome.Capture.Verdicts, v => Assert.Equal(VendorId.OpenAI.Value, v.Judge.Vendor));
    }

    private static DebateRequest Request(DemoApp app, DebateDemo demo, FrozenRequirementSet requirements, DebateSubject subject) =>
        new(app.Project, demo.Mode, demo.QuestionFor(subject.Kind == DebateSubjectKind.Run ? subject.Reference : null), requirements, subject, demo.Participants, demo.Judges, demo.Judging, DebateDemo.Limits);

    /// <summary>The run the review modes judge: the latest of the project with a candidate commit, produced by Anthropic alone.</summary>
    internal static async Task<SubjectRun?> SubjectRunAsync(DemoApp app)
    {
        var log = await app.LoadLogAsync(Ct);
        foreach (var record in (await app.Get<IRunRegistry>().ListAsync(app.Project.ProjectId, Ct)).OrderByDescending(r => r.CreatedAt))
        {
            var entries = RunLog.EntriesOf(log.Entries, record.Id.Value);
            if (RunLog.CandidateCommit(entries) is { } candidate && RunLog.ProducerVendors(entries).SetEquals([VendorId.Anthropic]))
            {
                return new SubjectRun(record.Id.Value, candidate, RunLog.RunStarted(entries)?.RequirementSetHash ?? record.RequirementSetHash);
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for the run to end, like the end to end of F3-8: a run that rests in a phase only a person ends, or outlives
    /// <paramref name="limit"/>, is cancelled so the evidence is written; the reason is returned (null when it ended by itself).
    /// </summary>
    private static async Task<string?> WaitOrStopAsync(IOrchestrationRun run, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        var resting = 0;
        while (!run.Completion.IsCompleted)
        {
            await Task.WhenAny(run.Completion, Task.Delay(TimeSpan.FromSeconds(5), Ct));
            if (run.Completion.IsCompleted)
            {
                break;
            }

            var phase = run.Snapshot().Phase;
            resting = phase is RunPhase.Blocked or RunPhase.AwaitingHuman or RunPhase.WaitingForQuota or RunPhase.Paused or RunPhase.Interrupted ? resting + 1 : 0;
            var reason = resting >= 3
                ? $"La ejecución quedó en {phase} y la demostración la canceló para cerrar."
                : watch.Elapsed > limit
                    ? string.Create(CultureInfo.InvariantCulture, $"La ejecución superó {limit.TotalMinutes:0} minutos y la demostración la canceló.")
                    : null;
            if (reason is not null)
            {
                await run.CancelAsync("Demostración: " + reason);
                return reason;
            }
        }

        return null;
    }

    private static string Test([CallerMemberName] string method = "") => nameof(Phase5DebateDemoLiveTests) + "." + method;
}

/// <summary>The run the review modes and the challenge are about: its id, its candidate commit and the hash of its frozen requirements.</summary>
internal sealed record SubjectRun(string Run, string Candidate, string RequirementSetHash);
