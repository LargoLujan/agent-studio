using AgentStudio.Application.Projects;
using AgentStudio.Domain.Tracing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Desktop.Repository;

/// <summary>The four sections of «Repositorio y cambios» (docs/16 F9-9), in the order of its tabs.</summary>
public enum RepositorySection
{
    Summary,
    Branches,
    Commits,
    Activity,

    /// <summary>Phase 10 (docs/21 section 7.1): the pull requests, after the four of Phase 9.</summary>
    PullRequests,
}

/// <summary>A remote of the repository: its name and its URL without credentials (both are data of the configuration, shown as they are).</summary>
public sealed record RemoteRow(string Name, string Url)
{
    /// <summary>What a screen reader says of a remote: its name and its address.</summary>
    public override string ToString() => $"{Name} · {Url}";
}

/// <summary>A local branch with the saved version it points at; a branch of a run of the system says so.</summary>
/// <param name="RemoteText">Phase 10 (docs/21 section 7.3): what the log says of its branch on the remote (published, at which version, deleted there); empty when it says nothing.</param>
public sealed record BranchRow(string Name, string Version, string Marks, bool IsCurrent, bool IsRun, string RemoteText = "")
{
    /// <summary>What a screen reader says of a branch: its name, its marks and what is known of its remote branch.</summary>
    public override string ToString() => string.Join(" · ", new[] { Name, Marks, RemoteText }.Where(s => s.Length > 0));
}

/// <summary>One line of what a commit says of its origin: what the log backs, or a suggestion nobody confirmed (D-44).</summary>
/// <param name="Text">The line, ready to read.</param>
/// <param name="IsSuggestion">It is a suggestion, marked as such: it is not a fact of the log and the screen never draws it as one.</param>
public sealed record OriginLine(string Text, bool IsSuggestion)
{
    public override string ToString() => Text;
}

/// <summary>What the person asked when they opened the chain of a saved version: the project and the node of the trace to walk from.</summary>
public sealed class RepositoryChainRequest(ProjectContext project, TraceRef node) : EventArgs
{
    public ProjectContext Project { get; } = project;

    /// <summary>The saved version as a node of the trace (its full id): <c>ITraceChainReader.ChainAsync</c> walks from it.</summary>
    public TraceRef Node { get; } = node;
}

/// <summary>
/// A saved version (commit) of the graph. Its subject and its author are data of whoever wrote it, shown as they are; what it says of
/// its origin comes only from <see cref="AgentStudio.Application.Git.CommitAnnotation"/> (the log), never from its message.
/// </summary>
public sealed partial class CommitRow : ObservableObject
{
    internal CommitRow(
        string id,
        string shortId,
        string subject,
        string author,
        string when,
        string refs,
        IReadOnlyList<OriginLine> origin,
        bool isSystem,
        bool canOpenChain,
        Action<CommitRow> openChain)
    {
        Id = id;
        ShortId = shortId;
        Subject = subject;
        Author = author;
        When = when;
        Refs = refs;
        Origin = origin;
        IsSystem = isSystem;
        _canOpenChain = canOpenChain;
        OpenChainCommand = new RelayCommand(() => openChain(this));
    }

    /// <summary>The full id of the commit, the node of the trace.</summary>
    public string Id { get; }

    public string ShortId { get; }

    public string Subject { get; }

    public string Author { get; }

    /// <summary>When it was made, in local time.</summary>
    public string When { get; }

    /// <summary>The branches and tags that point at it, as git names them; empty when none.</summary>
    public string Refs { get; }

    /// <summary>What the log says of it, one line each; the suggestions are marked and come last.</summary>
    public IReadOnlyList<OriginLine> Origin { get; }

    /// <summary>The log backs that Agent Studio made it (a run, a task and an attempt of the log).</summary>
    public bool IsSystem { get; }

    /// <summary>Someone connected a way to open its chain: without it there is no button, since a button that does nothing is not offered.</summary>
    [ObservableProperty]
    private bool _canOpenChain;

    /// <summary>«Ver su cadena»: raises <see cref="RepositoryViewModel.ChainRequested"/> for this version.</summary>
    public IRelayCommand OpenChainCommand { get; }

    /// <summary>What a screen reader says of a version: its short id and its subject.</summary>
    public override string ToString() => $"{ShortId} · {Subject}";
}

/// <summary>An event of the timeline with its real result.</summary>
public sealed record ActivityRow(string When, string Kind, string Where, string Version, string Result, bool Succeeded)
{
    /// <summary>What a screen reader says of an event: when, what and its result.</summary>
    public override string ToString() => $"{When} · {Kind} · {Result}";
}
