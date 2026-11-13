using System.Globalization;
using System.Text.Json;
using AgentStudio.Domain.Common;

namespace AgentStudio.Infrastructure.Hosting.GitHub;

/// <summary>
/// The small readers every response goes through (docs/21 D-12): the body is data that is not trusted, so it is parsed with a depth limit of 16, and each field is read by name, with its type and its
/// bound; a field of another type is «not there», never converted. Nothing here throws for a body.
/// </summary>
internal static class GitHubJson
{
    internal const int MaxDepth = 16;

    internal const int MaxDisplayLength = 200;

    /// <summary>The document, or null when the body is not JSON or is deeper than <see cref="MaxDepth"/>. The caller disposes it.</summary>
    internal static JsonDocument? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    internal static JsonElement? Obj(JsonElement element, string name) => Prop(element, name) is { ValueKind: JsonValueKind.Object } value ? value : null;

    internal static JsonElement? Arr(JsonElement element, string name) => Prop(element, name) is { ValueKind: JsonValueKind.Array } value ? value : null;

    internal static string? Str(JsonElement element, string name, int maximum = 4096) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { } text && text.Length <= maximum ? text : null;

    internal static bool? Bool(JsonElement element, string name) => Prop(element, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    internal static long? Long(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    internal static int? Int(JsonElement element, string name) => Long(element, name) is { } number and >= 0 and <= int.MaxValue ? (int)number : null;

    internal static DateTimeOffset? Date(JsonElement element, string name) =>
        Str(element, name, 64) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    /// <summary>A full commit id of 40 or 64 lowercase hexadecimal characters, or null.</summary>
    internal static string? CommitId(string? value) => GitHubApiPath.IsCommitId(value) ? value : null;

    /// <summary>A text of the remote for the screen only: control and format characters out, one line, at most 200 characters, never cut in the middle of a pair. Null when nothing is left.</summary>
    internal static string? Display(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var clean = UntrustedText.Clean(text).Trim();
        if (clean.Length > MaxDisplayLength)
        {
            var length = MaxDisplayLength;
            if (char.IsHighSurrogate(clean[length - 1]))
            {
                length--;
            }

            clean = clean[..length];
        }

        return clean.Length == 0 ? null : clean;
    }
}
