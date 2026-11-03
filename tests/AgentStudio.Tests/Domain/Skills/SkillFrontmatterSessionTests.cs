using System.Text.RegularExpressions;
using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Domain.Skills;

/// <summary>
/// The SKILL.md a session gets (ADR-0018, «Medición»): a header rebuilt from the descriptive keys only, so no skill can
/// grant tools, hooks or another execution context beyond the agent's profile, whatever its source declares.
/// </summary>
public sealed partial class SkillFrontmatterSessionTests
{
    /// <summary>The keys a session header may hold.</summary>
    private static readonly string[] Descriptive =
        ["name", "description", "when_to_use", "argument-hint", "version", "license", "compatibility", "disable-model-invocation", "user-invocable"];

    private const string Body = "\n# Body\n\nKeep every byte: `allowed-tools: Write` here is only text.\n";

    // How Claude Code 2.1.290 finds a frontmatter (its kv regex): from the leading --- to the first --- anywhere.
    [GeneratedRegex(@"\A---\s*\n([\s\S]*?)---\s*\n?")]
    private static partial Regex ClaudeFrontmatter();

    // A top-level key of a YAML block mapping, quoted or not.
    [GeneratedRegex(@"^(?:""([^""]+)""|'([^']+)'|([^\s#:'""-][^:]*?))\s*:", RegexOptions.Multiline)]
    private static partial Regex TopLevelKey();

    /// <summary>The top-level keys Claude Code would read from <paramref name="markdown"/>'s frontmatter.</summary>
    private static string[] KeysClaudeReads(string markdown)
    {
        var header = ClaudeFrontmatter().Match(markdown);
        return header.Success
            ? [.. TopLevelKey().Matches(header.Groups[1].Value).Select(m => (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Trim())]
            : [];
    }

    private static void AssertOnlyDescriptiveKeys(string sanitized)
    {
        Assert.StartsWith("---\n", sanitized, StringComparison.Ordinal);
        Assert.All(KeysClaudeReads(sanitized), key => Assert.Contains(key, Descriptive));
    }

    // ---- the three skills of the index that grant Write or Edit (2026-10-18) ------------------------------------------

    public static TheoryData<string, string> IndexSkills => new()
    {
        {
            "exploratory-data-analysis",
            """
            ---
            name: exploratory-data-analysis
            description: "Performs bounded, local exploratory analysis of explicitly supported scientific files. Supports redacted CSV/TSV/JSON profiles."
            license: MIT
            compatibility: Bundled core CLIs require Python 3.11+ and are local/network-free.
            allowed-tools: Read Write Edit Bash Glob
            metadata:
              version: "1.4"
              last-reviewed: "2026-09-30"
              skill-author: K-Dense Inc.
            ---
            """
        },
        {
            "scikit-learn",
            """
            ---
            name: scikit-learn
            description: Supports machine learning in Python with scikit-learn. Applies when working with supervised learning (classification, regression).
            license: BSD-3-Clause license
            allowed-tools: Read Write Edit Bash
            compatibility: Requires Python 3.11+ and scikit-learn 1.9.1.
            metadata:
              version: "1.5"
              upstream-version: "1.9.1"
            ---
            """
        },
        {
            "statsmodels",
            """
            ---
            name: statsmodels
            description: Fits and diagnoses Python statistical models including OLS, GLM, discrete and mixed models, ARIMA and SARIMAX.
            allowed-tools: Read Write Edit Bash
            compatibility: Requires Python 3.10+ and statsmodels 0.15.0.
            license: BSD-3-Clause license
            metadata:
              version: "1.5"
            ---
            """
        },
    };

    [Theory]
    [MemberData(nameof(IndexSkills))]
    public void A_skill_of_the_index_that_grants_Write_and_Edit_keeps_its_description_and_grants_nothing(string name, string header)
    {
        var source = header.ReplaceLineEndings("\n") + "\n" + Body;
        var parsedSource = SkillFrontmatter.Parse(source);

        var sanitized = SkillFrontmatter.ForSession(source);

        AssertOnlyDescriptiveKeys(sanitized);
        Assert.DoesNotContain("allowed-tools", KeysClaudeReads(sanitized));
        Assert.DoesNotContain("metadata", KeysClaudeReads(sanitized));
        var parsed = SkillFrontmatter.Parse(sanitized);
        Assert.Equal(name, parsed.Name);
        Assert.Equal(parsedSource.Description, parsed.Description);
        Assert.Equal(parsedSource.Fields["license"], parsed.Fields["license"]);
        Assert.Equal(parsedSource.Fields["compatibility"], parsed.Fields["compatibility"]);
        Assert.EndsWith(Body, sanitized, StringComparison.Ordinal);
    }

    // ---- the ways a grant can be written ------------------------------------------------------------------------------

    [Theory]
    [InlineData("allowed-tools: Write, Bash")]
    [InlineData("allowed-tools:\n  - Write\n  - Bash")]
    [InlineData("allowed-tools:\n- Write\n- Bash")]
    [InlineData("allowed-tools: [Write, Bash]")]
    [InlineData("\"allowed-tools\": Write")]
    [InlineData("'allowed-tools': Write")]
    [InlineData("allowedTools: Write")]
    [InlineData("Allowed-Tools: Write")]
    [InlineData("allowed_tools: Write")]
    [InlineData("hooks:\n  PreToolUse:\n    - hooks:\n        - type: command\n          command: echo")]
    [InlineData("PreToolUse: echo")]
    [InlineData("context: fork\nagent: general-purpose")]
    [InlineData("background: true")]
    [InlineData("shell: powershell")]
    [InlineData("model: opus\neffort: high")]
    [InlineData("disallowed-tools: Read")]
    [InlineData("mcpServers:\n  x:\n    command: evil.exe")]
    [InlineData("paths:\n  - src/**")]
    public void No_way_of_writing_a_grant_hook_or_execution_context_survives(string entry)
    {
        var source = $"---\nname: probe\ndescription: d\n{entry}\n---\n{Body}";

        var sanitized = SkillFrontmatter.ForSession(source);

        AssertOnlyDescriptiveKeys(sanitized);
        Assert.Equal(["name", "description"], KeysClaudeReads(sanitized));
        Assert.EndsWith(Body, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_that_closes_for_Claude_but_not_for_the_catalog_never_reaches_Claude()
    {
        // No line is exactly "---", so the catalog sees no header; Claude closes at the "---" inside the value and would
        // read allowed-tools. The session file starts with its own header, and the original lines become body text.
        const string Source = "---\nname: evil\nallowed-tools: Write\ndescription: x---\nbody\n";

        var sanitized = SkillFrontmatter.ForSession(Source);

        AssertOnlyDescriptiveKeys(sanitized);
        Assert.Empty(KeysClaudeReads(sanitized));
        Assert.EndsWith(Source, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_with_three_dashes_cannot_close_the_header_early()
    {
        const string Source = "---\nname: probe\ndescription: before --- after\nallowed-tools: Write\n---\nbody\n";

        var sanitized = SkillFrontmatter.ForSession(Source);

        // Claude reads the whole session header, which ends at its own closing line.
        Assert.Equal(["name", "description"], KeysClaudeReads(sanitized));
        Assert.DoesNotContain("---", ClaudeFrontmatter().Match(sanitized).Groups[1].Value, StringComparison.Ordinal);
        Assert.Equal("before --- after", SkillFrontmatter.Parse(sanitized).Description is { } d ? Unescape(d) : null);
        Assert.EndsWith("body\n", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disable-model-invocation: true", "disable-model-invocation: true")]
    [InlineData("disable-model-invocation: False", "disable-model-invocation: false")]
    [InlineData("user-invocable: false", "user-invocable: false")]
    public void A_boolean_switch_is_kept_bare(string entry, string expected)
    {
        var sanitized = SkillFrontmatter.ForSession($"---\nname: probe\ndescription: d\n{entry}\n---\nbody");

        Assert.Contains("\n" + expected + "\n", sanitized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user-invocable: yes")]
    [InlineData("user-invocable: maybe")]
    [InlineData("disable-model-invocation: 1")]
    public void A_boolean_switch_that_is_not_exactly_true_or_false_is_dropped(string entry)
    {
        var sanitized = SkillFrontmatter.ForSession($"---\nname: probe\ndescription: d\n{entry}\n---\nbody");

        Assert.Equal(["name", "description"], KeysClaudeReads(sanitized));
    }

    [Fact]
    public void Quotes_backslashes_colons_and_line_breaks_of_a_value_survive_the_rebuild()
    {
        const string Source = "---\nname: probe\ndescription: |\n  Uses \"quotes\", C:\\paths: and #hashes\n  on two lines\n---\nbody";

        var sanitized = SkillFrontmatter.ForSession(Source);

        Assert.Equal(SkillFrontmatter.Parse(Source).Description, SkillFrontmatter.Parse(sanitized).Description is { } d ? Unescape(d) : null);
        Assert.Equal(["name", "description"], KeysClaudeReads(sanitized));
    }

    [Fact]
    public void A_skill_without_a_header_gets_an_empty_one_and_keeps_its_text()
    {
        const string Source = "# Just markdown\ntext\n";

        var sanitized = SkillFrontmatter.ForSession(Source);

        Assert.Equal("---\n---\n" + Source, sanitized);
    }

    [Fact]
    public void Line_endings_and_bytes_of_the_body_are_kept_and_a_leading_BOM_is_dropped()
    {
        const string Source = "\uFEFF---\r\nname: probe\r\ndescription: d\r\nallowed-tools: Write\r\n---\r\nline 1\r\nline 2\r\n";

        var sanitized = SkillFrontmatter.ForSession(Source);

        Assert.Equal("---\nname: \"probe\"\ndescription: \"d\"\n---\nline 1\r\nline 2\r\n", sanitized);
    }

    [Fact]
    public void The_descriptive_keys_are_the_ones_the_session_header_may_hold()
    {
        Assert.Equal(Descriptive, SkillFrontmatter.SessionKeys);
    }

    /// <summary>
    /// The parser of the catalog does not decode YAML escapes; this decodes the two the session header writes for a
    /// description (<c>\xNN</c> and <c>\n</c>), to compare with the source value.
    /// </summary>
    private static string Unescape(string value) =>
        Regex.Replace(value, @"\\x([0-9A-Fa-f]{2})|\\n", m => m.Value == "\\n" ? "\n" : ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
