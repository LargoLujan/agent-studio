using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Providers.Cli.Gemini;

/// <summary>
/// Translates Gemini CLI <c>-o stream-json</c> lines (JSONL) to normalised <see cref="AgentEvent"/>s. One instance per
/// run: it remembers the session, the tool names (a <c>tool_result</c> only carries the tool id) and the assistant text.
/// <para>
/// IMPORTANT: the mapping is derived from the official documentation (docs/cli/headless.md) and the event types in
/// packages/core/src/output/types.ts at v0.64.0, NOT from a captured authenticated session (none exists yet: the only
/// real capture is the no-session failure, which has empty stdout). It is pending verification against a real capture.
/// </para>
/// <para>
/// Mapping: <c>init</c> to <see cref="SessionStarted"/>; assistant <c>message</c> to <see cref="MessageEmitted"/>
/// (<c>IsPartial</c> = <c>delta</c>; user echoes are ignored); <c>tool_use</c> to <see cref="ToolInvoked"/>;
/// <c>tool_result</c> to <see cref="ToolCompleted"/>; <c>error</c> to a <see cref="MessageEmitted"/> notice (warning or
/// error by severity); <c>result</c> to <see cref="UsageReported"/> (Exact tokens from <c>stats</c>). The terminal
/// <see cref="RunFinished"/> is NOT emitted here: the provider builds it once, combining this result with the exit code.
/// Unknown event types and malformed lines are ignored.
/// </para>
/// <para>
/// The class of the end (docs/16 D-41): none. Gemini CLI is parked (ADR-0009) and there is no capture of an authenticated
/// session, so nothing it writes is recognised as a class: its ends are the ones their status implies
/// (<see cref="AgentRunEnd.Implied"/>), and a failure or a quota error is «otra». <see cref="RateLimitSignaled.LimitType"/>
/// is never set: the CLI does not name a window.
/// </para>
/// </summary>
public sealed partial class GeminiStreamJsonParser
{
    private const int SummaryLimit = 200;
    private const string Source = "gemini result.stats";

    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new();

    public string? SessionId { get; private set; }

    public string? Model { get; private set; }

    /// <summary>Concatenated assistant text so far (Gemini's <c>result</c> carries no final text).</summary>
    public string FinalText => _text.ToString();

    /// <summary>True once a <c>result</c> event was seen.</summary>
    public bool ResultSeen { get; private set; }

    /// <summary>True when the <c>result</c> event reported <c>status: success</c>.</summary>
    public bool ResultSucceeded { get; private set; }

    /// <summary>
    /// True once a quota / 429 / RESOURCE_EXHAUSTED condition was seen (an <c>error</c> event, <c>result.error</c> or
    /// stderr text). DERIVED FROM DOCUMENTATION (Gemini CLI quota and error docs), not from a captured session.
    /// </summary>
    public bool RateLimited { get; private set; }

    /// <summary>True when <paramref name="message"/> describes a quota or rate-limit error (429, RESOURCE_EXHAUSTED, quota...).</summary>
    public static bool IsRateLimitMessage(string? message) => !string.IsNullOrEmpty(message) && RateLimitPattern().IsMatch(message);

    /// <summary>
    /// Inspects a stderr line; returns a <see cref="RateLimitSignaled"/> (only the first time) when it reports a quota error.
    /// </summary>
    public IReadOnlyList<AgentEvent> ObserveStderr(string text, DateTimeOffset at) =>
        IsRateLimitMessage(text) ? Signal(at) : [];

    /// <summary>Message of the <c>result.error</c> or of the last <c>error</c> event with severity error.</summary>
    public string? Error { get; private set; }

    public IReadOnlyList<AgentEvent> Parse(string line, DateTimeOffset fallbackTime)
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

            var at = ReadTime(root, fallbackTime);
            return GetString(root, "type") switch
            {
                "init" => Init(root, at),
                "message" => Message(root, at),
                "tool_use" => ToolUse(root, at),
                "tool_result" => ToolResult(root, at),
                "error" => ErrorEvent(root, at),
                "result" => Result(root, at),
                _ => [],
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private IReadOnlyList<AgentEvent> Init(JsonElement root, DateTimeOffset at)
    {
        var id = GetString(root, "session_id");
        if (string.IsNullOrEmpty(id))
        {
            return [];
        }

        SessionId = id;
        Model = GetString(root, "model");
        return [new SessionStarted(at, id, Model)];
    }

    private IReadOnlyList<AgentEvent> Message(JsonElement root, DateTimeOffset at)
    {
        if (GetString(root, "role") != "assistant")
        {
            return [];
        }

        var content = GetString(root, "content");
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var partial = root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.True;
        if (!partial && _text.Length > 0)
        {
            _text.Append('\n');
        }

        _text.Append(content);
        return [new MessageEmitted(at, content, partial)];
    }

    private IReadOnlyList<AgentEvent> ToolUse(JsonElement root, DateTimeOffset at)
    {
        var name = GetString(root, "tool_name") ?? "unknown";
        var id = GetString(root, "tool_id");
        if (id is not null)
        {
            _toolNames[id] = name;
        }

        var summary = root.TryGetProperty("parameters", out var parameters) ? parameters.GetRawText() : string.Empty;
        if (summary.Length > SummaryLimit)
        {
            summary = summary[..SummaryLimit] + "...";
        }

        return [new ToolInvoked(at, name, summary)];
    }

    private IReadOnlyList<AgentEvent> ToolResult(JsonElement root, DateTimeOffset at)
    {
        var id = GetString(root, "tool_id");
        var name = id is not null && _toolNames.TryGetValue(id, out var known) ? known : "unknown";
        return [new ToolCompleted(at, name, GetString(root, "status") == "success")];
    }

    private IReadOnlyList<AgentEvent> ErrorEvent(JsonElement root, DateTimeOffset at)
    {
        var message = GetString(root, "message") ?? "(sin detalle)";
        if (GetString(root, "severity") == "error")
        {
            Error = message;
            List<AgentEvent> events = [new MessageEmitted(at, $"Error de Gemini CLI: {message}", false)];
            if (IsRateLimitCode(root) || IsRateLimitMessage(message))
            {
                events.AddRange(Signal(at));
            }

            return events;
        }

        return [new MessageEmitted(at, $"Aviso de Gemini CLI: {message}", false)];
    }

    private IReadOnlyList<AgentEvent> Result(JsonElement root, DateTimeOffset at)
    {
        ResultSeen = true;
        ResultSucceeded = GetString(root, "status") == "success";
        List<AgentEvent> events = [];
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            Error = GetString(error, "message") ?? Error;
            if (!ResultSucceeded && (IsRateLimitCode(error) || IsRateLimitMessage(Error)))
            {
                events.AddRange(Signal(at));
            }
        }

        if (!root.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
        {
            return events;
        }

        var usage = new TokenUsage(
            Tokens(stats, "input_tokens"),
            Tokens(stats, "output_tokens"),
            Tokens(stats, "cached"),
            MetricValue<long>.Unavailable("Gemini CLI does not report the context window"),
            MetricValue<decimal>.Unavailable("Gemini CLI does not report cost"));
        events.Add(new UsageReported(at, usage));
        return events;
    }

    /// <summary>Structured signal: a <c>code</c> or <c>status</c> field equal to 429 or RESOURCE_EXHAUSTED.</summary>
    private static bool IsRateLimitCode(JsonElement element)
    {
        foreach (var name in new[] { "code", "status" })
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 429)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String
                && value.GetString() is "429" or "RESOURCE_EXHAUSTED")
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<AgentEvent> Signal(DateTimeOffset at)
    {
        if (RateLimited)
        {
            return [];
        }

        RateLimited = true;
        return [new RateLimitSignaled(at, "quota_exceeded", null)];
    }

    // Only unambiguous provider formats; a bare word (a path, "not rate limited") must never match.
    [GeneratedRegex(@"\b(?:status|code)[""']?\s*[:=]\s*[""']?(?:429|RESOURCE_EXHAUSTED)\b|exceeded your current quota|Quota exceeded for quota metric", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPattern();

    private static MetricValue<long> Tokens(JsonElement stats, string name) =>
        stats.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? MetricValue<long>.Exact(number, Source)
            : MetricValue<long>.Unavailable($"Gemini CLI result.stats has no {name}");

    private static DateTimeOffset ReadTime(JsonElement root, DateTimeOffset fallback) =>
        GetString(root, "timestamp") is { } text && DateTimeOffset.TryParse(text, out var parsed) ? parsed : fallback;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
