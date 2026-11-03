using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Judging;
using AgentStudio.Application.Providers;
using AgentStudio.Debate.Judging;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Judging;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Memory.Journal;
using AgentStudio.Providers.Composition;
using AgentStudio.Tests.Debate.Fakes;
using AgentStudio.Tests.Providers.Live;

namespace AgentStudio.Tests.Debate;

/// <summary>
/// The independent review with a real judge: Codex in its read-only sandbox (<c>codex exec -s read-only</c>) with the
/// verdict schema (<c>--output-schema</c>), on a small prepared run whose stores live in memory and whose dossier
/// package and workspace live in a temporary folder. Claude orchestrated and executed, so the judge's vendor (OpenAI)
/// must not be excluded. It spends one judge run (two if the correction round happens), so it only runs with
/// AGENTSTUDIO_PROVIDER_TESTS=1.
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class JudgeLiveTests
{
    private const string Diff = """
        diff --git a/src/Calc/Calc.cs b/src/Calc/Calc.cs
        --- a/src/Calc/Calc.cs
        +++ b/src/Calc/Calc.cs
        @@ -3,4 +3,7 @@ public static class Calc
             public static int Add(int a, int b) => a + b;
        +
        +    /// <summary>Multiplica dos enteros.</summary>
        +    public static int Multiply(int a, int b) => a * b;
         }
        diff --git a/tests/Calc.Tests/CalcTests.cs b/tests/Calc.Tests/CalcTests.cs
        --- a/tests/Calc.Tests/CalcTests.cs
        +++ b/tests/Calc.Tests/CalcTests.cs
        @@ -6,4 +6,10 @@ public class CalcTests
             public void Add_adds() => Assert.Equal(5, Calc.Add(2, 3));
        +
        +    [Fact]
        +    public void Multiply_multiplies() => Assert.Equal(6, Calc.Multiply(2, 3));
        +
        +    [Fact]
        +    public void Multiply_by_zero_is_zero() => Assert.Equal(0, Calc.Multiply(7, 0));
         }
        """;

    private const string TestReport = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun name="dotnet test" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Calc.Tests.CalcTests.Add_adds" outcome="Passed" />
            <UnitTestResult testName="Calc.Tests.CalcTests.Multiply_multiplies" outcome="Passed" />
            <UnitTestResult testName="Calc.Tests.CalcTests.Multiply_by_zero_is_zero" outcome="Passed" />
          </Results>
          <ResultSummary outcome="Completed"><Counters total="3" executed="3" passed="3" failed="0" /></ResultSummary>
        </TestRun>
        """;

    [Fact]
    public async Task A_real_codex_judge_in_its_read_only_sandbox_returns_a_verdict_the_system_parses_and_validates()
    {
        LiveTestSupport.RequireOptIn();
        var ct = TestContext.Current.CancellationToken;
        using var world = await JudgingWorld.CreateAsync(new JudgingWorldOptions
        {
            DiffContent = Diff,
            TestRunContent = TestReport,
            TestRun = new TestRunSummary(Passed: 3, Failed: 0, Skipped: 0, ExitCode: 0),
        });
        WriteWorkspace(world.Workspace);
        Directory.CreateDirectory(world.ProjectContext.RootPath);
        var before = world.LogStore.Of(JudgingWorld.Project).Count;

        // The production composition: every provider of the project, journaled and concurrency-limited.
        var providers = new ProjectAgentProviders(new ExecutableLocator(), new StreamingProcessRunner(), new MarkdownDailyJournalFactory(new ProjectStorageFiles(), TimeProvider.System), TimeProvider.System);
        var review = new IndependentReview(
            world.Recorder,
            new JudgeDossierBuilder(world.Recorder, world.Runs, world.Plans, world.Requirements, world.Evidence, new JudgingOptions(world.DossierRoot)),
            world.Runs,
            new AgentJudgeFactory(providers, TimeProvider.System),
            world.Verdicts,
            world.Evidence,
            world.Clock);
        var progress = new ListProgress<AgentEvent>();

        var watch = Stopwatch.StartNew();
        var outcome = await review.ReviewRunAsync(new ReviewRequest(world.ProjectContext, JudgingWorld.Run, JudgingWorld.Review, JudgingWorld.Judge, world.Workspace), progress, ct);
        watch.Stop();

        await ReportAsync(world, outcome, progress, watch.Elapsed);

        Assert.True(outcome.DecisionLogIntact);
        Assert.InRange(world.Verdicts.Stored.Count, 1, 2);
        var first = world.Verdicts.Stored[0];
        Assert.DoesNotContain(first.Issues, i => i.Code == VerdictViolationCode.MalformedVerdict);
        Assert.All(world.Verdicts.Stored, v => Assert.Equal(JudgingWorld.Judge, v.Verdict.Judge.Agent));
        Assert.All(world.Verdicts.Stored, v => Assert.Equal((ProviderId.CodexCli, VendorId.OpenAI), (v.Verdict.Judge.Model.Provider, v.Verdict.Judge.Model.Vendor)));
        Assert.DoesNotContain(outcome.Violations, v => v.Code is VerdictViolationCode.RequirementSetMismatch or VerdictViolationCode.DecisionLogMismatch or VerdictViolationCode.JudgeNotIndependent);

        var entries = world.LogStore.Of(JudgingWorld.Project).Skip(before).ToList();
        Assert.Equal(DecisionKind.ReviewRequested, entries[0].Kind);
        Assert.Equal(world.Verdicts.Stored.Count, entries.Count(e => e.Kind == DecisionKind.AgentRunStarted));
        Assert.Equal(world.Verdicts.Stored.Count, entries.Count(e => e.Kind == DecisionKind.AgentRunFinished));
        var issued = entries.Where(e => e.Kind == DecisionKind.VerdictIssued).Select(e => DecisionPayloadSerializer.Deserialize<VerdictIssuedPayload>(e.PayloadJson)).ToList();
        Assert.Equal(world.Verdicts.Stored.Select(v => v.Id), issued.Select(p => p.VerdictId));
        Assert.All(issued, p => Assert.Equal(entries[0].Hash, p.DecisionLogHeadHash));
        Assert.True((await world.LoadLogAsync()).Verification.IsValid);
    }

    private static void WriteWorkspace(string workspace)
    {
        Directory.CreateDirectory(Path.Combine(workspace, "src", "Calc"));
        Directory.CreateDirectory(Path.Combine(workspace, "tests", "Calc.Tests"));
        File.WriteAllText(Path.Combine(workspace, "src", "Calc", "Calc.cs"), """
            namespace Calc;

            public static class Calc
            {
                public static int Add(int a, int b) => a + b;

                /// <summary>Multiplica dos enteros.</summary>
                public static int Multiply(int a, int b) => a * b;
            }
            """);
        File.WriteAllText(Path.Combine(workspace, "tests", "Calc.Tests", "CalcTests.cs"), """
            namespace Calc.Tests;

            public class CalcTests
            {
                [Fact]
                public void Add_adds() => Assert.Equal(5, Calc.Add(2, 3));

                [Fact]
                public void Multiply_multiplies() => Assert.Equal(6, Calc.Multiply(2, 3));

                [Fact]
                public void Multiply_by_zero_is_zero() => Assert.Equal(0, Calc.Multiply(7, 0));
            }
            """);
    }

    /// <summary>What happened, for the report: test output, and evidence.log when AGENTSTUDIO_CAPTURE_DIR is set.</summary>
    private static async Task ReportAsync(JudgingWorld world, ReviewOutcome outcome, ListProgress<AgentEvent> progress, TimeSpan elapsed)
    {
        LiveTestSupport.Note($"[juez en vivo] {elapsed.TotalSeconds:0.0} s; disposición {outcome.Disposition}; corregido {outcome.Corrected}; veredictos {world.Verdicts.Stored.Count}");
        foreach (var stored in world.Verdicts.Stored)
        {
            var v = stored.Verdict;
            LiveTestSupport.Note($"[juez en vivo] veredicto {stored.Id}: {v.Decision}, válido {stored.Valid}, juez {v.Judge.Agent}/{v.Judge.Model.Provider}/{v.Judge.Model.Vendor}/{v.Judge.Model.Model ?? "(modelo no informado)"}, sustituye {stored.Supersedes ?? "-"}");
            LiveTestSupport.Note($"[juez en vivo]   incidencias: {(stored.Issues.Count == 0 ? "ninguna" : string.Join("; ", stored.Issues.Select(i => $"{i.Code}: {i.Message}")))}");
            LiveTestSupport.Note($"[juez en vivo]   resumen: {v.Summary}");
            foreach (var a in v.Assessments ?? [])
            {
                LiveTestSupport.Note($"[juez en vivo]   {a?.Requirement}: {a?.Status} [{string.Join(", ", a?.Evidence ?? [])}] {a?.Rationale}");
            }

            foreach (var f in v.Findings ?? [])
            {
                LiveTestSupport.Note($"[juez en vivo]   hallazgo {f?.Category}/{f?.Severity}: {f?.Description}");
            }

            LiveTestSupport.Note($"[juez en vivo]   confianza: {v.Confidence?.Level} ({v.Confidence?.Justification})");
            LiveTestSupport.Note("[juez en vivo]   JSON: " + VerdictJson.ToJson(v).ReplaceLineEndings(" "));
        }

        var tools = progress.Items.OfType<ToolInvoked>().Select(t => $"{t.Tool}: {t.Summary}").ToList();
        LiveTestSupport.Note($"[juez en vivo] herramientas ({tools.Count}): {string.Join(" | ", tools)}");
        foreach (var usage in progress.Items.OfType<UsageReported>())
        {
            LiveTestSupport.Note($"[juez en vivo] uso: entrada {usage.Usage.InputTokens.Value}, salida {usage.Usage.OutputTokens.Value}, caché {usage.Usage.CachedInputTokens.Value}");
        }

        var evidence = await world.Evidence.ListByRunAsync(JudgingWorld.Project, JudgingWorld.Run.Value, CancellationToken.None);
        foreach (var answer in evidence.Where(e => e.Kind == EvidenceKind.AgentStatement && e.Title.StartsWith("Respuesta del juez", StringComparison.Ordinal)))
        {
            LiveTestSupport.Note($"[juez en vivo] {answer.Title}: {world.Evidence.ContentText(answer.Id).ReplaceLineEndings(" ")}");
        }
    }
}
