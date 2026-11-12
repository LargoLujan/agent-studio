using AgentStudio.Application.Judging;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Judging;

namespace AgentStudio.Debate.Judging;

/// <summary>Where the judging module writes its dossier packages, decided by the composition root (<c>OrchestrationPaths.DossierRoot</c>).</summary>
public sealed record JudgingOptions
{
    /// <summary>The most bytes of one evidence copied into a package; the rest is left out and the file is marked truncated.</summary>
    public const long DefaultMaxEvidenceFileBytes = 2 * 1024 * 1024;

    public JudgingOptions(string dossierRoot, long maxEvidenceFileBytes = DefaultMaxEvidenceFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dossierRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvidenceFileBytes);
        DossierRoot = dossierRoot;
        MaxEvidenceFileBytes = maxEvidenceFileBytes;
    }

    public string DossierRoot { get; }

    public long MaxEvidenceFileBytes { get; }
}

/// <summary>
/// The copy of one evidence in a package. <see cref="RelativePath"/> is null when its content could not be read;
/// <see cref="ContentVerified"/> says the whole content still matches the SHA-256 recorded when it was captured (the original,
/// not the copy). <see cref="SaidByAgent"/> names the fields of a system-captured document that the agent wrote and the copy
/// marks as such (an attempt record's <c>outputs</c>, ADR-0051); the copy then differs from the stored content in that mark only.
/// </summary>
public sealed record DossierEvidenceFile(EvidenceId Evidence, string? RelativePath, bool ContentVerified, bool Truncated, long SizeBytes)
{
    public IReadOnlyList<string> SaidByAgent { get; init; } = [];
}

/// <summary>
/// The files of a dossier on disk, under the dossier root: <c>dossier.json</c> (the same data the judge gets in its
/// prompt), <c>request.txt</c> (the literal original request) and <c>evidence/</c> (the content of every evidence).
/// <see cref="SystemChecks"/> are the deterministic findings of the system (replay problems, evidence that changed
/// after capture or cannot be read, a run header that disagrees with the log).
/// </summary>
public sealed record DossierFiles(
    string Folder,
    string ProjectId,
    string RunId,
    IReadOnlyList<DossierEvidenceFile> Evidence,
    IReadOnlyList<string> SystemChecks);

/// <summary>A dossier and its files.</summary>
public sealed record JudgeDossierPackage(JudgeDossier Dossier, DossierFiles Files);

/// <summary>
/// The richer port of the judging module: the dossier of <see cref="IJudgeDossierBuilder"/> plus the package of files the
/// judge reads. Callers pass identifiers only.
/// </summary>
public interface IJudgeDossierPackageBuilder
{
    Task<JudgeDossierPackage> BuildPackageAsync(DossierRequest request, CancellationToken cancellationToken);
}
