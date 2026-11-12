using System.Globalization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>The names of the phases of a run in the language of the interface, with the tone of their state dot.</summary>
public static class RunPhaseText
{
    public static string Label(RunPhase phase) => phase switch
    {
        RunPhase.Running => Texts.Get("Orchestrator_Phase_Running"),
        RunPhase.Paused => Texts.Get("Orchestrator_Phase_Paused"),
        RunPhase.WaitingForQuota => Texts.Get("Orchestrator_Phase_WaitingForQuota"),
        RunPhase.AwaitingHuman => Texts.Get("Orchestrator_Phase_AwaitingHuman"),
        RunPhase.Interrupted => Texts.Get("Orchestrator_Phase_Interrupted"),
        RunPhase.Succeeded => Texts.Get("Orchestrator_Phase_Succeeded"),
        RunPhase.Failed => Texts.Get("Orchestrator_Phase_Failed"),
        RunPhase.Blocked => Texts.Get("Orchestrator_Phase_Blocked"),
        RunPhase.Cancelled => Texts.Get("Orchestrator_Phase_Cancelled"),
        _ => phase.ToString(),
    };

    public static Tone ToneOf(RunPhase phase) => phase switch
    {
        RunPhase.Running => Tone.Active,
        RunPhase.Paused or RunPhase.WaitingForQuota or RunPhase.AwaitingHuman or RunPhase.Interrupted => Tone.Warning,
        RunPhase.Succeeded => Tone.Success,
        RunPhase.Failed or RunPhase.Blocked => Tone.Error,
        _ => Tone.Neutral,
    };

    /// <summary>The phases after which the run does nothing more.</summary>
    public static bool IsFinal(RunPhase phase) => phase is RunPhase.Succeeded or RunPhase.Failed or RunPhase.Cancelled;
}

/// <summary>
/// The structured block reasons of a task (docs/13 section 3.7): <c>Interrupted</c>, <c>ProviderUnavailable</c>,
/// <c>RateLimitedUntil(t)</c> and <c>AwaitingHuman</c>. An interrupted task is not a blocked one: the app stopped, the
/// graph is fine and resuming continues it.
/// </summary>
public static class BlockReasonText
{
    /// <summary>The block reason of a task that waits for the answer to a question of its agent (<c>BlockKind.AwaitingAnswer</c>).</summary>
    public const string AwaitingAnswerReason = "AwaitingAnswer";

    /// <summary>The instant a <c>RateLimitedUntil(t)</c> reason waits for; null for any other reason or one that cannot be read.</summary>
    public static DateTimeOffset? RateLimitedUntil(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || !Is(reason.Trim(), "RateLimitedUntil"))
        {
            return null;
        }

        var text = reason.Trim();
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        return open >= 0 && close > open
            && DateTimeOffset.TryParse(text[(open + 1)..close], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
            ? until
            : null;
    }

    public static string Label(string? reason, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Texts.Get("Orchestrator_Block_Blocked");
        }

        var text = reason.Trim();
        if (Is(text, "Interrupted"))
        {
            return Texts.Get("Orchestrator_Block_Interrupted");
        }

        if (Is(text, "ProviderUnavailable"))
        {
            return Texts.Get("Orchestrator_Block_ProviderUnavailable");
        }

        if (Is(text, "AwaitingHuman"))
        {
            return Texts.Get("Orchestrator_Block_AwaitingHuman");
        }

        // Phase 13 (docs/17 D-29): a task whose agent asked waits for the answer; it is not a block to retry.
        if (Is(text, AwaitingAnswerReason))
        {
            return Texts.Get("Text_question_awaiting_answer");
        }

        if (Is(text, "RateLimitedUntil"))
        {
            var open = text.IndexOf('(', StringComparison.Ordinal);
            var close = text.LastIndexOf(')');
            if (open >= 0 && close > open
                && DateTimeOffset.TryParse(text[(open + 1)..close], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
            {
                return Texts.Format("Monitor_AgentStatus_WaitingForQuota", TimeZoneInfo.ConvertTime(until, zone).ToString("HH:mm", CultureInfo.InvariantCulture));
            }

            return Texts.Get("Monitor_AgentStatus_WaitingForQuotaNoTime");
        }

        return Texts.Format("Orchestrator_Block_Other", text);
    }

    private static bool Is(string text, string reason) => text.StartsWith(reason, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One line, in the language of the interface, for what an agent did; null for what is not worth a line (partial text).</summary>
public static class AgentActivityText
{
    private const int MaxLength = 160;

    /// <summary>
    /// The line for an event of an agent, with what the agent wrote cleaned: the control and direction (bidi) characters are taken out
    /// by the rule of the questions (<see cref="QuestionRules"/>), so a line of the activity cannot reorder the text around it nor
    /// move the cursor of a terminal.
    /// </summary>
    public static string? Describe(AgentEvent agentEvent, TimeZoneInfo zone) => Clean(DescribeRaw(agentEvent, zone));

    /// <summary>The text without the characters that control the layout: control, line and paragraph separators and bidi (U+202A to U+202E, U+2066 to U+2069, U+200E, U+200F, U+061C).</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text) || !(QuestionRules.HasControlCharacter(text) || QuestionRules.HasBidiCharacter(text)))
        {
            return text;
        }

        return string.Concat(text.Where(c =>
        {
            var one = c.ToString();
            return !QuestionRules.HasControlCharacter(one) && !QuestionRules.HasBidiCharacter(one);
        }));
    }

    private static string? DescribeRaw(AgentEvent agentEvent, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        ArgumentNullException.ThrowIfNull(zone);
        return agentEvent switch
        {
            SessionStarted e => e.Model is null ? Texts.Get("Orchestrator_Activity_SessionStarted") : Texts.Format("Orchestrator_Activity_SessionStartedModel", e.Model),
            MessageEmitted { IsPartial: true } => null,
            MessageEmitted e => FirstLine(e.Text) is { Length: > 0 } line ? Shorten(line) : null,
            ToolInvoked e => Shorten(Texts.Format("Orchestrator_Activity_ToolUsed", e.Tool, FirstLine(e.Summary))),
            ToolCompleted e => e.Succeeded ? Texts.Format("Orchestrator_Activity_ToolDone", e.Tool) : Texts.Format("Orchestrator_Activity_ToolFailed", e.Tool),
            PermissionRequested e => Shorten(Texts.Format("Orchestrator_Activity_Permission", e.Tool, FirstLine(e.Description))),
            UsageReported e => Texts.Format("Orchestrator_Activity_Usage", Tokens(e.Usage.InputTokens), Tokens(e.Usage.OutputTokens)),
            RateLimitSignaled e => e.ResetsAt is { } at
                ? Texts.Format("Orchestrator_Activity_RateLimitUntil", e.Status, TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture))
                : Texts.Format("Orchestrator_Activity_RateLimit", e.Status),
            QuotaReported e => Texts.Format("Orchestrator_Activity_Quota", string.Join(", ", e.Windows.Select(w => $"{QuotaWindowText.Name(w.Name)} {Percent(w.Utilization)}"))),

            // The occupancy of each step goes to its task's context line, not to the activity (docs/14 D-12).
            ContextObserved => null,
            CompactionObserved e => Compaction(e),
            RunFinished e => Texts.Format("Orchestrator_Activity_Finished", Outcome(e.Outcome.Status)),
            _ => null,
        };
    }

    public static string Outcome(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Succeeded => Texts.Get("Orchestrator_Outcome_Succeeded"),
        AgentRunStatus.Failed => Texts.Get("Orchestrator_Outcome_Failed"),
        AgentRunStatus.Cancelled => Texts.Get("Orchestrator_Outcome_Cancelled"),
        AgentRunStatus.LimitReached => Texts.Get("Orchestrator_Outcome_LimitReached"),
        AgentRunStatus.RateLimited => Texts.Get("Orchestrator_Outcome_RateLimited"),
        _ => status.ToString(),
    };

    /// <summary>A number with its provenance: an estimate is never shown as a measurement, a missing value never as zero.</summary>
    private static string Tokens(MetricValue<long> value) => value.Provenance switch
    {
        MetricProvenance.Exact when value.Value is { } exact => MetricText.Integer(exact),
        MetricProvenance.Estimated when value.Value is { } estimate => Texts.Format("Orchestrator_Activity_TokensEstimated", MetricText.Integer(estimate)),
        _ => Texts.Get("Orchestrator_Activity_NotExposed"),
    };

    /// <summary>A compaction of the CLI: what it may have lost (master prompt, section 10), with the tokens it had before.</summary>
    private static string Compaction(CompactionObserved e)
    {
        var before = MetricText.Tokens(e.TokensBefore);
        var tokens = before.Value is { } value ? Texts.Format("Orchestrator_Activity_TokensBefore", value, before.Origin) : Texts.Format("Orchestrator_Activity_TokensBeforeUnknown", before.Origin);
        return Shorten(Texts.Format("Orchestrator_Activity_Compaction", e.Trigger, tokens));
    }

    private static string Percent(MetricValue<double> value) => value.Value is { } fraction
        ? fraction.ToString("P0", Texts.Current.Culture) + (value.Provenance == MetricProvenance.Estimated ? Texts.Get("Orchestrator_Activity_Estimated") : string.Empty)
        : Texts.Get("Orchestrator_Activity_NotExposedFeminine");

    private static string FirstLine(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static string Shorten(string text) => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}

/// <summary>
/// The windows of the quota of an account, by the name the provider gives them (Claude's <c>five_hour</c> and
/// <c>seven_day</c>), in the language of the interface as the «Consumo» screen and the status panel of the tray name them
/// («5 horas», «7 días»; <c>QuotaViewModel</c>); an unknown window is shown as the provider names it.
/// </summary>
public static class QuotaWindowText
{
    public static string Name(string window) => window switch
    {
        "five_hour" => Texts.Get("Orchestrator_Quota_FiveHour"),
        "seven_day" => Texts.Get("Orchestrator_Quota_SevenDay"),
        "seven_day_opus" => Texts.Get("Orchestrator_Quota_SevenDayOpus"),
        "seven_day_sonnet" => Texts.Get("Orchestrator_Quota_SevenDaySonnet"),
        _ => window,
    };
}

/// <summary>
/// The checks of the deterministic auditor of the orchestrator (docs/15 D-33), by the stable code of their findings, in
/// the language of the interface; an unknown code is shown as it is.
/// </summary>
public static class AuditFindingText
{
    public static string Check(string code) => code switch
    {
        "orchestrator.assignment" => Texts.Get("Orchestrator_Audit_Assignment"),
        "orchestrator.dependency-order" => Texts.Get("Orchestrator_Audit_DependencyOrder"),
        "orchestrator.instructions" => Texts.Get("Orchestrator_Audit_Instructions"),
        "orchestrator.review-omitted" => Texts.Get("Orchestrator_Audit_ReviewOmitted"),
        "orchestrator.permissions" => Texts.Get("Orchestrator_Audit_Permissions"),
        "orchestrator.approval-missing" => Texts.Get("Orchestrator_Audit_ApprovalMissing"),
        "orchestrator.integration-unverified" => Texts.Get("Orchestrator_Audit_IntegrationUnverified"),
        "orchestrator.replay" => Texts.Get("Orchestrator_Audit_Replay"),
        "orchestrator.premature-finish" => Texts.Get("Orchestrator_Audit_PrematureFinish"),
        _ => code,
    };
}

/// <summary>Labels of evidence kinds and origins in the language of the interface.</summary>
public static class EvidenceText
{
    public static string Kind(EvidenceKind kind) => kind switch
    {
        EvidenceKind.TestRun => Texts.Get("Orchestrator_Evidence_TestRun"),
        EvidenceKind.BuildLog => Texts.Get("Orchestrator_Evidence_BuildLog"),
        EvidenceKind.CommandOutput => Texts.Get("Orchestrator_Evidence_CommandOutput"),
        EvidenceKind.CodeDiff => Texts.Get("Orchestrator_Evidence_CodeDiff"),
        EvidenceKind.Document => Texts.Get("Orchestrator_Evidence_Document"),
        EvidenceKind.AgentStatement => Texts.Get("Orchestrator_Evidence_AgentStatement"),
        EvidenceKind.HumanNote => Texts.Get("Orchestrator_Evidence_HumanNote"),
        _ => kind.ToString(),
    };

    public static string Origin(EvidenceOrigin origin) => origin switch
    {
        EvidenceOrigin.CapturedBySystem => Texts.Get("Orchestrator_Evidence_CapturedBySystem"),
        EvidenceOrigin.ReportedByAgent => Texts.Get("Orchestrator_Evidence_ReportedByAgent"),
        EvidenceOrigin.ProvidedByHuman => Texts.Get("Orchestrator_Evidence_ProvidedByHuman"),
        _ => origin.ToString(),
    };

    public static string? Tests(TestRunSummary? summary) => summary is null
        ? null
        : Texts.Format("Orchestrator_Evidence_Tests", summary.Passed.ToString(CultureInfo.InvariantCulture), summary.Failed.ToString(CultureInfo.InvariantCulture), summary.Skipped.ToString(CultureInfo.InvariantCulture));
}

/// <summary>The tokens a CLI reported at the end of an attempt, as the monitor shows them for its task (docs/18 P3).</summary>
public static class MonitorTokens
{
    /// <summary>
    /// «Tokens del último intento: 61.222 de entrada y 50 de salida · exacto · informado por el programa del agente», with the weakest origin of
    /// the two figures; a figure that is not there says so instead of a zero. They are what the CLI reports: for some, the
    /// total of the session; the usage of the attempt itself is in «Consumo».
    /// </summary>
    public static string Line(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var input = MetricText.Tokens(usage.InputTokens);
        var output = MetricText.Tokens(usage.OutputTokens);
        if (input.Value is null && output.Value is null)
        {
            return Texts.Format("Orchestrator_Tokens_Unknown", MetricText.Origin(MetricOrigins.Weakest(usage.InputTokens.Origin, usage.OutputTokens.Origin)));
        }

        // The origin of the figures that are there: one that is missing shows a dash, and does not make the other «sin dato».
        var known = new[] { usage.InputTokens, usage.OutputTokens }.Where(v => v.HasValue && v.Origin.CarriesValue()).Select(v => v.Origin).ToArray();
        var origin = MetricText.Origin(MetricOrigins.Weakest(known));
        return Texts.Format("Orchestrator_Tokens_Line", input.Shown, output.Shown, origin);
    }
}
