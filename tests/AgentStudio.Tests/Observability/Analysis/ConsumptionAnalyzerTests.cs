using System.IO;
using System.Text;
using AgentStudio.Application.Analysis;
using AgentStudio.Application.Decisions;
using AgentStudio.Application.Persistence;
using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Decisions;
using AgentStudio.Domain.Evidence;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Observability.Analysis;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Observability.Analysis;

/// <summary>
/// F13-15 (docs/17 D-34, ADR-0052): the service of the deterministic analysis. It reads only the verified log (one that does
/// not verify fails closed), writes nothing, and for <c>model.oversized</c> reads the size of a change only from evidence that
/// matches the SHA-256 the log anchored.
/// </summary>
public sealed class ConsumptionAnalyzerTests
{
    private static readonly ProjectContext Project = new(ConsumptionLog.ProjectId, @"C:\proyecto");

    private static readonly ModelCatalog Catalog = new(
        1,
        [new ModelCatalogEntry(ProviderId.ClaudeCode, "2.1.290", ["claude-opus-5-5"], ModelTier.Maximum, [ReasoningEffort.High])],
        ModelRecommendationRules.Default);

    private const string Record = """{"v":1,"kind":"agent-studio/attempt-record","run":"20261107-a1b2c3","task":"T1","attempt":1,"outcome":"Succeeded","attemptBase":null,"head":null,"changedPaths":["src/a.cs","src/b.cs"],"outputs":[],"tests":null,"reportEvidence":null,"reportSha256":null}""";

    private const string Diff = "diff --git a/src/a.cs b/src/a.cs\n--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1,2 +1,3 @@\n-viejo\n+nuevo\n+otro\n contexto\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ConsumptionLog Log, StoredEvidence Evidence) Oversized(string? recordSha = null)
    {
        var evidence = new StoredEvidence();
        evidence.Add("ev-record", Record);
        evidence.Add("ev-diff", Diff);
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, model: "claude-opus-5-5", input: 50_000)
            .Evidence("ev-diff", EvidenceKind.CodeDiff, "T1", 1, "Diff de T1 (intento 1)", Sha(Diff))
            .Evidence("ev-record", EvidenceKind.Document, "T1", 1, "Registro del intento T1 #1", recordSha ?? Sha(Record));
        return (log, evidence);
    }

    private static string Sha(string text) => ContentHash.Sha256Hex(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task It_analyses_the_verified_log_at_the_time_of_its_clock_and_reads_the_change_only_from_anchored_evidence()
    {
        var (log, evidence) = Oversized();
        var recorder = new FixedRecorder(log.Entries, DecisionLogVerification.Valid);
        var analyzer = new ConsumptionAnalyzer(recorder, evidence, new FakeTimeProvider(T0.AddDays(1)), new ConsumptionAnalysisOptions { Catalog = Catalog });

        var report = await analyzer.AnalyzeAsync(Project, ConsumptionLog.Run, Ct);

        var finding = Assert.Single(report.Findings, f => f.Code == ConsumptionFindingCodes.ModelOversized);
        Assert.Equal(["ejecutor", "claude-opus-5-5", 1], finding.Arguments);
        Assert.Equal(T0.AddDays(1), report.At);
        Assert.Equal(log.Entries[^1].Hash, report.LogHeadHash);
        Assert.Equal(0, recorder.Writes);
        Assert.Equal(["ev-diff", "ev-record"], evidence.Opened.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Evidence_rewritten_after_it_was_anchored_gives_no_size_and_no_finding()
    {
        var (log, evidence) = Oversized(recordSha: HashA);
        var analyzer = new ConsumptionAnalyzer(new FixedRecorder(log.Entries, DecisionLogVerification.Valid), evidence, TimeProvider.System, new ConsumptionAnalysisOptions { Catalog = Catalog });

        var report = await analyzer.AnalyzeAsync(Project, ConsumptionLog.Run, Ct);

        Assert.DoesNotContain(report.Findings, f => f.Code == ConsumptionFindingCodes.ModelOversized);
    }

    [Theory]
    [InlineData(EvidenceKind.AgentStatement, EvidenceOrigin.CapturedBySystem)]
    [InlineData(EvidenceKind.Document, EvidenceOrigin.ReportedByAgent)]
    public async Task A_record_that_is_not_a_document_the_system_captured_gives_no_size(EvidenceKind kind, EvidenceOrigin origin)
    {
        var evidence = new StoredEvidence();
        evidence.Add("ev-record", Record);
        evidence.Add("ev-diff", Diff);
        var log = new ConsumptionLog().Started()
            .Attempt("T1", 1, model: "claude-opus-5-5")
            .Evidence("ev-diff", EvidenceKind.CodeDiff, "T1", 1, "Diff de T1 (intento 1)", Sha(Diff))
            .Evidence("ev-record", kind, "T1", 1, "Registro del intento T1 #1", Sha(Record), origin);
        var analyzer = new ConsumptionAnalyzer(new FixedRecorder(log.Entries, DecisionLogVerification.Valid), evidence, TimeProvider.System, new ConsumptionAnalysisOptions { Catalog = Catalog });

        var report = await analyzer.AnalyzeAsync(Project, ConsumptionLog.Run, Ct);

        Assert.DoesNotContain(report.Findings, f => f.Code == ConsumptionFindingCodes.ModelOversized);
        Assert.DoesNotContain("ev-record", evidence.Opened);
    }

    [Fact]
    public async Task Without_a_catalog_no_evidence_is_read()
    {
        var (log, evidence) = Oversized();
        var analyzer = new ConsumptionAnalyzer(new FixedRecorder(log.Entries, DecisionLogVerification.Valid), evidence, TimeProvider.System);

        await analyzer.AnalyzeAsync(Project, ConsumptionLog.Run, Ct);

        Assert.Empty(evidence.Opened);
    }

    [Fact]
    public async Task A_log_that_does_not_verify_fails_closed()
    {
        var (log, evidence) = Oversized();
        var analyzer = new ConsumptionAnalyzer(new FixedRecorder(log.Entries, new DecisionLogVerification(false, 2, "hash")), evidence, TimeProvider.System, new ConsumptionAnalysisOptions { Catalog = Catalog });

        await Assert.ThrowsAsync<DecisionLogIntegrityException>(() => analyzer.AnalyzeAsync(Project, ConsumptionLog.Run, Ct));
        Assert.Empty(evidence.Opened);
    }

    [Fact]
    public void The_size_of_a_change_counts_the_files_of_the_record_and_the_lines_of_the_diff_without_its_headers()
    {
        Assert.Equal(2, ConsumptionAnalyzer.ChangedFiles(Record));
        Assert.Null(ConsumptionAnalyzer.ChangedFiles("""{"kind":"otra-cosa","changedPaths":[]}"""));
        Assert.Null(ConsumptionAnalyzer.ChangedFiles("no es JSON"));
        Assert.Equal(3, ConsumptionAnalyzer.ChangedLines(Diff));
    }

    /// <summary>A log as the recorder loads it, verified or not; it refuses every write.</summary>
    private sealed class FixedRecorder(IReadOnlyList<DecisionEntry> entries, DecisionLogVerification verification) : IDecisionRecorder
    {
        public int Writes { get; private set; }

        public Task<DecisionLogState> LoadAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(new DecisionLogState(entries, verification));

        public Task<DecisionLogState> LoadAsync(string projectId, LogLoad load, CancellationToken cancellationToken) => LoadAsync(projectId, cancellationToken);

        public Task<DecisionEntry> RecordAsync(string projectId, Actor actor, DecisionKind kind, string summary, DecisionPayload payload, long? corrects = null, CancellationToken cancellationToken = default)
        {
            Writes++;
            throw new InvalidOperationException("The analysis of consumption never writes the log.");
        }
    }

    /// <summary>Evidence by id, with the ids opened.</summary>
    private sealed class StoredEvidence : IEvidenceStore
    {
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

        public List<string> Opened { get; } = [];

        public void Add(string id, string text) => _content[id] = Encoding.UTF8.GetBytes(text);

        public Task<Stream> OpenContentAsync(string projectId, EvidenceId id, CancellationToken cancellationToken)
        {
            Opened.Add(id.Value);
            return _content.TryGetValue(id.Value, out var bytes)
                ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
                : Task.FromException<Stream>(new KeyNotFoundException(id.Value));
        }

        public Task<EvidenceRecord> CaptureAsync(string projectId, EvidenceDraft draft, Stream content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The analysis of consumption never captures evidence.");

        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EvidenceRecord>>([]);

        public Task<IReadOnlyList<EvidenceRecord>> ListByRunAsync(string projectId, string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EvidenceRecord>>([]);
    }
}
