using AgentStudio.Domain.Common;

namespace AgentStudio.Domain.Evidence;

public enum EvidenceKind
{
    TestRun,
    BuildLog,
    CommandOutput,
    CodeDiff,
    Document,
    AgentStatement,
    HumanNote,

    // Appended: the stores persist the name, and nothing may shift the values before it.

    /// <summary>The paths and git messages of a merge that conflicted, captured by the integrator. It is never resolved automatically.</summary>
    ConflictReport,
}

/// <summary>
/// Who produced the evidence. A judge must not accept "the agent says the tests pass" as proof: only evidence captured
/// by Agent Studio itself (or provided by a human) can support approval of a mandatory requirement.
/// </summary>
public enum EvidenceOrigin
{
    CapturedBySystem,
    ReportedByAgent,
    ProvidedByHuman,
}

/// <summary>Result of a test run executed and parsed by Agent Studio.</summary>
public sealed record TestRunSummary(int Passed, int Failed, int Skipped, int ExitCode)
{
    /// <summary>A run that executed no test is not a success: it proves nothing.</summary>
    public bool Succeeded => ExitCode == 0 && Failed == 0 && Passed > 0;
}

/// <summary>
/// Metadata of an immutable piece of evidence. The content itself lives in the evidence store, addressed by its
/// SHA-256; any later change to the content is detectable with <see cref="Matches"/>.
/// </summary>
public sealed record EvidenceRecord
{
    public EvidenceRecord(
        EvidenceId id,
        EvidenceKind kind,
        EvidenceOrigin origin,
        string title,
        string producedBy,
        DateTimeOffset capturedAt,
        string contentSha256,
        TestRunSummary? testRun = null,
        IReadOnlyList<RequirementId>? covers = null,
        string? subjectCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(producedBy);

        if (!ContentHash.IsSha256Hex(contentSha256))
        {
            throw new DomainRuleViolationException("evidence.invalid-hash", "Evidence hash must be 64 lowercase hex characters.");
        }

        if (kind == EvidenceKind.TestRun && testRun is null)
        {
            throw new DomainRuleViolationException("evidence.test-run-needs-summary", "Test run evidence must include its summary.");
        }

        if (subjectCommit is not null && !IsObjectId(subjectCommit))
        {
            throw new DomainRuleViolationException("evidence.invalid-commit", "The subject commit must be a full git object id (40 or 64 lowercase hex characters).");
        }

        Id = id;
        Kind = kind;
        Origin = origin;
        Title = title;
        ProducedBy = producedBy;
        CapturedAt = capturedAt;
        ContentSha256 = contentSha256;
        TestRun = testRun;
        Covers = covers ?? [];
        SubjectCommit = subjectCommit;
    }

    public EvidenceId Id { get; }

    public EvidenceKind Kind { get; }

    public EvidenceOrigin Origin { get; }

    public string Title { get; }

    public string ProducedBy { get; }

    public DateTimeOffset CapturedAt { get; }

    public string ContentSha256 { get; }

    public TestRunSummary? TestRun { get; }

    /// <summary>Requirements this evidence claims to cover (a hint for the judge, not a verdict).</summary>
    public IReadOnlyList<RequirementId> Covers { get; }

    /// <summary>
    /// The commit this evidence verified: the one checked out where the build or the test run ran, as the system knew
    /// it when it captured the evidence. Null when the evidence is not about one commit (a prompt, a report).
    /// </summary>
    public string? SubjectCommit { get; }

    public bool IsObjective => Origin is EvidenceOrigin.CapturedBySystem or EvidenceOrigin.ProvidedByHuman;

    public bool Matches(ReadOnlySpan<byte> content) => ContentHash.Sha256Hex(content) == ContentSha256;

    /// <summary>A full git object id as git prints it: SHA-1 (40) or SHA-256 (64) lowercase hex characters.</summary>
    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
