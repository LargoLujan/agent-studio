using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Work;
using AgentStudio.Orchestration.Engine;
using AgentStudio.Tests.Integration.Phase3;
using AgentStudio.Tests.Orchestration.Fakes;
using static AgentStudio.Tests.Orchestration.Fakes.OrchestrationFixtures;
using JudgeData = AgentStudio.Tests.Debate.Fakes.PromptData;

namespace AgentStudio.Tests.Integration.Phase4;

/// <summary>
/// The memory derived from the log (docs/14 D-18 and D-22, WP6) after a real run of the engine on SQLite and a temporary
/// git repository: the plan approved on the canvas, the executor's work checked by a step of the trusted profile (a
/// PowerShell script of the repository that writes a JUnit report) and the judge's verdict (a scripted Codex that approves
/// on that system test run). Right after the end of the run can be seen, with no wait nor retry, the derivation finds the
/// project idle (the run released it first, <see cref="IRunActivity"/>) and records what the log says, each entry citing
/// the entry of the log it comes from; deriving again records nothing; and the export writes the Markdown of the memory.
/// </summary>
[Trait("Category", "LocalTools")]
public sealed class MemoryDerivationIntegrationTests
{
    /// <summary>Passes when the executor wrote <c>src/feature.txt</c>, and writes its JUnit report where the step says.</summary>
    private const string CheckScript = """
        param([string] $Report)
        $ok = Test-Path -LiteralPath 'src/feature.txt'
        $case = if ($ok) { '<testcase name="feature" classname="check" />' } else { '<testcase name="feature" classname="check"><failure message="falta src/feature.txt" /></testcase>' }
        Set-Content -LiteralPath $Report -Value "<testsuites><testsuite name='check'>$case</testsuite></testsuites>" -Encoding utf8
        if ($ok) { exit 0 } else { exit 1 }

        """;

    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(4);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The judge's answer to the prompt it got: an approval that echoes the hashes of its dossier and cites, for each
    /// mandatory requirement, the latest passing test run the system captured that the dossier lists.
    /// </summary>
    private static AgentScript Approving(string prompt)
    {
        var tests = JudgeData.Dossier(prompt).GetProperty("evidence").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "TestRun"
                && e.GetProperty("origin").GetString() == "CapturedBySystem"
                && e.GetProperty("testRun").ValueKind == System.Text.Json.JsonValueKind.Object
                && e.GetProperty("testRun").GetProperty("succeeded").GetBoolean())
            .Select(e => e.GetProperty("id").GetString()!)
            .ToList();
        var proof = tests.Count > 0 ? tests[^1] : throw new InvalidOperationException("The dossier of the judge lists no passing test run the system captured.");
        var answer = JudgeData.EchoingApproval(prompt, new EvidenceId(proof)).ToJsonString();
        return new AgentScript { StructuredOutput = answer, FinalText = answer, SessionId = "thread-juez-1" };
    }

    [Fact]
    public async Task Right_after_a_reviewed_run_ends_the_memory_is_derived_from_its_log_once_and_exported()
    {
        using var repo = Phase3Repos.WithScript("check.ps1", CheckScript);
        var paths = Phase4Paths.Beside(repo.Root);
        var claude = ScriptedCli.Claude(TimeProvider.System);
        var codex = ScriptedCli.Codex(TimeProvider.System);
        claude.Script.Script("ejecutor", "T1", 1, AgentScript.Completed(("src/feature.txt", "función nueva\n")));
        codex.Script.Fallback = (request, _) => request.Agent.Id.Value == "juez" ? Approving(request.Prompt) : AgentScript.Completed();
        var person = new ScriptedApprovalPrompt();
        var profile = new VerificationProfile("memoria derivada", [Phase3Repos.ScriptStep("check", "Comprobar la función", "check.ps1", TimeSpan.FromMinutes(3))]);
        var plan = Plan(
            Work("T1", scope: ["src/"]).Verified("check"),
            Node("R1", WorkTaskKind.Review).After(On("T1")));
        var services = Phase4Composition.Services(paths, person, Phase4ScriptedProviders.Over(claude, codex)).WithoutCliProbes();
        await using var app = await Phase4App.OpenAsync(repo.Root, services, Roster());
        var derivation = app.Get<IMemoryDerivation>();

        await using var run = await app.Orchestrator.StartAsync(await app.RunRequestAsync("Añade la función y que el juez la revise.", Requirements(), plan, profile), Ct);
        var result = await run.Completion.WaitAsync(Guard, Ct);

        // Right after the end, with no wait nor retry: the run released its project before its end could be seen.
        var derived = await derivation.DeriveAsync(app.Project, Ct);

        var log = await app.LoadLogAsync();
        Assert.True(result.Phase == RunPhase.Succeeded, Phase4Checks.Describe(result) + "\n" + Phase4Checks.Describe(log));
        Assert.True(log.Verification.IsValid, log.Verification.Problem);
        var verdictIssued = Assert.Single(await app.PayloadsAsync<VerdictIssuedPayload>(run.Id.Value));
        Assert.Equal((VerdictDecision.Approved, true), (verdictIssued.Payload.Decision, verdictIssued.Payload.Valid));
        Assert.Equal(result.VerdictId, verdictIssued.Payload.VerdictId);
        Assert.False(app.Get<IRunActivity>().IsBusy(app.Project.ProjectId));

        Assert.True(derived.Completed, derived.Reason);
        Assert.Empty(derived.Problems);
        Assert.Empty(derived.AlreadyRecorded);

        // What the log says, each entry with its source: the approval of the plan, recorded when the run started, and the
        // verdict, both in the system's words and so trusted (D-20).
        var approval = Assert.Single(
            await app.PayloadsAsync<ApprovalAnsweredPayload>(run.Id.Value),
            p => p.Entry.Kind == DecisionKind.ApprovalGranted && p.Payload.Action == Orchestrator.PlanApprovalAction);
        var planDecision = Assert.Single(derived.Recorded, e => e.Kind == MemoryKind.Decision && e.Origin == MemoryOrigin.System);
        Assert.Equal(MemorySources.LogEntry(run.Id.Value, approval.Entry.Sequence), planDecision.Source);
        var verdict = Assert.Single(derived.Recorded, e => e.Kind == MemoryKind.Verdict);
        Assert.Equal((MemoryOrigin.System, MemorySources.LogEntry(run.Id.Value, verdictIssued.Entry.Sequence)), (verdict.Origin, verdict.Source));
        var fromLog = derived.Recorded.Where(e => e.Kind != MemoryKind.JournalEntry).ToList();
        Assert.All(fromLog, e => Assert.StartsWith($"{MemorySources.LogEntryPrefix}{run.Id.Value}#", e.Source, StringComparison.Ordinal));
        Assert.All(derived.Recorded, e => Assert.Equal(MemoryTrust.Trusted, e.Trust));

        // Phase 12 (WP1): the entries Agent Studio wrote in the journal for the run are anchored too, each by its day and the
        // hash of its text, and they are the system's own words: their receipt in AgentRunFinished matched.
        var journal = derived.Recorded.Where(e => e.Kind == MemoryKind.JournalEntry).ToList();
        Assert.NotEmpty(journal);
        Assert.All(journal, e => Assert.True(MemorySources.TryParseJournal(e.Source, out _, out _), e.Source));
        Assert.All(journal, e => Assert.Equal(MemoryOrigin.System, e.Origin));
        Assert.All(new[] { planDecision, verdict }, e => Assert.Contains(run.Id.Value, e.Topic, StringComparison.Ordinal));

        // Each entry announced once in the log, outside every run, with no text in its payload (D-19).
        var announced = await app.PayloadsAsync<MemoryRecordedPayload>();
        Assert.Equal(derived.Recorded.Select(e => e.Id).Order(StringComparer.Ordinal), announced.Select(a => a.Payload.Entry).Order(StringComparer.Ordinal));
        Assert.All(announced, a => Assert.Equal(DecisionPayload.NoRun, a.Payload.RunId));
        Assert.All(announced, a => Assert.DoesNotContain(verdict.Content, a.Entry.PayloadJson, StringComparison.Ordinal));
        var recall = await app.Get<IProjectMemory>().RecallAsync(app.Project, new MemoryQuery(Kinds: [MemoryKind.Decision, MemoryKind.Verdict]), Ct);
        Assert.Contains(recall, e => e.Id == planDecision.Id);
        Assert.Contains(recall, e => e.Id == verdict.Id);

        // Deriving again records nothing: every fact is already in the memory, and the log does not move.
        var entries = log.Entries.Count;
        var again = await derivation.DeriveAsync(app.Project, Ct);
        Assert.True(again.Completed, again.Reason);
        Assert.Empty(again.Recorded);
        Assert.Equal(derived.Recorded.Select(e => e.Source).Order(StringComparer.Ordinal), again.AlreadyRecorded.Order(StringComparer.Ordinal));
        var after = await app.LoadLogAsync();
        Assert.Equal(entries, after.Entries.Count);
        Assert.Equal(announced.Count, (await app.PayloadsAsync<MemoryRecordedPayload>()).Count);

        // The export writes the trusted memory to .agentstudio/memory/: the month and one file per category.
        var export = await app.Get<IMemoryExporter>().ExportAsync(app.Project, "es-ES", Ct);
        Assert.True(export.Exported, export.Reason);
        // The journal entries are not exported (ADR-0047): the journal is already a file of the repository.
        Assert.Equal(fromLog.Count, export.Entries);
        Assert.Contains("memory/tema-decisiones.md", export.Files);
        Assert.Contains("memory/tema-veredictos.md", export.Files);
        Assert.Contains(export.Files, f => f.StartsWith("memory/cronologico-", StringComparison.Ordinal));
        foreach (var file in export.Files)
        {
            Assert.StartsWith("memory/", file, StringComparison.Ordinal);
            var full = Path.Combine(repo.Root, ".agentstudio", file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"The export did not write {full}.");
        }

        Assert.Contains(run.Id.Value, await File.ReadAllTextAsync(Path.Combine(repo.Root, ".agentstudio", "memory", "tema-veredictos.md"), Ct), StringComparison.Ordinal);
    }
}
