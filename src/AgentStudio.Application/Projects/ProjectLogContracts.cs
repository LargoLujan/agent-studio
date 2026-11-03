using AgentStudio.Domain.Decisions;

namespace AgentStudio.Application.Projects;

// Phase 4 contract (docs/14 D-05): the decision log of another project, read only, for the usage of recent projects.

/// <summary>How the anchor outside the project stands for a log read from another project (ADR-0010 and its addenda).</summary>
public enum AnchorStatus
{
    /// <summary>
    /// An anchor recognises the log at the project's location, and the log was verified against it. A format-1 anchor kept
    /// under the project id counts too; a read never moves it.
    /// </summary>
    Anchored,

    /// <summary>The log is empty and no anchor exists: a project that never recorded anything.</summary>
    NoHistory,

    /// <summary>The log has entries but no anchor for its location recognises it (moved, copied, or from the repository).</summary>
    Unanchored,

    /// <summary>The anchor of the location belongs to another project.</summary>
    AnotherProject,

    /// <summary>The anchor cannot be read.</summary>
    Unreadable,
}

/// <summary>
/// The decision log of a project as read, without the lock, the migrations or any write: its entries, how they verify
/// against the chain and the anchor, and how the anchor stands. Only a trusted log is counted (<see cref="IsTrusted"/>);
/// any other is listed as excluded with its reason, never mixed.
/// </summary>
public sealed record ProjectLogSnapshot(string ProjectId, string Root, IReadOnlyList<DecisionEntry> Entries, DecisionLogVerification Verification, AnchorStatus Anchor)
{
    /// <summary>The log verifies and an anchor recognises it, or it is empty and has none.</summary>
    public bool IsTrusted => Verification.IsValid && Anchor is AnchorStatus.Anchored or AnchorStatus.NoHistory;
}

/// <summary>
/// Reads the log of another project (D-05). It writes no row, migrates no database, moves no anchor and follows no link:
/// a link in its <c>.agentstudio</c> is refused as opening the project refuses it, with an <see cref="IOException"/> that
/// names the path and why. SQLite may create the <c>-wal</c> and <c>-shm</c> files of the database, already excluded
/// from git and from the comparison of the user's repository.
/// </summary>
public interface IProjectLogReader
{
    Task<ProjectLogSnapshot> ReadAsync(string projectRoot, CancellationToken ct);
}
