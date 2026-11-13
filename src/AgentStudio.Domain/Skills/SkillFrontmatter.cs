using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Domain.Skills;

/// <summary>
/// Minimal parser for the YAML-like header of a SKILL.md (<c>---</c> ... <c>---</c>), plus validation against the
/// Agent Skills specification.
/// <para>
/// Supported: top-level <c>key: value</c> pairs; plain, "double-quoted" and 'single-quoted' scalars; plain/quoted
/// values continued on indented lines (folded with a space); block scalars <c>&gt;</c> (folded) and <c>|</c>
/// (literal) with optional chomping indicators (<c>&gt;-</c>, <c>|+</c>); UTF-8 BOM and CRLF.
/// </para>
/// <para>
/// A key with an empty value followed by indented lines is a plain multiline scalar (folded with a space) unless
/// the first of those lines looks like a nested map entry (<c>k: v</c>) or a list item (<c>- x</c>). Duplicate keys
/// are reported by <see cref="DuplicateKeys"/>; the last occurrence wins.
/// </para>
/// <para>
/// Not supported (ignored, not an error): nested maps and lists (their lines are skipped, the parent key keeps an
/// empty value), anchors/aliases, flow collections, inline <c>#</c> comments on values, multi-document files, and
/// escapes other than <c>\"</c> and <c>\\</c> in double quotes. Chomping indicators are accepted but the result is
/// always trimmed.
/// </para>
/// </summary>
public sealed partial record SkillFrontmatter(
    bool HasHeader,
    IReadOnlyDictionary<string, string> Fields,
    int BodyLineCount)
{
    /// <summary>Top-level keys that appear more than once in the header (the last occurrence wins).</summary>
    public IReadOnlyList<string> DuplicateKeys { get; init; } = [];

    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;
    public const int RecommendedMaxBodyLines = 500;

    /// <summary>
    /// The keys a SKILL.md keeps when it is materialized for an agent session (ADR-0018, «Medición»): the ones that only
    /// describe the skill. Everything else is dropped, because a CLI may read it as a grant, a hook or another way to run:
    /// Claude Code 2.1.290 reads, among others, <c>allowed-tools</c> (allow rules while the skill is active),
    /// <c>disallowed-tools</c>, <c>hooks</c> and hook events at the top level, <c>context</c>, <c>agent</c>,
    /// <c>background</c>, <c>shell</c>, <c>model</c>, <c>effort</c>, <c>fallback</c>, <c>paths</c>, <c>arguments</c> and
    /// <c>metadata</c>. The agent's profile, not the skill, decides what the session may do.
    /// </summary>
    public static IReadOnlyList<string> SessionKeys { get; } =
        ["name", "description", "when_to_use", "argument-hint", "version", "license", "compatibility", "disable-model-invocation", "user-invocable"];

    /// <summary>The <see cref="SessionKeys"/> that are switches: kept only as a bare <c>true</c> or <c>false</c>.</summary>
    private static readonly HashSet<string> SessionSwitches = new(StringComparer.Ordinal) { "disable-model-invocation", "user-invocable" };

    /// <summary>
    /// The SKILL.md an agent session gets: a header rebuilt from the <see cref="SessionKeys"/> that <see cref="Parse"/>
    /// reads in <paramref name="skillMarkdown"/> (values double-quoted, the switches bare), then the body exactly as it
    /// was. The original header text never reaches the session, because Claude Code 2.1.290 ends a frontmatter at the
    /// first <c>---</c> anywhere, even inside a value: a header this parser does not see may still be one for the CLI. For
    /// the same reason every <c>-</c> of a value that holds <c>---</c> is written <c>\x2D</c>. A source without a header
    /// gets an empty one, so text it starts with is never read as a header. A leading BOM is dropped.
    /// </summary>
    public static string ForSession(string skillMarkdown)
    {
        ArgumentNullException.ThrowIfNull(skillMarkdown);
        var text = skillMarkdown.TrimStart((char)0xFEFF);
        var parsed = Parse(text);
        var header = new StringBuilder("---\n");
        foreach (var key in SessionKeys)
        {
            if (!parsed.Fields.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!SessionSwitches.Contains(key))
            {
                header.Append(key).Append(": ").Append(DoubleQuoted(value)).Append('\n');
            }
            else if (value.Trim() is var flag && (flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                header.Append(key).Append(": ").Append(flag.ToLowerInvariant()).Append('\n');
            }
        }

        var body = parsed.HasHeader ? text[BodyStart(text)..] : text;
        return header.Append("---\n").Append(body).ToString();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidName();

    // A nested map entry ("key: value" / "key:") or a list item ("- x" / "-").
    [GeneratedRegex(@"^(-(\s|$)|[^\s:#'""\-][^:]*:(\s|$))")]
    private static partial Regex NestedStart();

    public string? Name => Get("name");

    public string? Description => Get("description");

    private string? Get(string key) =>
        Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public static SkillFrontmatter Parse(string skillMarkdown)
    {
        ArgumentNullException.ThrowIfNull(skillMarkdown);
        var lines = skillMarkdown.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        var close = -1;
        if (lines[0].Trim() == "---")
        {
            for (var j = 1; j < lines.Length; j++)
            {
                if (lines[j].Trim() == "---")
                {
                    close = j;
                    break;
                }
            }
        }

        if (close < 0)
        {
            return new SkillFrontmatter(false, fields, CountLines(lines, 0));
        }

        var duplicates = new List<string>();
        var i = 1;
        while (i < close)
        {
            var line = lines[i];
            i++;
            if (string.IsNullOrWhiteSpace(line) || char.IsWhiteSpace(line[0]) || line[0] == '#' || line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            // Indented continuation lines (blank lines included) that belong to this key.
            var cont = new List<string>();
            while (i < close && (string.IsNullOrWhiteSpace(lines[i]) || char.IsWhiteSpace(lines[i][0])))
            {
                cont.Add(lines[i]);
                i++;
            }

            if (fields.ContainsKey(key) && !duplicates.Contains(key))
            {
                duplicates.Add(key);
            }

            if (value.Length > 0 && (value[0] == '>' || value[0] == '|'))
            {
                fields[key] = BlockScalar(value[0] == '>', cont);
            }
            else if (value.Length == 0)
            {
                var first = cont.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
                fields[key] = first is null || NestedStart().IsMatch(first)
                    ? string.Empty // nested map/list: skipped
                    : Unquote(string.Join(' ', cont.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())));
            }
            else
            {
                var joined = string.Join(' ', new[] { value }.Concat(cont.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())));
                fields[key] = Unquote(joined);
            }
        }

        return new SkillFrontmatter(true, fields, CountLines(lines, close + 1)) { DuplicateKeys = duplicates };
    }

    /// <summary>
    /// Checks the parsed header against the Agent Skills specification. The texts are codes (<see cref="SkillHealthTextCodes"/>). When <paramref name="contextTokens"/>
    /// is given (the estimated tokens of the whole SKILL.md, <see cref="SkillContextCost.OfText"/>), the size check carries it as a number next to the lines.
    /// </summary>
    public IReadOnlyList<SkillHealthCheck> Validate(string folderName, long? contextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(folderName);
        var checks = new List<SkillHealthCheck>();

        void Fail(SkillHealthStatus status, string textCode, params object[] arguments) =>
            checks.Add(SkillHealthCheck.Coded(SkillHealthCodes.Frontmatter, status, textCode, arguments));

        if (!HasHeader)
        {
            Fail(SkillHealthStatus.Critical, SkillHealthTextCodes.FrontmatterNoHeader);
        }
        else
        {
            var name = Name;
            if (name is null)
            {
                Fail(SkillHealthStatus.Critical, SkillHealthTextCodes.FrontmatterNameMissing);
            }
            else
            {
                if (name.Length > MaxNameLength)
                {
                    Fail(SkillHealthStatus.Critical, SkillHealthTextCodes.FrontmatterNameTooLong, MaxNameLength);
                }

                if (!ValidName().IsMatch(name))
                {
                    Fail(SkillHealthStatus.Critical, SkillHealthTextCodes.FrontmatterNameInvalid);
                }

                if (!string.Equals(name, folderName, StringComparison.Ordinal))
                {
                    Fail(SkillHealthStatus.Warning, SkillHealthTextCodes.FrontmatterNameFolderMismatch, name, folderName);
                }
            }

            var description = Description;
            if (description is null)
            {
                Fail(SkillHealthStatus.Critical, SkillHealthTextCodes.FrontmatterDescriptionMissing);
            }
            else if (description.Length > MaxDescriptionLength)
            {
                Fail(SkillHealthStatus.Critical, SkillHealthTextCodes.FrontmatterDescriptionTooLong, MaxDescriptionLength);
            }

            foreach (var key in DuplicateKeys)
            {
                Fail(SkillHealthStatus.Warning, SkillHealthTextCodes.FrontmatterDuplicateKey, key);
            }

            if (checks.Count == 0)
            {
                Fail(SkillHealthStatus.Healthy, SkillHealthTextCodes.FrontmatterOk);
            }
        }

        var tooLong = BodyLineCount > RecommendedMaxBodyLines;
        checks.Add((tooLong, contextTokens) switch
        {
            (true, { } tokens) => SkillHealthCheck.Coded(SkillHealthCodes.Size, SkillHealthStatus.Warning, SkillHealthTextCodes.SizeLongCost, BodyLineCount, RecommendedMaxBodyLines, tokens),
            (true, null) => SkillHealthCheck.Coded(SkillHealthCodes.Size, SkillHealthStatus.Warning, SkillHealthTextCodes.SizeLong, BodyLineCount, RecommendedMaxBodyLines),
            (false, { } tokens) => SkillHealthCheck.Coded(SkillHealthCodes.Size, SkillHealthStatus.Healthy, SkillHealthTextCodes.SizeOkCost, BodyLineCount, tokens),
            _ => SkillHealthCheck.Coded(SkillHealthCodes.Size, SkillHealthStatus.Healthy, SkillHealthTextCodes.SizeOk, BodyLineCount),
        });
        return checks;
    }

    /// <summary>
    /// Where the body of <paramref name="text"/> starts: right after its closing <c>---</c> line, found with the line rules
    /// of <see cref="Parse"/> (CRLF, LF or a lone CR end a line; the first line after the opening that trims to
    /// <c>---</c> closes the header). Only called when <see cref="Parse"/> found a header.
    /// </summary>
    private static int BodyStart(string text)
    {
        var start = 0;
        for (var line = 0; start < text.Length; line++)
        {
            var end = start;
            while (end < text.Length && text[end] is not ('\n' or '\r'))
            {
                end++;
            }

            var next = end == text.Length ? end : end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            if (line > 0 && text.AsSpan(start, end - start).Trim().SequenceEqual("---"))
            {
                return next;
            }

            start = next;
        }

        return text.Length;
    }

    /// <summary>A YAML double-quoted scalar that holds <paramref name="value"/> exactly and never spells <c>---</c>.</summary>
    private static string DoubleQuoted(string value)
    {
        var dashes = value.Contains("---", StringComparison.Ordinal);
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\n':
                    quoted.Append("\\n");
                    break;
                case '\r':
                    quoted.Append("\\r");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                case '-' when dashes:
                    quoted.Append("\\x2D");
                    break;
                case var other when char.IsControl(other) || other is (char)0x2028 or (char)0x2029 or (char)0xFEFF:
                    quoted.Append("\\u").Append(((int)other).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    private static int CountLines(string[] lines, int from)
    {
        var end = lines.Length;
        while (end > from && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        return Math.Max(0, end - from);
    }

    private static string BlockScalar(bool folded, List<string> cont)
    {
        var nonBlank = cont.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (nonBlank.Count == 0)
        {
            return string.Empty;
        }

        var indent = nonBlank.Min(c => c.Length - c.TrimStart().Length);
        var sb = new StringBuilder();
        foreach (var raw in nonBlank)
        {
            var text = raw[indent..].TrimEnd();
            if (sb.Length > 0)
            {
                sb.Append(folded ? ' ' : '\n');
            }

            sb.Append(text);
        }

        return sb.ToString().Trim();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }
}
