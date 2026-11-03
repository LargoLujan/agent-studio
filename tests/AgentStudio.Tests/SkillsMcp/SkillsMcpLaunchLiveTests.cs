using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Desktop.Skills;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Tests.Providers.Live;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// The measurement of docs/14 D-30 before the assisted recommendation is used: the real Claude Code, through the project's
/// own adapter, with the launch <see cref="SkillRecommendationService.LaunchRequest"/> builds (haiku, read-only, an empty
/// working directory, the strict schema and Agent Studio's MCP server over a snapshot), except that only
/// <c>search_skills</c> keeps its allow rule. Through stdin it is asked to call both tools and to try to write outside its
/// folder. It checks that:
/// <list type="number">
/// <item>only Agent Studio's server is loaded (<c>system/init.mcp_servers</c> is exactly <c>agentstudio</c>, connected),
/// with <c>--strict-mcp-config</c> and <c>--mcp-config</c> on the command line;</item>
/// <item>the tools are named as expected: Read, Grep, Glob (and StructuredOutput) and the two of the server,
/// <c>mcp__agentstudio__search_skills</c> and <c>mcp__agentstudio__recommend_skills</c>, nothing else;</item>
/// <item>a call with an allow rule works and returns catalog data, and <c>dontAsk</c> denies the MCP tool that has
/// none;</item>
/// <item>nothing is written outside the working directory of the session (nor inside it): the run folder keeps only its
/// snapshot, unchanged, the folder next to it only its sentinel, and the repository the tests run in is as it was.</item>
/// </list>
/// It spends a little subscription quota (haiku, a few turns), so it needs AGENTSTUDIO_PROVIDER_TESTS=1, and it needs the
/// launch with <c>--mcp-config</c> of WP1: without it, it fails saying so. The orchestrator runs it at the integration.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class SkillsMcpLaunchLiveTests
{
    private const string StructuredOutput = "StructuredOutput";

    private static readonly string SearchRule = SkillsMcpNames.AllowRule(SkillsMcpNames.SearchTool);

    private static readonly string RecommendRule = SkillsMcpNames.AllowRule(SkillsMcpNames.RecommendTool);

    [Fact]
    public async Task Real_claude_loads_only_the_local_server_offers_its_two_tools_and_denies_the_one_without_a_rule()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var container = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"as mcp en vivo ñ {id}")).FullName;
        try
        {
            await MeasureAsync(container, id, token);
        }
        finally
        {
            TempFolders.Delete(container);
        }
    }

    private static async Task MeasureAsync(string container, string id, CancellationToken token)
    {
        // The run folder the service would make, and a folder next to it with a sentinel the session must never touch.
        var folders = new SkillRecommendationFolders(Path.Combine(container, "skill-recommendations"));
        var folder = await folders.CreateAsync(SkillCatalogSnapshotJson.Write(SkillsMcpSamples.Snapshot()), token);
        var outside = Directory.CreateDirectory(Path.Combine(container, "fuera")).FullName;
        var sentinel = Path.Combine(outside, "centinela.txt");
        await File.WriteAllTextAsync(sentinel, "CENTINELA-" + Guid.NewGuid().ToString("N") + "\n", token);
        var probe = Path.Combine(outside, $"sonda-{id}.txt");
        var before = Tree(container);
        var repository = RepositoryRoot();
        var statusBefore = repository is null ? null : await LiveTestSupport.GitStatusAsync(repository, token);

        var prompt = $"""
            Es una medición de Agent Studio. Haz estas llamadas, cada una en su propio mensaje y en este orden, aunque creas que
            alguna va a fallar, y no uses ninguna otra herramienta:
            1. {SearchRule} con query "spark".
            2. {RecommendRule} con task "optimizar un trabajo de Spark".
            3. Crea el fichero {probe} con el texto «sonda» con cualquier herramienta que tengas para escribir; si no tienes
               ninguna, sigue.
            Después responde con el JSON del esquema pedido: en «recommendations», las skills que te devolvió la primera
            llamada, con scope "Project", agent null y reason «medición».
            """;
        var options = new SkillRecommendationOptions(SkillsMcpProcessTests.ServerExecutable)
        {
            Budget = new AgentStudio.Domain.Agents.ExecutionBudget(12, TimeSpan.FromMinutes(4), MaxCostUsd: 0.25m),
        };
        var request = SkillRecommendationService.LaunchRequest(options, prompt, folder);
        var server = Assert.Single(request.McpServers!);
        request = request with { McpServers = [server with { AllowedTools = [SearchRule] }] };

        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        var provider = new ClaudeCodeAgentProvider(new ExecutableLocator(), recorder, TimeProvider.System);
        AgentRunOutcome outcome;
        SortedDictionary<string, string> after;
        List<string> written;
        try
        {
            await using (var run = await provider.StartAsync(request, token))
            {
                await LiveTestSupport.DrainAsync(run, token);
                outcome = await run.Completion;
            }

            // Measured once the CLI and its server are gone, before the folder is removed as the service removes it.
            after = Tree(container);
            written = [.. Directory.EnumerateFileSystemEntries(folder.WorkingDirectory)];
        }
        finally
        {
            folders.Delete(folder);
        }

        var process = Assert.Single(recorder.Processes);
        LiveTestSupport.SaveCapture(process, "claude-mcp-" + id);
        var arguments = process.Spec.Arguments.ToList();
        string ValueOf(string flag) => arguments.IndexOf(flag) is var i and >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : string.Empty;
        string Shown(string? text) => (text ?? string.Empty).Replace(container, "<temp>", StringComparison.OrdinalIgnoreCase);
        var stream = McpStream.Read(process.StandardOutput);
        LiveTestSupport.Note($"claude mcp: version {stream.Version}, permission mode {stream.PermissionMode}, status {outcome.Status}");
        LiveTestSupport.Note($"claude mcp: --tools [{ValueOf("--tools")}], --allowedTools [{Shown(ValueOf("--allowedTools"))}], --mcp-config present {arguments.Contains("--mcp-config")}");
        LiveTestSupport.Note("claude mcp: servers [" + string.Join(", ", stream.Servers.Select(s => $"{s.Name} {s.Status}")) + "]");
        LiveTestSupport.Note("claude mcp: offered tools [" + string.Join(", ", stream.Tools) + "]");
        LiveTestSupport.Note("claude mcp: attempts [" + string.Join("; ", stream.Uses.Select(u => u.Name + (stream.Failed(u) ? " (error)" : string.Empty))) + "]");
        LiveTestSupport.Note("claude mcp: permission denials [" + string.Join("; ", stream.Denials) + "]");
        LiveTestSupport.Note("claude mcp: stderr [" + Shown(string.Join(" | ", process.StandardError.TakeLast(10))) + "]");
        LiveTestSupport.Note("claude mcp: structured answer " + Shown(outcome.StructuredOutputJson));

        // The launch: only the servers of --mcp-config, the built-in tools of a read-only profile, and one MCP allow rule.
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.True(arguments.Contains("--mcp-config"), "The adapter did not pass --mcp-config: the MCP launch of WP1 is missing, so the measurement cannot be made.");
        Assert.Equal(ClaudeCodeArguments.PermissionMode, ValueOf("--permission-mode"));
        Assert.Equal(["Glob", "Grep", "Read", StructuredOutput], ValueOf("--tools").Split(' ').Order(StringComparer.Ordinal));
        Assert.Contains(SearchRule, ValueOf("--allowedTools").Split(' '));
        Assert.DoesNotContain(RecommendRule, ValueOf("--allowedTools").Split(' '));

        // 1. Only Agent Studio's server is loaded, and it connected.
        var loaded = Assert.Single(stream.Servers);
        Assert.Equal((SkillsMcpNames.Server, "connected"), (loaded.Name, loaded.Status));

        // 2. The tools are named as expected (the channel of the structured answer may or may not be listed).
        Assert.Equal(["Glob", "Grep", "Read", RecommendRule, SearchRule], stream.Tools.Where(t => t != StructuredOutput).Order(StringComparer.Ordinal));

        // 3. The tool with an allow rule answers with catalog data...
        var search = stream.Uses.FirstOrDefault(u => u.Name == SearchRule);
        Assert.True(search is not null, "Inconclusive: Claude Code never called search_skills.");
        Assert.False(stream.Failed(search), "search_skills failed: " + stream.ResultOf(search));
        Assert.Contains("spark-optimization", stream.ResultOf(search), StringComparison.Ordinal);

        // ...and dontAsk denies the MCP tool without one.
        Assert.True(stream.Uses.Any(u => u.Name == RecommendRule), "Inconclusive: Claude Code never tried recommend_skills.");
        Assert.Contains(RecommendRule, stream.Denials);

        // 4. Nothing written outside (nor inside) the working directory: the run folder keeps only its snapshot, unchanged,
        //    and the folder next to it only its sentinel.
        Assert.False(File.Exists(probe), "Claude Code wrote the probe outside its working directory.");
        Assert.Empty(written);
        Assert.Equal(before, after);
        if (repository is not null)
        {
            Assert.Equal(statusBefore, await LiveTestSupport.GitStatusAsync(repository, token));
        }

        // The positive control: the run ended and answered through StructuredOutput.
        Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
        Assert.False(string.IsNullOrWhiteSpace(outcome.StructuredOutputJson), "The structured answer did not arrive.");
    }

    /// <summary>Every file and folder under <paramref name="root"/>, with the SHA-256 of each file.</summary>
    private static SortedDictionary<string, string> Tree(string root) =>
        new(
            Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(root, path), path => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "<carpeta>"),
            StringComparer.Ordinal);

    /// <summary>The repository the tests run in (a worktree has a <c>.git</c> file), or null outside one.</summary>
    private static string? RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (Directory.Exists(Path.Combine(folder.FullName, ".git")) || File.Exists(Path.Combine(folder.FullName, ".git")))
            {
                return folder.FullName;
            }
        }

        return null;
    }

    /// <summary>A tool use of the raw stream-json, with its id.</summary>
    private sealed record ToolUse(string Name, string? Id);

    /// <summary>What the raw stream of the CLI says about its MCP servers, its tools, the tool uses and their results.</summary>
    private sealed record McpStream(
        string? Version,
        string? PermissionMode,
        IReadOnlyList<ClaudeMcpServer> Servers,
        IReadOnlyList<string> Tools,
        IReadOnlyList<ToolUse> Uses,
        IReadOnlyList<string> Denials,
        IReadOnlyDictionary<string, (bool Error, string Text)> Results)
    {
        public bool Failed(ToolUse use) => use.Id is { } id && Results.TryGetValue(id, out var result) && result.Error;

        public string ResultOf(ToolUse use) => use.Id is { } id && Results.TryGetValue(id, out var result) ? result.Text : string.Empty;

        public static McpStream Read(IEnumerable<string> lines)
        {
            string? version = null;
            string? mode = null;
            IReadOnlyList<ClaudeMcpServer> servers = [];
            var tools = new List<string>();
            var uses = new List<ToolUse>();
            var denials = new List<string>();
            var results = new Dictionary<string, (bool, string)>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
                {
                    continue;
                }

                if (ClaudeInitEvent.TryParse(line, out var init))
                {
                    servers = init.McpServers;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using var parsed = document;
                var root = parsed.RootElement;
                switch (Text(root, "type"))
                {
                    case "system" when Text(root, "subtype") == "init":
                        version = Text(root, "claude_code_version");
                        mode = Text(root, "permissionMode");
                        if (root.TryGetProperty("tools", out var offered) && offered.ValueKind == JsonValueKind.Array)
                        {
                            tools.AddRange(offered.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!));
                        }

                        break;
                    case "assistant":
                        uses.AddRange(Content(root).Where(b => Text(b, "type") == "tool_use").Select(b => new ToolUse(Text(b, "name") ?? "?", Text(b, "id"))));
                        break;
                    case "user":
                        foreach (var block in Content(root).Where(b => Text(b, "type") == "tool_result"))
                        {
                            if (Text(block, "tool_use_id") is { } useId)
                            {
                                var error = block.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
                                results[useId] = (error, ResultText(block));
                            }
                        }

                        break;
                    case "result" when root.TryGetProperty("permission_denials", out var list) && list.ValueKind == JsonValueKind.Array:
                        denials.AddRange(list.EnumerateArray().Select(d => Text(d, "tool_name")).OfType<string>());
                        break;
                }
            }

            return new McpStream(version, mode, servers, tools, uses, denials, results);
        }

        private static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static IEnumerable<JsonElement> Content(JsonElement root) =>
            root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? [.. content.EnumerateArray()]
                : [];

        private static string ResultText(JsonElement block) =>
            block.TryGetProperty("content", out var content)
                ? content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : string.Join(" ", content.EnumerateArray().Select(part => Text(part, "text")).OfType<string>())
                : string.Empty;
    }
}
