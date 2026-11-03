using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Desktop.Debate;

/// <summary>One claim of a contribution and what it says supports it (contributions, evidence or quotes).</summary>
public sealed record ContributionClaim(string Claim, IReadOnlyList<string> Support);

/// <summary>One quote of code a contribution made: its id in the contribution, the path and lines it named, and why.</summary>
public sealed record ContributionQuote(string Id, string Path, int StartLine, int EndLine, string Why)
{
    public string Where => string.Create(CultureInfo.InvariantCulture, $"{Path}:{StartLine}-{EndLine}");
}

/// <summary>
/// A contribution as the room shows it, read from the JSON the coordinator stored as its evidence (docs/15 D-10: kind,
/// repliesTo, summary, argument, claims, citations, concedes and finished). The parser of the coordinator is strict; this
/// one only displays, so it takes what is there and leaves out what is not, and a text that is not a contribution is shown
/// as it is.
/// </summary>
public sealed record ContributionText(
    string? Summary,
    string? Argument,
    IReadOnlyList<ContributionClaim> Claims,
    IReadOnlyList<ContributionQuote> Quotes,
    IReadOnlyList<string> Concedes,
    string? Raw)
{
    private const string None = "ninguna";

    /// <summary>Reads <paramref name="json"/>; a text that is not a JSON object becomes <see cref="Raw"/>.</summary>
    public static ContributionText Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return AsRaw(json);
            }

            return new ContributionText(
                String(root, "summary"),
                String(root, "argument"),
                [.. Items(root, "claims").Select(c => new ContributionClaim(String(c, "claim") ?? string.Empty, Strings(c, "support")))],
                [.. Items(root, "citations").Select(Quote)],
                [.. Strings(root, "concedes").Where(c => !string.Equals(c, None, StringComparison.OrdinalIgnoreCase))],
                null);
        }
        catch (JsonException)
        {
            return AsRaw(json);
        }
    }

    private static ContributionText AsRaw(string text) => new(null, null, [], [], [], text);

    private static ContributionQuote Quote(JsonElement element) => new(
        String(element, "id") ?? string.Empty,
        String(element, "path") ?? string.Empty,
        Integer(element, "startLine"),
        Integer(element, "endLine"),
        String(element, "why") ?? string.Empty);

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static List<string> Strings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [];
}
