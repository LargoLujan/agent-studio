using System.Globalization;
using System.Text;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Localization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Debate;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;

namespace AgentStudio.Memory.Derivation;

/// <summary>
/// The coded form of what <see cref="DerivationTexts"/> writes (docs/17 D-47 and D-48, ADR-0054): the same sentence, as a code with
/// its arguments, so the screens say it in the language of the person while the anchored entry keeps its Spanish text and its
/// hash. Each method here takes the same arguments as its twin there and says the same in Spanish, word for word (a test compares
/// the two over every shape of the log). A sentence is made of pieces (who, when, how many, in which state), each a coded text of
/// its own and passed as an argument (<see cref="INestedText"/>), so no language depends on the order of another.
/// </summary>
/// <remarks>
/// Pieces and sentences have no space at their ends: an optional part that starts with a space gets a sentence of its own for when
/// it is there, and one that starts with a comma or a semicolon is a piece that is an empty text when it is not.
/// </remarks>
internal static class DerivationUserTexts
{
    private static UserText Code(string code, params object[] arguments) => new(code, arguments);

    // ---- topics ----

    public static UserText PlanTopic(string run) => Code("memory.topic.plan", run);

    public static UserText PlanSummaryTopic(string run) => Code("memory.topic.plan-summary", run);

    public static UserText DeliveryTopic(string run) => Code("memory.topic.delivery", run);

    public static UserText PendingTopic(string run) => Code("memory.topic.pending", run);

    public static UserText OpenIssuesTopic(TaskId task, string run) => Code("memory.topic.open-issues", task.Value, run);

    public static UserText ChallengeTopic(string verdict, string unit) => Code("memory.topic.challenge", verdict, OfUnit(unit));

    public static UserText RevisionAcceptanceTopic(string unit) => Code("memory.topic.revision-acceptance", InUnit(unit));

    public static UserText ReplanTopic(string unit) => Code("memory.topic.replan", Unit(unit));

    public static UserText DebateTopic(string debate, DebateMode mode) => Code("memory.topic.debate", debate, Mode(mode));

    public static UserText FailedTestsTopic(TaskId? task, string run) =>
        task is { } id ? Code("memory.topic.failed-tests-task", id.Value, InUnit(run)) : Code("memory.topic.failed-tests", InUnit(run));

    public static UserText ConflictTopic(TaskId? task, string run) =>
        task is { } id ? Code("memory.topic.conflict-task", id.Value, run) : Code("memory.topic.conflict", run);

    public static UserText AlertTopic(string code, string run) => Code("memory.topic.alert", code, InUnit(run));

    public static UserText IntegrationTopic(TaskId? task, string run) =>
        task is { } id ? Code("memory.topic.integration-task", id.Value, run) : Code("memory.topic.integration", run);

    public static UserText VerdictTopic(TaskId? task, string run, string? revises = null) =>
        !IsRun(run)
            ? task is { } judge ? Code("memory.topic.verdict-unit-task", judge.Value, InUnit(run)) : Code("memory.topic.verdict-unit", OfUnit(run))
            : revises is { } challenged ? Code("memory.topic.verdict-revision", challenged, run)
            : task is { } id ? Code("memory.topic.verdict-review", id.Value, run) : Code("memory.topic.verdict-run", run);

    /// <summary>The topic of an entry of the journal (<see cref="DerivationTexts.JournalTopic"/>): its day as <c>yyyy-MM-dd</c> and its key.</summary>
    public static UserText JournalTopic(DateOnly day, string key) =>
        Code("memory.topic.journal", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), key);

    // ---- Decision ----

    public static UserText PlanDecision(Actor actor, bool granted, string? planHash, int tasks, string run, DateTimeOffset at)
    {
        var plan = planHash is { Length: >= 12 } ? Code("memory.plan.hash", planHash[..12]) : Code("memory.plan.none");
        if (!granted)
        {
            return Code("memory.plan-decision.denied", Who(actor), plan, run, When(at));
        }

        return tasks > 0
            ? Code("memory.plan-decision.granted-sized", Who(actor), plan, run, Count("tasks", tasks), When(at))
            : Code("memory.plan-decision.granted", Who(actor), plan, run, When(at));
    }

    public static UserText DeliveryDecision(Actor actor, bool granted, int? changedPaths, string run, DateTimeOffset at) =>
        !granted ? Code("memory.delivery-decision.denied", Who(actor), run, When(at))
        : changedPaths is { } paths ? Code("memory.delivery-decision.granted-changes", Who(actor), run, When(at), Changes(paths))
        : Code("memory.delivery-decision.granted", Who(actor), run, When(at));

    public static UserText Challenge(Actor actor, VerdictChallengedPayload challenge, string unit, DateTimeOffset at) =>
        Code("memory.challenge", Person(actor), challenge.Verdict, OfUnit(unit), When(at), challenge.RevisionDebate, challenge.Reasons.Value);

    public static UserText RevisionAcceptance(Actor actor, bool granted, string unit, DateTimeOffset at) =>
        Code(granted ? "memory.revision-acceptance.granted" : "memory.revision-acceptance.denied", Who(actor), InUnit(unit), When(at));

    public static UserText ReplanDismissal(Actor actor, bool granted, string unit, DateTimeOffset at) =>
        Code(granted ? "memory.replan-dismissal.granted" : "memory.replan-dismissal.denied", Who(actor), Unit(unit), When(at));

    // ---- PendingTask ----

    public static UserText Pending(string run, RunPhase phase, IReadOnlyList<(TaskCreatedPayload Task, TaskStatusChangedPayload? Last)> tasks) =>
        Code("memory.pending", run, Phase(phase), Count("tasks", tasks.Count), Joined(tasks.Select(t => Unfinished(t.Task, t.Last))));

    private static UserText Unfinished(TaskCreatedPayload task, TaskStatusChangedPayload? last)
    {
        object attempts = last is { Attempt: > 0 } moved ? Code("memory.unfinished.attempts", Count("attempts", moved.Attempt)) : string.Empty;
        object reason = last is { To: WorkTaskStatus.Blocked } blocked && Block(blocked.BlockReason) is { } why ? Code("memory.unfinished.reason", why) : string.Empty;
        return Code("memory.unfinished", task.Task.Value, TaskKind(task.Kind), Status(last), attempts, reason);
    }

    // ---- DebateSummary ----

    public static UserText DebateSummary(
        string debate,
        DebateOpenedPayload opened,
        DebateClosedPayload closed,
        IReadOnlyList<(string Id, VerdictIssuedPayload? Verdict)> verdicts,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt)
    {
        object about = opened.Challenges is { } challenged && DerivationTexts.IsLabel(challenged)
            ? Code("memory.debate.challenge", challenged)
            : Subject(opened);
        object chosen = closed.ChosenProposal is { } proposal
            ? Code("memory.debate.chosen", DerivationTexts.IsLabel(proposal) ? proposal : Code("memory.debate.not-cited"))
            : string.Empty;
        var verdictText = verdicts.Count == 0
            ? Code("memory.debate.no-verdicts")
            : Code("memory.debate.verdicts", Joined(verdicts.Select(VerdictLine)));
        return Code("memory.debate-summary", debate, Mode(opened.Mode), about, When(openedAt), When(closedAt), Outcome(closed.Outcome), Rule(closed.Rule), chosen, verdictText);
    }

    /// <summary>The summary with the sentence about its question (<see cref="DerivationTexts.WithQuestion"/>).</summary>
    public static UserText WithQuestion(UserText summary, UserText question) => Code("memory.with-question", summary, question);

    public static UserText QuestionQuoted(string question) => Code("memory.question.quoted", question);

    public static UserText QuestionEmpty() => Code("memory.question.empty");

    public static UserText QuestionNotAnchored(string evidence) => Code("memory.question.not-anchored", evidence);

    public static UserText QuestionNotByAPerson(string evidence) => Code("memory.question.not-by-person", evidence);

    public static UserText QuestionTampered(string evidence) => Code("memory.question.tampered", evidence);

    // ---- KnownError ----

    public static UserText FailedTests(EvidenceRegisteredPayload test, TestRunSnapshot tests, string run)
    {
        var where = test.Task is { } task
            ? test.Attempt is { } attempt ? Code("memory.failed-tests.where-attempt", task.Value, attempt) : Code("memory.failed-tests.where-task", task.Value)
            : OfUnit(run);
        var subject = string.IsNullOrWhiteSpace(test.Title) ? where : Code("memory.failed-tests.subject-titled", test.Title.Trim(), where);
        var detail = tests.Failed > 0
            ? tests.Failed == 1
                ? Code("memory.tests.failed-one", tests.Passed, tests.Skipped, tests.ExitCode)
                : Code("memory.tests.failed-other", tests.Failed, tests.Passed, tests.Skipped, tests.ExitCode)
            : tests.Passed == 0
                ? Code("memory.tests.none-passed", tests.Skipped, tests.ExitCode)
                : Code("memory.tests.bad-exit", tests.ExitCode, tests.Passed, tests.Skipped);
        return test.SubjectCommit is { } commit
            ? Code("memory.failed-tests-commit", subject, commit, detail)
            : Code("memory.failed-tests", subject, detail);
    }

    public static UserText Conflict(IntegrationPerformedPayload merge, string run)
    {
        var count = Count("files", merge.ConflictPaths.Count);
        var paths = List(merge.ConflictPaths);
        return merge.Task is { } id
            ? Code("memory.conflict-task", id.Value, run, count, paths)
            : Code("memory.conflict", run, count, paths);
    }

    public static UserText Alert(AuditAlertPayload alert, string run)
    {
        var unit = InUnit(run);
        var hasTasks = alert.Tasks.Count > 0;
        var tasks = string.Join(", ", alert.Tasks.Select(t => t.Value));
        var detail = string.IsNullOrWhiteSpace(alert.Message) ? null : alert.Message.Trim();
        return (hasTasks, detail) switch
        {
            (true, { } message) => Code("memory.alert-tasks-detail", alert.Code, unit, tasks, message),
            (true, null) => Code("memory.alert-tasks", alert.Code, unit, tasks),
            (false, { } message) => Code("memory.alert-detail", alert.Code, unit, message),
            _ => Code("memory.alert", alert.Code, unit),
        };
    }

    public static UserText Replan(Actor actor, ReplanRequestedPayload request, string unit, DateTimeOffset at)
    {
        var bySystem = actor.Kind is ActorKind.Auditor or ActorKind.System;
        var who = actor.Kind switch
        {
            ActorKind.Auditor => Code("memory.replan-who.auditor"),
            ActorKind.Judge => Code("memory.replan-who.judge", actor.Id),
            _ => Code("memory.replan-who.system"),
        };
        var findings = request.Findings.Count == 0
            ? Code("memory.replan.no-findings")
            : bySystem ? Code("memory.replan.findings", List(request.Findings)) : Count("findings", request.Findings.Count);
        object tasks = request.Tasks.Count > 0 ? Code("memory.replan.tasks", string.Join(", ", request.Tasks.Select(t => t.Value))) : string.Empty;
        object verdict = request.Verdict is { } id ? Code("memory.replan.verdict", id) : string.Empty;
        return bySystem && !string.IsNullOrWhiteSpace(request.Message)
            ? Code("memory.replan-detail", who, Unit(unit), When(at), findings, tasks, verdict, request.Message.Trim())
            : Code("memory.replan", who, Unit(unit), When(at), findings, tasks, verdict);
    }

    // ---- Change ----

    public static UserText Integration(IntegrationPerformedPayload merge, string commit, string run) =>
        merge.Task is { } id
            ? Code("memory.integration-task", id.Value, run, merge.TargetRef, commit, Sources(merge.Sources))
            : Code("memory.integration", run, merge.TargetRef, commit, Sources(merge.Sources));

    public static UserText Delivery(IntegrationPerformedPayload delivery, string commit, IReadOnlyList<string>? changedPaths, string run) =>
        changedPaths switch
        {
            null => Code("memory.delivery", run, delivery.TargetRef, commit, Sources(delivery.Sources)),
            { Count: 0 } => Code("memory.delivery-no-changes", run, delivery.TargetRef, commit, Sources(delivery.Sources)),
            _ => Code("memory.delivery-changes", run, delivery.TargetRef, commit, Sources(delivery.Sources), ChangesCapital(changedPaths.Count), List(changedPaths)),
        };

    // ---- Verdict ----

    public static UserText Verdict(VerdictIssuedPayload verdict, string judge, string run, string? revises = null)
    {
        var target = !IsRun(run) ? Code("memory.verdict-target.unit", Unit(run))
            : revises is { } challenged ? Code("memory.verdict-target.revision", challenged)
            : verdict.Task is { } task ? Code("memory.verdict-target.review", task.Value) : Code("memory.verdict-target.run", run);
        var validity = verdict.Valid ? Code("memory.validity.valid")
            : verdict.Violations.Count == 0 ? Code("memory.validity.invalid")
            : Code("memory.validity.invalid-with", string.Join(", ", verdict.Violations.Select(v => v.ToString())));
        object correction = verdict.Supersedes is not { } superseded ? string.Empty
            : revises is not null ? Code("memory.verdict.revises", superseded)
            : Code("memory.verdict.corrects", superseded);
        return string.IsNullOrWhiteSpace(verdict.VendorNote)
            ? Code("memory.verdict", judge, verdict.VerdictId, target, Decision(verdict.Decision), validity, correction)
            : Code("memory.verdict-note", judge, verdict.VerdictId, target, Decision(verdict.Decision), validity, correction, verdict.VendorNote.Trim());
    }

    // ---- why nothing was derived ----

    public static UserText BusyReason() => Code("memory.reason.busy");

    public static UserText StoppedReason() => Code("memory.reason.stopped");

    public static UserText UnanchoredReason() => Code("memory.reason.unanchored");

    public static UserText BrokenMidwayReason() => Code("memory.reason.broken-midway");

    public static UserText NotVerifiedReason(DecisionLogVerification verification) =>
        verification.FirstInvalidSequence is { } sequence ? Code("memory.reason.not-verified-at", sequence) : Code("memory.reason.not-verified");

    /// <summary>The coded form of one of the reasons of <see cref="DerivationTexts"/> that have no argument; null for any other text.</summary>
    public static UserText? ReasonOf(string reason) =>
        string.Equals(reason, DerivationTexts.BusyReason, StringComparison.Ordinal) ? BusyReason()
        : string.Equals(reason, DerivationTexts.StoppedReason, StringComparison.Ordinal) ? StoppedReason()
        : string.Equals(reason, DerivationTexts.UnanchoredReason, StringComparison.Ordinal) ? UnanchoredReason()
        : string.Equals(reason, DerivationTexts.BrokenMidwayReason, StringComparison.Ordinal) ? BrokenMidwayReason()
        : null;

    /// <summary>
    /// The coded topic of an entry of the journal, read from its topic (<see cref="DerivationTexts.JournalTopic"/>): only when the
    /// topic is exactly what that method writes for the day and the key it names.
    /// </summary>
    public static UserText? TryJournalTopic(string topic)
    {
        const string Start = "Bitácora del ";
        const string Middle = " · ";
        const int DayLength = 10;
        if (!topic.StartsWith(Start, StringComparison.Ordinal)
            || topic.Length < Start.Length + DayLength + Middle.Length
            || !DateOnly.TryParseExact(topic.AsSpan(Start.Length, DayLength), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        var key = topic[(Start.Length + DayLength + Middle.Length)..];
        return string.Equals(DerivationTexts.JournalTopic(day, key), topic, StringComparison.Ordinal) ? JournalTopic(day, key) : null;
    }

    /// <summary>Why the export of the memory waits (<c>MemoryMarkdownExporter</c>).</summary>
    public static UserText ExportBusyReason() => Code("memory.reason.export-busy");

    // ---- pieces ----

    private static UserText Who(Actor actor) =>
        actor.Kind == ActorKind.User ? Code("memory.who.person") : Code("memory.who.system-no-answer");

    private static UserText Person(Actor actor) =>
        actor.Kind == ActorKind.User ? Code("memory.who.person") : Code("memory.who.system");

    private static bool IsRun(string unit) =>
        !unit.StartsWith(JournalingAgentProvider.DebatePrefix, StringComparison.Ordinal)
        && !unit.StartsWith(JournalingAgentProvider.AuditPrefix, StringComparison.Ordinal);

    private static string UnitKind(string unit) =>
        unit.StartsWith(JournalingAgentProvider.DebatePrefix, StringComparison.Ordinal) ? "debate"
        : unit.StartsWith(JournalingAgentProvider.AuditPrefix, StringComparison.Ordinal) ? "audit"
        : "run";

    private static UserText Unit(string unit) => Code("memory.unit." + UnitKind(unit), unit);

    private static UserText OfUnit(string unit) => Code("memory.of-unit." + UnitKind(unit), unit);

    private static UserText InUnit(string unit) => Code("memory.in-unit." + UnitKind(unit), unit);

    /// <summary>A moment in UTC, said so; the instant goes in as it is and the offset is zero, so the zone of the machine is never in the text.</summary>
    private static UserText When(DateTimeOffset at) => Code("memory.when", at.ToUniversalTime());

    private static UserText Count(string noun, int count) =>
        count == 1 ? Code($"memory.count.{noun}-one") : Code($"memory.count.{noun}-other", count);

    private static UserText Changes(int paths) => paths switch
    {
        0 => Code("memory.changes.none"),
        1 => Code("memory.changes.one"),
        _ => Code("memory.changes.other", paths),
    };

    private static UserText ChangesCapital(int paths) => paths == 1 ? Code("memory.changes-capital.one") : Code("memory.changes-capital.other", paths);

    /// <summary>The items, up to <see cref="DerivationTexts.MaxListed"/>, and how many more there are.</summary>
    private static object List(IReadOnlyList<string> items)
    {
        var listed = string.Join(", ", items.Take(DerivationTexts.MaxListed));
        return items.Count > DerivationTexts.MaxListed ? Code("memory.list.more", listed, items.Count - DerivationTexts.MaxListed) : listed;
    }

    /// <summary>The pieces of a list, one after another, separated by a semicolon.</summary>
    private static UserText Joined(IEnumerable<UserText> pieces) =>
        pieces.Aggregate((joined, piece) => Code("memory.join", joined, piece));

    private static object Sources(IReadOnlyList<string> sources) =>
        sources.Count == 0 ? string.Empty : Code("memory.sources", Count("commits", sources.Count), List(sources));

    private static UserText Phase(RunPhase phase) => Named("memory.phase.", phase);

    private static UserText Status(TaskStatusChangedPayload? last) =>
        last is null ? Code("memory.status.never-started") : Named("memory.status.", last.To);

    private static UserText TaskKind(WorkTaskKind kind) => Named("memory.task-kind.", kind);

    private static UserText Decision(VerdictDecision decision) => Named("memory.decision.", decision);

    private static UserText Mode(DebateMode mode) => Named("memory.mode.", mode);

    private static UserText Outcome(DebateOutcome outcome) => Named("memory.outcome.", outcome);

    private static UserText Rule(string rule) => rule switch
    {
        JudgingPanel.Single => Code("memory.rule.single"),
        JudgingPanel.Agree => Code("memory.rule.agree"),
        JudgingPanel.Disagree => Code("memory.rule.disagree"),
        JudgingPanel.Invalid => Code("memory.rule.invalid"),
        JudgingPanel.User => Code("memory.rule.user"),
        DebateClosedPayload.AbortedRule => Code("memory.rule.aborted"),
        _ => Code("memory.rule.unknown"),
    };

    private static UserText Subject(DebateOpenedPayload opened) => (opened.Subject, opened.SubjectRef) switch
    {
        (DebateSubjectKind.Commit, { } commit) when DerivationTexts.IsLabel(commit) => Code("memory.debate.subject-commit", commit),
        (DebateSubjectKind.Run, { } run) when DerivationTexts.IsLabel(run) => Code("memory.debate.subject-run", run),
        (DebateSubjectKind.Verdict, { } verdict) when DerivationTexts.IsLabel(verdict) => Code("memory.debate.subject-verdict", verdict),
        (DebateSubjectKind.None, _) => Code("memory.raw", string.Empty),
        _ => Code("memory.debate.subject-unnamed"),
    };

    private static UserText VerdictLine((string Id, VerdictIssuedPayload? Verdict) verdict) =>
        verdict.Verdict is { } issued
            ? Code("memory.debate.verdict-line", verdict.Id, Decision(issued.Decision), issued.Valid ? Code("memory.validity.valid") : Code("memory.validity.invalid"))
            : Code("memory.debate.verdict-missing", verdict.Id);

    /// <summary>
    /// The block reason of a task as the engine writes it, said as a piece; null for one that is not quoted. The same reasons as
    /// <see cref="DerivationTexts"/>.
    /// </summary>
    private static UserText? Block(string? reason)
    {
        foreach (var (limited, code) in new[] { ("RateLimitedUntil(", "memory.block.rate-limit"), ("RateLimitedUntilEstimated(", "memory.block.rate-limit-estimated") })
        {
            if (reason is not null
                && reason.StartsWith(limited, StringComparison.Ordinal)
                && reason.EndsWith(')')
                && DateTimeOffset.TryParseExact(reason[limited.Length..^1], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
            {
                return Code(code, When(until));
            }
        }

        return reason switch
        {
            "Interrupted" => Code("memory.block.interrupted"),
            "ProviderUnavailable" => Code("memory.block.provider-unavailable"),
            "AwaitingHuman" => Code("memory.block.awaiting-human"),
            _ => null,
        };
    }

    /// <summary>The piece of an enumeration value: <c>prefix</c> and its name in lowercase words joined by <c>-</c>; a value without a name is shown as it is.</summary>
    private static UserText Named<T>(string prefix, T value)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? Code(prefix + Kebab(value.ToString())) : Code("memory.raw", value.ToString());

    /// <summary><c>WaitingForQuota</c> as <c>waiting-for-quota</c>.</summary>
    internal static string Kebab(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append('-');
            }

            text.Append(char.ToLowerInvariant(c));
        }

        return text.ToString();
    }
}
