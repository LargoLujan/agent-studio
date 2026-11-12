using AgentStudio.Application.Projects;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Tracing;

namespace AgentStudio.Application.Diagrams;

// Phase 6 contracts (docs/20 D-23 to D-26 and section 4.2, ADR-0045). The generators, the grammar, the cache, the index and the export are WP-D
// (src/AgentStudio.Memory/Diagrams). Phase 16 adds values to DiagramKind and more IDiagramGenerator; its IDiagramRenderer (docs/19 D-18) takes a DiagramSource.

/// <summary>
/// Which diagram. The solution writes two kinds of file (the solution, and the namespaces of each project), so there are four values, and Phase 16
/// appends: the names are written to the index of the cache.
/// </summary>
public enum DiagramKind
{
    /// <summary><c>flowchart LR</c> of the projects, with a <c>subgraph</c> per solution folder.</summary>
    Solution,

    /// <summary>The namespaces of one project; the subject is the id of the project in the model.</summary>
    ProjectNamespaces,

    /// <summary><c>flowchart TD</c> of the plan of a run; the subject is the run.</summary>
    RunPlan,

    /// <summary><c>sequenceDiagram</c> of a run, from its entries in the log; the subject is the run.</summary>
    RunSequence,
}

/// <summary>The state of a diagram in the cache (docs/20 D-25).</summary>
public enum DiagramState
{
    /// <summary>It is not in the cache.</summary>
    NotGenerated,

    /// <summary>The fingerprint of the model now is the one of the index.</summary>
    Current,

    /// <summary>It is not: it says which commit it describes.</summary>
    Stale,
}

/// <summary>The state of the copy exported to the repository, compared with the cache and with what Agent Studio wrote (docs/20 D-25). What is exported is never read back.</summary>
public enum DiagramExportState
{
    /// <summary>Nothing was exported on this computer.</summary>
    NotExported,

    /// <summary>The same as the cache.</summary>
    SameAsCache,

    /// <summary>An earlier version was exported and the cache has another.</summary>
    Older,

    /// <summary>The SHA-256 of the file is not the one written: the person edited it or deleted it. Only its fingerprint is calculated.</summary>
    ChangedOutside,

    /// <summary>The file exists and this computer did not export it (a clone).</summary>
    Unknown,
}

/// <summary>What is generated, apart from where it goes: the text and what it was made from.</summary>
public sealed record DiagramSource
{
    /// <summary>The longest text of a diagram, in characters.</summary>
    public const int MaxMermaidLength = 1_000_000;

    /// <summary>The longest subject.</summary>
    public const int MaxSubjectLength = 400;

    /// <param name="subject">What the diagram is about: empty for the solution, the id of a project, the id of a run.</param>
    /// <param name="modelHash">The SHA-256 of the model it was made from: the <c>ModelHash</c> of the graph; for a run, the hash of its plan or of the head of its log.</param>
    /// <param name="mermaid">The text: <c>\n</c> as the only line end and no byte-order mark, so the golden files are equal byte by byte on any computer.</param>
    /// <exception cref="ArgumentException">A subject that is not one line, a hash that is not a SHA-256, or a text that is empty, too long, has a carriage return or starts with a byte-order mark.</exception>
    public DiagramSource(DiagramKind kind, string subject, string modelHash, string mermaid)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of diagram.");
        }

        if (subject is null || subject.Length > MaxSubjectLength || UntrustedText.HasHidden(subject) || (kind == DiagramKind.Solution) != (subject.Length == 0))
        {
            throw new ArgumentException("The subject is one line: empty for the solution and the id of a project or of a run for the others.", nameof(subject));
        }

        if (!ContentHash.IsSha256Hex(modelHash))
        {
            throw new ArgumentException("The model is named by its SHA-256 (64 lowercase hexadecimal characters).", nameof(modelHash));
        }

        if (string.IsNullOrEmpty(mermaid) || mermaid.Length > MaxMermaidLength || mermaid.Contains('\r') || mermaid[0] == '﻿')
        {
            throw new ArgumentException($"The text of a diagram has 1 to {MaxMermaidLength} characters, ends its lines with \\n and has no byte-order mark.", nameof(mermaid));
        }

        Kind = kind;
        Subject = subject;
        ModelHash = modelHash;
        Mermaid = mermaid;
    }

    public DiagramKind Kind { get; }

    public string Subject { get; }

    public string ModelHash { get; }

    public string Mermaid { get; }
}

/// <summary>Which diagram, for the calls that act on one.</summary>
public sealed record DiagramRef
{
    public DiagramRef(DiagramKind kind, string subject)
    {
        // The rules of the subject are the ones of the source: build one to check them.
        _ = new DiagramSource(kind, subject, new string('0', ContentHash.HexLength), "x");
        Kind = kind;
        Subject = subject;
    }

    public DiagramKind Kind { get; }

    public string Subject { get; }
}

/// <summary>A diagram as the catalog lists it: its state, and what it describes when it exists.</summary>
public sealed record DiagramStatus
{
    /// <exception cref="ArgumentException">A diagram that is not generated has no commit, hash or date; one that is has them. A commit that is not a full id.</exception>
    public DiagramStatus(
        DiagramRef diagram,
        DiagramState state,
        DiagramExportState export,
        string? describes,
        string? modelHash,
        DateTimeOffset? generatedAt,
        bool hasPrevious)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        if (!Enum.IsDefined(state) || !Enum.IsDefined(export))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state of a diagram.");
        }

        var generated = state != DiagramState.NotGenerated;
        if (generated != (modelHash is not null && generatedAt is not null) || (!generated && (describes is not null || hasPrevious)))
        {
            throw new ArgumentException("A diagram that is not generated has no hash, no date, no commit and no previous text; one that is generated has a hash and a date.", nameof(state));
        }

        if (modelHash is not null && !ContentHash.IsSha256Hex(modelHash))
        {
            throw new ArgumentException("The model is named by its SHA-256.", nameof(modelHash));
        }

        // A diagram of the solution says which commit it describes; one of a run says its run, which is its subject.
        if (describes is not null && !TraceRef.IsFullCommitId(describes))
        {
            throw new ArgumentException("The commit a diagram describes is a full id.", nameof(describes));
        }

        Diagram = diagram;
        State = state;
        Export = export;
        Describes = describes;
        ModelHash = modelHash;
        GeneratedAt = generatedAt;
        HasPrevious = hasPrevious;
    }

    public DiagramRef Diagram { get; }

    public DiagramState State { get; }

    public DiagramExportState Export { get; }

    /// <summary>The commit the cached diagram describes, for the diagrams of the solution; null for the others and for one not generated.</summary>
    public string? Describes { get; }

    /// <summary>The fingerprint of the model the cached text was made from.</summary>
    public string? ModelHash { get; }

    public DateTimeOffset? GeneratedAt { get; }

    /// <summary>There is a previous text next to the cache (<c>&lt;diagram&gt;.prev</c>), so «Deshacer» can restore it.</summary>
    public bool HasPrevious { get; }
}

/// <summary>What «Exportar al repositorio» does when the file it would write is there and the person changed it, or this computer did not write it (docs/20 D-25).</summary>
public enum DiagramExportConflictChoice
{
    /// <summary>Write nothing for that file and return it as a conflict, so the screen asks.</summary>
    Ask,

    Overwrite,

    /// <summary>Write it under another name, next to the existing file.</summary>
    SaveAsNew,

    /// <summary>Leave the file as it is.</summary>
    Skip,
}

/// <summary>What the person asked to export. The folder is relative to the project, by default <c>docs/generado/diagramas/</c>; the names of the files are of a closed list and are not chosen here.</summary>
public sealed record DiagramExportRequest
{
    /// <summary>The folder Agent Studio exports to when the person does not choose another.</summary>
    public const string DefaultFolder = "docs/generado/diagramas";

    public const int MaxDiagrams = 200;

    public DiagramExportRequest(IReadOnlyList<DiagramRef> diagrams, string folder, DiagramExportConflictChoice onConflict)
    {
        ArgumentNullException.ThrowIfNull(diagrams);
        if (diagrams.Count is < 1 or > MaxDiagrams || diagrams.Any(d => d is null) || diagrams.Distinct().Count() != diagrams.Count)
        {
            throw new ArgumentException($"An export names 1 to {MaxDiagrams} diagrams, each once.", nameof(diagrams));
        }

        if (!RepositoryPaths.IsSafeRelative(folder) || folder.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) || folder.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The folder is relative to the project, in forward slashes, never leads out of it and is not the one of git.", nameof(folder));
        }

        if (!Enum.IsDefined(onConflict))
        {
            throw new ArgumentOutOfRangeException(nameof(onConflict), onConflict, "Unknown choice.");
        }

        Diagrams = [.. diagrams];
        Folder = folder;
        OnConflict = onConflict;
    }

    public IReadOnlyList<DiagramRef> Diagrams { get; }

    public string Folder { get; }

    public DiagramExportConflictChoice OnConflict { get; }

    public bool Equals(DiagramExportRequest? other) =>
        other is not null && Folder == other.Folder && OnConflict == other.OnConflict && Diagrams.SequenceEqual(other.Diagrams);

    public override int GetHashCode() => HashCode.Combine(Folder, OnConflict, Diagrams.Count);
}

/// <summary>A file the export wrote: which diagram, where (relative to the project) and the SHA-256 of what was written, kept in the index.</summary>
public sealed record DiagramExportedFile
{
    public DiagramExportedFile(DiagramRef diagram, string path, string sha256)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        if (!RepositoryPaths.IsSafeRelative(path) || !ContentHash.IsSha256Hex(sha256))
        {
            throw new ArgumentException("An exported file has a relative path and the SHA-256 of what was written.", nameof(path));
        }

        Diagram = diagram;
        Path = path;
        Sha256 = sha256;
    }

    public DiagramRef Diagram { get; }

    public string Path { get; }

    public string Sha256 { get; }
}

/// <summary>A file the export did not write because it exists and is not the one Agent Studio wrote: the screen asks what to do with it.</summary>
public sealed record DiagramExportConflict
{
    public DiagramExportConflict(DiagramRef diagram, string path, DiagramExportState state)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        if (!RepositoryPaths.IsSafeRelative(path) || state is not (DiagramExportState.ChangedOutside or DiagramExportState.Unknown))
        {
            throw new ArgumentException("A conflict is a relative path whose file was changed outside or was not written by this computer.", nameof(path));
        }

        Diagram = diagram;
        Path = path;
        State = state;
    }

    public DiagramRef Diagram { get; }

    public string Path { get; }

    public DiagramExportState State { get; }
}

/// <summary>What an export did: the files it wrote and the conflicts it left for the person.</summary>
public sealed record DiagramExportResult
{
    public DiagramExportResult(IReadOnlyList<DiagramExportedFile> written, IReadOnlyList<DiagramExportConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(conflicts);
        Written = [.. written];
        Conflicts = [.. conflicts];
    }

    public IReadOnlyList<DiagramExportedFile> Written { get; }

    public IReadOnlyList<DiagramExportConflict> Conflicts { get; }

    public bool Equals(DiagramExportResult? other) =>
        other is not null && Written.SequenceEqual(other.Written) && Conflicts.SequenceEqual(other.Conflicts);

    public override int GetHashCode() => HashCode.Combine(Written.Count, Conflicts.Count);
}

/// <summary>One generator per kind: pure text from a model (docs/20 D-23). It uses a clock only for the header, injected, so the golden files fix it.</summary>
public interface IDiagramGenerator
{
    DiagramKind Kind { get; }

    Task<DiagramSource> GenerateAsync(ProjectContext project, string subject, CancellationToken ct);
}

/// <summary>
/// The diagrams of a project (docs/20 D-25 and D-26): the cache with its index, the state of each diagram and the export to the repository. It
/// never reads back what was exported, and it never exports with a run going.
/// </summary>
public interface IDiagramCatalog
{
    Task<IReadOnlyList<DiagramStatus>> ListAsync(ProjectContext project, CancellationToken ct);

    /// <summary>Generates into the cache and keeps the previous text of that diagram. On one that is current, it changes nothing.</summary>
    Task<DiagramStatus> GenerateAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct);

    /// <summary>Puts back the previous text and its line of the index; the opposite of <see cref="GenerateAsync"/>, byte by byte.</summary>
    Task<DiagramStatus> RestorePreviousAsync(ProjectContext project, DiagramKind kind, string subject, CancellationToken ct);

    /// <summary>Writes the diagrams to the repository as Markdown with a <c>mermaid</c> block; it does not overwrite a file that is not the one it wrote unless the request says so.</summary>
    Task<DiagramExportResult> ExportAsync(ProjectContext project, DiagramExportRequest request, CancellationToken ct);
}
