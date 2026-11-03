using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Metrics;

namespace AgentStudio.Tests.Integration.Phase5.Live;

/// <summary>
/// What one demonstration leaves in <c>AGENTSTUDIO_CAPTURE_DIR\fase5\&lt;block&gt;.json</c>, read back from the stores and
/// the log after it ran (never from what the test intended): the debate, the verdicts and the usage with its origin, with
/// the user's profile folder replaced by <c>&lt;usuario&gt;</c>. <see cref="DemoReport"/> composes
/// <c>docs/evidencias/fase5-demostraciones.md</c> from these files. Enums are kept by name, figures with their origin.
/// </summary>
internal sealed record DemoCapture
{
    /// <summary>The stable id of the block (<see cref="DemoPlan"/>): <c>libre</c>, <c>auditoria-implementacion</c>…</summary>
    public string Block { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>The test that produced it: class and method.</summary>
    public string Test { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset FinishedAt { get; init; }

    /// <summary>What happened, in one line, in Spanish.</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>Whether the demonstration went the whole way (a debate closed by its judgment, an audit with a verdict…).</summary>
    public bool Completed { get; init; }

    public IReadOnlyList<DemoTool> Tools { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    public DemoRunFacts? Run { get; init; }

    public DemoDebateFacts? Debate { get; init; }

    public DemoAuditFacts? Audit { get; init; }

    public DemoSuiteFacts? Suite { get; init; }

    public DemoChallengeFacts? Challenge { get; init; }

    public IReadOnlyList<DemoPreparedFacts> Prepared { get; init; } = [];

    public IReadOnlyList<DemoVerdictFacts> Verdicts { get; init; } = [];

    public IReadOnlyList<DemoAttemptFacts> Attempts { get; init; } = [];

    public IReadOnlyList<DemoQuotaFacts> Quota { get; init; } = [];

    /// <summary>The lines of the project's prompt journal that name the unit (its id): each one with its tag.</summary>
    public IReadOnlyList<string> Journal { get; init; } = [];

    /// <summary>The entries of the unit in the log, in order.</summary>
    public IReadOnlyList<DemoEntryFacts> Log { get; init; } = [];

    public bool LogIntact { get; init; }

    public string? HeadHash { get; init; }

    /// <summary>The alerts of the unit (<c>AuditAlertRaised</c>): code and message.</summary>
    public IReadOnlyList<string> Alerts { get; init; } = [];

    /// <summary>What the scripted person was asked, and whether it approved.</summary>
    public IReadOnlyList<DemoApprovalFacts> Approvals { get; init; } = [];
}

/// <summary>A figure with its origin (docs/14 D-03): the value, the name of its <see cref="MetricOrigin"/> and where it came from.</summary>
internal sealed record DemoFigure(decimal? Value, string Origin, string Source)
{
    public static DemoFigure Of(MetricValue<long> metric) => new(metric.Value, metric.Origin.ToString(), metric.Source);

    public static DemoFigure Of(MetricValue<decimal> metric) => new(metric.Value, metric.Origin.ToString(), metric.Source);

    public static DemoFigure Of(MetricValue<double> metric) => new(metric.Value is { } value ? (decimal)value : null, metric.Origin.ToString(), metric.Source);

    public static DemoFigure Of(MetricSnapshot? snapshot) => snapshot is null
        ? new(null, nameof(MetricOrigin.Missing), "sin dato")
        : new(snapshot.Value, (snapshot.Origin ?? FromProvenance(snapshot.Provenance)).ToString(), snapshot.Source);

    public MetricOrigin OriginValue => Enum.TryParse<MetricOrigin>(Origin, out var origin) ? origin : MetricOrigin.Missing;

    /// <summary>The figure as a value of the domain again (a value that does not fit its origin is «sin dato»).</summary>
    public MetricValue<long> AsLong()
    {
        var origin = OriginValue;
        var value = Value is { } v ? (long?)decimal.ToInt64(decimal.Truncate(v)) : null;
        return origin.CarriesValue() == value.HasValue ? MetricValue<long>.Of(origin, value, Source.Length == 0 ? "-" : Source) : MetricValue<long>.Missing(Source.Length == 0 ? "sin dato" : Source);
    }

    public MetricValue<decimal> AsDecimal()
    {
        var origin = OriginValue;
        return origin.CarriesValue() == Value.HasValue ? MetricValue<decimal>.Of(origin, Value, Source.Length == 0 ? "-" : Source) : MetricValue<decimal>.Missing(Source.Length == 0 ? "sin dato" : Source);
    }

    private static MetricOrigin FromProvenance(MetricProvenance provenance) => provenance switch
    {
        MetricProvenance.Exact => MetricOrigin.ReportedByProvider,
        MetricProvenance.Estimated => MetricOrigin.EstimatedBySystem,
        _ => MetricOrigin.Missing,
    };
}

/// <summary>A CLI as the app's probes saw it (<c>--version</c>, <c>claude auth status</c>, <c>codex login status</c>); nothing that names the account.</summary>
internal sealed record DemoTool(string Name, string Installation, string? Version, string Authentication);

internal sealed record DemoCriterion(string Id, string Method, string Description);

internal sealed record DemoRequirement(string Id, string Statement, string Priority, IReadOnlyList<DemoCriterion> Criteria);

/// <summary>A participant: the label the judge saw and who was behind it (D-15).</summary>
internal sealed record DemoParticipant(string Label, string Role, string Agent, string Provider, string Vendor, string? Model);

internal sealed record DemoJudge(string Agent, string Provider, string Vendor, string? Model);

/// <summary>A <c>DebateRoundClosed</c>: the phase that follows, why it went to judgment, the limit, the tokens with their origin and the seconds.</summary>
internal sealed record DemoRound(int Round, string Next, string? Judgment, string Limit, DemoFigure Tokens, bool IncludesEstimates, long ElapsedSeconds, string Reason);

/// <summary>A published contribution, its text read against its anchored hash (the summary and the argument of its JSON).</summary>
internal sealed record DemoContribution(
    string Label,
    int Round,
    string Author,
    string Kind,
    string? RepliesTo,
    string? Summary,
    string? Argument,
    int Claims,
    int Citations,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Concedes,
    bool Finished,
    int Redactions,
    DateTimeOffset AnsweredAt,
    string Sha256);

/// <summary>A piece of evidence the system captured (a build or a test run): its title, commit, isolation and counters.</summary>
internal sealed record DemoEvidence(string Id, string Kind, string Origin, string Title, string? SubjectCommit, string? Isolation, int? Passed, int? Failed, int? Skipped, int? ExitCode);

/// <summary>Everything a debate shows: what was approved, how it went round by round, and how it ended.</summary>
internal sealed record DemoDebateFacts
{
    public string Id { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string? SubjectReference { get; init; }

    public string? SnapshotCommit { get; init; }

    public string? Question { get; init; }

    public string RequirementSetHash { get; init; } = string.Empty;

    public IReadOnlyList<DemoRequirement> Requirements { get; init; } = [];

    public IReadOnlyList<DemoParticipant> Participants { get; init; } = [];

    public IReadOnlyList<DemoJudge> Judges { get; init; } = [];

    public string Judging { get; init; } = string.Empty;

    public IReadOnlyList<string> WaivedVendors { get; init; } = [];

    public IReadOnlyList<string> ProducerVendors { get; init; } = [];

    public int MaxRounds { get; init; }

    public double MaxMinutes { get; init; }

    public long? MaxTokens { get; init; }

    public int MaxAgentRuns { get; init; }

    public long? EstimatedTokens { get; init; }

    public IReadOnlyList<string> EstimateNotes { get; init; } = [];

    public string ApprovalText { get; init; } = string.Empty;

    public IReadOnlyList<DemoRound> Rounds { get; init; } = [];

    public IReadOnlyList<DemoContribution> Contributions { get; init; } = [];

    public string Phase { get; init; } = string.Empty;

    public string Outcome { get; init; } = string.Empty;

    public string? Rule { get; init; }

    public string? Decision { get; init; }

    public string? ChosenProposal { get; init; }

    public string? JudgmentReason { get; init; }

    public string LimitReached { get; init; } = string.Empty;

    public string ResultSummary { get; init; } = string.Empty;

    /// <summary>Why the test stopped the debate (it waited for a person, or ran past the limit of the test); null when it ended by itself.</summary>
    public string? Stopped { get; init; }

    public string? AwaitingReason { get; init; }

    public string? AwaitingMessage { get; init; }

    public DemoFigure? Tokens { get; init; }

    public int ReportedTurns { get; init; }

    public int AgentRuns { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>What the room showed while the debate went on: turns launched and finished, sealed rounds, judges…</summary>
    public IReadOnlyList<string> Timeline { get; init; } = [];

    /// <summary>The evidence of «Ejecutar pruebas», when the demonstration asked for it.</summary>
    public IReadOnlyList<DemoEvidence> Tests { get; init; } = [];
}

internal sealed record DemoIssue(string Code, string Message);

internal sealed record DemoAssessment(string Requirement, string Status, int Evidence, string Rationale);

internal sealed record DemoFinding(string Category, string Severity, string Description);

internal sealed record DemoComparison(string Proposal, string Strengths, string Weaknesses);

/// <summary>A stored verdict as the judge gave it, with what the validator found in it and the note of its <c>VerdictIssued</c>.</summary>
internal sealed record DemoVerdictFacts
{
    public string Id { get; init; } = string.Empty;

    public string? Unit { get; init; }

    public string Scope { get; init; } = string.Empty;

    public DemoJudge Judge { get; init; } = new(string.Empty, string.Empty, string.Empty, null);

    public string Decision { get; init; } = string.Empty;

    public string? ChosenProposal { get; init; }

    public string Confidence { get; init; } = string.Empty;

    public string ConfidenceJustification { get; init; } = string.Empty;

    public bool Valid { get; init; }

    public string? Supersedes { get; init; }

    public IReadOnlyList<DemoIssue> Issues { get; init; } = [];

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<DemoAssessment> Assessments { get; init; } = [];

    public IReadOnlyList<DemoFinding> Findings { get; init; } = [];

    public IReadOnlyList<DemoComparison> Comparison { get; init; } = [];

    public IReadOnlyList<string> Risks { get; init; } = [];

    public IReadOnlyList<string> CorrectiveActions { get; init; } = [];

    public IReadOnlyList<string> PendingObjections { get; init; } = [];

    public bool RequiresHumanIntervention { get; init; }

    public string RequirementSetHash { get; init; } = string.Empty;

    public string DecisionLogHeadHash { get; init; } = string.Empty;

    public DateTimeOffset IssuedAt { get; init; }

    public string? VendorNote { get; init; }
}

/// <summary>One agent attempt as the usage projection of Phase 4 counts it (ADR-0019): its own figures, each with its origin.</summary>
internal sealed record DemoAttemptFacts(
    string Unit,
    string? Task,
    int Attempt,
    string? Purpose,
    string Agent,
    string Provider,
    string Vendor,
    string? ConfiguredModel,
    string? ReportedModel,
    string Outcome,
    double? DurationSeconds,
    DemoFigure Input,
    DemoFigure CacheRead,
    DemoFigure CacheWrite,
    DemoFigure Output,
    DemoFigure Reasoning,
    DemoFigure Cost);

/// <summary>The latest observation of a window of quota (docs/14 D-14), whitelisted: nothing that names the account.</summary>
internal sealed record DemoQuotaFacts(string Provider, string Window, string? Label, DemoFigure Utilization, DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt, string Source, string? Reached);

internal sealed record DemoEntryFacts(long Sequence, DateTimeOffset At, string Actor, string Kind, string Summary);

internal sealed record DemoApprovalFacts(string Action, bool Approved, string Description);

internal sealed record DemoTask(string Id, string Kind, string? Agent, IReadOnlyList<string> DependsOn);

internal sealed record DemoAuditorFinding(string Code, string Severity, string Message);

/// <summary>The run the review modes judge (F3-8 on the repository of the demonstrations), and what the auditor of the orchestrator found in it.</summary>
internal sealed record DemoRunFacts
{
    public string Run { get; init; } = string.Empty;

    public string PlanningRun { get; init; } = string.Empty;

    public string Request { get; init; } = string.Empty;

    public IReadOnlyList<DemoRequirement> Requirements { get; init; } = [];

    public string RequirementSetHash { get; init; } = string.Empty;

    public bool PlanFromOrchestrator { get; init; }

    public bool PlanCorrected { get; init; }

    public IReadOnlyList<string> PlanViolations { get; init; } = [];

    public IReadOnlyList<DemoTask> Tasks { get; init; } = [];

    public string Phase { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string? BaseCommit { get; init; }

    public string? IntegrationCommit { get; init; }

    /// <summary>The commit the run's last clean integration left, as the log records it: what the review modes judge.</summary>
    public string? CandidateCommit { get; init; }

    /// <summary>The vendors that produced the run, as the log says (never the judges): Anthropic alone, so Codex may judge it.</summary>
    public IReadOnlyList<string> ProducerVendors { get; init; } = [];

    public string? VerdictId { get; init; }

    public bool AuditorLogValid { get; init; }

    public IReadOnlyList<DemoAuditorFinding> AuditorFindings { get; init; } = [];

    public bool ReplanRequested { get; init; }

    public string? Stopped { get; init; }
}

internal sealed record DemoStep(string Id, string Title, string Executable, IReadOnlyList<string> Arguments, double TimeoutMinutes, string Isolation);

internal sealed record DemoPending(string Code, string Source, string Message);

/// <summary>The whole-project audit of Agent Studio (D-32): what was prepared and approved, how HEAD verified and what the judge said.</summary>
internal sealed record DemoAuditFacts
{
    public string? Audit { get; init; }

    public string Repository { get; init; } = string.Empty;

    public string? Head { get; init; }

    public bool Uncommitted { get; init; }

    public string RequirementSetHash { get; init; } = string.Empty;

    public IReadOnlyList<DemoRequirement> Requirements { get; init; } = [];

    public DemoJudge? Judge { get; init; }

    public IReadOnlyList<string> Producers { get; init; } = [];

    public IReadOnlyList<string> Declared { get; init; } = [];

    public IReadOnlyList<string> Skipped { get; init; } = [];

    public IReadOnlyList<DemoStep> Profile { get; init; } = [];

    public IReadOnlyList<string> Exclusions { get; init; } = [];

    public long? EstimatedTokens { get; init; }

    public bool CanJudge { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>What the same preparation says with the Claude judge: rule P must refuse it (the code is declared Anthropic's).</summary>
    public IReadOnlyList<string> ClaudeJudgeProblems { get; init; } = [];

    public bool ClaudeJudgeCanJudge { get; init; }

    public string ApprovalText { get; init; } = string.Empty;

    public string? Disposition { get; init; }

    public bool? Trustworthy { get; init; }

    public bool? LogIntact { get; init; }

    public bool? Corrected { get; init; }

    public string? VerdictId { get; init; }

    public IReadOnlyList<DemoEvidence> Verification { get; init; } = [];

    public IReadOnlyList<DemoPending> Pending { get; init; } = [];

    public long? DossierBytes { get; init; }

    public bool? SnapshotRemoved { get; init; }
}

internal sealed record DemoStepResult(string Id, string Title, bool Succeeded, bool TimedOut, int ExitCode, int? Passed, int? Failed, int? Skipped, string? Isolation);

internal sealed record DemoFailedTest(string Name, IReadOnlyList<string> Categories);

/// <summary>The suite of Agent Studio run by the isolated verifier at Low integrity, without quota (D-32): what fails there and by which category.</summary>
internal sealed record DemoSuiteFacts
{
    public string Repository { get; init; } = string.Empty;

    public string Head { get; init; } = string.Empty;

    public IReadOnlyList<DemoStep> Profile { get; init; } = [];

    public IReadOnlyList<DemoStepResult> Steps { get; init; } = [];

    public double Minutes { get; init; }

    public IReadOnlyList<DemoFailedTest> Failed { get; init; } = [];

    /// <summary>The categories with a failing test: what <c>AGENTSTUDIO_PHASE5_AUDIT_EXCLUDE</c> would leave out.</summary>
    public IReadOnlyList<string> SuggestedExclusions { get; init; } = [];

    /// <summary>Failing tests of no category, which no trait filter leaves out.</summary>
    public IReadOnlyList<string> Uncategorized { get; init; } = [];
}

/// <summary>The challenge of a verdict of the demonstrations (F5-7): the verdict, the reasons, the revision and where it stands.</summary>
internal sealed record DemoChallengeFacts
{
    public string Verdict { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public DemoJudge Judge { get; init; } = new(string.Empty, string.Empty, string.Empty, null);

    public string Reasons { get; init; } = string.Empty;

    public string? Revision { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> ExcludedVendors { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What «Abrir debate» or the preparation of the audit shows, without spending anything (D-06).</summary>
internal sealed record DemoPreparedFacts(
    string Block,
    string Mode,
    string Judging,
    IReadOnlyList<DemoJudge> Judges,
    IReadOnlyList<string> WaivedVendors,
    int MaxAgentRuns,
    long? EstimatedTokens,
    IReadOnlyList<string> Problems,
    string ApprovalText);

/// <summary>Writes and reads the captures, always without the user's profile folder.</summary>
internal static class DemoCaptures
{
    public const string FolderName = "fase5";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Folder(string captureDirectory) => Path.Combine(captureDirectory, FolderName);

    /// <summary>Writes <c>&lt;capture&gt;\fase5\&lt;block&gt;.json</c> (replacing the one of an earlier run of the same block) and returns its path.</summary>
    public static string Write(string captureDirectory, DemoCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var folder = Folder(captureDirectory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, capture.Block + ".json");
        File.WriteAllText(path, DemoScrub.Text(JsonSerializer.Serialize(capture, Json)) + "\n", Utf8NoBom);
        return path;
    }

    /// <summary>
    /// What a demonstration leaves when reading its evidence failed after it spent quota: the block, the unit, the notes and
    /// the error, so the document says it and the rest can still be read from the workspace. It never throws.
    /// </summary>
    public static void WriteFailure(string block, string title, string test, DateTimeOffset startedAt, IReadOnlyList<DemoTool> tools, string? unit, IReadOnlyList<string> notes, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (DemoSettings.CaptureDirectory is not { } folder)
        {
            return;
        }

        try
        {
            Write(folder, new DemoCapture
            {
                Block = block,
                Title = title,
                Test = test,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Result = $"La demostración se interrumpió o su evidencia no se pudo leer entera ({error.GetType().Name}: {error.Message}).",
                Completed = false,
                Tools = tools,
                Notes = [.. notes, unit is null ? "Lo que llegó a registrarse está en la base del espacio de trabajo." : $"Lo que llegó a registrarse de {unit} está en la base del espacio de trabajo."],
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("The failure capture could not be written: " + ex.Message);
        }
    }

    /// <summary>Every capture in <c>&lt;capture&gt;\fase5</c>; one that cannot be read is reported instead of failing.</summary>
    public static (IReadOnlyList<DemoCapture> Captures, IReadOnlyList<string> Unreadable) ReadAll(string captureDirectory)
    {
        var folder = Folder(captureDirectory);
        var captures = new List<DemoCapture>();
        var unreadable = new List<string>();
        if (!Directory.Exists(folder))
        {
            return (captures, unreadable);
        }

        foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (JsonSerializer.Deserialize<DemoCapture>(File.ReadAllText(file, Encoding.UTF8), Json) is { Block.Length: > 0 } capture)
                {
                    captures.Add(capture);
                }
                else
                {
                    unreadable.Add(Path.GetFileName(file) + ": no es una captura de la Fase 5");
                }
            }
            catch (JsonException ex)
            {
                unreadable.Add(Path.GetFileName(file) + ": " + ex.Message);
            }
        }

        return (captures, unreadable);
    }
}

/// <summary>
/// Removes the user's profile folder from everything a demonstration writes: <c>C:\Users\&lt;name&gt;</c> becomes
/// <c>&lt;usuario&gt;</c> in every spelling a path takes here (backslashes, escaped in JSON, forward slashes and the
/// <c>//c/…</c> form of the read rules of Claude Code).
/// </summary>
internal static class DemoScrub
{
    public const string Replacement = "<usuario>";

    public static string Text(string? text) => Text(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Text(string? text, string profile)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(profile))
        {
            return text ?? string.Empty;
        }

        var root = profile.TrimEnd('\\', '/');
        var forward = root.Replace('\\', '/');
        var spellings = new List<string>
        {
            root.Replace("\\", "\\\\", StringComparison.Ordinal),
            root,
            forward,
        };
        if (forward.Length > 2 && forward[1] == ':')
        {
            spellings.Add("//" + char.ToLowerInvariant(forward[0]) + forward[2..]);
            spellings.Add("/" + char.ToLowerInvariant(forward[0]) + forward[2..]);
        }

        var result = text;
        foreach (var spelling in spellings)
        {
            result = result.Replace(spelling, Replacement, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
