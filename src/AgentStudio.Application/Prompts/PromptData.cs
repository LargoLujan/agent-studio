using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace AgentStudio.Application.Prompts;

/// <summary>
/// How text that is data (an upstream report, a failure, the user's request) goes into a prompt: as JSON written with
/// an encoder that escapes the HTML-sensitive characters (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, quotes) but keeps
/// letters readable. A string that came from an agent can therefore never contain the closing tag of the block it sits
/// in (risk R4: data is never presented as instructions).
/// </summary>
/// <remarks>
/// It moved from the engine to Application in Phase 5 (docs/15 section 5.5), unchanged, so the debate can quote what one
/// participant wrote to another (D-18) and the module of the debate never depends on the engine (ADR-0002).
/// </remarks>
public static class PromptData
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false,
    };

    public static string Quote(string? text) => JsonSerializer.Serialize(text ?? string.Empty, Options);

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Re-serializes a JSON document through the escaping encoder (the delimiter of the block cannot be forged).</summary>
    public static string Reencode(JsonElement element) => JsonSerializer.Serialize(element, Options);

    /// <summary>A value safe inside a double-quoted attribute of a data tag (ids and hashes are already safe; anything else is reduced).</summary>
    public static string Attribute(string value) =>
        new([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')]);
}
