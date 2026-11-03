using System.IO;
using System.Text.Json;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Skills;
using AgentStudio.SkillsMcp;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using static AgentStudio.Tests.SkillsMcp.SkillsMcpSamples;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// docs/14 D-30, driven over two in-memory pipes (no process, no socket, no file): the server offers exactly two read-only
/// tools over the snapshot it was given, answers with catalog metadata only, rejects invalid arguments, and uses no
/// network nor configuration API.
/// </summary>
public sealed class SkillsMcpServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public async Task The_server_offers_exactly_the_two_tools_the_launch_allows_and_both_are_read_only_and_closed_world()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        var tools = await mcp.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal([SkillsMcpNames.RecommendTool, SkillsMcpNames.SearchTool], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["mcp__agentstudio__search_skills", "mcp__agentstudio__recommend_skills"], SkillsMcpNames.AllowRules);
        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            Assert.True(annotations.ReadOnlyHint, $"{tool.Name} is not read-only.");
            Assert.False(annotations.OpenWorldHint, $"{tool.Name} says it reaches outside the snapshot.");
            Assert.False(annotations.DestructiveHint, $"{tool.Name} says it may destroy something.");
            Assert.True(annotations.IdempotentHint, $"{tool.Name} is not idempotent.");
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
    }

    [Fact]
    public async Task The_server_offers_tools_and_nothing_else()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        var capabilities = mcp.Client.ServerCapabilities;

        Assert.NotNull(capabilities.Tools);
        Assert.Null(capabilities.Resources);
        Assert.Null(capabilities.Prompts);
        Assert.Null(capabilities.Completions);
    }

    [Fact]
    public async Task Search_answers_with_the_catalog_metadata_of_the_snapshot()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        var result = await mcp.CallAsync(SkillsMcpNames.SearchTool, Args(("query", "spark")), Ct);

        Assert.NotEqual(true, result.IsError);
        using var answer = JsonDocument.Parse(Text(result));
        Assert.Equal(1, answer.RootElement.GetProperty("total").GetInt32());
        var skill = Assert.Single(answer.RootElement.GetProperty("skills").EnumerateArray());
        Assert.Equal("spark-optimization", skill.GetProperty("id").GetString());
        Assert.Equal("Optimiza trabajos de Spark y tablas Delta.", skill.GetProperty("summary").GetString());
        Assert.Equal(("ingenieria-datos", "Ingeniería de datos"), (skill.GetProperty("sector").GetString(), skill.GetProperty("sectorTitle").GetString()));
        Assert.Equal(["delta", "ingenieria-datos", "spark"], skill.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("Healthy", skill.GetProperty("health").GetString());
        Assert.False(skill.GetProperty("linked").GetBoolean());
        Assert.Equal(SkillContextCost.Of(Catalog[0].Entry).Value, skill.GetProperty("estimatedContextTokens").GetInt64());
    }

    [Fact]
    public async Task Search_says_which_agents_have_a_skill_active_and_which_skills_cannot_be_given_to_an_agent()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        using var wpf = JsonDocument.Parse(Text(await mcp.CallAsync(SkillsMcpNames.SearchTool, Args(("query", "wpf")), Ct)));
        using var linked = JsonDocument.Parse(Text(await mcp.CallAsync(SkillsMcpNames.SearchTool, Args(("query", "tokens")), Ct)));

        Assert.Equal(["ejecutor"], wpf.RootElement.GetProperty("skills")[0].GetProperty("activeAgents").EnumerateArray().Select(a => a.GetString()));
        Assert.True(linked.RootElement.GetProperty("skills")[0].GetProperty("linked").GetBoolean());
    }

    [Fact]
    public async Task Search_filters_by_sector_and_tags_and_cuts_at_top_while_the_total_counts_every_hit()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        using var bySector = JsonDocument.Parse(Text(await mcp.CallAsync(SkillsMcpNames.SearchTool, Args(("query", ""), ("sector", "ingenieria-datos"), ("top", 1)), Ct)));
        using var byTags = JsonDocument.Parse(Text(await mcp.CallAsync(SkillsMcpNames.SearchTool, Args(("query", ""), ("tags", new[] { "sql" })), Ct)));

        Assert.Equal(2, bySector.RootElement.GetProperty("total").GetInt32());
        Assert.Single(bySector.RootElement.GetProperty("skills").EnumerateArray());
        Assert.Equal(["sql-optimization"], byTags.RootElement.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Recommend_is_the_deterministic_recommender_over_the_snapshot()
    {
        const string Task = "Optimizar un trabajo de Spark que escribe tablas Delta";
        var expected = SkillRecommender.Recommend(new SkillQuery(Task, new HashSet<string>(StringComparer.Ordinal) { "sql" }, null), Catalog.Select(c => c.Entry), 5);
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        var result = await mcp.CallAsync(SkillsMcpNames.RecommendTool, Args(("task", Task), ("contextTags", new[] { "sql" })), Ct);

        Assert.NotEqual(true, result.IsError);
        using var answer = JsonDocument.Parse(Text(result));
        var recommendations = answer.RootElement.GetProperty("recommendations").EnumerateArray().ToList();
        Assert.Equal(expected.Select(r => r.Skill.Id.Value), recommendations.Select(r => r.GetProperty("skill").GetProperty("id").GetString()));
        Assert.Equal(expected.Select(r => r.Score), recommendations.Select(r => r.GetProperty("score").GetDouble()));
        Assert.Equal(expected[0].Reasons, recommendations[0].GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task No_answer_carries_a_path_a_source_or_the_body_of_a_skill()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        var texts = new[]
        {
            Text(await mcp.CallAsync(SkillsMcpNames.SearchTool, Args(("query", ""), ("top", 20)), Ct)),
            Text(await mcp.CallAsync(SkillsMcpNames.RecommendTool, Args(("task", "spark sql wpf tdd tokens"), ("top", 10)), Ct)),
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain("skills-index", text, StringComparison.Ordinal);
            Assert.DoesNotContain(":\\\\", text, StringComparison.Ordinal);
            Assert.DoesNotContain("owner/private-repo", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Sha, text, StringComparison.Ordinal);
            Assert.DoesNotContain("SKILL.md", text, StringComparison.Ordinal);
        }
    }

    public static TheoryData<string, string, object?> InvalidArguments => new()
    {
        { SkillsMcpNames.SearchTool, "top", 0 },
        { SkillsMcpNames.SearchTool, "top", 21 },
        { SkillsMcpNames.SearchTool, "query", new string('a', SkillSearch.MaxQueryLength + 1) },
        { SkillsMcpNames.SearchTool, "sector", "no-existe" },
        { SkillsMcpNames.SearchTool, "tags", new[] { "bad/tag!" } },
        { SkillsMcpNames.SearchTool, "tags", Enumerable.Range(0, 11).Select(i => "t" + i).ToArray() },
        { SkillsMcpNames.RecommendTool, "task", "" },
        { SkillsMcpNames.RecommendTool, "task", "   " },
        { SkillsMcpNames.RecommendTool, "task", new string('a', SkillsMcpServer.MaxTaskLength + 1) },
        { SkillsMcpNames.RecommendTool, "top", 0 },
        { SkillsMcpNames.RecommendTool, "top", 11 },
        { SkillsMcpNames.RecommendTool, "contextTags", new[] { "bad/tag!" } },
        { SkillsMcpNames.RecommendTool, "contextTags", Enumerable.Range(0, 21).Select(i => "t" + i).ToArray() },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task Invalid_arguments_are_rejected_as_a_tool_error_the_model_can_read(string tool, string argument, object? value)
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);
        var arguments = tool == SkillsMcpNames.SearchTool ? Args(("query", "spark")) : Args(("task", "spark"));
        arguments[argument] = value;

        var result = await mcp.CallAsync(tool, arguments, Ct);

        Assert.True(result.IsError, $"{tool} accepted {argument} = {value}.");
        Assert.False(string.IsNullOrWhiteSpace(Text(result)));
    }

    [Fact]
    public async Task A_tool_the_server_does_not_have_is_refused()
    {
        await using var mcp = await InMemoryMcp.StartAsync(Snapshot(), Ct);

        var refused = await Record.ExceptionAsync(() => mcp.CallAsync("activate_skill", Args(("skillId", "spark-optimization")), Ct));

        Assert.IsType<McpProtocolException>(refused);
    }

    [Fact]
    public void The_server_uses_no_network_nor_configuration_api()
    {
        var references = typeof(SkillsMcpServer).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Extensions.Configuration", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("ModelContextProtocol.AspNetCore", StringComparison.Ordinal));
    }

    // ---- the command line and the snapshot file ----

    [Theory]
    [InlineData]
    [InlineData("--snapshot")]
    [InlineData("--snapshot", "relativo.json")]
    [InlineData("--snapshot", @"C:\runs\snapshot.json", "--extra")]
    [InlineData("--otro", @"C:\runs\snapshot.json")]
    [InlineData("--snapshot", "   ")]
    [InlineData("--snapshot", @"C:\runs\snapshot.json", "--snapshot", @"C:\runs\otra.json")]
    [InlineData("--history", @"C:\runs\history.json", "--history", @"C:\runs\otra.json")]
    [InlineData("--history", "relativo.json")]
    [InlineData("--history", @"C:\runs\history.json", "--snapshot", "relativo.json")]
    [InlineData("--history", @"C:\runs\history.json", "--snapshot")]
    public void The_only_command_line_is_the_absolute_path_of_each_snapshot_once(params string[] args)
    {
        Assert.False(Program.TryParseArguments(args, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void An_absolute_snapshot_path_is_accepted()
    {
        Assert.True(Program.TryParseArguments(["--snapshot", @"C:\runs\a b\snapshot.json"], out var arguments, out _));
        Assert.Equal(new ServerArguments(@"C:\runs\a b\snapshot.json", null), arguments);
    }

    [Fact]
    public void The_history_alone_or_with_the_catalog_in_either_order_is_accepted()
    {
        Assert.True(Program.TryParseArguments(["--history", @"C:\runs\a b\history.json"], out var history, out _));
        Assert.True(Program.TryParseArguments(["--history", @"C:\runs\h.json", "--snapshot", @"C:\runs\s.json"], out var both, out _));
        Assert.True(Program.TryParseArguments(["--snapshot", @"C:\runs\s.json", "--history", @"C:\runs\h.json"], out var bothAgain, out _));

        Assert.Equal(new ServerArguments(null, @"C:\runs\a b\history.json"), history);
        Assert.Equal(new ServerArguments(@"C:\runs\s.json", @"C:\runs\h.json"), both);
        Assert.Equal(both, bothAgain);
    }

    [Fact]
    public void The_snapshot_file_reads_what_the_service_writes()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "snapshot.json");
        File.WriteAllText(path, SkillCatalogSnapshotJson.Write(Snapshot()));

        var read = SnapshotFile.Read(path);

        Assert.Equal(Snapshot().Skills.Select(s => s.Id), read.Skills.Select(s => s.Id));
    }

    [Fact]
    public void A_snapshot_that_is_missing_too_large_a_folder_or_not_a_snapshot_is_refused()
    {
        using var folder = new TempFolder();
        var invalid = Path.Combine(folder.Path, "invalid.json");
        File.WriteAllText(invalid, "{\"schema\":\"otra\"}");
        var large = Path.Combine(folder.Path, "large.json");
        File.WriteAllText(large, new string(' ', (int)SnapshotFile.MaxBytes + 1));

        Assert.ThrowsAny<IOException>(() => SnapshotFile.Read(Path.Combine(folder.Path, "missing.json")));
        Assert.ThrowsAny<IOException>(() => SnapshotFile.Read(folder.Path));
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Read(invalid));
        Assert.Throws<InvalidDataException>(() => SnapshotFile.Read(large));
    }

    /// <summary>A temporary folder deleted at the end, never through a link.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "as mcp " + Guid.NewGuid().ToString("N")[..12])).FullName;

        public void Dispose() => TestSupport.TempFolders.Delete(Path);
    }
}
