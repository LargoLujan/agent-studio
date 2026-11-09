using System.Globalization;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Desktop.Observability;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Questions;

namespace AgentStudio.Desktop.Orchestrator.Monitor;

/// <summary>Spanish names of the phases of a run, with the tone of their state dot.</summary>
public static class RunPhaseText
{
    public static string Label(RunPhase phase) => phase switch
    {
        RunPhase.Running => "En ejecución",
        RunPhase.Paused => "En pausa",
        RunPhase.WaitingForQuota => "Esperando cuota",
        RunPhase.AwaitingHuman => "Esperando a una persona",
        RunPhase.Interrupted => "Interrumpida",
        RunPhase.Succeeded => "Completada",
        RunPhase.Failed => "Fallida",
        RunPhase.Blocked => "Bloqueada",
        RunPhase.Cancelled => "Cancelada",
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
            return "Bloqueada";
        }

        var text = reason.Trim();
        if (Is(text, "Interrupted"))
        {
            return "Interrumpida";
        }

        if (Is(text, "ProviderUnavailable"))
        {
            return "Bloqueada · proveedor no disponible";
        }

        if (Is(text, "AwaitingHuman"))
        {
            return "Bloqueada · espera a una persona";
        }

        // Phase 13 (docs/17 D-29): a task whose agent asked waits for the answer; it is not a block to retry.
        if (Is(text, AwaitingAnswerReason))
        {
            return Localization.Texts.Get("Text_question_awaiting_answer");
        }

        if (Is(text, "RateLimitedUntil"))
        {
            var open = text.IndexOf('(', StringComparison.Ordinal);
            var close = text.LastIndexOf(')');
            if (open >= 0 && close > open
                && DateTimeOffset.TryParse(text[(open + 1)..close], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
            {
                return $"Esperando cuota hasta las {TimeZoneInfo.ConvertTime(until, zone):HH:mm}";
            }

            return "Esperando cuota";
        }

        return $"Bloqueada · {text}";
    }

    private static bool Is(string text, string reason) => text.StartsWith(reason, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One line, in Spanish, for what an agent did; null for what is not worth a line (partial text).</summary>
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
            SessionStarted e => e.Model is null ? "Sesión iniciada" : $"Sesión iniciada · {e.Model}",
            MessageEmitted { IsPartial: true } => null,
            MessageEmitted e => FirstLine(e.Text) is { Length: > 0 } line ? Shorten(line) : null,
            ToolInvoked e => Shorten($"Usa {e.Tool}: {FirstLine(e.Summary)}"),
            ToolCompleted e => e.Succeeded ? $"{e.Tool}: terminada" : $"{e.Tool}: falló",
            PermissionRequested e => Shorten($"Pide permiso para {e.Tool}: {FirstLine(e.Description)}"),
            UsageReported e => $"Uso: {Tokens(e.Usage.InputTokens)} tokens de entrada, {Tokens(e.Usage.OutputTokens)} de salida",
            RateLimitSignaled e => e.ResetsAt is { } at
                ? $"Límite de uso del proveedor ({e.Status}) hasta las {TimeZoneInfo.ConvertTime(at, zone):HH:mm}"
                : $"Límite de uso del proveedor ({e.Status})",
            QuotaReported e => "Cuota: " + string.Join(", ", e.Windows.Select(w => $"{QuotaWindowText.Name(w.Name)} {Percent(w.Utilization)}")),

            // The occupancy of each step goes to its task's context line, not to the activity (docs/14 D-12).
            ContextObserved => null,
            CompactionObserved e => Compaction(e),
            RunFinished e => $"Agente terminado: {Outcome(e.Outcome.Status)}",
            _ => null,
        };
    }

    public static string Outcome(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Succeeded => "correcto",
        AgentRunStatus.Failed => "fallido",
        AgentRunStatus.Cancelled => "cancelado",
        AgentRunStatus.LimitReached => "límite alcanzado",
        AgentRunStatus.RateLimited => "límite de uso del proveedor",
        _ => status.ToString(),
    };

    /// <summary>A number with its provenance: an estimate is never shown as a measurement, a missing value never as zero.</summary>
    private static string Tokens(MetricValue<long> value) => value.Provenance switch
    {
        MetricProvenance.Exact when value.Value is { } exact => exact.ToString(CultureInfo.InvariantCulture),
        MetricProvenance.Estimated when value.Value is { } estimate => $"~{estimate.ToString(CultureInfo.InvariantCulture)} (estimado)",
        _ => "no expuesto",
    };

    /// <summary>A compaction of the CLI: what it may have lost (master prompt, section 10), with the tokens it had before.</summary>
    private static string Compaction(CompactionObserved e)
    {
        var before = MetricText.Tokens(e.TokensBefore);
        var tokens = before.Value is { } value ? $"{value} tokens antes · {before.Origin}" : $"tokens de antes: {before.Origin}";
        return Shorten($"El CLI compactó el contexto ({e.Trigger}): {tokens}");
    }

    private static string Percent(MetricValue<double> value) => value.Value is { } fraction
        ? fraction.ToString("P0", CultureInfo.GetCultureInfo("es-ES")) + (value.Provenance == MetricProvenance.Estimated ? " (estimado)" : string.Empty)
        : "no expuesta";

    private static string FirstLine(string? text) =>
        (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static string Shorten(string text) => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}

/// <summary>
/// The windows of the quota of an account, by the name the provider gives them (Claude's <c>five_hour</c> and
/// <c>seven_day</c>), in Spanish as the «Consumo» screen and the status panel of the tray name them («5 horas», «7 días»;
/// <c>QuotaViewModel</c>); an unknown window is shown as the provider names it.
/// </summary>
public static class QuotaWindowText
{
    public static string Name(string window) => window switch
    {
        "five_hour" => "5 horas",
        "seven_day" => "7 días",
        "seven_day_opus" => "7 días · Opus",
        "seven_day_sonnet" => "7 días · Sonnet",
        _ => window,
    };
}

/// <summary>
/// The checks of the deterministic auditor of the orchestrator (docs/15 D-33), by the stable code of their findings, in
/// Spanish; an unknown code is shown as it is.
/// </summary>
public static class AuditFindingText
{
    public static string Check(string code) => code switch
    {
        "orchestrator.assignment" => "Asignación de tareas",
        "orchestrator.dependency-order" => "Orden de las dependencias",
        "orchestrator.instructions" => "Instrucciones completas",
        "orchestrator.review-omitted" => "Revisión obligatoria omitida",
        "orchestrator.permissions" => "Permisos respetados",
        "orchestrator.approval-missing" => "Aprobación sin pedir",
        "orchestrator.integration-unverified" => "Integración sin verificar",
        "orchestrator.replay" => "Reproducción del registro",
        "orchestrator.premature-finish" => "Final prematuro",
        _ => code,
    };
}

/// <summary>Spanish labels of evidence kinds and origins.</summary>
public static class EvidenceText
{
    public static string Kind(EvidenceKind kind) => kind switch
    {
        EvidenceKind.TestRun => "Pruebas",
        EvidenceKind.BuildLog => "Compilación",
        EvidenceKind.CommandOutput => "Salida de un comando",
        EvidenceKind.CodeDiff => "Diff",
        EvidenceKind.Document => "Documento",
        EvidenceKind.AgentStatement => "Declaración del agente",
        EvidenceKind.HumanNote => "Nota de una persona",
        _ => kind.ToString(),
    };

    public static string Origin(EvidenceOrigin origin) => origin switch
    {
        EvidenceOrigin.CapturedBySystem => "capturada por el sistema",
        EvidenceOrigin.ReportedByAgent => "declarada por el agente",
        EvidenceOrigin.ProvidedByHuman => "aportada por una persona",
        _ => origin.ToString(),
    };

    public static string? Tests(TestRunSummary? summary) => summary is null
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"{summary.Passed} correctas, {summary.Failed} fallidas, {summary.Skipped} omitidas");
}

/// <summary>The tokens a CLI reported at the end of an attempt, as the monitor shows them for its task (docs/18 P3).</summary>
public static class MonitorTokens
{
    /// <summary>
    /// «Tokens del último intento: 61.222 de entrada y 50 de salida · exacto · informado por el CLI», with the weakest origin of
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
            return "Tokens del último intento: " + MetricText.Origin(MetricOrigins.Weakest(usage.InputTokens.Origin, usage.OutputTokens.Origin));
        }

        // The origin of the figures that are there: one that is missing shows a dash, and does not make the other «sin dato».
        var known = new[] { usage.InputTokens, usage.OutputTokens }.Where(v => v.HasValue && v.Origin.CarriesValue()).Select(v => v.Origin).ToArray();
        var origin = MetricText.Origin(MetricOrigins.Weakest(known));
        return $"Tokens del último intento: {input.Shown} de entrada y {output.Shown} de salida · {origin}";
    }
}
