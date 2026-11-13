using AgentStudio.Application.Hosting;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Desktop.Repository.PullRequests;

/// <summary>What the section of pull requests can say, in the order the screen checks it (docs/21 section 7.1): each state but <see cref="Ready"/> has its own guide.</summary>
public enum PullRequestsStatus
{
    /// <summary>Nothing was read yet.</summary>
    NotRead,

    /// <summary>No project is open.</summary>
    NoProject,

    /// <summary>The shell was composed without the ports of the remote: the section says it is not available.</summary>
    Unavailable,

    /// <summary>The remote of the project is not one the app recognises (another host, no remote, an address with a secret).</summary>
    NoRemote,

    /// <summary><c>gh</c> is not installed.</summary>
    NoGh,

    /// <summary>The platform asked for a session.</summary>
    SignInNeeded,

    /// <summary>The platform asked to wait: the section says until when.</summary>
    RateLimited,

    /// <summary>The remote is known and the program is there: the list is what the log holds, whatever it says.</summary>
    Ready,
}

/// <summary>One sentence of a card or of a detail, and whether it warns. The screen reader says its text.</summary>
public sealed record CardLine(string Text, bool IsWarning = false)
{
    public override string ToString() => Text;
}

/// <summary>A way to merge the person may pick: only those the repository and the branch admit.</summary>
public sealed record MethodOption(MergeMethod Method, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A run that has a branch in the repository and no pull request the log knows: it can be published.</summary>
public sealed record PublishableRunRow(string Run)
{
    public override string ToString() => Run;
}

/// <summary>A piece of remote work that waits to be synchronised, with its reason.</summary>
public sealed record PendingLine(string Text)
{
    public override string ToString() => Text;
}

/// <summary>A standing permission to publish, as the log holds it, with who and when.</summary>
public sealed record GrantRow(string GrantId, string ClassText, string WhereText, string WhoText, string WhenText)
{
    public override string ToString() => $"{ClassText} · {WhereText} · {WhenText}";
}

/// <summary>
/// A followed pull request as the list draws it (docs/21 section 7.1). Every cell comes from the projection of the verified log (<see cref="PullRequestView"/>) and from the
/// capabilities of the provider: a cell with no data is empty and the screen leaves it out, never a default that looks real. The title, the names of the checks and the
/// environment are texts of the remote: bounded and cleaned, and drawn as data.
/// </summary>
public sealed class PullRequestRow
{
    internal PullRequestRow(PullRequestView view)
    {
        View = view;
    }

    internal PullRequestView View { get; }

    public PullRequestRef Reference => View.PullRequest;

    public int Number => View.PullRequest.Number;

    /// <summary>«#12».</summary>
    public string NumberText { get; init; } = string.Empty;

    /// <summary>The title of the remote, as data; empty when the cache has none.</summary>
    public string Title { get; init; } = string.Empty;

    public string StateText { get; init; } = string.Empty;

    /// <summary>The run it comes from; empty when the log does not say.</summary>
    public string OriginText { get; init; } = string.Empty;

    /// <summary>The verdict in force, or that there is none.</summary>
    public string VerdictText { get; init; } = string.Empty;

    /// <summary>Whether the head is the version the judge saw; empty when there is no observation.</summary>
    public string JudgedText { get; init; } = string.Empty;

    public bool HeadMoved { get; init; }

    public string ChecksText { get; init; } = string.Empty;

    public string ReviewsText { get; init; } = string.Empty;

    /// <summary>Only when the platform reports deployments and one was seen; empty otherwise.</summary>
    public string DeploymentText { get; init; } = string.Empty;

    /// <summary>The environment of the deployment, as the remote wrote it (data: cleaned and bounded, drawn apart from any sentence); empty without one.</summary>
    public string EnvironmentText { get; init; } = string.Empty;

    /// <summary>«Hace 3 min», «Todavía sin observar» or the pending reason.</summary>
    public string SyncText { get; init; } = string.Empty;

    public bool IsPending { get; init; }

    public bool HasTitle => Title.Length > 0;

    public bool HasDeployment => DeploymentText.Length > 0;

    /// <summary>What a screen reader says of the row: its number, its state and its verdict, then its title.</summary>
    public override string ToString() =>
        string.Join(" · ", new[] { NumberText, StateText, VerdictText, Title }.Where(s => s.Length > 0));
}
