using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Prompts;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Requirements;
using AgentStudio.Domain.Work;

namespace AgentStudio.Orchestration.Engine;

/// <summary>Why the next attempt of a task goes in a new session, with a handoff (docs/16 D-42 and D-43).</summary>
internal enum HandoffReason
{
    /// <summary>The context of the attempt ran out (its end has the class <c>ContextExhausted</c>).</summary>
    ContextExhausted,

    /// <summary>The context of a failed attempt reached the threshold from which Claude compresses it.</summary>
    ContextNearlyFull,

    /// <summary>The app closed during the attempt: its session is never resumed (D-43).</summary>
    Interrupted,

    /// <summary>
    /// Phase 13 (docs/17 D-30): the attempt that asked a question left no session to resume (its CLI announced none), so the
    /// attempt with the answer goes in a new session, with the question and the answer in the handoff.
    /// </summary>
    SessionNotResumable,
}

/// <summary>
/// What the last attempts of a task left for its handoff: the last tests the system ran, and what the agent said in its last
/// report (its summary and what it left open), which is the agent's word, never verified.
/// </summary>
internal sealed record AttemptNotes(TestRunSummary? Tests, string? AgentSummary, IReadOnlyList<string> AgentOpenIssues)
{
    /// <summary>The notes after an attempt that ran <paramref name="tests"/> and reported <paramref name="summary"/>: each kept from before when this one has none.</summary>
    public static AttemptNotes Merge(AttemptNotes? before, TestRunSummary? tests, string? summary, IReadOnlyList<string> openIssues) =>
        summary is null && openIssues.Count == 0
            ? new AttemptNotes(tests ?? before?.Tests, before?.AgentSummary, before?.AgentOpenIssues ?? [])
            : new AttemptNotes(tests ?? before?.Tests, summary, openIssues);
}

/// <summary>What the loop knows of a task and of its run when it renews the session of the task.</summary>
internal sealed record HandoffFacts(
    HandoffReason Reason,
    WorkTaskStatus Status,
    int RetryCount,
    int MaxRetries,
    IReadOnlyList<(TaskId Task, WorkTaskStatus Status)> Graph,
    string? PreviousErrors,
    AttemptNotes? Notes,
    IReadOnlyList<Questions.RecordedAnswer>? Answers = null);

/// <summary>
/// The handoff that the system builds when the session of a task is renewed (F8-6; docs/16 D-42 and D-43, ADR-0035): a
/// <c>&lt;handoff&gt;</c> block of the prompt of the next attempt, which goes in a new session, never resuming the old one. It
/// holds the objective and the criteria of the task (from the plan and the frozen set), the approved plan, the state of the
/// task and of the graph, the files changed and the commit the worktree is at, the counters of the last tests the system
/// ran, the errors of the attempt before, and what the agent proposed in its last report, marked as the agent's. All of it
/// comes from the log and the ledger, written as one JSON document through <see cref="PromptData"/>, so nothing in it can
/// close the block. It carries no memory of its own: the memory of an agent whose policy allows it stays in its section of
/// the prompt, whose launch <c>AgentRunStarted</c> marks, so the judge never receives it.
/// </summary>
internal static class Handoff
{
    /// <summary>The share of the context window from which Claude compresses the context (ADR-0021, 85 %).</summary>
    public const decimal CompressionThreshold = 0.85m;

    /// <summary>Whether the context of an attempt reached the compression threshold, as its end recorded the largest step.</summary>
    public static bool NearlyFull(ContextPeakSnapshot? peak) =>
        peak is { UsedTokens.Value: { } used, ContextWindow.Value: { } window } && window > 0 && used / window >= CompressionThreshold;

    /// <summary>Why the session is renewed, in Spanish, for the line the block goes after.</summary>
    public static string Why(HandoffReason reason) => reason switch
    {
        HandoffReason.ContextExhausted => "El contexto del intento anterior se agotó",
        HandoffReason.ContextNearlyFull => "El contexto del intento anterior llegó al umbral en el que se comprime",
        HandoffReason.SessionNotResumable => "El intento que hizo la pregunta no dejó una sesión que se pueda reanudar",
        _ => "El intento anterior se interrumpió al cerrarse la aplicación, y su sesión no se reanuda",
    };

    /// <summary>The block of the handoff for <paramref name="attempt"/> of <paramref name="spec"/>, its tag and its one JSON document.</summary>
    /// <param name="head">The commit the worktree of the task is at, the base of the attempt; null for a task without a worktree.</param>
    /// <param name="changedFiles">The files the task changed so far, from the base of its worktree.</param>
    public static string Block(RunContext run, TaskSpec spec, int attempt, HandoffFacts facts, string? head, IReadOnlyList<string> changedFiles)
    {
        var criteria = spec.Covers
            .Select(run.Requirements.Find)
            .OfType<Requirement>()
            .SelectMany(r => r.Criteria.Select(c => new { requirement = r.Id.Value, criterion = c.Id, method = c.Method.ToString(), description = c.Description }))
            .ToList();
        var tests = facts.Notes?.Tests;
        var agent = facts.Notes is { } notes && (notes.AgentSummary is not null || notes.AgentOpenIssues.Count > 0)
            ? new { summary = notes.AgentSummary, openIssues = notes.AgentOpenIssues }
            : null;
        var json = PromptData.Json(new
        {
            reason = facts.Reason.ToString(),
            task = spec.Id.Value,
            attempt,
            objective = spec.Objective,
            criteria,
            plan = run.Plan.Summary,
            taskState = new { status = facts.Status.ToString(), retries = facts.RetryCount, maxRetries = facts.MaxRetries },
            graph = facts.Graph.Select(g => new { task = g.Task.Value, status = g.Status.ToString() }).ToList(),
            head,
            changedFiles,
            tests = tests is null ? null : new { passed = tests.Passed, failed = tests.Failed, skipped = tests.Skipped, exitCode = tests.ExitCode },
            previousErrors = facts.PreviousErrors,
            agent,
            humanAnswers = (facts.Answers ?? []).Select(a => new
            {
                question = a.Asked.Id,
                attempt = a.Asked.Attempt,
                evidence = a.Asked.Evidence.Value,
                sha256 = a.Asked.Sha256,
                option = a.Option,
                ownAnswerEvidence = a.OwnTextEvidence?.Value,
            }).ToList(),
        });

        var text = new StringBuilder();
        text.Append("<handoff task=\"").Append(PromptData.Attribute(spec.Id.Value))
            .Append("\" attempt=\"").Append(attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\" reason=\"").Append(facts.Reason).AppendLine("\">");
        text.AppendLine(json);
        text.Append("</handoff>");
        return text.ToString();
    }
}
