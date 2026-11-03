using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Providers;

namespace AgentStudio.Providers.Cli.Codex.AppServer;

/// <summary>
/// Reads the answer of <c>account/rateLimits/read</c> from the standard output of <c>codex app-server</c> (JSON-RPC 2.0
/// without the <c>"jsonrpc"</c> field, one message per line; docs/14 D-15), by a whitelist. The shape is the one of
/// <c>GetAccountRateLimitsResponse</c> in codex-rs <c>rust-v0.160.0</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Taken:</b> the buckets of <c>rateLimitsByLimitId</c> (in order of their id) or, without it, the single bucket of
/// <c>rateLimits</c>; of each one <c>limitId</c>, <c>limitName</c>, <c>primary</c> and <c>secondary</c>
/// (<c>usedPercent</c>, <c>windowDurationMins</c>, <c>resetsAt</c>), <c>rateLimitReachedType</c> and <c>planType</c>; and
/// <c>ordinaryUsageAllowed</c>.</item>
/// <item><b>Never kept:</b> <c>accountId</c>, <c>rateLimitUpsell</c>, <c>credits</c> (the balance), <c>rateLimitResetCredits</c>,
/// <c>individualLimit</c>, <c>spendControlReached</c> and <c>normalModelSlug</c>. They are not read at all.</item>
/// <item>The utilization is the provider's own figure: <c>usedPercent</c> divided by 100, a change of unit, never a
/// computation. A value out of range is left out, never guessed: a negative utilization drops its window, and an id, a
/// name or a plan that is not a plain token is dropped.</item>
/// <item>An error is classified: an API key is «no expuesto», a missing session and any other failure are «sin dato»,
/// with a short reason in Spanish.</item>
/// </list>
/// </remarks>
public static class CodexRateLimitsParser
{
    /// <summary>The id of the <c>initialize</c> request (the handshake).</summary>
    public const int InitializeId = 1;

    /// <summary>The id of the <c>account/rateLimits/read</c> request.</summary>
    public const int RateLimitsId = 2;

    /// <summary>What every observation says it came from.</summary>
    public const string Source = "codex app-server account/rateLimits/read";

    private const string UtilizationSource = "codex account/rateLimits/read usedPercent / 100";
    private const int MaxReason = 120;
    private const int MaxName = 64;

    // Valid range of DateTimeOffset.FromUnixTimeSeconds.
    private const long MaxUnixSeconds = 253402300799;

    /// <summary>
    /// The result a line gives: the answer of the quota read (id 2, a result or an error), or the refusal of the handshake
    /// (id 1 with an error). Null for any other line: notifications, the answer of <c>initialize</c>, other ids, lines that
    /// are not JSON objects.
    /// </summary>
    public static QuotaReadResult? FromLine(string? line, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out var number))
            {
                return null;
            }

            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : (JsonElement?)null;
            return number switch
            {
                RateLimitsId when error is { } failure => FromError(failure),
                RateLimitsId when root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object => FromResult(result, observedAt),
                RateLimitsId => Missing("la respuesta no trae resultado"),
                InitializeId when error is { } refused => Missing("el app-server rechazó el saludo (initialize): " + Clean(Text(refused, "message"))),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The «sin dato» result of a read that did not get an answer, with its reason.</summary>
    public static QuotaReadResult Missing(string reason) => new(QuotaReadStatus.Missing, [], "sin dato: " + reason);

    private static QuotaReadResult FromResult(JsonElement result, DateTimeOffset observedAt)
    {
        var buckets = new List<(string? Key, JsonElement Snapshot)>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            buckets.AddRange(byId.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => ((string?)p.Name, p.Value)));
        }

        var single = result.TryGetProperty("rateLimits", out var view) && view.ValueKind == JsonValueKind.Object ? view : (JsonElement?)null;
        if (buckets.Count == 0 && single is { } only)
        {
            buckets.Add((null, only));
        }

        if (buckets.Count == 0)
        {
            return Missing("la respuesta no trae rateLimits");
        }

        var observations = new List<QuotaObservation>();
        foreach (var (key, snapshot) in buckets)
        {
            var limit = Token(Text(snapshot, "limitId")) ?? Token(key);
            var label = Name(Text(snapshot, "limitName"));
            var reached = Token(Text(snapshot, "rateLimitReachedType"));
            foreach (var slot in (string[])["primary", "secondary"])
            {
                if (Window(snapshot, slot, limit, observedAt) is { } observation)
                {
                    observations.Add(observation with { Label = label, Reached = reached });
                }
            }
        }

        // The plan of the account: that of the single bucket view, else the first bucket that says it.
        var plan = (single is { } s ? Token(Text(s, "planType")) : null)
            ?? buckets.Select(b => Token(Text(b.Snapshot, "planType"))).FirstOrDefault(p => p is not null);
        bool? ordinary = result.TryGetProperty("ordinaryUsageAllowed", out var allowed) && allowed.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? allowed.GetBoolean()
            : null;
        return new QuotaReadResult(QuotaReadStatus.Read, observations) { PlanType = plan, OrdinaryUsageAllowed = ordinary };
    }

    private static QuotaObservation? Window(JsonElement snapshot, string slot, string? limit, DateTimeOffset observedAt)
    {
        if (!snapshot.TryGetProperty(slot, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var used)
            || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent)
            || percent < 0)
        {
            return null;
        }

        TimeSpan? length = Whole(window, "windowDurationMins") is { } minutes && minutes > 0 && minutes <= (long)TimeSpan.MaxValue.TotalMinutes
            ? TimeSpan.FromMinutes(minutes)
            : null;
        DateTimeOffset? resets = Whole(window, "resetsAt") is { } seconds && seconds >= 0 && seconds <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return new QuotaObservation(
            ProviderId.CodexCli,
            limit is null ? slot : $"{limit}/{slot}",
            MetricValue<double>.Exact(percent / 100, UtilizationSource),
            length,
            resets,
            observedAt,
            Source);
    }

    private static QuotaReadResult FromError(JsonElement error)
    {
        var message = Text(error, "message") ?? string.Empty;
        if (message.Contains("chatgpt authentication required", StringComparison.OrdinalIgnoreCase))
        {
            return new QuotaReadResult(QuotaReadStatus.NotExposed, [], "no expuesto: Codex usa API key");
        }

        if (message.Contains("authentication required", StringComparison.OrdinalIgnoreCase))
        {
            return Missing("Codex no tiene la sesión iniciada; inicia sesión con «codex login»");
        }

        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var value)
            ? string.Create(CultureInfo.InvariantCulture, $" (error {value})")
            : string.Empty;
        return Missing($"el app-server de Codex no pudo leer la cuota{code}: {Clean(message)}");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Whole(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    /// <summary>A plain token (an id, a plan, a reached type): lowercase letters, digits, '_', '-' and '.'; anything else is dropped.</summary>
    private static string? Token(string? text) =>
        text is { Length: > 0 and <= MaxName } && text.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '-' or '.')
            ? text
            : null;

    /// <summary>A display name: printable, without control characters, and short.</summary>
    private static string? Name(string? text) =>
        text is { Length: > 0 and <= MaxName } && !text.Any(char.IsControl) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>The message of an error as a short line: control characters become spaces, runs of spaces collapse.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "sin mensaje";
        }

        var line = new StringBuilder(Math.Min(text.Length, MaxReason));
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
            {
                line.Append(' ');
                space = false;
            }

            line.Append(ch);
            if (line.Length >= MaxReason)
            {
                line.Append('…');
                break;
            }
        }

        return line.ToString();
    }
}
