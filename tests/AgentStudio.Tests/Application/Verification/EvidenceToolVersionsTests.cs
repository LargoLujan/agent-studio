using AgentStudio.Application.Verification;

namespace AgentStudio.Tests.Application.Verification;

/// <summary>docs/20 D-12 (WP-E): the <c>tools:</c> block of the header of an evidence, written by the runner and read back by the card of the evidence and by Phase 16.</summary>
public sealed class EvidenceToolVersionsTests
{
    /// <summary>The first lines of a header as the runner writes them: the block comes right after them.</summary>
    private const string Head = "# Agent Studio · verification step 'b' (B)\n# command: dotnet build\n# isolation: none (the profile turns it off)\n";

    [Fact]
    public void The_block_has_one_line_per_tool_and_reads_back_as_it_was_written()
    {
        var tools = new[]
        {
            new EvidenceToolVersion("dotnet", "10.0.303", "version resource of the dotnet host; the SDK is the one global.json picks"),
            new EvidenceToolVersion("node", "22.11.0"),
            EvidenceToolVersion.Unknown("npm", "the package.json of npm was not found"),
        };

        var text = EvidenceToolVersions.FormatText(tools);

        Assert.Equal(
            "# tools:\n#   dotnet 10.0.303 (version resource of the dotnet host; the SDK is the one global.json picks)\n#   node 22.11.0\n#   npm unknown (the package.json of npm was not found)",
            text);
        Assert.Equal(tools, EvidenceToolVersions.Parse(Head + text + "\n# working directory: C:\\x\nstdout| 1\n"));
    }

    [Fact]
    public void Without_tools_there_is_no_block()
    {
        Assert.Empty(EvidenceToolVersions.Format([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# command: dotnet build\n# working directory: C:\\x\nstdout| ok\n# exit code: 0\n")]
    [InlineData("no header at all\n# tools:\n#   dotnet 1.0\n")]
    public void An_evidence_written_before_the_block_or_a_text_that_is_not_a_header_has_no_tools(string? content)
    {
        Assert.Empty(EvidenceToolVersions.Parse(content));
    }

    [Fact]
    public void Only_the_header_counts_and_the_first_block_wins()
    {
        var content = Head + "# tools:\n#   dotnet 10.0.303\n# started: now\nstdout| # tools:\n#   node 1.0\n# tools:\n#   python 3.12";

        var tools = EvidenceToolVersions.Parse(content);

        var tool = Assert.Single(tools);
        Assert.Equal(("dotnet", "10.0.303"), (tool.Tool, tool.Version));
    }

    [Fact]
    public void A_block_is_only_read_in_its_own_place_after_the_isolation_and_source_lines()
    {
        const string block = "# tools:\n#   dotnet 1.0\n";
        const string title = "# Agent Studio · verification step 'b' (B)\n";
        const string command = "# command: dotnet build\n";

        Assert.Single(EvidenceToolVersions.Parse(title + command + "# isolation: low integrity\n# source: commit abc\n" + block));
        Assert.Single(EvidenceToolVersions.Parse(title + command + "# isolation: none\n" + block));
        // A title that spans lines puts a block where the command line should be.
        Assert.Empty(EvidenceToolVersions.Parse(title.TrimEnd() + "\n" + block + command + "# isolation: none\n"));
        Assert.Empty(EvidenceToolVersions.Parse(title + block + command + "# isolation: none\n" + block));
        // A block with no header before it, or after the working directory, is not the block.
        Assert.Empty(EvidenceToolVersions.Parse(block));
        Assert.Empty(EvidenceToolVersions.Parse(title + command + "# isolation: none\n# working directory: x\n" + block));
        Assert.Empty(EvidenceToolVersions.Parse(title + command + "# isolation: none\n# source: a\n# source: b\n" + block));
    }

    [Fact]
    public void An_entry_that_is_not_well_formed_is_skipped_and_the_others_are_kept()
    {
        var content = Head + "# tools:\n#   dotnet 10.0.303\n#   lonely\n#   node 22 not-in-parentheses\n#   bad$name 1.0\n#   python 3.12.5 (note)\n";

        Assert.Equal(["dotnet", "python"], EvidenceToolVersions.Parse(content).Select(t => t.Tool));
    }

    [Fact]
    public void The_block_ends_at_the_first_line_that_is_not_an_entry()
    {
        var content = Head + "# tools:\n#   dotnet 1.0\n# working directory: x\n#   node 2.0\n";

        Assert.Equal(["dotnet"], EvidenceToolVersions.Parse(content).Select(t => t.Tool));
    }

    [Fact]
    public void Windows_line_endings_are_read_too_and_the_number_of_entries_is_bounded()
    {
        var many = Head.Replace("\n", "\r\n") + "# tools:\r\n" + string.Concat(Enumerable.Range(0, 100).Select(i => $"#   tool{i} 1.0\r\n"));

        var tools = EvidenceToolVersions.Parse(many);

        Assert.Equal(EvidenceToolVersions.MaxEntries, tools.Count);
        Assert.Equal(EvidenceToolVersions.MaxEntries + 1, EvidenceToolVersions.Format(Enumerable.Range(0, 100).Select(i => new EvidenceToolVersion($"tool{i}", "1.0"))).Count);
    }

    [Fact]
    public void The_version_of_a_tool_is_looked_up_ignoring_case_and_unknown_is_null()
    {
        var content = Head + "# tools:\n#   Node 22.11.0\n#   npm unknown (why)\n";

        Assert.Equal("22.11.0", EvidenceToolVersions.VersionOf(content, "node"));
        Assert.Null(EvidenceToolVersions.VersionOf(content, "npm"));
        Assert.Null(EvidenceToolVersions.VersionOf(content, "git"));
    }

    [Theory]
    [InlineData("two words", "1.0")]
    [InlineData("", "1.0")]
    [InlineData("node", "1.0 beta")]
    [InlineData("node", "")]
    [InlineData("nöde", "1.0")]
    public void A_tool_and_a_version_are_one_token(string tool, string version)
    {
        Assert.Throws<ArgumentException>(() => new EvidenceToolVersion(tool, version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("closes ) early")]
    [InlineData(" padded")]
    public void A_note_is_one_clean_line_without_a_closing_parenthesis(string note)
    {
        Assert.Throws<ArgumentException>(() => new EvidenceToolVersion("node", "1.0", note));
    }

    [Fact]
    public void A_reason_is_cleaned_and_cut_to_fit_and_a_name_becomes_a_token()
    {
        var unknown = EvidenceToolVersion.Unknown("node", "line one\nline (two) " + new string('x', 500));

        Assert.True(unknown.Note!.Length <= EvidenceToolVersion.MaxNoteLength);
        Assert.DoesNotContain('\n', unknown.Note);
        Assert.DoesNotContain(')', unknown.Note);
        Assert.Equal("My_Tool_1.0", EvidenceToolVersion.TokenOf("My Tool\u202E1.0"));
        Assert.Equal("_", EvidenceToolVersion.TokenOf(""));
        Assert.Equal("no reason given", EvidenceToolVersion.Unknown("node", "  ").Note);
    }
}
