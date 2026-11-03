using System.IO;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Security;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Orchestration.Fakes;

/// <summary>How a scripted verification step ends.</summary>
internal enum StepResult
{
    Pass,
    Fail,
    TimedOut,
}

/// <summary>
/// Runs no process: each step "passes" unless the script says otherwise, and its output is captured in the evidence
/// store (a test step with a <see cref="TestRunSummary"/>), as the real runner does.
/// </summary>
internal sealed class FakeVerificationRunner(InMemoryEvidenceStore evidence, TimeProvider time) : IVerificationRunner
{
    private readonly Lock _gate = new();
    private readonly List<(string WorkingDirectory, EvidenceScope Scope, IReadOnlyList<string> Steps)> _calls = [];
    private readonly List<VerificationStep> _steps = [];

    /// <summary>Every step it was asked to run, in order and as it was given (its isolation included).</summary>
    public IReadOnlyList<VerificationStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>Decides each step; null (or no script) means it passes.</summary>
    public Func<EvidenceScope, VerificationStep, StepResult?>? Script { get; set; }

    /// <summary>When set, every run waits for it (cancellation still ends the wait).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public IReadOnlyList<(string WorkingDirectory, EvidenceScope Scope, IReadOnlyList<string> Steps)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public async Task<IReadOnlyList<VerificationResult>> RunAsync(
        string workingDirectory,
        IReadOnlyList<VerificationStep> steps,
        EvidenceScope scope,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((workingDirectory, scope, [.. steps.Select(s => s.Id)]));
            _steps.AddRange(steps);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        var results = new List<VerificationResult>();
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output?.Report(new ProcessOutputLine(ProcessStream.StandardOutput, $"{step.Id}: running", time.GetUtcNow()));
            var result = Script?.Invoke(scope, step) ?? StepResult.Pass;
            var passed = result == StepResult.Pass;
            var summary = step.Kind == EvidenceKind.TestRun
                ? passed ? new TestRunSummary(3, 0, 0, 0) : new TestRunSummary(2, 1, 0, 1)
                : null;
            var record = await evidence.CaptureAsync(
                scope.ProjectId,
                new EvidenceDraft(
                    step.Kind,
                    EvidenceOrigin.CapturedBySystem,
                    $"{step.Id} {scope.Task}#{scope.Attempt}",
                    "agent-studio:verification",
                    summary,
                    RunId: scope.Run?.Value,
                    TaskId: scope.Task?.Value,
                    Attempt: scope.Attempt,
                    MediaType: "text/plain",
                    SubjectCommit: scope.SubjectCommit),
                new MemoryStream(Encoding.UTF8.GetBytes($"{step.Id} {result}")),
                cancellationToken);
            results.Add(new VerificationResult(step.Id, record, passed, result == StepResult.TimedOut, passed ? 0 : 1));
        }

        return results;
    }
}

/// <summary>
/// Merges task snapshots in the fake object store and, like the real integrator, verifies a clean merge through the
/// verification runner with its evidence bound to the merged commit (the commit a review approves or not). A script can
/// force a conflict or a failed verification.
/// </summary>
internal sealed class FakeIntegrator(FakeWorktreeManager worktrees, InMemoryEvidenceStore evidence, IVerificationRunner verification) : IIntegrator
{
    private readonly Lock _gate = new();
    private readonly List<IntegrationRequest> _requests = [];

    /// <summary>When it returns true for a request, the verification of the merged result fails.</summary>
    public Func<IntegrationRequest, bool>? FailVerification { get; set; }

    /// <summary>When it returns paths for a request, the merge conflicts on them (nothing is merged).</summary>
    public Func<IntegrationRequest, IReadOnlyList<string>?>? ForceConflict { get; set; }

    /// <summary>
    /// Runs with the result just before it is returned, and returns what is returned instead: for example after rewriting
    /// a row of its evidence behind the engine's back, or without the records of its evidence.
    /// </summary>
    public Func<IntegrationRequest, IntegrationResult, IntegrationResult>? OnResult { get; set; }

    public IReadOnlyList<IntegrationRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public async Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, IProgress<ProcessOutputLine>? output, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        var forced = ForceConflict?.Invoke(request);
        IReadOnlyList<string> conflicts = forced ?? [];
        var commit = forced is { Count: > 0 } ? null : worktrees.Merge(request.BaseCommit, [.. request.Sources.Select(s => s.Ref)], out conflicts);
        IReadOnlyList<VerificationResult> verified = [];
        if (commit is not null)
        {
            var scope = new EvidenceScope(request.Project.ProjectId, request.Run, request.Task, request.Attempt, commit);
            verified = await verification.RunAsync("_integration", request.Verification, scope, output, cancellationToken);
        }

        var failedVerification = commit is not null && ((FailVerification?.Invoke(request) ?? false) || verified.Any(v => !v.Succeeded));
        var text = commit is null ? "CONFLICT: " + string.Join(", ", conflicts) : $"merged into {commit}";
        var record = await evidence.CaptureAsync(
            request.Project.ProjectId,
            new EvidenceDraft(
                commit is null ? EvidenceKind.CommandOutput : EvidenceKind.BuildLog,
                EvidenceOrigin.CapturedBySystem,
                $"integration {request.Task}#{request.Attempt}",
                "agent-studio:integration",
                RunId: request.Run.Value,
                TaskId: request.Task.Value,
                Attempt: request.Attempt,
                MediaType: "text/plain"),
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            cancellationToken);
        var result = new IntegrationResult(
            commit is not null && !failedVerification,
            commit,
            conflicts,
            verified,
            [record.Id, .. verified.Select(v => v.Evidence.Id)],
            [record, .. verified.Select(v => v.Evidence)]);
        return OnResult?.Invoke(request, result) ?? result;
    }
}

/// <summary>
/// An independent review whose outcome is scripted per review task. Given the decision recorder, it writes what the
/// real review writes to the log: <c>ReviewRequested</c>, the judge's launch around its work (<c>AgentRunStarted</c> and
/// <c>AgentRunFinished</c>, with no task, as the real ones carry none) and, unless the judge gave no verdict,
/// <c>VerdictIssued</c> for the review task.
/// </summary>
internal sealed class FakeIndependentReview(IDecisionRecorder? decisions = null) : IIndependentReview
{
    private static readonly Actor System = new(ActorKind.System, "agent-studio");
    private readonly Lock _gate = new();
    private readonly List<ReviewRequest> _requests = [];
    private int _verdicts;

    public Func<ReviewRequest, ReviewDisposition>? Script { get; set; }

    /// <summary>When the usage limit of a <see cref="ReviewDisposition.RateLimited"/> review resets (null: the provider did not say).</summary>
    public DateTimeOffset? RetryAfter { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>What the judge of one review waits for before it answers, when it names something; <see cref="Gate"/> otherwise.</summary>
    public Func<ReviewRequest, Task?>? GateFor { get; set; }

    /// <summary>Runs while the (fake) judge works, for example to change the user's repository behind the engine's back.</summary>
    public Action<ReviewRequest>? OnReview { get; set; }

    public IReadOnlyList<ReviewRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public async Task<ReviewOutcome> ReviewRunAsync(ReviewRequest request, IProgress<AgentStudio.Application.Providers.AgentEvent>? progress, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        var project = request.Project.ProjectId;
        var run = request.Run.Value;
        var head = new string('b', 64);
        if (decisions is not null)
        {
            await decisions.RecordAsync(project, System, DecisionKind.ReviewRequested, $"Revisión pedida a {request.Judge}", new ReviewRequestedPayload(request.ReviewTask, request.Judge) { RunId = run }, null, cancellationToken);

            // As the real review: the dossier is built on the log as it stands once the review was asked, and the verdict
            // echoes that head, which names the integration the judge was shown.
            head = (await decisions.LoadAsync(project, cancellationToken)).HeadHash;
            await decisions.RecordAsync(
                project,
                System,
                DecisionKind.AgentRunStarted,
                $"Juez '{request.Judge}' lanzado",
                new AgentRunStartedPayload(null, 1, request.Judge, ProviderId.CodexCli, VendorId.OpenAI, null, request.WorkspacePath, new EvidenceId("prompt-del-juez"), new string('c', 64), []) { RunId = run },
                null,
                cancellationToken);
        }

        OnReview?.Invoke(request);
        try
        {
            if ((GateFor?.Invoke(request) ?? Gate?.Task) is { } gate)
            {
                await gate.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await FinishedAsync(project, run, request, AttemptOutcome.Cancelled);
            throw;
        }

        var disposition = Script?.Invoke(request) ?? ReviewDisposition.Approved;
        await FinishedAsync(project, run, request, disposition == ReviewDisposition.RateLimited ? AttemptOutcome.RateLimited : AttemptOutcome.Succeeded);
        var decision = disposition switch
        {
            ReviewDisposition.Approved => VerdictDecision.Approved,
            ReviewDisposition.ChangesRequested => VerdictDecision.ChangesRequested,
            ReviewDisposition.Rejected => VerdictDecision.Rejected,
            _ => VerdictDecision.EscalateToHuman,
        };
        var verdict = new Verdict(
            decision,
            "summary",
            [],
            [],
            [],
            [],
            [],
            new Confidence(ConfidenceLevel.High, "scripted"),
            [],
            disposition == ReviewDisposition.NeedsHuman,
            new JudgeIdentity(request.Judge, new ModelBinding(ProviderId.CodexCli, VendorId.OpenAI)),
            new string('a', 64),
            head,
            DateTimeOffset.UnixEpoch);
        if (disposition == ReviewDisposition.RateLimited)
        {
            // The judge gave no verdict: nothing is stored, and the caller learns when to ask again.
            return new ReviewOutcome(string.Empty, verdict, [], false, true, disposition, RetryAfter);
        }

        var id = $"verdict-{Interlocked.Increment(ref _verdicts)}";
        if (decisions is not null)
        {
            await decisions.RecordAsync(
                project,
                new Actor(ActorKind.Judge, request.Judge.Value),
                DecisionKind.VerdictIssued,
                $"Veredicto {decision} válido",
                new VerdictIssuedPayload(id, request.ReviewTask, decision, true, [], verdict.RequirementSetHash, verdict.DecisionLogHeadHash, null, null) { RunId = run },
                null,
                CancellationToken.None);
        }

        return new ReviewOutcome(id, verdict, [], false, true, disposition);
    }

    /// <summary>Closes the judge's launch, also when the review was cancelled (the real judge does the same).</summary>
    private async Task FinishedAsync(string project, string run, ReviewRequest request, AttemptOutcome outcome)
    {
        if (decisions is not null)
        {
            await decisions.RecordAsync(
                project,
                System,
                DecisionKind.AgentRunFinished,
                $"Juez '{request.Judge}' terminado: {outcome}",
                new AgentRunFinishedPayload(null, 1, outcome, 0, "sesion-del-juez", null, null, null) { RunId = run },
                null,
                CancellationToken.None);
        }
    }
}

/// <summary>An approval gate whose answers are scripted; by default it approves.</summary>
internal sealed class FakeApprovalGate : IApprovalGate
{
    private readonly Lock _gate = new();
    private readonly List<ApprovalRequest> _requests = [];

    public Func<ApprovalRequest, bool>? Approve { get; set; }

    public TaskCompletionSource? Hold { get; set; }

    public IReadOnlyList<ApprovalRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        var approved = Approve?.Invoke(request) ?? true;
        return new ApprovalDecision(approved, "usuario", DateTimeOffset.UnixEpoch, approved ? "ok" : "no");
    }
}

internal sealed class FakeSkillCatalog(params CatalogSkill[] skills) : ISkillCatalog
{
    public Task<IReadOnlyList<CatalogSkill>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogSkill>>(skills);

    public static CatalogSkill Vendored(string id) => new(
        new SkillEntry(
            new SkillId(id),
            id,
            "summary",
            SkillSectors.IngenieriaSoftware,
            ["testing"],
            new SkillSource("repo", id, null, null, "MIT", SkillDistribution.Vendored)),
        "C:/skills-index/" + id);
}

internal sealed class FakeSkillActivations(params SkillActivation[] activations) : ISkillActivationStore
{
    public Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SkillActivation>>(activations);

    public Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct) =>
        throw new NotSupportedException("The engine only reads activations.");
}

/// <summary>Records what the real <see cref="SkillProvisioner"/> asks the materializer to do.</summary>
internal sealed class RecordingSkillMaterializer : ISkillMaterializer
{
    private readonly Lock _gate = new();

    public List<(ProviderId Cli, IReadOnlyList<string> Skills, SkillSessionContext Session, string Directory)> Materialized { get; } = [];

    public List<string> Cleaned { get; } = [];

    public Task<string?> MaterializeAsync(ProviderId cli, IReadOnlyList<ActivatedSkill> activatedSkills, SkillSessionContext session, CancellationToken ct)
    {
        if (activatedSkills.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var directory = Path.Combine(session.SessionBaseDirectory, "skills-" + session.RunId);
        lock (_gate)
        {
            Materialized.Add((cli, [.. activatedSkills.Select(s => s.Skill.Id.Value)], session, directory));
        }

        return Task.FromResult<string?>(directory);
    }

    public void Cleanup(string materializedDirectory)
    {
        lock (_gate)
        {
            Cleaned.Add(materializedDirectory);
        }
    }
}
