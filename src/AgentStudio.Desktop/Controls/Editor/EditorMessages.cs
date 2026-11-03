using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Desktop.Controls.Editor;

// Typed protocol between the WPF host and Assets/editor/editor.js. Every message is a JSON object with a "type"
// discriminator. "versionId" is Monaco's alternative version id: it returns to an earlier value when the user
// undoes back to a previous state, which is what makes the dirty flag exact.

/// <summary>A message sent from the host to the web page.</summary>
public abstract record EditorHostMessage([property: JsonPropertyOrder(-1)] string Type);

/// <summary>Replaces the edited document.</summary>
public sealed record OpenMessage(string Content, string Language, bool ReadOnly) : EditorHostMessage("open");

/// <summary>Shows a read-only side-by-side comparison.</summary>
public sealed record ShowDiffMessage(string Original, string Modified, string Language) : EditorHostMessage("showDiff");

/// <summary>Leaves the diff view and returns to the editor.</summary>
public sealed record HideDiffMessage() : EditorHostMessage("hideDiff");

/// <summary>Asks the page for the current text; answered by <see cref="ContentMessage"/>.</summary>
public sealed record RequestContentMessage(string RequestId) : EditorHostMessage("requestContent");

/// <summary>Moves keyboard focus into the editor.</summary>
public sealed record FocusMessage() : EditorHostMessage("focus");

/// <summary>Tells the page that the given version is now on disk, so it stops being dirty.</summary>
public sealed record MarkSavedMessage(long VersionId) : EditorHostMessage("markSaved");

/// <summary>A message sent from the web page to the host.</summary>
public abstract record EditorWebMessage;

/// <summary>The page finished loading Monaco and is listening.</summary>
public sealed record ReadyMessage : EditorWebMessage;

/// <summary>The text changed (or its dirty state did).</summary>
public sealed record ChangedMessage(long VersionId, bool IsDirty) : EditorWebMessage;

/// <summary>Answer to a <see cref="RequestContentMessage"/>.</summary>
public sealed record ContentMessage(string RequestId, string Text, long VersionId) : EditorWebMessage;

/// <summary>The user pressed Ctrl+S.</summary>
public sealed record SaveRequestedMessage : EditorWebMessage;

/// <summary>The page could not start (for example Monaco failed to load).</summary>
public sealed record ErrorMessage(string Message) : EditorWebMessage;

/// <summary>JSON encoding of the editor protocol.</summary>
public static class EditorMessageCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Source text is sent verbatim; never let the serializer escape or rewrite it.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serializes a host message using its runtime type, so the "type" discriminator is included.</summary>
    public static string Serialize(EditorHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, message.GetType(), Options);
    }

    /// <summary>
    /// Parses a message from the page. Returns false (with a reason) for malformed JSON, unknown types and
    /// messages missing required fields; the caller logs and ignores those.
    /// </summary>
    public static bool TryParse(string? json, out EditorWebMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty message.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The message is not a JSON object.";
                return false;
            }

            var type = GetString(root, "type");
            if (type is null)
            {
                error = "The message has no string 'type'.";
                return false;
            }

            switch (type)
            {
                case "ready":
                    message = new ReadyMessage();
                    return true;

                case "saveRequested":
                    message = new SaveRequestedMessage();
                    return true;

                case "changed":
                    if (GetLong(root, "versionId") is { } changedVersion && GetBool(root, "isDirty") is { } isDirty)
                    {
                        message = new ChangedMessage(changedVersion, isDirty);
                        return true;
                    }

                    error = "'changed' requires numeric 'versionId' and boolean 'isDirty'.";
                    return false;

                case "content":
                    if (GetString(root, "requestId") is { } requestId
                        && GetString(root, "text") is { } text
                        && GetLong(root, "versionId") is { } contentVersion)
                    {
                        message = new ContentMessage(requestId, text, contentVersion);
                        return true;
                    }

                    error = "'content' requires string 'requestId' and 'text' and numeric 'versionId'.";
                    return false;

                case "error":
                    message = new ErrorMessage(GetString(root, "message") ?? "Unknown editor error.");
                    return true;

                default:
                    error = $"Unknown message type '{type}'.";
                    return false;
            }
        }
        catch (JsonException ex)
        {
            error = "Malformed JSON: " + ex.Message;
            return false;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
