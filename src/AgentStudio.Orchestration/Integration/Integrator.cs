using System.Globalization;
using System.Text;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Git;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Orchestration.Verification;

namespace AgentStudio.Orchestration.Integration;

/// <summary>
/// Merges the commits of a run's tasks into the candidate of the integration task, verifies it in the
/// <c>_integration</c> worktree and, only when every step passed, fast-forwards the run's branch,
/// <c>agentstudio/run-&lt;run&gt;</c>, to it (docs/13 sections 4.2 and 5, ADR-0012): the branch only ever points at
/// verified integrations.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The sources merge in the order of the request. The engine gives them in the deterministic order of
/// <see cref="IntegrationOrder"/>: depth in the graph, then task id.</item>
/// <item>Every source must resolve to a commit, and the branch must descend from the base, before anything is merged.
/// The branch is created at the base the first time.</item>
/// <item>The merges go to the candidate, <see cref="IntegrationNames.CandidateRef"/>, prepared at the head of the branch
/// (<see cref="IWorktreeManager.PrepareCandidateAsync"/>): kept when it already holds that head, which is what an earlier
/// attempt of the task merged, and started again from it otherwise. Each merge is
/// <see cref="IWorktreeManager.MergeIntoCandidateAsync"/>: <c>merge-tree</c> without a worktree, a merge commit and a
/// compare-and-swap of the candidate, with the expected value chained from one merge to the next. A source already in the
/// candidate is a no-op, so integrating again, or resuming, merges nothing new and gives the same commit.</item>
/// <item>A conflict stops the integration. The result fails, lists the conflicting paths and carries a conflict report
/// as evidence; the candidate keeps the merges made before it (the partial head, which a follow-up run starts from), the
/// branch does not move, and nothing with conflict markers is ever committed. It is never resolved here, and
/// <c>_integration</c> is left as it is, because a person may be resolving the conflict there.</item>
/// <item>A clean merge puts <c>_integration</c> exactly on the candidate and runs the verification steps there, with their
/// evidence bound to it (<see cref="EvidenceScope.SubjectCommit"/>: the commit a review approves or not). At least one
/// step is required: an integration nothing verified proves nothing. The checkout discards what the worktree holds, so no
/// agent may be working in <c>_integration</c> at the same time.</item>
/// <item>Only when every step ran and succeeded is the branch moved, by <see cref="IWorktreeManager.FastForwardRunBranchAsync"/>:
/// a compare-and-swap fast-forward from the head the integration started from, refused if the branch moved meanwhile.
/// Then the head of the branch, the commit of the test evidence and <see cref="IntegrationResult.ResultCommit"/> are the
/// same commit. Otherwise the branch stays where it was and a report of the rejected candidate is added to the evidence.
/// A branch already at the candidate (a crash after the fast-forward) is left as it is.</item>
/// <item>Every git operation goes through <see cref="IWorktreeManager"/>, the hardened git layer. Nothing is recorded in
/// the decision log here: the engine records the integration (<c>IntegrationPerformed</c>) once it has the result, and
/// anchors its evidence as the result hands it back (<see cref="IntegrationResult.EvidenceRecords"/>: the records of the
/// verification and of the reports captured here, as the store returned them), never as a row it reads back later.</item>
/// </list>
/// </remarks>
public sealed class Integrator : IIntegrator
{
    public const string EvidenceProducer = "agent-studio";

    private readonly IWorktreeManager _worktrees;
    private readonly IVerificationRunner _verification;
    private readonly IEvidenceStore _evidence;

    public Integrator(IWorktreeManager worktrees, IVerificationRunner verification, IEvidenceStore evidence)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(evidence);

        _worktrees = worktrees;
        _verification = verification;
        _evidence = evidence;
    }

    /// <exception cref="ArgumentException">The request is not safe to integrate (repeated sources, no or invalid verification); nothing was done.</exception>
    /// <exception cref="InvalidOperationException">
    /// A source or the base does not exist, or the run's branch does not descend from the base: nothing was merged. Or the
    /// branch moved while the candidate was verified: it was not fast-forwarded.
    /// </exception>
    public async Task<IntegrationResult> IntegrateAsync(IntegrationRequest request, IProgress<ProcessOutputLine>? output, CancellationToken cancellationToken)
    {
        Validate(request);
        var repoRoot = request.Project.RootPath;
        var branch = IntegrationNames.BranchRef(request.Run);

        var baseCommit = await _worktrees.ResolveCommitAsync(repoRoot, request.BaseCommit, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The base commit {request.BaseCommit} does not exist in '{repoRoot}'.");

        // A missing source must not leave a partial integration behind: resolve them all first.
        var sources = new List<(IntegrationSource Source, string Commit)>(request.Sources.Count);
        foreach (var source in request.Sources)
        {
            var commit = await _worktrees.ResolveCommitAsync(repoRoot, source.Ref, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The ref '{source.Ref}' of task '{source.Task}' does not exist; nothing was merged.");
            sources.Add((source, commit));
        }

        // The run's branch only ever points at verified integrations: the merges go to the candidate of this task, which
        // starts at the head of the branch (or holds it already: what an earlier attempt merged, reused on resume).
        var head = await EnsureBranchAsync(repoRoot, request.Run, branch, baseCommit, cancellationToken).ConfigureAwait(false);
        var candidate = await _worktrees.PrepareCandidateAsync(repoRoot, request.Run, request.Task, head, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < sources.Count; i++)
        {
            var (source, commit) = sources[i];
            var attempt = await _worktrees.MergeIntoCandidateAsync(repoRoot, request.Run, request.Task, candidate, commit, MergeMessage(request, source, commit), cancellationToken)
                .ConfigureAwait(false);
            if (attempt.Merged || attempt.AlreadyMerged)
            {
                candidate = attempt.ResultCommit
                    ?? throw new InvalidOperationException($"The merge of '{source.Ref}' reported success without a result commit.");
                continue;
            }

            var report = await CaptureConflictAsync(request, head, candidate, source, commit, attempt, sources[..i], sources[(i + 1)..], cancellationToken)
                .ConfigureAwait(false);
            return new IntegrationResult(false, candidate, attempt.ConflictPaths, [], [report.Id], [report]);
        }

        var lease = await _worktrees.EnsureAsync(
            new WorktreeRequest(request.Project, request.Run, IntegrationNames.IntegrationWorktree, baseCommit),
            cancellationToken).ConfigureAwait(false);
        await _worktrees.CheckoutAsync(lease, candidate, cancellationToken).ConfigureAwait(false);

        // The evidence is bound to the candidate it verifies: the commit the review approves or not. An isolated step copies
        // it from the objects of the project's repository, never through the .git of _integration.
        var scope = new EvidenceScope(request.Project.ProjectId, request.Run, request.Task, request.Attempt, candidate, repoRoot);
        var results = await _verification.RunAsync(lease.Path, request.Verification, scope, output, cancellationToken).ConfigureAwait(false);
        var records = results.Select(r => r.Evidence).ToArray();
        var evidence = records.Select(r => r.Id).ToArray();
        if (results.Count != request.Verification.Count || !results.All(r => r.Succeeded))
        {
            // The branch stays where it was; the candidate that failed is kept in its ref and said in the evidence.
            var rejected = await CaptureRejectedCandidateAsync(request, head, candidate, sources, results, cancellationToken).ConfigureAwait(false);
            return new IntegrationResult(false, candidate, [], results, [.. evidence, rejected.Id], [.. records, rejected]);
        }

        // Every step passed: only now does the branch move, by a compare-and-swap fast-forward from the head this
        // integration started from, which is refused if the branch moved meanwhile.
        await _worktrees.FastForwardRunBranchAsync(repoRoot, request.Run, head, candidate, cancellationToken).ConfigureAwait(false);
        return new IntegrationResult(true, candidate, [], results, evidence, records);
    }

    /// <summary>The run's branch: created at the base the first time; afterwards it must still descend from it.</summary>
    private async Task<string> EnsureBranchAsync(string repoRoot, RunId run, string branch, string baseCommit, CancellationToken cancellationToken)
    {
        var head = await _worktrees.EnsureRunBranchAsync(repoRoot, run, baseCommit, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(head, baseCommit, StringComparison.OrdinalIgnoreCase)
            && !await _worktrees.IsAncestorAsync(repoRoot, baseCommit, head, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The branch '{branch}' is at {head}, which does not descend from the base commit {baseCommit}; refusing to merge into it.");
        }

        return head;
    }

    private async Task<EvidenceRecord> CaptureConflictAsync(
        IntegrationRequest request,
        string head,
        string candidate,
        IntegrationSource source,
        string sourceCommit,
        MergeAttempt attempt,
        IReadOnlyList<(IntegrationSource Source, string Commit)> merged,
        IReadOnlyList<(IntegrationSource Source, string Commit)> notAttempted,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .Append("# Agent Studio · integration conflict\n")
            .Append(CultureInfo.InvariantCulture, $"# run: {request.Run.Value}\n")
            .Append(CultureInfo.InvariantCulture, $"# integration task: {request.Task.Value} (attempt {request.Attempt})\n")
            .Append(Targets(request, head, candidate))
            .Append(CultureInfo.InvariantCulture, $"# conflicting source: {source.Task.Value} {source.Ref} at {sourceCommit}\n")
            .Append("# merged before it: ").Append(Describe(merged)).Append('\n')
            .Append("# not attempted: ").Append(Describe(notAttempted)).Append('\n')
            .Append(CultureInfo.InvariantCulture, $"# conflicting paths ({attempt.ConflictPaths.Count}):\n");
        foreach (var path in attempt.ConflictPaths)
        {
            text.Append(path).Append('\n');
        }

        text.Append("# git messages:\n")
            .Append(attempt.Messages.Replace('\0', '\n').TrimEnd()).Append('\n')
            .Append("# nothing was committed for this source: resolve it by hand in _integration or in a follow-up run.\n");

        return await CaptureAsync(
            request,
            EvidenceKind.ConflictReport,
            $"Informe de conflicto: {source.Task.Value} en {IntegrationNames.BranchName(request.Run)}",
            text.ToString(),
            subjectCommit: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The candidate that failed its verification, as evidence bound to it: the commit, the head the run's branch keeps,
    /// what was merged and how each step ended. The run's branch never points at it.
    /// </summary>
    private async Task<EvidenceRecord> CaptureRejectedCandidateAsync(
        IntegrationRequest request,
        string head,
        string candidate,
        IReadOnlyList<(IntegrationSource Source, string Commit)> merged,
        IReadOnlyList<VerificationResult> results,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .Append("# Agent Studio · integration candidate rejected\n")
            .Append(CultureInfo.InvariantCulture, $"# run: {request.Run.Value}\n")
            .Append(CultureInfo.InvariantCulture, $"# integration task: {request.Task.Value} (attempt {request.Attempt})\n")
            .Append(Targets(request, head, candidate))
            .Append("# merged: ").Append(Describe(merged)).Append('\n')
            .Append(CultureInfo.InvariantCulture, $"# verification ({results.Count} of {request.Verification.Count} steps ran):\n");
        foreach (var step in request.Verification)
        {
            var result = results.FirstOrDefault(r => string.Equals(r.StepId, step.Id, StringComparison.Ordinal));
            var outcome = result is null ? "did not run"
                : result.Succeeded ? "passed"
                : result.TimedOut ? "timed out"
                : "failed";
            text.Append(step.Id).Append(": ").Append(outcome);
            if (result is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" (exit code {result.ExitCode}, evidence {result.Evidence.Id.Value})");
            }

            text.Append('\n');
        }

        text.Append("# fix the code and integrate again, or start a follow-up run from the candidate.\n");
        return await CaptureAsync(
            request,
            EvidenceKind.Document,
            $"Candidato rechazado: {request.Task.Value} en {IntegrationNames.BranchName(request.Run)}",
            text.ToString(),
            candidate,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Where the merges are and where the run's branch is: the candidate holds them, and the branch only moves to a verified one.</summary>
    private static string Targets(IntegrationRequest request, string head, string candidate) =>
        $"# candidate: {IntegrationNames.CandidateRef(request.Run, request.Task)} at {candidate}\n"
        + $"# run branch: {IntegrationNames.BranchRef(request.Run)} stays at {head}: it only moves to a verified integration\n";

    private async Task<EvidenceRecord> CaptureAsync(IntegrationRequest request, EvidenceKind kind, string title, string text, string? subjectCommit, CancellationToken cancellationToken)
    {
        var draft = new EvidenceDraft(
            kind,
            EvidenceOrigin.CapturedBySystem,
            title,
            EvidenceProducer,
            RunId: request.Run.Value,
            TaskId: request.Task.Value,
            Attempt: request.Attempt,
            MediaType: VerificationRunner.EvidenceMediaType,
            SubjectCommit: subjectCommit);
        using var content = new MemoryStream(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
        return await _evidence.CaptureAsync(request.Project.ProjectId, draft, content, cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(IReadOnlyList<(IntegrationSource Source, string Commit)> sources) =>
        sources.Count == 0 ? "(none)" : string.Join(", ", sources.Select(s => $"{s.Source.Task.Value} {s.Commit}"));

    // The message travels on standard input (MergeIntoCandidateAsync), never on a command line.
    private static string MergeMessage(IntegrationRequest request, IntegrationSource source, string commit) =>
        $"Agent Studio: integra {source.Task.Value} en {IntegrationNames.BranchName(request.Run)}\n\n"
        + $"Agent-Studio-Run: {request.Run.Value}\n"
        + string.Create(CultureInfo.InvariantCulture, $"Agent-Studio-Task: {request.Task.Value}/{request.Attempt}\n")
        + $"Agent-Studio-Source: {source.Task.Value} {commit}\n";

    private static void Validate(IntegrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Project.RootPath, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseCommit, nameof(request));
        ArgumentNullException.ThrowIfNull(request.Sources);
        ArgumentNullException.ThrowIfNull(request.Verification);
        _ = IntegrationNames.BranchRef(request.Run);

        if (request.Verification.Count == 0)
        {
            throw new ArgumentException(
                "An integration needs at least one verification step: without one nothing shows that the merged code builds and passes its tests.",
                nameof(request));
        }

        var problems = VerificationRules.CheckSteps(request.Verification);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The verification steps are not valid: " + string.Join(" ", problems), nameof(request));
        }

        foreach (var source in request.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Ref)
                || (!IntegrationNames.IsObjectId(source.Ref) && !IntegrationNames.IsAgentStudioRef(source.Ref)))
            {
                throw new ArgumentException($"Source '{source?.Ref}' is neither a commit id nor an Agent Studio ref.", nameof(request));
            }
        }

        var repeatedTask = request.Sources.GroupBy(s => s.Task).FirstOrDefault(g => g.Count() > 1);
        if (repeatedTask is not null)
        {
            throw new ArgumentException($"Task '{repeatedTask.Key}' is given more than once.", nameof(request));
        }

        var repeatedRef = request.Sources.GroupBy(s => s.Ref, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (repeatedRef is not null)
        {
            throw new ArgumentException($"Ref '{repeatedRef.Key}' is given more than once.", nameof(request));
        }
    }
}
