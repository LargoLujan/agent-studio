using System.Globalization;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Providers.Cli.Claude;

/// <summary>
/// Translates Claude Code <c>stream-json</c> lines into normalised events. One instance per run: it remembers tool
/// names by id, the model announced by <c>system/init</c>, the steps already observed and the model of the last one.
/// Unknown event types and non-JSON lines are ignored.
/// </summary>
/// <remarks>
/// Usage (docs/14 section 3.1 and D-09, verified on the captures of 2.1.290):
/// <list type="bullet">
/// <item>Each step (one model call of the main loop) arrives as several <c>assistant</c> events with the same
/// <c>message.id</c> and usage. Its prompt (input plus cache read plus cache created) is the occupancy of the context:
/// one <see cref="ContextObserved"/> per id, with the model of that step. The window is only known when <c>result</c>
/// arrives, so during the run it is missing (D-10: a window seen earlier is applied by whoever shows it). The steps of a
/// subagent (<c>parent_tool_use_id</c>) are another context and are not observed.</item>
/// <item><c>result.usage</c> sums the steps of the main loop. Its window is the one of the model of the last step in
/// <c>modelUsage</c>, never the one of <c>init</c>, which can announce an auxiliary model (D-13). <c>modelUsage</c> gives
/// the breakdown per model, auxiliary models included; as in the totals, the input of a model is its whole prompt.</item>
/// <item>A figure the CLI offers but that did not arrive (or arrived out of range) is missing, never zero.</item>
/// <item><c>system/compact_boundary</c> is a compaction (documented in the SDK types; no recorded run compacted).</item>
/// </list>
/// The class of the end of an attempt (docs/16 D-41, F8-1) comes only from the signals of <see cref="EndClassSignals"/>, each
/// in a capture of 2.1.290; the ones with no real capture yet are synthetic and say so
/// (<c>Fixtures/claude-code/2.1.290/limits/README.md</c>). It refines the status and never changes it; a turn that completed,
/// and a failure the parser does not recognise, have none (<see cref="AgentRunOutcome.End"/> is null): completed and «otra».
/// The first that applies, in this order:
/// <list type="number">
/// <item>A budget: the subtypes <c>error_max_turns</c> and <c>error_max_budget_usd</c>.</item>
/// <item>The context: <c>terminal_reason</c> <c>prompt_too_long</c>.</item>
/// <item>The session: the structured error <c>authentication_failed</c> of the last message of the main loop.</item>
/// <item>A window that ran out: the last <c>rate_limit_event</c> says <c>rejected</c>, with the window
/// (<c>rateLimitType</c>, a token, kept as it is) and its <c>resetsAt</c>. Only <c>rejected</c>: <c>allowed_warning</c> is a
/// window nearly spent.</item>
/// <item>An overload: the error <c>overloaded</c>, or the API's 529.</item>
/// <item>The network: the documented texts of a connection that failed, in the <c>result</c> of a turn that did not
/// complete.</item>
/// </list>
/// </remarks>
public sealed class ClaudeStreamJsonParser(TimeProvider clock)
{
    private const int SummaryLimit = 120;
    private const string StepSource = "claude assistant.message.usage";
    private const string UsageSource = "claude result.usage";
    private const string ModelUsageSource = "claude result.modelUsage";
    private const string CompactionSource = "claude compact_boundary";

    // The signals of the class of the end (docs/16 D-41), each in a capture of 2.1.290 (see EndClassSignals).
    private const string RejectedWindow = "rejected";
    private const string SignedOutError = "authentication_failed";
    private const string OverloadedError = "overloaded";
    private const string ContextExhaustedReason = "prompt_too_long";
    private const int OverloadedApiStatus = 529;
    private static readonly (string Subtype, AttemptBudgetKind Budget)[] BudgetSubtypes =
        [("error_max_turns", AttemptBudgetKind.Turns), ("error_max_budget_usd", AttemptBudgetKind.Cost)];

    // The documented texts of a connection that failed (2.1.227 and later), as the result of a turn that did not complete.
    private static readonly string[] NetworkTexts = ["Unable to connect to API", "Request timed out"];

    // Valid range of DateTimeOffset.FromUnixTimeSeconds; a reset in year 1 or 9999 means nothing useful.
    private const long MinUnixSeconds = 0;
    private const long MaxUnixSeconds = 253402300799;
    private static readonly string[] SummaryKeys = ["file_path", "path", "command", "pattern", "url", "query", "description", "skill"];

    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _steps = new(StringComparer.Ordinal);
    private string? _model;
    private string? _lastStepModel;

    // The window the last rate_limit_event said ran out (null once one says otherwise), and the structured error of the last
    // message of the main loop (null when it had none).
    private (string? LimitType, DateTimeOffset? ResetsAt)? _rejectedWindow;
    private string? _lastError;

    /// <summary>
    /// Every signal the class of the end is deduced from, as the CLI writes it (a JSON key and its value without spaces, or
    /// a text of the <c>result</c>). Each one is in a capture of 2.1.290, real or synthetic: the test of the captures fails
    /// for a signal that is in none, so no class is deduced from a text nobody captured.
    /// </summary>
    public static IReadOnlyList<string> EndClassSignals { get; } =
    [
        $"\"status\":\"{RejectedWindow}\"",
        .. BudgetSubtypes.Select(b => $"\"subtype\":\"{b.Subtype}\""),
        $"\"terminal_reason\":\"{ContextExhaustedReason}\"",
        $"\"error\":\"{SignedOutError}\"",
        $"\"error\":\"{OverloadedError}\"",
        "\"api_error_status\":" + OverloadedApiStatus.ToString(CultureInfo.InvariantCulture),
        .. NetworkTexts,
    ];

    /// <summary>Lines that were not valid JSON objects.</summary>
    public int IgnoredLines { get; private set; }

    public IReadOnlyList<AgentEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                IgnoredLines++;
                return [];
            }

            return Dispatch(document.RootElement);
        }
        catch (JsonException)
        {
            IgnoredLines++;
            return [];
        }
    }

    private List<AgentEvent> Dispatch(JsonElement root)
    {
        var events = new List<AgentEvent>();
        var at = clock.GetUtcNow();
        switch (Str(root, "type"))
        {
            case "system" when Str(root, "subtype") == "init":
                _model = Str(root, "model");
                events.Add(new SessionStarted(at, Str(root, "session_id") ?? string.Empty, _model));
                break;
            case "system" when Str(root, "subtype") == "compact_boundary":
                ParseCompaction(root, at, events);
                break;
            case "assistant":
                ParseAssistant(root, at, events);
                break;
            case "user":
                ParseUser(root, at, events);
                break;
            case "rate_limit_event":
                ParseRateLimit(root, at, events);
                break;
            case "result":
                ParseResult(root, at, events);
                break;
        }

        return events;
    }

    private void ParseAssistant(JsonElement root, DateTimeOffset at, List<AgentEvent> events)
    {
        ObserveStep(root, at, events);
        if (!IsSubagent(root))
        {
            // The structured error of the message (SDKAssistantMessageError): only the last one of the main loop ends the turn.
            _lastError = Str(root, "error") is { } error && IsToken(error, 64) ? error : null;
        }

        foreach (var block in Blocks(root))
        {
            switch (Str(block, "type"))
            {
                case "text" when !string.IsNullOrEmpty(Str(block, "text")):
                    events.Add(new MessageEmitted(at, Str(block, "text")!, false));
                    break;
                case "tool_use":
                    var name = Str(block, "name") ?? "unknown";
                    if (Str(block, "id") is { } id)
                    {
                        _toolNames[id] = name;
                    }

                    events.Add(new ToolInvoked(at, name, Summarise(block)));
                    break;
            }
        }
    }

    /// <summary>
    /// The first event of a step of the main loop: its prompt (input plus cache read plus cache created) with the model
    /// that served it. The later events of the same message id repeat it and are not observed again.
    /// </summary>
    private void ObserveStep(JsonElement root, DateTimeOffset at, List<AgentEvent> events)
    {
        if (IsSubagent(root))
        {
            return; // a subagent: another context
        }

        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || Str(message, "id") is not { Length: > 0 } id
            || !message.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var input = Long(usage, "input_tokens");
        var created = Long(usage, "cache_creation_input_tokens");
        var read = Long(usage, "cache_read_input_tokens");
        if ((input is null && created is null && read is null) || !_steps.Add(id))
        {
            return;
        }

        var model = ModelName(Str(message, "model"));
        _lastStepModel = model ?? _lastStepModel;
        var prompt = SafeSum(input, created, read) is { } total
            ? MetricValue<long>.Exact(total, StepSource)
            : MetricValue<long>.Missing(StepSource + " con tokens fuera de rango");
        var window = MetricValue<long>.Missing($"claude aún no informa de la ventana de {model ?? "este modelo"}: llega con el resultado");
        events.Add(new ContextObserved(at, model, prompt, window));
    }

    private static void ParseCompaction(JsonElement root, DateTimeOffset at, List<AgentEvent> events)
    {
        var hasMetadata = root.TryGetProperty("compact_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object;
        var trigger = hasMetadata && Str(metadata, "trigger") is { } t && IsToken(t, 32) ? t : "unknown";
        var before = hasMetadata && Long(metadata, "pre_tokens") is { } tokens && tokens >= 0
            ? MetricValue<long>.Exact(tokens, CompactionSource)
            : MetricValue<long>.Missing(CompactionSource + " sin pre_tokens");
        events.Add(new CompactionObserved(at, trigger, before));
    }

    private void ParseUser(JsonElement root, DateTimeOffset at, List<AgentEvent> events)
    {
        foreach (var block in Blocks(root))
        {
            if (Str(block, "type") != "tool_result")
            {
                continue;
            }

            var tool = Str(block, "tool_use_id") is { } id && _toolNames.TryGetValue(id, out var name) ? name : "unknown";
            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            events.Add(new ToolCompleted(at, tool, !isError));
        }
    }

    private void ParseRateLimit(JsonElement root, DateTimeOffset at, List<AgentEvent> events)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var windows = new List<QuotaWindow>();
        if (info.TryGetProperty("unifiedWindows", out var unified) && unified.ValueKind == JsonValueKind.Object)
        {
            foreach (var window in unified.EnumerateObject())
            {
                if (window.Value.ValueKind == JsonValueKind.Object && Num(window.Value, "utilization") is { } utilization)
                {
                    windows.Add(new QuotaWindow(
                        window.Name,
                        MetricValue<double>.Exact(utilization, "claude rate_limit_event"),
                        Unix(window.Value, "resetsAt")));
                }
            }
        }

        if (windows.Count > 0)
        {
            events.Add(new QuotaReported(at, windows));
        }

        // rateLimitType names the window the event is about (the five-hour, the weekly...), kept as the CLI writes it.
        var status = Str(info, "status");
        var limitType = Str(info, "rateLimitType") is { } type && IsToken(type, 32) ? type : null;
        var resetsAt = Unix(info, "resetsAt");
        _rejectedWindow = status == RejectedWindow ? (limitType, resetsAt) : null;
        if (status is not null && status != "allowed")
        {
            events.Add(new RateLimitSignaled(at, status, resetsAt, limitType));
        }
    }

    private void ParseResult(JsonElement root, DateTimeOffset at, List<AgentEvent> events)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            events.Add(new UsageReported(at, BuildUsage(root, usage)));
        }

        events.Add(new RunFinished(at, BuildOutcome(root)));
    }

    private TokenUsage BuildUsage(JsonElement root, JsonElement usage)
    {
        var input = Long(usage, "input_tokens");
        var created = Long(usage, "cache_creation_input_tokens");
        var read = Long(usage, "cache_read_input_tokens");
        var output = Long(usage, "output_tokens");

        var inputMetric = input is null && created is null && read is null
            ? MetricValue<long>.Missing("claude result.usage sin tokens de entrada")
            : SafeSum(input, created, read) is { } total
                ? MetricValue<long>.Exact(total, UsageSource)
                : MetricValue<long>.Missing("claude result.usage con tokens de entrada fuera de rango");
        var cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var costValue)
            ? MetricValue<decimal>.Estimated(costValue, "estimación del CLI")
            : MetricValue<decimal>.Missing("claude no informa del coste");

        var perModel = PerModel(root);
        var window = WindowOfLastStep(perModel)
            ?? MetricValue<long>.Missing("claude no informa de la ventana del modelo de los pasos");
        var reasoning = usage.TryGetProperty("output_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object && Long(details, "thinking_tokens") is { } thinking
            ? MetricValue<long>.Exact(thinking, UsageSource + ".output_tokens_details")
            : MetricValue<long>.Missing("claude result.usage sin tokens de razonamiento");

        return new TokenUsage(inputMetric, Tokens(output, UsageSource, "sin tokens de salida"), Tokens(read, UsageSource, "sin caché leída"), window, cost)
        {
            CacheWriteInputTokens = Tokens(created, UsageSource, "sin caché creada"),
            ReasoningOutputTokens = reasoning,
            PerModel = perModel,
            Turns = root.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number && turns.TryGetInt32(out var count) && count >= 0 ? count : null,
        };
    }

    /// <summary>
    /// The window of the model that served the last step (docs/14 D-13), never the one <c>init</c> announced. A dated key
    /// (<c>claude-haiku-4-5-20251001</c>) matches its undated name. Without steps, only a breakdown with one model says it.
    /// </summary>
    private MetricValue<long>? WindowOfLastStep(IReadOnlyList<ModelUsage> perModel)
    {
        var step = _lastStepModel;
        var match = step is null
            ? null
            : perModel.FirstOrDefault(m => m.Model == step)
                ?? perModel.FirstOrDefault(m => m.Model.StartsWith(step, StringComparison.Ordinal) || step.StartsWith(m.Model, StringComparison.Ordinal));
        if (match is null && step is null && perModel.Count == 1)
        {
            match = perModel[0];
        }

        return match is { ContextWindow.HasValue: true } ? match.ContextWindow : null;
    }

    /// <summary>The usage of each model of <c>result.modelUsage</c>, in the order the CLI wrote them.</summary>
    private static List<ModelUsage> PerModel(JsonElement root)
    {
        var models = new List<ModelUsage>();
        if (!root.TryGetProperty("modelUsage", out var entries) || entries.ValueKind != JsonValueKind.Object)
        {
            return models;
        }

        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object || ModelName(entry.Name) is not { } name)
            {
                continue;
            }

            var figures = entry.Value;
            var fresh = Long(figures, "inputTokens");
            var read = Long(figures, "cacheReadInputTokens");
            var created = Long(figures, "cacheCreationInputTokens");
            var input = fresh is null && read is null && created is null
                ? MetricValue<long>.Missing(ModelUsageSource + " sin tokens de entrada")
                : SafeSum(fresh, read, created) is { } prompt
                    ? MetricValue<long>.Exact(prompt, ModelUsageSource)
                    : MetricValue<long>.Missing(ModelUsageSource + " con tokens de entrada fuera de rango");
            var cost = figures.TryGetProperty("costUSD", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var costValue)
                ? MetricValue<decimal>.Estimated(costValue, "estimación del CLI")
                : MetricValue<decimal>.Missing(ModelUsageSource + " sin coste");
            var basis = Str(figures, "costBasis") is { } b && IsToken(b, 32) ? b : null;
            models.Add(new ModelUsage(
                name,
                input,
                Tokens(Long(figures, "outputTokens"), ModelUsageSource, "sin tokens de salida"),
                Tokens(read, ModelUsageSource, "sin caché leída"),
                Tokens(created, ModelUsageSource, "sin caché creada"),
                cost,
                Tokens(Long(figures, "contextWindow"), ModelUsageSource, "sin ventana de contexto"),
                basis));
        }

        return models;
    }

    private static MetricValue<long> Tokens(long? value, string source, string missing) =>
        value is { } v && v >= 0 ? MetricValue<long>.Exact(v, source) : MetricValue<long>.Missing(source + " " + missing);

    /// <summary>A model name as the CLI wrote it, or null when it is not one (it goes into the decision log).</summary>
    private static string? ModelName(string? name) =>
        name is { Length: > 0 and <= 128 } && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or ':' or '/' or '[' or ']' or '-')
            ? name
            : null;

    private static bool IsToken(string text, int maxLength) =>
        text.Length > 0 && text.Length <= maxLength && text.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_');

    private AgentRunOutcome BuildOutcome(JsonElement root)
    {
        var subtype = Str(root, "subtype");
        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var text = Str(root, "result");
        var status = root.TryGetProperty("api_error_status", out var api) && api.ValueKind == JsonValueKind.Number && api.TryGetInt32(out var apiStatus) ? apiStatus : (int?)null;

        string? structured = null;
        if (root.TryGetProperty("structured_output", out var so) && so.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            structured = so.GetRawText();
        }

        AgentRunStatus outcome;
        string? error = null;
        if (subtype is "error_max_turns" or "error_max_budget_usd")
        {
            outcome = AgentRunStatus.LimitReached;
            error = subtype == "error_max_turns" ? "Se alcanzó el máximo de turnos." : "Se alcanzó el presupuesto máximo.";
        }
        else if (status == 429 || (isError && LooksLikeQuota(text)))
        {
            outcome = AgentRunStatus.RateLimited;
            error = text ?? "Límite de uso alcanzado.";
        }
        else if (isError || subtype != "success")
        {
            outcome = AgentRunStatus.Failed;
            error = text ?? subtype ?? "Error desconocido de Claude Code.";
        }
        else
        {
            outcome = AgentRunStatus.Succeeded;
        }

        var end = outcome == AgentRunStatus.Succeeded ? null : ClassifyEnd(subtype, Str(root, "terminal_reason"), status, text);
        return new AgentRunOutcome(outcome, null, text, structured, Str(root, "session_id"), error, end);
    }

    /// <summary>
    /// The class of a turn that did not complete, from the signals of <see cref="EndClassSignals"/> only, in this order: the
    /// budget and the context that ended the loop, the session, the window that ran out, an overload and the network. Null
    /// when none of them came: the status says what it says, and the class is «otra» (docs/04 §18.1).
    /// </summary>
    private AgentRunEnd? ClassifyEnd(string? subtype, string? terminalReason, int? apiStatus, string? text)
    {
        foreach (var (budgetSubtype, budget) in BudgetSubtypes)
        {
            if (subtype == budgetSubtype)
            {
                return new AgentRunEnd(AttemptEndClass.AttemptBudget, Budget: budget);
            }
        }

        if (terminalReason == ContextExhaustedReason)
        {
            return new AgentRunEnd(AttemptEndClass.ContextExhausted);
        }

        if (_lastError == SignedOutError)
        {
            return new AgentRunEnd(AttemptEndClass.SignedOut);
        }

        if (_rejectedWindow is { } window)
        {
            return new AgentRunEnd(AttemptEndClass.WindowQuota, window.LimitType, window.ResetsAt);
        }

        if (_lastError == OverloadedError || apiStatus == OverloadedApiStatus)
        {
            return new AgentRunEnd(AttemptEndClass.RateOrOverload);
        }

        return text is not null && NetworkTexts.Any(fragment => text.Contains(fragment, StringComparison.Ordinal))
            ? new AgentRunEnd(AttemptEndClass.Network)
            : null;
    }

    private static bool IsSubagent(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String;

    private static bool LooksLikeQuota(string? text) =>
        text is not null
        && (text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase));

    // A short one-line hint of what the tool does; never the content being written or edited.
    private static string Summarise(JsonElement toolUse)
    {
        if (!toolUse.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in SummaryKeys)
        {
            if (Str(input, key) is { Length: > 0 } value)
            {
                var line = value.ReplaceLineEndings("\n").Split('\n')[0].Trim();
                return line.Length <= SummaryLimit ? line : line[..SummaryLimit] + "…";
            }
        }

        return string.Empty;
    }

    private static IEnumerable<JsonElement> Blocks(JsonElement root) =>
        root.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(b => b.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static double? Num(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    // Out-of-range seconds (negative, absurdly large) are treated as "not reported" instead of throwing.
    private static DateTimeOffset? Unix(JsonElement element, string name) =>
        Long(element, name) is { } seconds && seconds >= MinUnixSeconds && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static long? SafeSum(params long?[] values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value is not { } v)
            {
                continue;
            }

            if (v < 0 || total > long.MaxValue - v)
            {
                return null;
            }

            total += v;
        }

        return total;
    }
}
