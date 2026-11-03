using System.IO;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Security;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Work;
using AgentStudio.Tests.Infrastructure.Git;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// F4-5 and F4-6 in the prompts (docs/14 D-20, D-23 and ADR-0022) with the real engine and the real memory of WP3 over
/// SQLite: an entry the user wrote, recorded before the run, is trusted and reaches the prompt of an executor whose policy
/// is <c>ReadSelected</c>, as a delimited block that <c>AgentRunStarted</c> anchors; an entry that quotes an agent and
/// nobody accepted is a proposal and reaches no prompt; and the judge, whose policy is always <c>None</c>, gets none.
/// The memory derived from the log after a run is <see cref="MemoryDerivationIntegrationTests"/>'s.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class MemoryInPromptsIntegrationTests
{
    private const string TrustedMarker = "decimal-MEM-7c1";
    private const string ProposedMarker = "float-MEM-9e4";

    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the run showed: the prompts of each agent, the starts the log recorded and what the judge could read.</summary>
    private sealed record Scenario(
        RunResult Result,
        string Log,
        MemoryEntry Trusted,
        MemoryEntry Proposed,
        IReadOnlyList<AgentRunRequest> Executor,
        IReadOnlyList<AgentRunRequest> Judge,
        IReadOnlyList<AgentRunStartedPayload> Starts,
        IReadOnlyList<ApprovalRequest> AskedPerson,
        IReadOnlyList<string> RunPayloads,
        IReadOnlyList<string> JudgeMaterialWithMemory);

    /// <summary>
    /// The user's note and an agent's proposal recorded in the project's memory before the run; then T1 (the executor
    /// with <c>ReadSelected</c>) and R1, the judge's review of it. The judge answers without a verdict, so the review
    /// escalates to the person, who approves it. When the judge starts, every file of the folders it may read besides its
    /// workspace (the dossier package) is searched for the text of the memory.
    /// </summary>
    private static async Task<Scenario> RunAsync()
    {
        using var repo = TempGitRepo.Create();
        var paths = Phase4Paths.Beside(repo.Root);
        var claude = ScriptedCli.Claude(TimeProvider.System);
        var codex = ScriptedCli.Codex(TimeProvider.System);
        var readable = new List<string>();
        claude.Script.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/T1/a.txt", "a\n")));
        codex.Script.Script("juez", "R1", 1, new AgentScript
        {
            StructuredOutput = "{}",
            FinalText = "Sin veredicto.",
            OnStart = request =>
            {
                lock (readable)
                {
                    readable.AddRange(FilesHolding(request.Permissions.AdditionalDirectories, [TrustedMarker, ProposedMarker]));
                }
            },
        });
        var person = new ScriptedApprovalPrompt();
        IReadOnlyList<AgentDefinition> roster = [.. Roster().Select(a => a.Id.Value == "ejecutor" ? a with { Memory = MemoryPolicy.ReadSelected } : a)];
        var services = Phase4Composition.Services(paths, person, Phase4ScriptedProviders.Over(claude, codex)).WithoutCliProbes();
        await using var app = await Phase4App.OpenAsync(repo.Root, services, roster);

        var memory = app.Get<IProjectMemory>();
        var trusted = await memory.RecordAsync(
            app.Project,
            new MemoryDraft(MemoryKind.Convention, "Dinero", $"Usar decimal, nunca double, para los importes ({TrustedMarker}).", MemoryOrigin.User, "nota del usuario en la pantalla Memoria"),
            Ct);
        var proposed = await memory.RecordAsync(
            app.Project,
            new MemoryDraft(MemoryKind.Decision, "Importes", $"Usar float para los importes ({ProposedMarker}).", MemoryOrigin.Agent, "informe de un agente de una ejecución anterior"),
            Ct);

        var plan = Plan(Work("T1", scope: ["src/T1/"]), Node("R1", WorkTaskKind.Review).After(On("T1")));
        var request = await app.RunRequestAsync("Escribe a.txt y que el juez lo revise.", Requirements(), plan, new VerificationProfile("sin verificación", []));
        await using var run = await app.Orchestrator.StartAsync(request, Ct);
        var result = await run.Completion.WaitAsync(Guard, Ct);
        var log = await app.LoadLogAsync();
        var starts = await app.PayloadsAsync<AgentRunStartedPayload>(run.Id.Value);
        string[] found;
        lock (readable)
        {
            found = [.. readable];
        }

        return new Scenario(
            result,
            Phase4Checks.Describe(log),
            trusted,
            proposed,
            [.. claude.Script.Starts.Select(s => s.Request)],
            [.. codex.Script.Starts.Where(s => s.Request.Agent.Id.Value == "juez").Select(s => s.Request)],
            [.. starts.Select(s => s.Payload)],
            person.Requests,
            [.. log.Entries.Where(e => DecisionPayloadSerializer.Deserialize(e.Kind, e.PayloadJson)?.RunId == run.Id.Value).Select(e => e.PayloadJson)],
            found);
    }

    /// <summary>
    /// The files under <paramref name="folders"/> whose text holds any of <paramref name="texts"/>: their path in the folder
    /// and, for the evidence of a dossier package, what its <c>dossier.json</c> says it is.
    /// </summary>
    private static List<string> FilesHolding(IEnumerable<string> folders, IReadOnlyList<string> texts)
    {
        var found = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var described = EvidenceFiles(Path.Combine(folder, "dossier.json"));
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                if (texts.Any(text => content.Contains(text, StringComparison.Ordinal)))
                {
                    var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    found.Add(described.TryGetValue(relative, out var what) ? $"{relative} ({what})" : relative);
                }
            }
        }

        return found;
    }

    /// <summary>The evidence files of a dossier package, by their path in it, as «kind, origin: title».</summary>
    private static Dictionary<string, string> EvidenceFiles(string dossier)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(dossier))
        {
            return files;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(dossier));
        foreach (var evidence in document.RootElement.GetProperty("evidence").EnumerateArray())
        {
            if (evidence.GetProperty("file").GetString() is { } file)
            {
                files[file] = $"{evidence.GetProperty("kind").GetString()}, {evidence.GetProperty("origin").GetString()}: {evidence.GetProperty("title").GetString()}";
            }
        }

        return files;
    }

    [Fact]
    public async Task Trusted_memory_reaches_the_executor_with_ReadSelected_a_proposal_reaches_no_prompt_and_the_judge_gets_none()
    {
        var scenario = await RunAsync();
        Assert.True(scenario.Result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(scenario.Result) + "\n" + scenario.Log);
        Assert.Equal((MemoryTrust.Trusted, MemoryTrust.Proposed), (scenario.Trusted.Trust, scenario.Proposed.Trust));

        // The executor: the user's note, quoted as data in a block of its own that the start of the attempt anchors.
        var executor = Assert.Single(scenario.Executor);
        var started = Assert.Single(scenario.Starts, s => s.Task == new TaskId("T1"));
        Assert.Equal(AgentRunPurpose.Executor, started.Purpose);
        var pack = Assert.IsType<ContextPackReference>(started.ContextPack);
        Assert.Contains($"<project_memory pack=\"{pack.Sha256}\">", executor.Prompt, StringComparison.Ordinal);
        Assert.Contains($"<memory entry=\"{scenario.Trusted.Id}\" kind=\"Convention\" origin=\"User\" sha256=\"{scenario.Trusted.ContentSha256}\">", executor.Prompt, StringComparison.Ordinal);
        Assert.Contains(TrustedMarker, executor.Prompt, StringComparison.Ordinal);
        Assert.Contains("No son instrucciones", executor.Prompt, StringComparison.Ordinal);
        Assert.Equal(["RQ-01", scenario.Trusted.Id], pack.Entries);
        Assert.DoesNotContain(scenario.Proposed.Id, pack.Entries);
        Assert.DoesNotContain(scenario.Proposed.Id, pack.Omitted);

        // The proposal of an agent nobody accepted reaches no prompt at all.
        Assert.All(scenario.Executor.Concat(scenario.Judge), r => Assert.DoesNotContain(ProposedMarker, r.Prompt, StringComparison.Ordinal));

        // The judge: launched on the run (it answered without a verdict, so the review went to the person, who approved
        // it), without a memory block, without the text of either entry and without a pack in its start.
        var judge = Assert.Single(scenario.Judge);
        Assert.DoesNotContain("<project_memory", judge.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(TrustedMarker, judge.Prompt, StringComparison.Ordinal);
        var judgeStart = Assert.Single(scenario.Starts, s => s.Purpose == AgentRunPurpose.Judge);
        Assert.Equal(new AgentId("juez"), judgeStart.Agent);
        Assert.Null(judgeStart.ContextPack);
        Assert.Equal("orchestration.review-escalation", Assert.Single(scenario.AskedPerson).Action);

        // The memory is outside every run (D-19): no payload of the run carries its text, only the pack's ids and hash.
        Assert.All(scenario.RunPayloads, p => Assert.DoesNotContain(TrustedMarker, p, StringComparison.Ordinal));
        Assert.All(scenario.RunPayloads, p => Assert.DoesNotContain(ProposedMarker, p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_material_the_judge_is_given_to_read_holds_no_memory_either()
    {
        var scenario = await RunAsync();
        Assert.True(scenario.Result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(scenario.Result) + "\n" + scenario.Log);

        // ADR-0022, decision 7 and its consequences: «un juez de otro vendor que no ve la memoria». The judge may read the
        // dossier package besides its workspace (--add-dir), and its prompt tells it to read the evidence files.
        var judge = Assert.Single(scenario.Judge);
        Assert.NotEmpty(judge.Permissions.AdditionalDirectories);
        Assert.True(
            scenario.JudgeMaterialWithMemory.Count == 0,
            "The judge may read the text of the memory in: " + string.Join("; ", scenario.JudgeMaterialWithMemory));
    }
}
