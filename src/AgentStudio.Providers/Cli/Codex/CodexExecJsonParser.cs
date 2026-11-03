using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Providers.Cli.Codex;

/// <summary>
/// Stateful translator of the JSONL written by <c>codex exec --json</c> (verified against 0.160.0 fixtures) into
/// <see cref="AgentEvent"/>s. Feed it one stdout line at a time. Unknown or malformed lines are ignored.
/// </summary>
/// <remarks>
/// <c>item.completed</c> of type <c>error</c> carries configuration warnings and never fails the run. A top-level
/// <c>error</c> is only remembered (Codex also emits it for transient reconnections): the run fails on
/// <c>turn.failed</c>, or when the process ends without <c>turn.completed</c> (see <see cref="LastError"/>).
/// <para>
/// The class of the end of a failed run (docs/16 D-41, F8-1): <c>exec --json</c> says why a turn failed only in the text of
/// its error (<c>ThreadErrorEvent.message</c>), so the class comes from the fragments of <see cref="EndClassSignals"/>, taken
/// from the texts of codex-rs 0.160.0 and each in a capture of that version (synthetic, marked as such in
/// <c>Fixtures/codex-cli/0.160.0/limits/</c>). The first fragment the message contains decides; a message with none has no
/// class («otra»). A window of <c>exec</c> has neither a type nor a reset: Codex writes the reset as a local time of day,
/// which is not parsed, so the wait is the progressive one (docs/04 §18.1). The class refines the status and never changes
/// it.
/// </para>
/// </remarks>
public sealed partial class CodexExecJsonParser(string? model = null, bool expectStructuredOutput = false)
{
    private const int MaxSummaryLength = 200;

    /// <summary>
    /// The fragments of the errors of codex-rs 0.160.0 the class is deduced from, in the order they are tried
    /// (<c>codex-rs/protocol/src/error.rs</c> and <c>codex-rs/login/src/auth/manager.rs</c>). The content filter goes before
    /// the network: its text starts like a stream that disconnected, and it is not one. The usage errors that are not a window
    /// (the credits or the spend cap of a workspace, a quota of the billing, a plan without Codex) have no fragment: waiting
    /// does not fix them, and they are «otra».
    /// </summary>
    private static readonly (string Fragment, AttemptEndClass Class)[] EndClasses =
    [
        ("reason: content_filter", AttemptEndClass.Other),
        ("Your access token could not be refreshed", AttemptEndClass.SignedOut),
        ("ran out of room in the model's context window", AttemptEndClass.ContextExhausted),
        ("hit your usage limit", AttemptEndClass.WindowQuota),
        ("Selected model is at capacity", AttemptEndClass.RateOrOverload),
        ("rate limit exceeded:", AttemptEndClass.RateOrOverload),
        ("exceeded retry limit, last status: 429", AttemptEndClass.RateOrOverload),
        ("Connection failed:", AttemptEndClass.Network),
        ("request timed out", AttemptEndClass.Network),
        ("Error while reading the server response:", AttemptEndClass.Network),
        ("stream disconnected before completion:", AttemptEndClass.Network),
    ];

    private readonly HashSet<string> _startedItems = [];
    private string? _sessionId;
    private string? _lastAgentMessage;

    /// <summary>True once a terminal event (<c>turn.completed</c> or <c>turn.failed</c>) was produced.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Message of the last top-level <c>error</c> / <c>turn.failed</c>, for runs that end without a result.</summary>
    public string? LastError { get; private set; }

    public string? SessionId => _sessionId;

    public string? LastAgentMessage => _lastAgentMessage;

    public IReadOnlyList<AgentEvent> Parse(string line, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return Str(root, "type") switch
            {
                "thread.started" => ThreadStarted(root, at),
                "item.started" or "item.completed" => Item(root, at),
                "turn.completed" => TurnCompleted(root, at),
                "turn.failed" => TurnFailed(root, at),
                "error" => TopLevelError(root),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every fragment the class of the end is deduced from, as Codex writes it. Each one is in a capture of 0.160.0: the test
    /// of the captures fails for a fragment that is in none, so no class is deduced from a text nobody captured.
    /// </summary>
    public static IReadOnlyList<string> EndClassSignals { get; } = [.. EndClasses.Select(c => c.Fragment)];

    /// <summary>True when an error message means the subscription or rate limit was hit (429, usage limit...).</summary>
    public static bool IsRateLimitMessage(string? message) => !string.IsNullOrEmpty(message) && RateLimitPattern().IsMatch(message);

    private List<AgentEvent> ThreadStarted(JsonElement root, DateTimeOffset at)
    {
        var id = Str(root, "thread_id");
        if (id is null)
        {
            return [];
        }

        _sessionId = id;
        return [new SessionStarted(at, id, model)];
    }

    private List<AgentEvent> Item(JsonElement root, DateTimeOffset at)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var completed = Str(root, "type") == "item.completed";
        var itemType = Str(item, "type");
        var id = Str(item, "id") ?? string.Empty;
        var events = new List<AgentEvent>();

        switch (itemType)
        {
            case "agent_message" when completed:
                if (Str(item, "text") is { } text)
                {
                    _lastAgentMessage = text;
                    events.Add(new MessageEmitted(at, text, IsPartial: false));
                }

                break;

            case "command_execution":
                Tool(events, at, id, completed, "shell", Summarize(Str(item, "command")), () => item.TryGetProperty("exit_code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var exit) && exit == 0);
                break;

            case "file_change":
                Tool(events, at, id, completed, "file_change", Summarize(FileChangeSummary(item)), () => Str(item, "status") == "completed");
                break;

            case "mcp_tool_call":
                Tool(events, at, id, completed, "mcp", Summarize($"{Str(item, "server")}/{Str(item, "tool")}"), () => Str(item, "status") == "completed");
                break;

            case "web_search":
                Tool(events, at, id, completed, "web_search", Summarize(Str(item, "query")), () => true);
                break;
        }

        return events;
    }

    private void Tool(List<AgentEvent> events, DateTimeOffset at, string id, bool completed, string tool, string summary, Func<bool> succeeded)
    {
        // Some items (file_change) only arrive completed: announce them before completing.
        if (_startedItems.Add(id))
        {
            events.Add(new ToolInvoked(at, tool, summary));
        }

        if (completed)
        {
            _startedItems.Remove(id);
            events.Add(new ToolCompleted(at, tool, succeeded()));
        }
    }

    private List<AgentEvent> TurnCompleted(JsonElement root, DateTimeOffset at)
    {
        IsFinished = true;
        var events = new List<AgentEvent>();
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            events.Add(new UsageReported(at, ReadUsage(usage)));
        }

        var status = AgentRunStatus.Succeeded;
        string? structured = null;
        string? error = null;
        if (expectStructuredOutput)
        {
            if (_lastAgentMessage is not null && IsValidJson(_lastAgentMessage))
            {
                structured = _lastAgentMessage;
            }
            else
            {
                // Shared rule: a requested schema without valid JSON is a failure; the final text is kept.
                status = AgentRunStatus.Failed;
                error = "A structured output was requested but the final message is not valid JSON.";
            }
        }

        events.Add(new RunFinished(at, new AgentRunOutcome(status, null, _lastAgentMessage, structured, _sessionId, error)));
        return events;
    }

    private List<AgentEvent> TurnFailed(JsonElement root, DateTimeOffset at)
    {
        IsFinished = true;
        string? message = null;
        if (root.TryGetProperty("error", out var error))
        {
            message = error.ValueKind == JsonValueKind.Object ? Str(error, "message") : error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }

        message ??= LastError ?? "Codex reported that the turn failed.";
        LastError = message;
        return FailureEvents(at, message);
    }

    private List<AgentEvent> TopLevelError(JsonElement root)
    {
        LastError = Str(root, "message") ?? LastError;
        return [];
    }

    /// <summary>
    /// Events for a run that failed with <paramref name="message"/>: <c>turn.failed</c>, or the adapter when the process ended
    /// without completing its turn (its last error and the tail of stderr). Usage limits are rate limiting, and the end gets
    /// the class of the first fragment of <see cref="EndClassSignals"/> the message contains.
    /// </summary>
    public List<AgentEvent> FailureEvents(DateTimeOffset at, string message)
    {
        var events = new List<AgentEvent>();
        var status = AgentRunStatus.Failed;
        if (IsRateLimitMessage(message))
        {
            status = AgentRunStatus.RateLimited;
            events.Add(new RateLimitSignaled(at, "rate_limited", null));
        }

        events.Add(new RunFinished(at, new AgentRunOutcome(status, null, _lastAgentMessage, null, _sessionId, message, ClassifyEnd(message))));
        return events;
    }

    /// <summary>The class of the first fragment the message contains, or null when it contains none («otra»).</summary>
    private static AgentRunEnd? ClassifyEnd(string message)
    {
        foreach (var (fragment, endClass) in EndClasses)
        {
            if (message.Contains(fragment, StringComparison.Ordinal))
            {
                return endClass == AttemptEndClass.Other ? null : new AgentRunEnd(endClass);
            }
        }

        return null;
    }

    /// <summary>
    /// The usage of <c>turn.completed</c>: in codex-rs 0.160.0 it is <c>ThreadTokenUsage.total</c>, the total of the
    /// thread (docs/14 D-02), so a resumed thread reports what its earlier turns spent too; the attempt's own usage is a
    /// projection of the log. <c>input_tokens</c> includes the cached input and <c>output_tokens</c> the reasoning. When no
    /// usage notification arrived, Codex writes every figure as zero (<c>Usage::default()</c>): that is no figure at all.
    /// </summary>
    private static TokenUsage ReadUsage(JsonElement usage)
    {
        string[] names = ["input_tokens", "output_tokens", "cached_input_tokens", "cache_write_input_tokens", "reasoning_output_tokens"];
        var figures = names.Select(name => (Name: name, Value: Number(usage, name))).ToList();
        var allZero = figures.All(f => f.Value is null or 0) && figures.Any(f => f.Value == 0);

        MetricValue<long> Tokens(string name)
        {
            var source = $"codex turn.completed.usage.{name} (acumulado del hilo)";
            return allZero
                ? MetricValue<long>.Missing(source + ": todo a cero, no llegó ninguna notificación de uso")
                : figures.First(f => f.Name == name).Value is { } value && value >= 0
                    ? MetricValue<long>.Exact(value, source)
                    : MetricValue<long>.Missing(source + ": no llegó");
        }

        return new TokenUsage(
            Tokens("input_tokens"),
            Tokens("output_tokens"),
            Tokens("cached_input_tokens"),
            MetricValue<long>.NotExposed("codex exec --json does not report the context window"),
            MetricValue<decimal>.NotExposed("codex exec --json does not report cost"))
        {
            CacheWriteInputTokens = Tokens("cache_write_input_tokens"),
            ReasoningOutputTokens = Tokens("reasoning_output_tokens"),
        };
    }

    private static long? Number(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string FileChangeSummary(JsonElement item)
    {
        if (item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
        {
            var paths = changes.EnumerateArray().Select(c => Str(c, "path")).Where(p => p is not null);
            return string.Join(", ", paths);
        }

        return "file changes";
    }

    private static string Summarize(string? text)
    {
        var collapsed = text is null ? string.Empty : WhitespacePattern().Replace(text, " ").Trim();
        return collapsed.Length <= MaxSummaryLength ? collapsed : collapsed[..MaxSummaryLength] + "…";
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\b429\b|usage limit|rate[ -]?limit|too many requests", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();
}
