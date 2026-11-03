using System.IO;
using System.Text;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Application.Usage;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Skills;
using AgentStudio.Infrastructure.Skills;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// F4-8 with the real modules but the CLI (docs/14 D-08, D-29 and section 9, «Integración»): the assisted recommendation
/// of WP4 (<see cref="SkillRecommendationService"/>) over the user's skills index, the project's roster and activations,
/// the local health of each skill and the real <see cref="AuxiliaryAgentRunner"/>, which records the run in the project's
/// SQLite log and launches it through the provider pipeline (journal and quota record) on a scripted Claude Code. With
/// the user's consent the recommendation is an auxiliary run of the log that the usage panel counts and the list of runs
/// never shows, its answer is validated against the catalog and the roster, and nothing is activated. Without a consent,
/// with an old one or with one already spent, nothing is launched nor recorded.
/// </summary>
public sealed class AssistedRecommendationIntegrationTests
{
    private const string Task = "Añadir pruebas al cálculo de facturas y optimizar el trabajo de Spark que las agrega.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The user's skills index, laid out like the real one: two vendored skills and one that is only a link.</summary>
    private static string WriteSkillsIndex(string root)
    {
        foreach (var (domain, name) in new[] { ("software-engineering", "tdd-lite"), ("data-engineering", "spark-optimization") })
        {
            var folder = Path.Combine(root, "domains", domain, "skills", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), $"---\nname: {name}\ndescription: Skill de prueba {name}.\nlicense: MIT\n---\n# {name}\n");
        }

        var catalog = Path.Combine(root, "catalog.json");
        File.WriteAllText(catalog, """
            { "marketplace": "test", "domains": {}, "mcp": [], "skills": [
              { "domain": "software-engineering", "name": "tdd-lite", "repo": "o/r", "path": "skills/tdd-lite", "license": "MIT", "mode": "vendor", "summary": "Escribe la prueba primero." },
              { "domain": "data-engineering", "name": "spark-optimization", "repo": "o/r", "path": "skills/spark-optimization", "license": "MIT", "mode": "vendor", "summary": "Optimiza trabajos de Spark y tablas Delta." },
              { "domain": "desktop-development", "name": "wpf-enlazada", "mode": "link", "summary": "Solo enlace.", "url": "https://github.com/acme/skills/tree/main/skills/wpf-enlazada", "ref": "main" }
            ] }
            """);
        return catalog;
    }

    /// <summary>What the scripted recommender answers: two valid proposals, one skill the catalog lacks and one linked skill.</summary>
    private static AgentScript Answer(DateTimeOffset at) => new()
    {
        StructuredOutput = JsonSerializer.Serialize(new
        {
            recommendations = new object[]
            {
                new { skillId = "tdd-lite", scope = "Agent", agent = "ejecutor", reason = "El ejecutor escribe el cálculo: que escriba la prueba primero." },
                new { skillId = "spark-optimization", scope = "Project", agent = (string?)null, reason = "El proyecto agrega facturas con Spark." },
                new { skillId = "no-existe", scope = "Project", agent = (string?)null, reason = "Inventada." },
                new { skillId = "wpf-enlazada", scope = "Global", agent = (string?)null, reason = "Solo es un enlace." },
            },
        }),
        FinalText = "Propuestas listas.",
        Events =
        [
            new QuotaReported(at, [new QuotaWindow("five_hour", MetricValue<double>.Exact(0.4, "claude rate_limit_event"), at.AddHours(2))]),
            new UsageReported(at, new TokenUsage(
                MetricValue<long>.Exact(3_200, "claude result.usage"),
                MetricValue<long>.Exact(180, "claude result.usage"),
                MetricValue<long>.Exact(1_000, "claude result.usage"),
                MetricValue<long>.Exact(200_000, "claude result.modelUsage"),
                MetricValue<decimal>.Estimated(0.004m, "claude total_cost_usd (precio de lista)"))),
        ],
    };

    /// <summary>The app over a plain project folder, with the user's index and a recorder of every activation written.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly TempDirectory _temp;

        private World(TempDirectory temp, Phase4Paths paths, ScriptedCli claude, Phase4App app)
        {
            _temp = temp;
            Paths = paths;
            Claude = claude;
            App = app;
        }

        public Phase4Paths Paths { get; }

        public ScriptedCli Claude { get; }

        public Phase4App App { get; }

        public string ProjectRoot => App.Project.RootPath;

        public RecordingActivations Activations => App.Get<RecordingActivations>();

        public ISkillRecommendationService Service => App.Get<ISkillRecommendationService>();

        public static async Task<World> CreateAsync()
        {
            var temp = new TempDirectory();
            try
            {
                var paths = Phase4Paths.Under(temp.Path);
                var catalog = WriteSkillsIndex(temp.CreateDirectory("skills-index"));
                var project = temp.CreateDirectory("proyecto");
                var claude = ScriptedCli.Claude(TimeProvider.System);
                claude.Script.Fallback = (_, _) => Answer(DateTimeOffset.UtcNow);
                var services = Phase4Composition.Services(paths, new ScriptedApprovalPrompt(), Phase4ScriptedProviders.Over(claude, ScriptedCli.Codex(TimeProvider.System)))
                    .WithoutCliProbes()
                    .WithCatalog(catalog);

                // The real store of activation decisions, behind a recorder of what is written to it.
                services.AddSingleton(sp => new RecordingActivations(ActivatorUtilities.CreateInstance<SkillActivationStore>(sp)));
                services.Replace(ServiceDescriptor.Singleton<ISkillActivationStore>(sp => sp.GetRequiredService<RecordingActivations>()));
                var app = await Phase4App.OpenAsync(project, services, Roster());
                return new World(temp, paths, claude, app);
            }
            catch
            {
                temp.Dispose();
                throw;
            }
        }

        public QuotaConsent Consent(TimeSpan age) =>
            new(ScriptedApprovalPrompt.Person, DateTimeOffset.UtcNow - age, "Pedir a Claude (haiku) que recomiende skills: gasta cuota de la suscripción.");

        /// <summary>What a refused request must leave as it was: the launches, the log, the journal and the folders of the runs.</summary>
        public async Task<(int Launches, int Entries, string Head, string Journal, int Folders)> StateAsync()
        {
            var log = await App.LoadLogAsync();
            return (
                Claude.Script.Starts.Count,
                log.Entries.Count,
                log.HeadHash,
                string.Concat(Phase3Checks.JournalFiles(ProjectRoot).Select(File.ReadAllText)),
                Directory.Exists(Paths.SkillRecommendations) ? Directory.EnumerateDirectories(Paths.SkillRecommendations).Count() : 0);
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            _temp.Dispose();
        }
    }

    /// <summary>The real store of activation decisions, keeping count of every decision written through it.</summary>
    internal sealed class RecordingActivations(ISkillActivationStore inner) : ISkillActivationStore
    {
        private int _appends;

        public int Appends => Volatile.Read(ref _appends);

        public Task<IReadOnlyList<SkillActivation>> ListAsync(string projectId, CancellationToken ct) => inner.ListAsync(projectId, ct);

        public Task AppendAsync(string projectId, SkillActivation activation, string decidedBy, CancellationToken ct)
        {
            Interlocked.Increment(ref _appends);
            return inner.AppendAsync(projectId, activation, decidedBy, ct);
        }
    }

    [Fact]
    public async Task With_a_valid_consent_the_recommendation_is_an_auxiliary_run_of_the_log_counted_by_the_panel_and_it_activates_nothing()
    {
        await using var world = await World.CreateAsync();
        var app = world.App;
        Assert.Empty(await world.Activations.ListAsync(app.Project.ProjectId, Ct));

        var result = await world.Service.AskAgentAsync(app.Project, Task, world.Consent(TimeSpan.Zero), Ct);

        // The answer, validated against the real catalog and roster: what the catalog lacks or only links is discarded.
        Assert.Equal(
            [
                new AssistedRecommendation(new SkillId("tdd-lite"), SkillScopeKind.Agent, new AgentId("ejecutor"), "El ejecutor escribe el cálculo: que escriba la prueba primero."),
                new AssistedRecommendation(new SkillId("spark-optimization"), SkillScopeKind.Project, null, "El proyecto agrega facturas con Spark."),
            ],
            result.Recommendations);
        Assert.Contains(result.Warnings, w => w.Contains("no-existe", StringComparison.Ordinal) && w.Contains("catálogo", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("wpf-enlazada", StringComparison.Ordinal) && w.Contains("enlazada", StringComparison.Ordinal));

        // One launch of Claude (haiku), read only, in an empty folder of its own, with Agent Studio's MCP server only.
        var (request, _) = Assert.Single(world.Claude.Script.Starts);
        Assert.Equal((SkillRecommendationService.RecommenderId, ProviderId.ClaudeCode, "haiku"), (request.Agent.Id, request.Agent.Model.Provider, request.Agent.Model.Model));
        Assert.Equal((false, false, false), (request.Permissions.AllowFileWrites, request.Permissions.AllowShell, request.Permissions.AllowNetwork));
        var server = Assert.Single(request.McpServers!);
        Assert.Equal((SkillsMcpNames.Server, world.Paths.SkillsServer), (server.Name, server.Command));
        Assert.Equal(SkillsMcpNames.AllowRules, server.AllowedTools);
        Assert.StartsWith(Path.GetFullPath(world.Paths.SkillRecommendations), Path.GetFullPath(request.WorkingDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Task, request.Prompt, StringComparison.Ordinal);

        // The run is in the project's verified log as an auxiliary run (D-08): AgentRunStarted v2 with its purpose before
        // the launch, AgentRunFinished v2 with its usage and its answer as evidence, under a run id of its own.
        var run = Assert.IsType<RunId>(result.Run);
        Assert.StartsWith(AuxiliaryAgentRunner.RunPrefix, run.Value, StringComparison.Ordinal);
        var log = await app.LoadLogAsync();
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
        var (startEntry, started) = Assert.Single(await app.PayloadsAsync<AgentRunStartedPayload>(run.Value));
        Assert.Equal((AgentRunStartedPayload.PurposeVersion, AgentRunPurpose.SkillRecommendation), (started.V, started.Purpose));
        Assert.Equal((SkillRecommendationService.RecommenderId, ProviderId.ClaudeCode, (TaskId?)null, (ContextPackReference?)null), (started.Agent, started.Provider, started.Task, started.ContextPack));
        var (finishEntry, finished) = Assert.Single(await app.PayloadsAsync<AgentRunFinishedPayload>(run.Value));
        Assert.True(startEntry.Sequence < finishEntry.Sequence);
        Assert.Equal((AgentRunFinishedPayload.UsageVersion, AttemptOutcome.Succeeded), (finished.V, finished.Status));
        Assert.Equal(3_200m, finished.Usage!.InputTokens.Value);
        Assert.Equal([DecisionKind.AgentRunStarted, DecisionKind.AgentRunFinished], (await app.PayloadsAsync<DecisionPayload>(run.Value)).Select(p => p.Entry.Kind));

        // The prompt is evidence the system captured, exactly as the CLI got it; the answer is the agent's.
        var evidence = await app.Evidence.ListByRunAsync(app.Project.ProjectId, run.Value, Ct);
        var prompt = Assert.Single(evidence, e => e.Id == started.PromptEvidenceId);
        Assert.Equal((EvidenceKind.Document, EvidenceOrigin.CapturedBySystem, started.PromptSha256), (prompt.Kind, prompt.Origin, prompt.ContentSha256));
        Assert.Equal(ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(request.Prompt)), prompt.ContentSha256);
        var answer = Assert.Single(evidence, e => e.Id == finished.ReportEvidenceId);
        Assert.Equal((EvidenceKind.AgentStatement, EvidenceOrigin.ReportedByAgent), (answer.Kind, answer.Origin));

        // It is not an orchestration run: the list of runs never shows it, but the usage panel counts it.
        Assert.Empty(await app.Orchestrator.ListRunsAsync(app.Project, Ct));
        var now = DateTimeOffset.UtcNow;
        var report = await app.Get<IUsageReport>().QueryAsync(
            [world.ProjectRoot],
            new UsageQuery(now.AddDays(-1), now.AddDays(1), TimeZoneInfo.Local, [UsageDimension.Purpose], new UsageFilter(Purposes: [AgentRunPurpose.SkillRecommendation])),
            Ct);
        var attempt = Assert.Single(report.Attempts);
        Assert.Equal((run.Value, (string?)null, SkillRecommendationService.RecommenderId, AgentRunFinishedPayload.UsageVersion), (attempt.RunId, attempt.TaskId, attempt.Agent, attempt.RecordedVersion));
        Assert.Equal((3_200L, MetricOrigin.ReportedByProvider), (attempt.Own.InputTokens.Value, attempt.Own.InputTokens.Origin));
        Assert.Equal(AgentRunPurpose.SkillRecommendation.ToString(), Assert.Single(report.Groups[1].Key).Value);

        // The prompt went through the project's journal, and the quota of the run reached the user's record (D-14).
        Assert.Contains("P-0001", string.Concat(Phase3Checks.JournalFiles(world.ProjectRoot).Select(File.ReadAllText)), StringComparison.Ordinal);
        Assert.Contains(await app.Get<IQuotaObservations>().LatestAsync(Ct), o => o.Provider == ProviderId.ClaudeCode && o.Window == "five_hour" && o.Utilization.Value == 0.4);

        // Nothing was activated: no decision was written, and the folder of the run is gone.
        Assert.Equal(0, world.Activations.Appends);
        Assert.Empty(await world.Activations.ListAsync(app.Project.ProjectId, Ct));
        Assert.False(File.Exists(world.Paths.Engine.GlobalSkillActivations));
        Assert.Empty(Directory.EnumerateDirectories(world.Paths.SkillRecommendations));
    }

    [Fact]
    public async Task Without_a_consent_with_an_old_one_or_with_one_already_spent_nothing_is_launched_nor_recorded()
    {
        await using var world = await World.CreateAsync();
        var app = world.App;
        var before = await world.StateAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => world.Service.AskAgentAsync(app.Project, Task, null!, Ct));
        Assert.Equal(before, await world.StateAsync());

        // Older than the ten minutes a consent is good for.
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Service.AskAgentAsync(app.Project, Task, world.Consent(TimeSpan.FromMinutes(11)), Ct));
        Assert.Equal(before, await world.StateAsync());

        // A consent is good for one run: the second use is refused before anything is read or launched.
        var consent = world.Consent(TimeSpan.Zero);
        var first = await world.Service.AskAgentAsync(app.Project, Task, consent, Ct);
        var spent = await world.StateAsync();
        Assert.Equal(1, spent.Launches);
        Assert.NotEqual(before.Head, spent.Head);

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Service.AskAgentAsync(app.Project, Task, consent, Ct));
        Assert.Equal(spent, await world.StateAsync());

        // The only auxiliary run of the log is the one that had a consent of its own, and nothing was activated.
        var starts = (await app.PayloadsAsync<AgentRunStartedPayload>()).Where(p => p.Payload.Purpose == AgentRunPurpose.SkillRecommendation).ToList();
        Assert.Equal(first.Run!.Value.Value, Assert.Single(starts).Payload.RunId);
        Assert.Equal(0, world.Activations.Appends);
    }
}
