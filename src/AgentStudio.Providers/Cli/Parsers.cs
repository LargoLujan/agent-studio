using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Providers;

namespace AgentStudio.Providers.Cli;

/// <summary>Authentication state read from a CLI, plus a short method label that never holds personal data.</summary>
public sealed record CliAuthStatus(AuthenticationState State, string? Method);

/// <summary>Extracts a semantic version from the output of <c>--version</c>.</summary>
public static partial class CliVersionParser
{
    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = SemVer().Match(output);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"(?<![\d.])\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?(?:\+[0-9A-Za-z.\-]+)?")]
    private static partial Regex SemVer();
}

/// <summary>
/// Reads the JSON of <c>claude auth status</c>. Privacy: only <c>loggedIn</c>, <c>authMethod</c> and
/// <c>subscriptionType</c> are read; e-mail, organisation and paths are never touched.
/// </summary>
public static partial class ClaudeAuthStatusParser
{
    public static CliAuthStatus Parse(string? output)
    {
        var unknown = new CliAuthStatus(AuthenticationState.Unknown, null);
        if (string.IsNullOrWhiteSpace(output))
        {
            return unknown;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return unknown;
            }

            if (loggedIn.ValueKind == JsonValueKind.False)
            {
                return new CliAuthStatus(AuthenticationState.NotAuthenticated, null);
            }

            var method = SafeToken(root, "authMethod");
            var subscription = SafeToken(root, "subscriptionType");
            var label = (method, subscription) switch
            {
                (null, _) => null,
                (_, null) => method,
                _ => $"{method} (suscripción {subscription})",
            };
            return new CliAuthStatus(AuthenticationState.Authenticated, label);
        }
        catch (JsonException)
        {
            return unknown;
        }
    }

    private static string? SafeToken(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return value is not null && Token().IsMatch(value) ? value : null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._\-]{1,40}\z")]
    private static partial Regex Token();
}

/// <summary>Reads the text of <c>codex login status</c> ("Logged in using ChatGPT").</summary>
public static partial class CodexLoginStatusParser
{
    public static CliAuthStatus Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new CliAuthStatus(AuthenticationState.Unknown, null);
        }

        if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return new CliAuthStatus(AuthenticationState.NotAuthenticated, null);
        }

        var match = LoggedIn().Match(output);
        if (!match.Success)
        {
            return new CliAuthStatus(AuthenticationState.Unknown, null);
        }

        // Allow-list: free text from the CLI (e-mail, key fragments) is never returned.
        var text = match.Groups[1].Value;
        var method = text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "ChatGPT"
            : text.Contains("API key", StringComparison.OrdinalIgnoreCase) ? "API key"
            : null;
        return new CliAuthStatus(AuthenticationState.Authenticated, method);
    }

    [GeneratedRegex(@"Logged in using\s+([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LoggedIn();
}
