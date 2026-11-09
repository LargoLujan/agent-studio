using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Verification;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Providers.Live;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.Integration.Phase3;

/// <summary>
/// The end to end of F3-8 with the real CLIs (docs/13 section 6, decision 7 as amended): Claude Code orchestrates (Haiku,
/// Read, Grep and Glob only) and executes (Haiku, <c>dontAsk</c> with Read and Edit rules for its worktree only, no shell
/// and no network), and Codex judges in its
/// read-only sandbox (<c>-s read-only</c>). The repository is the minimal .NET solution of <see cref="Phase3Repos.DotNet"/>
/// and the requirement is RQ-01, "Añadir Calc.Multiply(a,b)", checked by an automated test. The plan is approved the way
/// the user approves it on the canvas, and then the system runs it: executor, verification, integration, independent
/// review and validated verdict, with every module real.
/// </summary>
/// <remarks>
/// It spends subscription quota (3 agent runs normally, 7 at most), so it needs both AGENTSTUDIO_PROVIDER_TESTS=1 and
/// AGENTSTUDIO_E2E=1; run it alone with <c>--filter-class</c>. With AGENTSTUDIO_CAPTURE_DIR set it writes there
/// <c>fase3-e2e.md</c>, the evidence of the run in Spanish with personal paths replaced, and the notes in evidence.log.
/// </remarks>
[Trait("Category", "ProviderLive")]
public sealed class F38EndToEndLiveTests
{
    public const string E2EVariable = "AGENTSTUDIO_E2E";

    public const string ReportFile = "fase3-e2e.md";

    /// <summary>
    /// What the user types. The structure of the plan is the one of docs/13 section 6 (step 6); asking for it is the
    /// user's choice, and the validator stays the authority on the plan the orchestrator returns.
    /// </summary>
    public const string UserRequest =
        "Añade el método Multiply(a, b) a la clase Calc de src/Calc/Calc.cs, con una prueba xUnit en tests/Calc.Tests/ que compruebe que Multiply(2, 3) devuelve 6. "
        + "El plan tiene tres tareas: una AgentWork del ejecutor que cubra RQ-01, con alcance de escritura src/Calc/ y tests/Calc.Tests/ y verificación build y test; "
        + "después una Integration de esa tarea con verificación build y test; y al final una Review del juez sobre la integración.";

    private static readonly AgentId Orchestrator = new("orquestador");
    private static readonly AgentId Executor = new("ejecutor");
    private static readonly AgentId Judge = new("juez");
    private static readonly RequirementId Rq01 = new("RQ-01");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IReadOnlyList<AgentDefinition> Roster() =>
    [
        Agent(Orchestrator, "Orquestador", AgentRole.Orchestrator, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku", ["Read", "Grep", "Glob"], new ExecutionBudget(12, TimeSpan.FromMinutes(6)), string.Empty),
        Agent(Executor, "Ejecutor", AgentRole.Executor, ProviderId.ClaudeCode, VendorId.Anthropic, "haiku", ["Read", "Edit", "Write", "Grep", "Glob"], new ExecutionBudget(30, TimeSpan.FromMinutes(10)),
            "Eres un desarrollador .NET cuidadoso: haces el cambio mínimo que pide la tarea, con su prueba xUnit, y no tocas nada fuera de tu alcance."),
        Agent(Judge, "Juez", AgentRole.Judge, ProviderId.CodexCli, VendorId.OpenAI, null, [], new ExecutionBudget(20, TimeSpan.FromMinutes(12)), string.Empty),
    ];

    public static FrozenRequirementSet Requirements() => FrozenRequirementSet.Freeze(
        [
            new Requirement(
                Rq01,
                "Añadir Calc.Multiply(a,b)",
                RequirementPriority.Mandatory,
                [new AcceptanceCriterion("c1", "Calc.Multiply(2, 3) devuelve 6 y lo comprueba una prueba xUnit", VerificationMethod.AutomatedTest)]),
        ],
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task Claude_plans_and_executes_and_Codex_judges_read_only_with_a_validated_verdict()
    {
        LiveTestSupport.RequireOptIn();
        Assert.SkipUnless(Environment.GetEnvironmentVariable(E2EVariable) == "1", $"Set {E2EVariable}=1 as well to run the end to end of F3-8 (3 to 7 agent runs).");
        var claude = new ExecutableLocator().Find("claude");
        Assert.SkipUnless(claude is not null && IsNative(claude), "The end to end needs the native claude executable (the inline --json-schema cannot go through a .cmd shim).");
        Assert.SkipUnless(new ExecutableLocator().Find("codex") is not null, "Codex CLI is not installed.");

        using var repo = Phase3Repos.DotNet();

        // The real CLIs work in the worktrees: a short ASCII folder keeps their paths far from MAX_PATH.
        using var appData = new ScratchFolder("as-e2e-");
        var paths = new Phase3Paths(Path.Combine(appData.Path, "AgentStudio"));

        // Nothing in this flow should need a person once the plan is approved; if something asks, the answer is no.
        var person = new ScriptedApprovalPrompt { Approve = _ => false };
        var roster = Roster();
        await using var app = await Phase3App.OpenAsync(repo.Root, paths, person, providers: null, roster);
        var diagnostics = await app.Services.GetRequiredService<ProviderDiagnostics>().RunAsync(Ct);
        var before = await app.Worktrees.CaptureUserRepositoryStateAsync(repo.Root, Ct);
        var requirements = Requirements();
        var profile = DotNetVerificationProfile.Propose();
        var total = Stopwatch.StartNew();

        // Steps 2 to 4: the orchestrator reads the repository in _base and answers with a plan; the validator decides.
        var planning = Stopwatch.StartNew();
        var proposal = await app.Orchestrator.PlanAsync(
            new PlanningRequest(app.Project, UserRequest, requirements, roster, Orchestrator, profile, new PlanLimits(MaxTasks: 6, MaxRetriesPerTask: 1)),
            null,
            Ct);
        planning.Stop();
        LiveTestSupport.Note($"[e2e] plan: {proposal.Status}, corregido {proposal.Corrected}, violaciones {proposal.Violations.Count}, {planning.Elapsed.TotalSeconds:0.0} s, error {proposal.Error ?? "-"}");
        Assert.True(proposal.Status == AgentRunStatus.Succeeded, $"The orchestrator could not plan: {proposal.Status} {proposal.Error}");

        // Step 5: the user reviews the plan and approves it, as the canvas does. A plan the validator still rejects, or
        // one without the integration and the review the user asked for, is completed on the canvas instead (it becomes
        // the user's plan), so the one run still goes the whole way; the report says so.
        var fromOrchestrator = proposal.Plan is { } proposed && proposal.Violations.Count == 0 && HasExpectedShape(proposed);
        var plan = fromOrchestrator ? proposal.Plan! : UserPlan();
        LiveTestSupport.Note($"[e2e] plan {(fromOrchestrator ? "del orquestador" : "completado por el usuario en el lienzo")}: {plan.ContentHash}");
        var approval = new ApprovalDecision(true, "user", DateTimeOffset.UtcNow, $"Plan {plan.ContentHash[..12]} aprobado en el lienzo.", Verification: ApprovalVerification.WindowsHello);

        // Steps 6 to 12: the system runs the plan.
        var execution = Stopwatch.StartNew();
        await using var run = await app.Orchestrator.StartAsync(
            new RunRequest(app.Project, fromOrchestrator ? proposal.Run : null, UserRequest, requirements, plan, approval, profile, new RunOptions(MaxAgentRuns: 4, DefaultTaskTimeout: TimeSpan.FromMinutes(15)), ValidatedRoster: await app.RosterAsync(), ApprovedWarning: await app.ApprovedWarningAsync(plan, profile)),
            Ct);
        var stopped = await WaitOrStopAsync(run, TimeSpan.FromMinutes(40));
        var result = await run.Completion.WaitAsync(TimeSpan.FromMinutes(2), Ct);
        execution.Stop();
        total.Stop();
        if (stopped is not null)
        {
            LiveTestSupport.Note("[e2e] " + stopped);
        }

        // The evidence first, whatever happened, so it survives a failing assertion below.
        var facts = await F38Facts.CollectAsync(app, proposal, run.Id, plan, fromOrchestrator, result, diagnostics, planning.Elapsed, execution.Elapsed, total.Elapsed, stopped);
        var report = F38Report.Write(facts);
        foreach (var line in facts.Notes)
        {
            LiveTestSupport.Note("[e2e] " + line);
        }

        var directory = Environment.GetEnvironmentVariable(LiveTestSupport.CaptureVariable);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, ReportFile), report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), Ct);
        }

        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), report, StringComparison.OrdinalIgnoreCase);
        Assert.Null(stopped);

        // The run went the whole way, and the verdict is valid, approves, and is the judge's own.
        Assert.True(result.Phase == RunPhase.Succeeded, Phase3Checks.Describe(result) + "\n" + await Phase3Checks.DescribeAsync(app, run.Id));
        var verdictId = Assert.IsType<string>(result.VerdictId);
        var verdict = Assert.Single(facts.Verdicts, v => v.Id == verdictId);
        Assert.True(verdict.Valid, string.Join("; ", verdict.Issues.Select(i => $"{i.Code}: {i.Message}")));
        Assert.Equal(VerdictDecision.Approved, verdict.Verdict.Decision);
        Assert.Equal(Judge, verdict.Verdict.Judge.Agent);
        Assert.Equal((ProviderId.CodexCli, VendorId.OpenAI), (verdict.Verdict.Judge.Model.Provider, verdict.Verdict.Judge.Model.Vendor));
        var (_, issued) = (await app.PayloadsAsync<VerdictIssuedPayload>(run.Id)).Single(p => p.Payload.VerdictId == verdictId);
        Assert.True(issued.Valid);
        Assert.Equal(requirements.ContentHash, issued.RequirementSetHash);
        Assert.Contains("anthropic", issued.VendorNote, StringComparison.Ordinal);

        // The work is in the integration: Multiply exists, and the system's tests (read from TRX) passed.
        var integration = Assert.IsType<string>(result.IntegrationCommit);
        Assert.Contains("Multiply", TempGitRepo.Git(repo.Root, "grep", "-n", "Multiply", integration, "--", "src/Calc/"), StringComparison.Ordinal);
        Assert.Contains(facts.TestRuns, t => t.Summary is { Failed: 0, Passed: > 1 });
        Assert.All(facts.TestRuns.Where(t => t.Task == facts.IntegrationTask), t => Assert.True(t.Summary is { Succeeded: true }, t.Title));

        // Every build and test ran isolated (ADR-0017), on a copy of its commit taken from the project's repository.
        Assert.NotEmpty(facts.Isolation);
        Assert.All(facts.Isolation, s => Assert.StartsWith("low integrity", s.Isolation, StringComparison.Ordinal));
        Assert.All(facts.Isolation, s => Assert.Contains("from the objects of the repository", s.Source ?? string.Empty, StringComparison.Ordinal));

        // The judge worked read-only: its worktree is exactly the integration, untouched.
        Assert.Equal(string.Empty, TempGitRepo.Git(facts.ReviewWorktree, "status", "--porcelain").Trim());
        Assert.Equal(integration, TempGitRepo.Git(facts.ReviewWorktree, "rev-parse", "HEAD").Trim());

        // Quota stayed within the plan of docs/13 (3 runs normally, 7 at most), and nobody had to be asked.
        Assert.InRange(facts.AgentRuns.Count, 3, 7);
        Assert.Empty(person.Requests);

        // The user's repository is as it was (but the journal of ADR-0008), and the log verifies.
        var after = await app.Worktrees.CaptureUserRepositoryStateAsync(repo.Root, Ct);
        Assert.Empty(UserRepositoryState.Compare(Phase3Checks.WithoutAgentStudio(before), Phase3Checks.WithoutAgentStudio(after)));
        await Phase3Checks.AssertEvidenceIntactAsync(app, run.Id, everyOneRegistered: false);
        Assert.True(facts.LogIntact, "The decision log does not verify.");
    }

    /// <summary>The plan of docs/13 section 6: the executor writes RQ-01, an integration merges it, and the judge reviews after it.</summary>
    internal static bool HasExpectedShape(PlanDefinition plan)
    {
        var byId = plan.Tasks.ToDictionary(t => t.Id);
        bool After(TaskSpec task, Func<TaskSpec, bool> ancestor)
        {
            var stack = new Stack<TaskId>(task.DependsOn.Select(d => d.Prerequisite));
            var seen = new HashSet<TaskId>();
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current) || !byId.TryGetValue(current, out var spec))
                {
                    continue;
                }

                if (ancestor(spec))
                {
                    return true;
                }

                foreach (var next in spec.DependsOn)
                {
                    stack.Push(next.Prerequisite);
                }
            }

            return false;
        }

        static bool IsWork(TaskSpec t) => t is { Kind: WorkTaskKind.AgentWork, Workspace: WorkspaceMode.Write } && t.Agent == Executor && t.Covers.Contains(Rq01);
        return plan.Tasks.Any(IsWork)
            && plan.Tasks.Any(t => t.Kind == WorkTaskKind.Integration && After(t, IsWork))
            && plan.Tasks.Any(t => t.Kind == WorkTaskKind.Review && t.Agent == Judge && After(t, a => a.Kind == WorkTaskKind.Integration));
    }

    /// <summary>The same plan written by the user on the canvas, for when the orchestrator's plan lacks part of it.</summary>
    internal static PlanDefinition UserPlan() => PlanDefinition.Create(
        "Añadir Calc.Multiply(a,b) con su prueba, integrarlo y revisarlo",
        [
            new TaskSpec(new TaskId("T1"), "Implementar Calc.Multiply", WorkTaskKind.AgentWork, Executor,
                "Añadir Calc.Multiply(a,b) a src/Calc/Calc.cs con una prueba xUnit en tests/Calc.Tests/ que compruebe que Multiply(2, 3) devuelve 6.",
                "Cambio mínimo; no toques nada fuera del alcance.", [], JoinPolicy.AllCompleted, [Rq01], ["src/Calc/", "tests/Calc.Tests/"], ["build", "test"], null, null, 1, null, WorkspaceMode.Write),
            new TaskSpec(new TaskId("T2"), "Integrar", WorkTaskKind.Integration, null, "Integrar el trabajo de T1.", string.Empty,
                [new DependencySpec(new TaskId("T1"))], JoinPolicy.AllCompleted, [], [], ["build", "test"], null, null, 0, null, WorkspaceMode.None),
            new TaskSpec(new TaskId("T3"), "Revisión independiente", WorkTaskKind.Review, Judge, "Revisar la integración contra RQ-01.", string.Empty,
                [new DependencySpec(new TaskId("T2"))], JoinPolicy.AllCompleted, [], [], [], null, null, 0, null, WorkspaceMode.ReadOnly),
        ],
        ["El juez puede pedir cambios: en ese caso se propone una ejecución de seguimiento."],
        PlanSource.User,
        null);

    /// <summary>
    /// Waits for the run to end. A run that rests in a phase only a person ends (blocked, waiting for a human or for
    /// quota, paused), or that outlives <paramref name="limit"/>, is cancelled, so the evidence is always written; the
    /// reason is returned (null when the run ended by itself).
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
                ? $"La ejecución quedó en {phase} y la prueba la canceló para cerrar."
                : watch.Elapsed > limit
                    ? string.Create(CultureInfo.InvariantCulture, $"La ejecución superó {limit.TotalMinutes:0} minutos y la prueba la canceló.")
                    : null;
            if (reason is not null)
            {
                await run.CancelAsync("E2E: " + reason);
                return reason;
            }
        }

        return null;
    }

    private static AgentDefinition Agent(AgentId id, string name, AgentRole role, ProviderId provider, VendorId vendor, string? model, IReadOnlyList<string> tools, ExecutionBudget budget, string instructions) =>
        new(id, name, role, new ModelBinding(provider, vendor, model), instructions, [], tools, [], budget, 3, MemoryPolicy.None, new ReviewPolicy(true, true));

    private static bool IsNative(string executable)
    {
        var name = executable.TrimEnd('.', ' ');
        return !name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One agent launch as the log tells it: who, through which CLI, how it ended and what the CLI reported it cost.</summary>
internal sealed record F38AgentRun(string Agent, string Provider, string? Model, string Task, int Attempt, string Outcome, TimeSpan Duration, UsageSnapshot? Usage);

/// <summary>A test run the system captured, with the task it belongs to.</summary>
internal sealed record F38TestRun(string Title, string? Task, TestRunSummary? Summary);

/// <summary>How a build or test step the system ran was isolated, as the header of its evidence says (ADR-0017).</summary>
/// <param name="Isolation">The <c># isolation:</c> line, without its prefix.</param>
/// <param name="Source">The <c># source:</c> line (where the copy of the commit came from), or null when the step had none.</param>
internal sealed record F38StepIsolation(string Title, string? Task, string? SubjectCommit, string Isolation, string? Source);

/// <summary>Everything the evidence of the end to end reports, read back from the stores and git after the run.</summary>
internal sealed record F38Facts(
    RunId Run,
    RunId PlanningRun,
    bool PlanFromOrchestrator,
    PlanProposal Proposal,
    PlanDefinition Plan,
    RunResult Result,
    IReadOnlyList<ProviderProbeResult> Diagnostics,
    TimeSpan Planning,
    TimeSpan Execution,
    TimeSpan Total,
    IReadOnlyList<DecisionEntry> Log,
    bool LogIntact,
    string HeadHash,
    RunStartedPayload? Started,
    IReadOnlyList<F38AgentRun> AgentRuns,
    IReadOnlyList<StoredVerdict> Verdicts,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyList<F38TestRun> TestRuns,
    IReadOnlyList<F38StepIsolation> Isolation,
    string? IntegrationTask,
    string ReviewWorktree,
    string ExecutorDiff,
    string Requirements,
    string? Stopped,
    IReadOnlyList<string> Notes)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<F38Facts> CollectAsync(
        Phase3App app,
        PlanProposal proposal,
        RunId run,
        PlanDefinition plan,
        bool fromOrchestrator,
        RunResult result,
        IReadOnlyList<ProviderProbeResult> diagnostics,
        TimeSpan planning,
        TimeSpan execution,
        TimeSpan total,
        string? stopped)
    {
        var state = await app.LoadLogAsync();
        var runs = new[] { proposal.Run.Value, run.Value }.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var entries = state.Entries
            .Select(e => (Entry: e, Payload: DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)))
            .Where(x => x.Payload is not null && runs.Contains(x.Payload.RunId))
            .ToList();

        // Each AgentRunFinished closes the latest AgentRunStarted of the same task and attempt that is still open.
        var agentRuns = new List<F38AgentRun>();
        var open = new List<(DecisionEntry Entry, AgentRunStartedPayload Payload)>();
        foreach (var (entry, payload) in entries)
        {
            switch (payload)
            {
                case AgentRunStartedPayload started:
                    open.Add((entry, started));
                    break;
                case AgentRunFinishedPayload finished:
                    var index = open.FindLastIndex(o => o.Payload.Task == finished.Task && o.Payload.Attempt == finished.Attempt);
                    if (index >= 0)
                    {
                        var (startEntry, start) = open[index];
                        open.RemoveAt(index);
                        agentRuns.Add(new F38AgentRun(start.Agent.Value, start.Provider.Value, start.Model, start.Task?.Value ?? (start.Agent == new AgentId("juez") ? "revisión" : "plan"), start.Attempt,
                            finished.Status.ToString(), entry.RecordedAt - startEntry.RecordedAt, finished.Usage));
                    }

                    break;
            }
        }

        var evidence = new List<EvidenceRecord>();
        foreach (var id in runs)
        {
            evidence.AddRange(await app.Evidence.ListByRunAsync(app.Project.ProjectId, id, Ct));
        }

        // The task of every test run, from the transitions and EvidenceRegistered entries that cite it.
        var taskOf = new Dictionary<EvidenceId, string>();
        foreach (var (_, payload) in entries)
        {
            if (payload is EvidenceRegisteredPayload { Task: { } task } registered)
            {
                taskOf.TryAdd(registered.Evidence, task.Value);
            }
        }

        // How every build and test was isolated, from the header the runner wrote into its evidence.
        var isolation = new List<F38StepIsolation>();
        foreach (var record in evidence.Where(e => e.Kind is EvidenceKind.BuildLog or EvidenceKind.TestRun && e.Origin == EvidenceOrigin.CapturedBySystem))
        {
            var lines = (await ReadAsync(app, record.Id)).Split('\n');
            string? Header(string prefix) => lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();
            isolation.Add(new F38StepIsolation(record.Title, taskOf.GetValueOrDefault(record.Id), record.SubjectCommit, Header("# isolation:") ?? "(sin cabecera)", Header("# source:")));
        }

        var integrationTask = plan.Tasks.FirstOrDefault(t => t.Kind == WorkTaskKind.Integration)?.Id.Value;
        var reviewTask = plan.Tasks.FirstOrDefault(t => t.Kind == WorkTaskKind.Review)?.Id;
        var reviewWorktree = entries.Select(e => e.Payload).OfType<WorktreeCreatedPayload>().FirstOrDefault(w => w.Task == reviewTask)?.Path ?? string.Empty;
        var workTask = plan.Tasks.FirstOrDefault(t => t.Kind == WorkTaskKind.AgentWork)?.Id.Value;
        var diff = evidence.LastOrDefault(e => e.Kind == EvidenceKind.CodeDiff && taskOf.GetValueOrDefault(e.Id) == workTask);
        var diffText = diff is null ? string.Empty : await ReadAsync(app, diff.Id);

        var notes = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"fase {result.Phase}; planificación {planning.TotalSeconds:0.0} s; ejecución {execution.TotalSeconds:0.0} s; total {total.TotalSeconds:0.0} s; ejecuciones de agente {agentRuns.Count}"),
        };
        notes.AddRange(agentRuns.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"{r.Agent} ({r.Provider}/{r.Model ?? "por defecto"}) {r.Task}#{r.Attempt}: {r.Outcome}, {r.Duration.TotalSeconds:0.0} s, coste {F38Report.Metric(r.Usage?.CostUsd)} USD, tokens entrada {F38Report.Metric(r.Usage?.InputTokens)} / salida {F38Report.Metric(r.Usage?.OutputTokens)} / caché {F38Report.Metric(r.Usage?.CachedInputTokens)}")));

        return new F38Facts(
            run,
            proposal.Run,
            fromOrchestrator,
            proposal,
            plan,
            result,
            diagnostics,
            planning,
            execution,
            total,
            [.. entries.Select(e => e.Entry)],
            state.Verification.IsValid,
            state.HeadHash,
            entries.Select(e => e.Payload).OfType<RunStartedPayload>().FirstOrDefault(p => p.RunId == run.Value),
            agentRuns,
            await app.Verdicts.ListAsync(app.Project.ProjectId, run.Value, Ct),
            evidence,
            [.. evidence.Where(e => e.Kind == EvidenceKind.TestRun).Select(e => new F38TestRun(e.Title, taskOf.GetValueOrDefault(e.Id), e.TestRun))],
            isolation,
            integrationTask,
            reviewWorktree,
            diffText,
            string.Join("\n", F38EndToEndLiveTests.Requirements().Requirements.Select(r => $"{r.Id}: {r.Statement}")),
            stopped,
            notes);
    }

    private static async Task<string> ReadAsync(Phase3App app, EvidenceId id)
    {
        await using var stream = await app.Evidence.OpenContentAsync(app.Project.ProjectId, id, Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }
}

/// <summary>The evidence of the end to end in Spanish, as <c>docs/evidencias/fase3-e2e.md</c> keeps it, with personal paths replaced.</summary>
internal static class F38Report
{
    public static string Write(F38Facts facts)
    {
        var md = new StringBuilder();
        var verdict = facts.Verdicts.LastOrDefault();
        md.AppendLine("# Fase 3 · E2E F3-8 con CLIs reales");
        md.AppendLine();
        md.AppendLine(Invariant($"> Generado por `F38EndToEndLiveTests` el {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC, sobre un repositorio temporal de prueba. Las rutas personales se sustituyen por `C:\\Users\\usuario`."));
        md.AppendLine();
        md.AppendLine("## Resultado");
        md.AppendLine();
        md.AppendLine(Invariant($"- Fase final de la ejecución `{facts.Run}`: **{facts.Result.Phase}**. {facts.Result.Summary}"));
        if (facts.Stopped is not null)
        {
            md.AppendLine("- " + facts.Stopped);
        }

        md.AppendLine(verdict is null
            ? "- Veredicto: ninguno."
            : Invariant($"- Veredicto `{verdict.Id}`: **{verdict.Verdict.Decision}**, {(verdict.Valid ? "válido" : "inválido")} ({(verdict.Issues.Count == 0 ? "sin incidencias" : string.Join("; ", verdict.Issues.Select(i => i.Code)))}){(verdict.Supersedes is null ? string.Empty : $", corrige `{verdict.Supersedes}`")}."));
        md.AppendLine(Invariant($"- Duración: planificación {facts.Planning.TotalSeconds:0.0} s; ejecución {facts.Execution.TotalSeconds:0.0} s; total {facts.Total.TotalSeconds:0.0} s."));
        md.AppendLine(Invariant($"- Ejecuciones de agente: {facts.AgentRuns.Count} ({string.Join(", ", facts.AgentRuns.GroupBy(r => r.Agent).Select(g => $"{g.Key} {g.Count()}"))})."));
        var claudeCost = facts.AgentRuns.Where(r => r.Provider == ProviderId.ClaudeCode.Value).Select(r => r.Usage?.CostUsd.Value).ToList();
        var claudeTotal = claudeCost.Count > 0 && claudeCost.All(c => c is not null)
            ? Invariant($"{claudeCost.Sum(c => c!.Value):0.0000} USD")
            : "no disponible en todas las ejecuciones";
        md.AppendLine(Invariant($"- Coste que informa Claude Code (`total_cost_usd`): {claudeTotal} en {claudeCost.Count} ejecuciones."));
        var codex = facts.AgentRuns.Where(r => r.Provider == ProviderId.CodexCli.Value).ToList();
        md.AppendLine(Invariant($"- Tokens que informa Codex: entrada {Sum(codex, u => u.InputTokens)}, salida {Sum(codex, u => u.OutputTokens)}, entrada en caché {Sum(codex, u => u.CachedInputTokens)} en {codex.Count} ejecuciones (Codex no informa coste en dinero)."));
        md.AppendLine(Invariant($"- Plan: {(facts.PlanFromOrchestrator ? "el del orquestador, sin cambios" : "completado por el usuario en el lienzo")}; {(facts.Proposal.Corrected ? "hubo una corrección" : "sin corrección")}."));
        if (!facts.PlanFromOrchestrator)
        {
            md.AppendLine("- Motivo: " + (facts.Proposal.Plan is null
                ? "el orquestador no devolvió un plan legible."
                : facts.Proposal.Violations.Count > 0
                    ? "el validador lo rechazó: " + string.Join("; ", facts.Proposal.Violations.Select(v => $"[{v.Code}] {Line(v.Message)}"))
                    : "le faltaba la integración o la revisión del juez que pedía la petición."));
        }

        md.AppendLine();

        md.AppendLine("## Herramientas");
        md.AppendLine();
        foreach (var probe in facts.Diagnostics.Where(p => p.Provider == ProviderId.ClaudeCode || p.Provider == ProviderId.CodexCli))
        {
            md.AppendLine(Invariant($"- {probe.DisplayName}: {probe.Installation}, versión {probe.Version ?? "?"}, sesión {probe.Authentication}."));
        }

        md.AppendLine();
        md.AppendLine("## Plantilla de agentes");
        md.AppendLine();
        md.AppendLine("| Agente | Rol | CLI | Vendor | Modelo | Herramientas | Permisos |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var agent in F38EndToEndLiveTests.Roster())
        {
            var permissions = agent.Role switch
            {
                AgentRole.Orchestrator => "`dontAsk`, solo lectura de su worktree",
                AgentRole.Executor => "`dontAsk`, Read y Edit solo en su worktree, sin Bash ni red",
                _ => "`-s read-only`",
            };
            md.AppendLine(Invariant($"| {agent.Id} | {agent.Role} | {agent.Model.Provider} | {agent.Model.Vendor} | {agent.Model.Model ?? "el de la CLI"} | {(agent.AllowedTools.Count == 0 ? "-" : string.Join(", ", agent.AllowedTools))} | {permissions} |"));
        }

        md.AppendLine();
        md.AppendLine("## Petición y requisitos congelados");
        md.AppendLine();
        md.AppendLine("> " + F38EndToEndLiveTests.UserRequest);
        md.AppendLine();
        md.AppendLine("- " + facts.Requirements.Replace("\n", "\n- ", StringComparison.Ordinal) + " (obligatorio; criterio c1 por `AutomatedTest`).");
        md.AppendLine();
        md.AppendLine("## Plan aprobado");
        md.AppendLine();
        md.AppendLine("| Tarea | Tipo | Agente | Depende de | Alcance | Verificación | Reintentos |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var task in facts.Plan.Tasks)
        {
            md.AppendLine(Invariant($"| {task.Id} | {task.Kind} | {task.Agent?.Value ?? "sistema"} | {Join(task.DependsOn.Select(d => d.Prerequisite.Value))} | {Join(task.WriteScope)} | {Join(task.VerificationSteps)} | {task.MaxRetries} |"));
        }

        md.AppendLine();
        md.AppendLine("## Ejecuciones de agente");
        md.AppendLine();
        md.AppendLine("| Agente | CLI / modelo | Tarea | Intento | Resultado | Duración | Coste (USD) | Tokens entrada / salida / caché |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var run in facts.AgentRuns)
        {
            md.AppendLine(Invariant($"| {run.Agent} | {run.Provider} / {run.Model ?? "por defecto"} | {run.Task} | {run.Attempt} | {run.Outcome} | {run.Duration.TotalSeconds:0.0} s | {Metric(run.Usage?.CostUsd)} | {Metric(run.Usage?.InputTokens)} / {Metric(run.Usage?.OutputTokens)} / {Metric(run.Usage?.CachedInputTokens)} |"));
        }

        md.AppendLine();
        md.AppendLine("## Pruebas (leídas del TRX por el sistema)");
        md.AppendLine();
        md.AppendLine("| Tarea | Paso | Correctas | Fallidas | Omitidas | Código de salida |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var test in facts.TestRuns)
        {
            md.AppendLine(Invariant($"| {test.Task ?? "-"} | {Cell(test.Title)} | {test.Summary?.Passed} | {test.Summary?.Failed} | {test.Summary?.Skipped} | {test.Summary?.ExitCode} |"));
        }

        md.AppendLine();
        md.AppendLine("## Aislamiento de la verificación (cabecera de cada evidencia, ADR-0017)");
        md.AppendLine();
        md.AppendLine("| Tarea | Paso | Commit verificado | Aislamiento | Copia |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var step in facts.Isolation)
        {
            md.AppendLine(Invariant($"| {step.Task ?? "-"} | {Cell(step.Title)} | `{step.SubjectCommit ?? "-"}` | {Cell(step.Isolation)} | {Cell(step.Source ?? "-")} |"));
        }

        md.AppendLine();
        md.AppendLine("## Veredicto");
        md.AppendLine();
        foreach (var stored in facts.Verdicts)
        {
            var v = stored.Verdict;
            md.AppendLine(Invariant($"### `{stored.Id}`: {v.Decision} ({(stored.Valid ? "válido" : "inválido")})"));
            md.AppendLine();
            md.AppendLine(Invariant($"- Juez: {v.Judge.Agent} en {v.Judge.Model.Provider} ({v.Judge.Model.Vendor}), modelo {v.Judge.Model.Model ?? "no informado"}; emitido {v.IssuedAt:yyyy-MM-dd HH:mm:ss} UTC."));
            md.AppendLine("- Resumen: " + Line(v.Summary));
            foreach (var assessment in v.Assessments ?? [])
            {
                md.AppendLine(Invariant($"- {assessment?.Requirement}: {assessment?.Status}. {Line(assessment?.Rationale)} (evidencias: {Join((assessment?.Evidence ?? []).Select(e => e.Value))})"));
            }

            foreach (var finding in v.Findings ?? [])
            {
                md.AppendLine(Invariant($"- Hallazgo {finding?.Category} ({finding?.Severity}): {Line(finding?.Description)}"));
            }

            md.AppendLine(Invariant($"- Confianza: {v.Confidence?.Level}. {Line(v.Confidence?.Justification)}"));
            md.AppendLine(Invariant($"- Requiere intervención humana: {(v.RequiresHumanIntervention ? "sí" : "no")}."));
            md.AppendLine("- Incidencias del validador: " + (stored.Issues.Count == 0 ? "ninguna." : string.Join("; ", stored.Issues.Select(i => $"{i.Code}: {Line(i.Message)}"))));
            md.AppendLine(Invariant($"- Hashes que repite: requisitos `{v.RequirementSetHash}`, cabeza del registro `{v.DecisionLogHeadHash}`."));
            md.AppendLine();
        }

        md.AppendLine("## Hashes");
        md.AppendLine();
        md.AppendLine(Invariant($"- Plan: `{facts.Started?.PlanHash ?? facts.Plan.ContentHash}`"));
        md.AppendLine(Invariant($"- Requisitos congelados: `{facts.Started?.RequirementSetHash}`"));
        md.AppendLine(Invariant($"- Perfil de verificación: `{facts.Started?.VerificationProfileHash}`"));
        md.AppendLine(Invariant($"- Commit base: `{facts.Started?.BaseCommit}`; commit de integración: `{facts.Result.IntegrationCommit}`"));
        md.AppendLine(Invariant($"- Cabeza del registro de decisiones al terminar: `{facts.HeadHash}` ({facts.Log.Count} entradas de la ejecución; la cadena {(facts.LogIntact ? "verifica" : "NO verifica")})."));
        md.AppendLine();
        md.AppendLine("| Evidencia | Tipo | Origen | SHA-256 |");
        md.AppendLine("|---|---|---|---|");
        foreach (var record in facts.Evidence)
        {
            md.AppendLine(Invariant($"| {Cell(record.Title)} | {record.Kind} | {record.Origin} | `{record.ContentSha256}` |"));
        }

        md.AppendLine();
        md.AppendLine("## Cronología del registro de decisiones");
        md.AppendLine();
        md.AppendLine("| # | Hora (UTC) | Actor | Tipo | Resumen |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var entry in facts.Log)
        {
            md.AppendLine(Invariant($"| {entry.Sequence} | {entry.RecordedAt:HH:mm:ss} | {entry.Actor.Kind}:{entry.Actor.Id} | {entry.Kind} | {Cell(entry.Summary)} |"));
        }

        md.AppendLine();
        md.AppendLine("## Diff del ejecutor (capturado por el sistema)");
        md.AppendLine();
        md.AppendLine("```diff");
        md.AppendLine(facts.ExecutorDiff.TrimEnd());
        md.AppendLine("```");
        return Scrub(md.ToString());
    }

    public static string Metric(MetricSnapshot? metric) =>
        metric?.Value is { } value ? value.ToString(value == decimal.Truncate(value) ? "0" : "0.0000", CultureInfo.InvariantCulture) : "n/d";

    /// <summary>The user's profile folder, in every spelling a path can take in the report, becomes <c>C:\Users\usuario</c>.</summary>
    public static string Scrub(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        return text
            .Replace(profile.Replace("\\", "\\\\", StringComparison.Ordinal), "C:\\\\Users\\\\usuario", StringComparison.OrdinalIgnoreCase)
            .Replace(profile, "C:\\Users\\usuario", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace('\\', '/'), "C:/Users/usuario", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sum(IReadOnlyList<F38AgentRun> runs, Func<UsageSnapshot, MetricSnapshot> metric)
    {
        var values = runs.Select(r => r.Usage is null ? null : metric(r.Usage).Value).ToList();
        return values.Count > 0 && values.All(v => v is not null) ? values.Sum(v => v!.Value).ToString("0", CultureInfo.InvariantCulture) : "n/d";
    }

    private static string Join(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "-" : string.Join(", ", list);
    }

    private static string Line(string? text) => (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

    private static string Cell(string? text) => Line(text).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
