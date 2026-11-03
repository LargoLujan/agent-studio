using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Application.Prompts;
using AgentStudio.Domain.Debate;
using static System.FormattableString;

namespace AgentStudio.Debate.Arena;

/// <summary>
/// The outcome of reading one participant's answer. <see cref="Answer"/> is null when it is not a contribution the rules
/// of its turn admit; <see cref="Problems"/> then says why, in English, because the one correction sends them back to the
/// participant as data (<c>&lt;contribution_problems&gt;</c>, docs/15 D-10).
/// </summary>
public sealed record ContributionParseResult(ContributionAnswer? Answer, IReadOnlyList<string> Problems);

/// <summary>
/// Reads a participant's contribution strictly, as <c>PlanParser</c> reads a plan (ADR-0013), and checks it against the
/// rules of its turn (docs/15 D-10):
/// <list type="bullet">
/// <item>exactly one JSON object with every member of the schema; nothing unknown, repeated or null (the schema has no
/// nullable member: <c>repliesTo</c> says <see cref="ContributionLabels.None"/>); no comments, trailing commas or type
/// coercion; kinds by their exact name;</item>
/// <item>only the kinds the round admits; <c>repliesTo</c> and <c>concedes</c> name published contributions, and the
/// support of a claim a published contribution, an evidence of the material or a citation of this very contribution
/// (<see cref="ContributionRules"/>); nothing twice;</item>
/// <item>the caps of <see cref="ContributionLimits"/>; citation ids <c>CITA-1</c> to <c>CITA-8</c>, once each; a citation
/// path relative to the copy of the commit (<see cref="CitationResolver.PathProblem"/>) and 1 to 80 lines
/// (<see cref="CitationResolver.LinesProblem"/>).</item>
/// </list>
/// Every problem is reported, not only the first.
/// </summary>
/// <remarks>
/// <para>A problem never repeats what the participant wrote: it names the members of the schema, JSON paths, the values of
/// the closed sets and numbers, nothing else. The correction prompt that carries them is evidence captured while its
/// round is still sealed (D-17), where no text of a participant may be; and an answer that fails is never published.</para>
/// <para>Whether a citation exists in the commit is not the parser's business: <see cref="CitationResolver"/> finds out, and
/// a citation that does not resolve is a fact for the judge, not a problem the participant corrects.</para>
/// </remarks>
public static class ContributionParser
{
    private static readonly string[] ContributionMembers = ["kind", "repliesTo", "summary", "argument", "claims", "citations", "concedes", "finished"];
    private static readonly string[] ClaimMembers = ["claim", "support"];
    private static readonly string[] CitationMembers = ["id", "path", "startLine", "endLine", "why"];

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
        AllowDuplicateProperties = true, // a repeated member is reported below, by its name in the schema and never by another
    };

    /// <param name="json">The structured output of the participant's turn.</param>
    /// <param name="rules">What the turn may write.</param>
    public static ContributionParseResult Parse(string? json, ContributionRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(json))
        {
            return Rejected("The answer is empty: expected one JSON object with the contribution.");
        }

        if (json.Length > ContributionLimits.MaxAnswerChars)
        {
            return Rejected(Invariant($"The answer has {json.Length} characters; a contribution takes at most {ContributionLimits.MaxAnswerChars}."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            return Rejected(Invariant(
                $"The answer is not one valid JSON value (line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1}): write one JSON object, without comments, trailing commas or anything after it."));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Rejected($"The answer is a JSON {Describe(root.ValueKind)}, not the object of the contribution.");
            }

            var problems = new List<string>();
            var members = Members(root, "the contribution", string.Empty, ContributionMembers, problems);
            var kind = Kind(Text(members, "kind", problems), rules, problems);
            var repliesRead = RepliesTo(Text(members, "repliesTo", problems), rules, problems, out var repliesTo);
            var summary = Text(members, "summary", problems);
            Cap(summary, "summary", ContributionLimits.MaxSummaryChars, problems);
            var argument = Text(members, "argument", problems);
            Cap(argument, "argument", ContributionLimits.MaxArgumentChars, problems);
            var citations = Citations(Items(members, "citations", problems), problems);
            var citationIds = citations.Select(c => c.Id).Where(id => ContributionLabels.Citations.Contains(id, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
            var claims = Claims(Items(members, "claims", problems), rules, citationIds, problems);
            var concedes = Concedes(Items(members, "concedes", problems), rules, problems);
            var finished = Boolean(members, "finished", problems);

            if (problems.Count > 0 || kind is null || !repliesRead || summary is null || argument is null || finished is null)
            {
                return new ContributionParseResult(null, problems);
            }

            return new ContributionParseResult(
                new ContributionAnswer(kind.Value, repliesTo, summary, argument, claims, citations, concedes, finished.Value),
                []);
        }
    }

    /// <summary>
    /// The canonical form of a contribution: the members in the order of the schema, compact, with the sentinel for no reply,
    /// written as prompt data (<see cref="PromptData"/>: '&lt;', '&gt;', '&amp;', the quotes, '+' and '`' always escaped, the
    /// letters of any language readable). The canonical form of the redacted answer is what the evidence of the contribution
    /// holds, so the text the other participants and the judge read (re-encoded with <see cref="PromptData.Reencode"/>, which
    /// changes no byte of it) is the evidence whose SHA-256 the log anchors.
    /// </summary>
    /// <remarks>
    /// The canonical form of an answer <see cref="Parse"/> accepted reads back with <see cref="Parse"/> under the same rules
    /// and gives the same form again. That of a redacted answer may not: a placeholder can take a text past its cap. A
    /// stored contribution is anchored by its hash, so it is read back for its shape (<see cref="ReadStored"/>), never
    /// checked against the caps again.
    /// </remarks>
    public static string ToJson(ContributionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var node = new JsonObject
        {
            ["kind"] = answer.Kind.ToString(),
            ["repliesTo"] = answer.RepliesTo ?? ContributionLabels.None,
            ["summary"] = answer.Summary,
            ["argument"] = answer.Argument,
            ["claims"] = new JsonArray([.. answer.Claims.Select(c => (JsonNode)new JsonObject
            {
                ["claim"] = c.Claim,
                ["support"] = Strings(c.Support),
            })]),
            ["citations"] = new JsonArray([.. answer.Citations.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["path"] = c.Path,
                ["startLine"] = c.StartLine,
                ["endLine"] = c.EndLine,
                ["why"] = c.Why,
            })]),
            ["concedes"] = Strings(answer.Concedes),
            ["finished"] = answer.Finished,
        };
        return PromptData.Json(node);
    }

    /// <summary>
    /// A contribution the system stored (its canonical form, already checked when it was answered and anchored by its hash),
    /// read back for its shape: every member there with its type, nothing unknown, the kind by its name. The caps and the
    /// closed sets of its turn are not checked again, so a redacted text a placeholder made longer still reads. Null when
    /// the text is not that shape.
    /// </summary>
    public static ContributionAnswer? ReadStored(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var problems = new List<string>();
            var members = Members(root, "the contribution", string.Empty, ContributionMembers, problems);
            var kindName = Text(members, "kind", problems);
            var reply = Text(members, "repliesTo", problems);
            var summary = Text(members, "summary", problems);
            var argument = Text(members, "argument", problems);
            var citations = Citations(Items(members, "citations", problems), []);
            var claims = new List<ContributionClaim>();
            foreach (var (at, claim) in Objects(Items(members, "claims", problems), "claims", ClaimMembers, problems))
            {
                claims.Add(new ContributionClaim(Text(claim, at + ".claim", problems) ?? string.Empty, StringList(claim, at + ".support", problems)));
            }

            var concedes = StringList(members, "concedes", problems);
            var finished = Boolean(members, "finished", problems);
            if (problems.Count > 0 || !Enum.GetNames<ContributionKind>().Contains(kindName, StringComparer.Ordinal) || reply is null || summary is null || argument is null || finished is null)
            {
                return null;
            }

            return new ContributionAnswer(
                Enum.Parse<ContributionKind>(kindName!, ignoreCase: false),
                string.Equals(reply, ContributionLabels.None, StringComparison.Ordinal) ? null : reply,
                summary,
                argument,
                claims,
                citations,
                concedes,
                finished.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ContributionParseResult Rejected(string problem) => new(null, [problem]);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    // ---- reading the shape: members, types and nulls, named by the schema and never by what the participant wrote ----

    /// <summary>
    /// The members of <paramref name="element"/> that the schema has, by name, with <paramref name="prefix"/> (the JSON path of
    /// the object, empty for the root) in front of their names. A member the schema does not have is counted, never named; one
    /// of the schema written twice is named; one missing is named.
    /// </summary>
    private static Dictionary<string, JsonElement> Members(JsonElement element, string where, string prefix, string[] schema, List<string> problems)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var unknown = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!schema.Contains(property.Name, StringComparer.Ordinal))
            {
                unknown++;
            }
            else if (!members.TryAdd(prefix + property.Name, property.Value))
            {
                problems.Add($"'{prefix}{property.Name}' is written twice in {where}.");
            }
        }

        if (unknown > 0)
        {
            problems.Add(Invariant($"{Capitalized(where)} has {unknown} {(unknown == 1 ? "member" : "members")} the schema does not have: write only {string.Join(", ", schema)}."));
        }

        foreach (var name in schema.Where(n => !members.ContainsKey(prefix + n)))
        {
            problems.Add($"'{prefix}{name}' is missing.");
        }

        return members;
    }

    private static string? Text(Dictionary<string, JsonElement> members, string path, List<string> problems)
    {
        if (!members.TryGetValue(path, out var value))
        {
            return null; // already reported as missing
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        problems.Add($"'{path}' must be a string, not {Describe(value.ValueKind)}.");
        return null;
    }

    private static bool? Boolean(Dictionary<string, JsonElement> members, string path, List<string> problems)
    {
        if (!members.TryGetValue(path, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        problems.Add($"'{path}' must be true or false, not {Describe(value.ValueKind)}.");
        return null;
    }

    private static int? Integer(Dictionary<string, JsonElement> members, string path, List<string> problems)
    {
        if (!members.TryGetValue(path, out var value))
        {
            return null;
        }

        // A number written with a fraction or an exponent (10.0, 1e1) is not a whole number of the schema.
        if (value.ValueKind == JsonValueKind.Number && value.GetRawText().All(c => char.IsAsciiDigit(c) || c == '-') && value.TryGetInt32(out var number))
        {
            return number;
        }

        problems.Add($"'{path}' must be a whole number{(value.ValueKind == JsonValueKind.Number ? " that fits in 32 bits, without fraction or exponent" : $", not {Describe(value.ValueKind)}")}.");
        return null;
    }

    /// <summary>The items of a list member, or null when it is missing or not a list (reported).</summary>
    private static List<(string At, JsonElement Item)>? Items(Dictionary<string, JsonElement> members, string path, List<string> problems)
    {
        if (!members.TryGetValue(path, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"'{path}' must be a list, not {Describe(value.ValueKind)}.");
            return null;
        }

        return [.. value.EnumerateArray().Select((item, i) => (Invariant($"{path}[{i}]"), item))];
    }

    /// <summary>The object items of a list, each with its members; a null or another value in their place is reported.</summary>
    private static IEnumerable<(string At, Dictionary<string, JsonElement> Members)> Objects(List<(string At, JsonElement Item)>? items, string list, string[] schema, List<string> problems)
    {
        foreach (var (at, item) in items ?? [])
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"'{at}' must be an object of '{list}', not {Describe(item.ValueKind)}.");
                continue;
            }

            yield return (at, Members(item, $"'{at}'", at + ".", schema, problems));
        }
    }

    /// <summary>A list of strings; a null or another value in it is reported, and the list is null when it is missing or not a list.</summary>
    private static List<string> StringList(Dictionary<string, JsonElement> members, string path, List<string> problems)
    {
        var strings = new List<string>();
        foreach (var (at, item) in Items(members, path, problems) ?? [])
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                strings.Add(item.GetString()!);
            }
            else
            {
                problems.Add($"'{at}' must be a string, not {Describe(item.ValueKind)}.");
            }
        }

        return strings;
    }

    // ---- what the turn admits ----

    private static ContributionKind? Kind(string? value, ContributionRules rules, List<string> problems)
    {
        if (value is null)
        {
            return null;
        }

        var admitted = string.Join(", ", rules.Kinds);
        if (!Enum.GetNames<ContributionKind>().Contains(value, StringComparer.Ordinal))
        {
            problems.Add($"'kind' is not one of {admitted}.");
            return null;
        }

        var kind = Enum.Parse<ContributionKind>(value, ignoreCase: false);
        if (!rules.Kinds.Contains(kind))
        {
            problems.Add(Invariant($"'kind' {value} is not admitted in round {rules.Round}, which admits {admitted}."));
            return null;
        }

        return kind;
    }

    /// <summary>Whether <c>repliesTo</c> was read; <paramref name="reply"/> is the label replied to, or null for the sentinel.</summary>
    private static bool RepliesTo(string? value, ContributionRules rules, List<string> problems, out string? reply)
    {
        reply = null;
        if (value is null)
        {
            return false;
        }

        if (string.Equals(value, ContributionLabels.None, StringComparison.Ordinal))
        {
            return true;
        }

        if (rules.Contributions.Contains(value, StringComparer.Ordinal))
        {
            reply = value;
            return true;
        }

        problems.Add(rules.Contributions.Count == 0
            ? Invariant($"'repliesTo' must be '{ContributionLabels.None}': nothing is published before round {rules.Round}.")
            : $"'repliesTo' is not a published contribution ({Published(rules)}) nor '{ContributionLabels.None}'.");
        return false;
    }

    private static void Cap(string? value, string path, int cap, List<string> problems)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"'{path}' is blank.");
        }
        else if (value.Length > cap)
        {
            problems.Add(Invariant($"'{path}' has {value.Length} characters; it takes at most {cap}."));
        }
    }

    private static List<ContributionCitation> Citations(List<(string At, JsonElement Item)>? items, List<string> problems)
    {
        if (items is { Count: > ContributionLimits.MaxCitations })
        {
            problems.Add(Invariant($"'citations' has {items.Count} items; a contribution takes at most {ContributionLimits.MaxCitations}."));
        }

        var result = new List<ContributionCitation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (at, citation) in Objects(items, "citations", CitationMembers, problems))
        {
            var id = Text(citation, at + ".id", problems);
            var path = Text(citation, at + ".path", problems);
            var startLine = Integer(citation, at + ".startLine", problems);
            var endLine = Integer(citation, at + ".endLine", problems);
            var why = Text(citation, at + ".why", problems);

            if (id is not null)
            {
                if (!ContributionLabels.Citations.Contains(id, StringComparer.Ordinal))
                {
                    problems.Add($"'{at}.id' is not one of {ContributionLabels.Citations[0]} to {ContributionLabels.Citations[^1]}.");
                }
                else if (!ids.Add(id))
                {
                    problems.Add($"'{at}.id' {id} is already the id of another citation.");
                }
            }

            if (path is not null && CitationResolver.PathProblem(path) is { } invalid)
            {
                problems.Add($"'{at}.path' is not a path relative to the copy of the commit: {invalid}. Write it from the root of the copy, with '/' between folders (src/Program.cs).");
            }

            if (startLine is { } start && endLine is { } end && CitationResolver.LinesProblem(start, end) is { } lines)
            {
                problems.Add($"'{at}': {lines}.");
            }

            Cap(why, at + ".why", ContributionLimits.MaxWhyChars, problems);
            if (id is not null && path is not null && startLine is not null && endLine is not null && why is not null)
            {
                result.Add(new ContributionCitation(id, path, startLine.Value, endLine.Value, why));
            }
        }

        return result;
    }

    private static List<ContributionClaim> Claims(List<(string At, JsonElement Item)>? items, ContributionRules rules, HashSet<string> citationIds, List<string> problems)
    {
        if (items is { Count: > ContributionLimits.MaxClaims })
        {
            problems.Add(Invariant($"'claims' has {items.Count} items; a contribution takes at most {ContributionLimits.MaxClaims}."));
        }

        var result = new List<ContributionClaim>();
        foreach (var (at, claim) in Objects(items, "claims", ClaimMembers, problems))
        {
            var text = Text(claim, at + ".claim", problems);
            Cap(text, at + ".claim", ContributionLimits.MaxClaimChars, problems);
            var support = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (where, item) in Items(claim, at + ".support", problems) ?? [])
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"'{where}' must be a string, not {Describe(item.ValueKind)}.");
                    continue;
                }

                var value = item.GetString()!;
                if (!IsSupport(value, rules, citationIds))
                {
                    problems.Add($"'{where}' is not a published contribution ({Published(rules)}), an evidence of the material nor a citation of this contribution.");
                }
                else if (!seen.Add(value))
                {
                    problems.Add($"'{where}' {value} is already in the support of this claim.");
                }

                support.Add(value);
            }

            if (text is not null)
            {
                result.Add(new ContributionClaim(text, support));
            }
        }

        return result;
    }

    private static List<string> Concedes(List<(string At, JsonElement Item)>? items, ContributionRules rules, List<string> problems)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (at, item) in items ?? [])
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                problems.Add($"'{at}' must be a string, not {Describe(item.ValueKind)}.");
                continue;
            }

            var conceded = item.GetString()!;
            if (!rules.Contributions.Contains(conceded, StringComparer.Ordinal))
            {
                problems.Add($"'{at}' is not a published contribution ({Published(rules)}).");
            }
            else if (!seen.Add(conceded))
            {
                problems.Add($"'{at}' {conceded} is conceded twice.");
            }

            result.Add(conceded);
        }

        return result;
    }

    private static bool IsSupport(string support, ContributionRules rules, HashSet<string> citationIds) =>
        rules.Contributions.Contains(support, StringComparer.Ordinal)
        || rules.Evidence.Any(e => string.Equals(e.Value, support, StringComparison.Ordinal))
        || citationIds.Contains(support);

    private static string Published(ContributionRules rules) =>
        rules.Contributions.Count == 0 ? "nothing is published yet" : string.Join(", ", rules.Contributions);

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null (the schema has no null)",
        _ => "nothing",
    };

    private static string Capitalized(string where) => where.Length > 0 && char.IsAsciiLetterLower(where[0]) ? char.ToUpperInvariant(where[0]) + where[1..] : where;
}
