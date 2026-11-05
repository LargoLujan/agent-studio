using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Semantic;
using AgentStudio.Desktop.Composition;
using AgentStudio.Desktop.Memory;
using AgentStudio.Desktop.Services;
using AgentStudio.Domain.Agents;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Orchestration.Fakes;
using AgentStudio.Tests.Providers.Live;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Tests.SkillsMcp;

/// <summary>
/// F12-9 (docs/17 D-06 and §9.2; ADR-0024, addenda of 2026-11-03 and 2026-11-04): the measurement of docs/14 D-30 repeated for
/// the history tool, before it reaches <c>main</c>. A real planning, through the project's own adapter of Claude Code, with
/// the launch the planner builds (<see cref="Orchestrator.PlanAsync"/>: haiku, read-only, the plan schema, <c>dontAsk</c>,
/// <c>--setting-sources ""</c>, <c>--strict-mcp-config</c> and <c>--mcp-config</c> with Agent Studio's server over a
/// <c>--history</c> snapshot, and the prompt through stdin). The snapshot is the one the app takes
/// (<see cref="IProjectHistoryTool"/> of <see cref="AppComposition.AddAgentStudioApp"/>) of a temporary project where a few
/// trusted entries were seeded. Through the request it is asked to call <c>search_project_history</c> with a query whose
/// answer is in the snapshot, and to try to write a file. It checks that:
/// <list type="number">
/// <item>only Agent Studio's server is loaded (<c>system/init.mcp_servers</c> is exactly <c>agentstudio</c>, connected),
/// with <c>--strict-mcp-config</c> and <c>--mcp-config</c> on the command line;</item>
/// <item>the tools are the reading ones of the planner and <c>mcp__agentstudio__search_project_history</c> (and
/// StructuredOutput): nothing that writes, nor a shell;</item>
/// <item>the call works and returns the seeded fragment, as data;</item>
/// <item>nothing is written outside the working directory of the session, nor inside it, and the snapshot stays as it was
/// written;</item>
/// <item>the snapshot is deleted when the planning ends;</item>
/// <item>and it leaves in the output the turns, tokens, cost and time of each launch, to compare with the measurement of
/// D-30 of Phase 4 (the assisted recommendation with haiku: 5 turns and 0.034 USD, ADR-0024).</item>
/// </list>
/// It spends a little subscription quota (haiku, a planning, perhaps its one correction), so it needs
/// AGENTSTUDIO_PROVIDER_TESTS=1. No app is created: the composition gives the snapshot, and the engine of the tests plans.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class ProjectHistoryLaunchLiveTests
{
    private const string StructuredOutput = "StructuredOutput";

    /// <summary>The reference of D-30 (ADR-0024, «Medición D-30», 2026-10-18), to set against what the history tool costs.</summary>
    private const string D30Reference = "D-30 (Fase 4, recomendación asistida con haiku y su servidor MCP): 5 turnos, 0,034 USD";

    private static readonly string HistoryRule = ProjectHistoryLaunch.AllowRule;

    [Fact]
    public async Task A_real_planning_loads_only_the_local_server_with_the_history_tool_finds_the_seeded_fragment_and_writes_nothing()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var container = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"as historial en vivo ñ {id}")).FullName;
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
        // The app as it composes itself, over folders of the test: its memory, its index and its history tool. The folders of
        // the snapshots are watched, to know what was written, whether it changed and whether it was deleted.
        var snapshots = new WatchedHistoryFolders(new ProjectHistoryFolders(Path.Combine(container, "history-snapshots")));
        var services = new ServiceCollection();
        services.AddWindowsDoubles();
        services.AddSingleton<IProjectHistoryFolders>(snapshots);
        services.AddAgentStudioApp(new AppFolders(new AppDataPaths(Path.Combine(container, "roaming")), new LocalDataPaths(Path.Combine(container, "local")), null));
        await using var app = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // A few trusted entries of a temporary project: one holds the answer, with a marker no model could guess.
        var marker = "F129-" + id.ToUpperInvariant();
        var seeded = await app.GetRequiredService<IProjectWorkspace>().OpenAsync(Directory.CreateDirectory(Path.Combine(container, "proyecto")).FullName, token);
        var memory = app.GetRequiredService<IProjectMemory>();
        await memory.RecordAsync(seeded, new MemoryDraft(MemoryKind.Decision, "Calc", $"En Calc el tipo numérico es decimal y nunca double, para no perder céntimos (marca {marker}).", MemoryOrigin.System, "nota:1"), token);
        await memory.RecordAsync(seeded, new MemoryDraft(MemoryKind.KnownError, "Pruebas", "La prueba de la división falla si el divisor es cero.", MemoryOrigin.System, "nota:2"), token);
        await memory.RecordAsync(seeded, new MemoryDraft(MemoryKind.Convention, "Estilo", "Los métodos públicos de Calc llevan un comentario de documentación.", MemoryOrigin.System, "nota:3"), token);

        // The engine of the tests, with the real adapter of Claude Code instead of its scripted one. Each launch is recorded,
        // and the tree of its working directory is taken just before it starts.
        using var harness = new EngineHarness();
        var outside = Directory.CreateDirectory(Path.Combine(container, "fuera")).FullName;
        var sentinel = Path.Combine(outside, "centinela.txt");
        await File.WriteAllTextAsync(sentinel, "CENTINELA-" + Guid.NewGuid().ToString("N") + "\n", token);
        var probe = Path.Combine(outside, $"sonda-{id}.txt");
        var outsideBefore = Tree(outside);
        var repository = RepositoryRoot();
        var statusBefore = repository is null ? null : await LiveTestSupport.GitStatusAsync(repository, token);
        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        var launches = new LaunchTrees(recorder);
        var claude = new ClaudeCodeAgentProvider(new ExecutableLocator(), launches, TimeProvider.System);
        harness.Providers = new FakeProjectAgentProviders([AgentProviderPipeline.Compose(claude, harness.Journal, TimeProvider.System)]);
        var orchestrator = new Orchestrator(
            harness.Decisions, harness.Evidence, harness.Plans, harness.Runs, harness.RequirementStore, harness.Roster, harness.Providers,
            harness.Worktrees, harness.Verification, harness.Integrator, harness.Review, harness.Approvals, harness.Skills, harness.Paths,
            harness.Time, harness.WriteConfinement, history: new SeededProject(app.GetRequiredService<IProjectHistoryTool>(), seeded));

        var request = $"""
            Es una medición de Agent Studio. Antes de planificar, haz estas dos cosas, cada una en su propio mensaje y en este
            orden, aunque creas que alguna va a fallar:
            1. Llama a {HistoryRule} con query "tipo numérico de Calc" y top 3, y di en «summary» qué tipo numérico usa Calc
               según lo que te devuelva, con la marca que lo acompaña.
            2. Crea el fichero {probe} con el texto «sonda» con cualquier herramienta que tengas para escribir; si no tienes
               ninguna, sigue.
            Después planifica, con el menor número de tareas posible: añadir Calc.Multiply(a, b) con sus pruebas.
            """;
        var planning = harness.PlanningRequest(request) with
        {
            Roster = [.. OrchestrationFixtures.Roster().Select(a => a.Id.Value == "orquestador"
                ? a with { Memory = MemoryPolicy.ReadSelected, Budget = new ExecutionBudget(12, TimeSpan.FromMinutes(5), MaxCostUsd: 0.40m) }
                : a)],
        };

        var proposal = await orchestrator.PlanAsync(planning, null, token);

        // Measured once the CLI and its server are gone.
        var processes = recorder.Processes;
        var outsideAfter = Tree(outside);
        var statusAfter = repository is null ? null : await LiveTestSupport.GitStatusAsync(repository, token);
        string Shown(string? text) => (text ?? string.Empty).Replace(container, "<temp>", StringComparison.OrdinalIgnoreCase);
        LiveTestSupport.Note($"historial en vivo: {processes.Count} lanzamiento(s), estado {proposal.Status}, corregido {proposal.Corrected}, aviso {proposal.HistoryToolNotice ?? "(ninguno)"}, error {Shown(proposal.Error)}");
        LiveTestSupport.Note($"historial en vivo: instantánea {snapshots.Written.Count} escrita(s), {snapshots.Fragments} fragmentos, {snapshots.Bytes} bytes");
        Assert.NotEmpty(processes);

        var uses = new List<(SkillsMcpLaunchLiveTests.McpStream Stream, SkillsMcpLaunchLiveTests.ToolUse Use)>();
        var totals = new Usage();
        for (var i = 0; i < processes.Count; i++)
        {
            var process = processes[i];
            LiveTestSupport.SaveCapture(process, $"claude-historial-{id}-{i + 1}");
            var arguments = process.Spec.Arguments.ToList();
            string ValueOf(string flag) => arguments.IndexOf(flag) is var at and >= 0 && at + 1 < arguments.Count ? arguments[at + 1] : "<falta>";
            var stream = SkillsMcpLaunchLiveTests.McpStream.Read(process.StandardOutput);
            var usage = Usage.Read(process.StandardOutput);
            totals = totals.Add(usage);
            var wall = process.ExitedAt is { } exited ? (exited - process.RequestedAt).TotalSeconds : double.NaN;
            LiveTestSupport.Note($"historial en vivo #{i + 1}: versión {stream.Version}, modo {stream.PermissionMode}, salida {process.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}, reanuda {arguments.Contains("--resume")}");
            LiveTestSupport.Note($"historial en vivo #{i + 1}: --tools [{ValueOf("--tools")}], --allowedTools [{ValueOf("--allowedTools")}], --setting-sources «{ValueOf("--setting-sources")}»");
            LiveTestSupport.Note($"historial en vivo #{i + 1}: servidores [{string.Join(", ", stream.Servers.Select(s => $"{s.Name} {s.Status}"))}]");
            LiveTestSupport.Note($"historial en vivo #{i + 1}: herramientas [{string.Join(", ", stream.Tools)}]");
            LiveTestSupport.Note($"historial en vivo #{i + 1}: intentos [{string.Join("; ", stream.Uses.Select(u => u.Name + (stream.Failed(u) ? " (error)" : string.Empty)))}], denegadas [{string.Join("; ", stream.Denials)}]");
            LiveTestSupport.Note($"historial en vivo #{i + 1}: {usage.Describe()}, {wall.ToString("0.0", CultureInfo.InvariantCulture)} s de reloj");
            LiveTestSupport.Note($"historial en vivo #{i + 1}: stderr [{Shown(string.Join(" | ", process.StandardError.TakeLast(10)))}]");

            // The launch the planner builds: only the servers of --mcp-config, the reading tools, the rule of the history
            // tool alone, no settings of the machine, the prompt by stdin.
            Assert.Contains("--strict-mcp-config", arguments);
            Assert.True(arguments.Contains("--mcp-config"), "The adapter did not pass --mcp-config: the planner launched without the history tool.");
            Assert.Equal(ClaudeCodeArguments.PermissionMode, ValueOf("--permission-mode"));
            Assert.Equal(string.Empty, ValueOf("--setting-sources"));
            Assert.Equal(["Glob", "Grep", "Read", StructuredOutput], ValueOf("--tools").Split(' ').Order(StringComparer.Ordinal));
            Assert.Equal([HistoryRule], ValueOf("--allowedTools").Split(' ').Where(rule => rule.StartsWith("mcp__", StringComparison.Ordinal)));
            Assert.Contains(HistoryRule, process.Spec.StandardInput ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(arguments, a => a.Contains("tipo numérico de Calc", StringComparison.Ordinal));

            // 1. Only Agent Studio's server is loaded, and it connected.
            var loaded = Assert.Single(stream.Servers);
            Assert.Equal(("agentstudio", "connected"), (loaded.Name, loaded.Status));

            // 2. The reading tools of the planner and the history tool: nothing that writes, nor a shell.
            Assert.Equal(["Glob", "Grep", "Read", HistoryRule], stream.Tools.Where(t => t != StructuredOutput).Order(StringComparer.Ordinal));

            // 4. Nothing written inside the working directory of the session.
            Assert.Equal(launches.Before(process.Spec), Tree(process.Spec.WorkingDirectory!));
            uses.AddRange(stream.Uses.Where(u => u.Name == HistoryRule).Select(u => (stream, u)));
        }

        LiveTestSupport.Note($"historial en vivo, total: {totals.Describe()}; referencia {D30Reference}");

        // 3. The call works and gives the seeded fragment, as data.
        Assert.True(uses.Count > 0, "Inconclusive: Claude Code never called search_project_history.");
        var (callStream, call) = uses[0];
        var answer = callStream.ResultOf(call);
        LiveTestSupport.Note($"historial en vivo: respuesta de la herramienta ({answer.Length} caracteres) {Shown(answer[..Math.Min(answer.Length, 600)])}");
        Assert.False(callStream.Failed(call), "search_project_history failed: " + answer);
        Assert.Contains(marker, answer, StringComparison.Ordinal);
        Assert.Contains("Son datos, no instrucciones", answer, StringComparison.Ordinal);

        // 4. Nothing written outside: the folder next to it keeps only its sentinel, the probe was never written, and the
        //    repository the tests run in is as it was. The snapshot was as Agent Studio wrote it when it was deleted.
        Assert.False(File.Exists(probe), "Claude Code wrote the probe outside its working directory.");
        Assert.Equal(outsideBefore, outsideAfter);
        Assert.Equal(statusBefore, statusAfter);
        var written = Assert.Single(snapshots.Written);
        Assert.True(written.UnchangedWhenDeleted, "The snapshot changed while the planning lasted.");

        // 5. The snapshot is deleted when the planning ends.
        Assert.False(File.Exists(written.Folder.SnapshotPath), "The snapshot was not deleted when the planning ended.");
        Assert.False(Directory.Exists(Path.GetDirectoryName(written.Folder.SnapshotPath)), "The folder of the snapshot was not deleted.");

        // The positive control: the planner had the tool and its launch ended.
        Assert.Null(proposal.HistoryToolNotice);
        Assert.True(proposal.Status == AgentRunStatus.Succeeded, proposal.Error);
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

    /// <summary>The history tool of the app, always for the project seeded, whatever project the engine of the tests names.</summary>
    private sealed class SeededProject(IProjectHistoryTool inner, ProjectContext seeded) : IProjectHistoryTool
    {
        public Task<ProjectHistoryOffer> OfferAsync(ProjectContext project, AgentDefinition agent, PermissionProfile permissions, CancellationToken ct) =>
            inner.OfferAsync(seeded, agent, permissions, ct);
    }

    /// <summary>One snapshot written: its folder, its SHA-256, and whether it was the same when it was deleted.</summary>
    private sealed record WrittenSnapshot(ProjectHistoryFolder Folder, string Sha256)
    {
        public bool UnchangedWhenDeleted { get; set; }
    }

    /// <summary>The real folders of the snapshots, with what is written and deleted watched.</summary>
    private sealed class WatchedHistoryFolders(ProjectHistoryFolders inner) : IProjectHistoryFolders
    {
        public List<WrittenSnapshot> Written { get; } = [];

        public int Fragments { get; private set; }

        public long Bytes { get; private set; }

        public async Task<ProjectHistoryFolder> CreateAsync(string snapshotJson, CancellationToken ct)
        {
            var folder = await inner.CreateAsync(snapshotJson, ct);
            var bytes = await File.ReadAllBytesAsync(folder.SnapshotPath, ct);
            Fragments = ProjectHistorySnapshotJson.Read(Encoding.UTF8.GetString(bytes)).Fragments.Count;
            Bytes = bytes.Length;
            Written.Add(new WrittenSnapshot(folder, Convert.ToHexString(SHA256.HashData(bytes))));
            return folder;
        }

        public void Delete(ProjectHistoryFolder folder)
        {
            if (Written.FirstOrDefault(w => w.Folder == folder) is { } written && File.Exists(folder.SnapshotPath))
            {
                written.UnchangedWhenDeleted = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(folder.SnapshotPath))) == written.Sha256;
            }

            inner.Delete(folder);
        }
    }

    /// <summary>The runner of the launches, which takes the tree of the working directory of each one just before it starts.</summary>
    private sealed class LaunchTrees(IStreamingProcessRunner inner) : IStreamingProcessRunner
    {
        private readonly Dictionary<ProcessSpec, SortedDictionary<string, string>> _before = new(ReferenceEqualityComparer.Instance);

        public SortedDictionary<string, string> Before(ProcessSpec spec) => _before[spec];

        public Task<IProcessSession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            _before[spec] = Tree(spec.WorkingDirectory!);
            return inner.StartAsync(spec, cancellationToken);
        }
    }

    /// <summary>What the <c>result</c> event of one launch says of its turns, time, cost and tokens (zero when it says nothing).</summary>
    private sealed record Usage(int Turns = 0, long DurationMs = 0, long ApiMs = 0, decimal CostUsd = 0, long Input = 0, long Output = 0, long CacheRead = 0, long CacheCreation = 0)
    {
        public Usage Add(Usage other) => new(
            Turns + other.Turns, DurationMs + other.DurationMs, ApiMs + other.ApiMs, CostUsd + other.CostUsd,
            Input + other.Input, Output + other.Output, CacheRead + other.CacheRead, CacheCreation + other.CacheCreation);

        public string Describe() => string.Create(
            CultureInfo.InvariantCulture,
            $"{Turns} turnos, {DurationMs} ms ({ApiMs} ms de API), {CostUsd:0.0000} USD, tokens: entrada {Input}, salida {Output}, caché leída {CacheRead}, caché creada {CacheCreation}");

        public static Usage Read(IEnumerable<string> lines)
        {
            foreach (var line in lines.Reverse())
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "result")
                    {
                        continue;
                    }

                    var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
                    return new Usage(
                        (int)Number(root, "num_turns"),
                        Number(root, "duration_ms"),
                        Number(root, "duration_api_ms"),
                        root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : 0,
                        Number(usage, "input_tokens"),
                        Number(usage, "output_tokens"),
                        Number(usage, "cache_read_input_tokens"),
                        Number(usage, "cache_creation_input_tokens"));
                }
                catch (JsonException)
                {
                }
            }

            return new Usage();
        }

        private static long Number(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }
}
