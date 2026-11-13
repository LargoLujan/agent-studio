using System.Globalization;
using AgentStudio.Domain.Orchestration;

namespace AgentStudio.Domain.Hosting;

/// <summary>The hosting platforms of a remote. Appended, never reordered; Azure DevOps has no adapter in Phase 10.</summary>
public enum HostingProviderKind
{
    GitHub,
    AzureDevOps,
}

/// <summary>
/// A repository on a hosting platform, by validated identifiers (docs/21 D-04). It is never built from free text: a parse rejects
/// what does not pass, it does not fix it. The address of a push is always written by the code from these identifiers
/// (<see cref="PushUrl"/>), never taken from the configuration of the repository.
/// </summary>
/// <remarks>
/// The properties have no setters, not even <c>init</c>, so a <c>with</c> expression cannot build a repository that was not
/// validated. Equality is exact (ordinal): a grant given for <c>Owner/Repo</c> does not cover <c>owner/repo</c>, which only asks again.
/// </remarks>
public sealed record RemoteRepository
{
    /// <summary>The only host of Phase 10, exactly: no port, no user, no other case.</summary>
    public const string GitHubHost = "github.com";

    private RemoteRepository(HostingProviderKind provider, string host, string owner, string name)
    {
        Provider = provider;
        Host = host;
        Owner = owner;
        Name = name;
    }

    public HostingProviderKind Provider { get; }

    /// <summary><c>github.com</c>, exactly.</summary>
    public string Host { get; }

    public string Owner { get; }

    public string Name { get; }

    /// <summary><c>https://github.com/&lt;owner&gt;/&lt;name&gt;.git</c>, written by the code.</summary>
    public string PushUrl => $"https://{Host}/{Owner}/{Name}.git";

    /// <summary><c>github.com/&lt;owner&gt;/&lt;name&gt;</c>: what a grant, a pull request and an approval are bound to.</summary>
    public string Id => $"{Host}/{Owner}/{Name}";

    public override string ToString() => Id;

    /// <summary>
    /// Identifies the repository a remote URL names, only to identify it: <c>https://github.com/o/r(.git)</c> and <c>git@github.com:o/r(.git)</c>.
    /// Anything else (another host, a port, a user or a token in the address, another form, an owner or a name outside the rules) is
    /// refused with a stable code in <paramref name="refusalCode"/> and nothing is returned.
    /// </summary>
    public static RemoteRepository? TryParseGitHub(string? remoteUrl, out string? refusalCode)
    {
        refusalCode = null;
        if (string.IsNullOrEmpty(remoteUrl) || remoteUrl.Length > 512 || remoteUrl != remoteUrl.Trim() || remoteUrl.Any(char.IsControl))
        {
            refusalCode = RefusalUnrecognized;
            return null;
        }

        string host;
        string path;
        if (remoteUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            var rest = remoteUrl["https://".Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                refusalCode = RefusalUnrecognized;
                return null;
            }

            var authority = rest[..slash];
            if (authority.Contains('@', StringComparison.Ordinal))
            {
                refusalCode = RefusalCredentialsInUrl;
                return null;
            }

            if (authority.Contains(':', StringComparison.Ordinal))
            {
                refusalCode = RefusalPort;
                return null;
            }

            host = authority;
            path = rest[(slash + 1)..];
        }
        else if (remoteUrl.StartsWith("git@", StringComparison.Ordinal))
        {
            var rest = remoteUrl["git@".Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                refusalCode = RefusalUnrecognized;
                return null;
            }

            host = rest[..colon];
            path = rest[(colon + 1)..];
        }
        else
        {
            refusalCode = RefusalUnrecognized;
            return null;
        }

        if (!string.Equals(host, GitHubHost, StringComparison.Ordinal))
        {
            refusalCode = RefusalHost;
            return null;
        }

        if (path.EndsWith(".git", StringComparison.Ordinal))
        {
            path = path[..^".git".Length];
        }

        var parts = path.Split('/');
        if (parts.Length != 2)
        {
            refusalCode = RefusalUnrecognized;
            return null;
        }

        if (!HostingIdentifiers.IsOwner(parts[0]))
        {
            refusalCode = RefusalOwner;
            return null;
        }

        if (!HostingIdentifiers.IsRepositoryName(parts[1]))
        {
            refusalCode = RefusalName;
            return null;
        }

        return new RemoteRepository(HostingProviderKind.GitHub, GitHubHost, parts[0], parts[1]);
    }

    /// <summary>A repository of GitHub by its owner and its name (what the log and the payloads hold), or null when either breaks the rules.</summary>
    public static RemoteRepository? TryCreateGitHub(string? owner, string? name) =>
        HostingIdentifiers.IsOwner(owner) && HostingIdentifiers.IsRepositoryName(name)
            ? new RemoteRepository(HostingProviderKind.GitHub, GitHubHost, owner!, name!)
            : null;

    /// <summary>Reads back an <see cref="Id"/> (<c>github.com/&lt;owner&gt;/&lt;name&gt;</c>), or null when it is not one.</summary>
    public static RemoteRepository? TryParseId(string? id)
    {
        if (id is null || !id.StartsWith(GitHubHost + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = id[(GitHubHost.Length + 1)..].Split('/');
        return parts.Length == 2 ? TryCreateGitHub(parts[0], parts[1]) : null;
    }

    /// <summary>The address is not one of the two forms that identify a repository.</summary>
    public const string RefusalUnrecognized = "remote.unrecognized";

    /// <summary>The host is not exactly <c>github.com</c>.</summary>
    public const string RefusalHost = "remote.host-not-supported";

    /// <summary>The address carries a user, a password or a token.</summary>
    public const string RefusalCredentialsInUrl = "remote.credentials-in-url";

    /// <summary>The address names a port.</summary>
    public const string RefusalPort = "remote.port";

    public const string RefusalOwner = "remote.owner-invalid";

    public const string RefusalName = "remote.name-invalid";
}

/// <summary>
/// A branch of a repository: a name under <c>refs/heads</c> by the rules of <c>git check-ref-format</c>, and never one that starts with <c>-</c>
/// (it would be read as an option). Phase 10 publishes only <c>agentstudio/run-&lt;id&gt;</c> (docs/21 decision 13).
/// </summary>
public sealed record RemoteBranch
{
    /// <summary>The prefix of the branch of a run on the remote, the same as the local one (<c>agentstudio/run-&lt;id&gt;</c>).</summary>
    public const string RunPrefix = "agentstudio/run-";

    public const int MaxLength = 255;

    private RemoteBranch(string name) => Name = name;

    public string Name { get; }

    /// <summary>Whether this is the branch of a run: <see cref="RunPrefix"/> and an identifier of a run (<c>[A-Za-z0-9_-]{1,64}</c>).</summary>
    public bool IsRunBranch =>
        Name.StartsWith(RunPrefix, StringComparison.Ordinal) && IsRunId(Name[RunPrefix.Length..]);

    /// <summary>The identifier of the run of a run branch; null for any other branch.</summary>
    public string? RunIdentifier => IsRunBranch ? Name[RunPrefix.Length..] : null;

    public override string ToString() => Name;

    /// <summary>The branch of a run: <c>agentstudio/run-&lt;id&gt;</c>.</summary>
    public static RemoteBranch ForRun(RunId run) => new(RunPrefix + run.Value);

    /// <summary>The refusal of a name with a character outside <see cref="Alphabet"/>.</summary>
    public const string RefusalCharacters = "branch.unsupported-characters";

    /// <summary>The refusal of a name that breaks the rules of <c>git check-ref-format</c> (even with only safe characters), starts with <c>-</c> or is <c>HEAD</c>.</summary>
    public const string RefusalForm = "branch.invalid-form";

    /// <summary>What a branch name may be made of in Phase 10: <c>[A-Za-z0-9._/-]</c>. git allows more; the app does not (a name travels as data to the CLI and the API, and to the screen), so a branch with other characters is not admitted.</summary>
    public const string Alphabet = "[A-Za-z0-9._/-]";

    /// <summary>The branch of that name, or null when it is outside <see cref="Alphabet"/>, breaks the rules of <c>git check-ref-format</c>, starts with <c>-</c> or is <c>HEAD</c>.</summary>
    public static RemoteBranch? TryParse(string? name) => TryParse(name, out _);

    /// <summary>The same, with the code of the refusal (<see cref="RefusalCharacters"/> or <see cref="RefusalForm"/>) when there is one: a base branch with other characters is refused with a code, never fixed.</summary>
    public static RemoteBranch? TryParse(string? name, out string? refusalCode)
    {
        refusalCode = null;
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            refusalCode = RefusalForm;
            return null;
        }

        if (!name.All(c => HostingIdentifiers.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '-'))
        {
            refusalCode = RefusalCharacters;
            return null;
        }

        if (!HasValidForm(name))
        {
            refusalCode = RefusalForm;
            return null;
        }

        return new RemoteBranch(name);
    }

    private static bool IsRunId(string value) =>
        value.Length is >= 1 and <= RunId.MaxLength && value.All(c => HostingIdentifiers.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    // Only safe characters reach here: no control character, no space and none of ~ ^ : ? * [ \ @ remain to be checked.
    private static bool HasValidForm(string name)
    {
        if (name[0] is '-' or '/' || name[^1] is '/' or '.' || name.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        // No component starts with '.' or ends with ".lock".
        foreach (var component in name.Split('/'))
        {
            if (component.Length == 0 || component[0] == '.' || component.EndsWith(".lock", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }}

/// <summary>A pull request: its repository and its number (greater than 0).</summary>
public sealed record PullRequestRef
{
    public PullRequestRef(RemoteRepository repository, int number)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        Repository = repository;
        Number = number;
    }

    public RemoteRepository Repository { get; }

    public int Number { get; }

    /// <summary><c>github.com/&lt;owner&gt;/&lt;name&gt;#&lt;number&gt;</c>: the id of <c>TraceNodeKind.PullRequest</c>.</summary>
    public string TraceId => Repository.Id + "#" + Number.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => TraceId;

    /// <summary>Reads back a <see cref="TraceId"/>, or null when it is not one.</summary>
    public static PullRequestRef? TryParseTraceId(string? id)
    {
        if (id is null)
        {
            return null;
        }

        var hash = id.LastIndexOf('#');
        if (hash <= 0 || RemoteRepository.TryParseId(id[..hash]) is not { } repository)
        {
            return null;
        }

        var digits = id[(hash + 1)..];
        return digits.Length is >= 1 and <= 9 && digits[0] != '0' && digits.All(char.IsAsciiDigit)
            ? new PullRequestRef(repository, int.Parse(digits, CultureInfo.InvariantCulture))
            : null;
    }
}

/// <summary>An account name of the platform, by the rule of the owner of a repository; anything else is «no consta» (docs/21 D-30).</summary>
public sealed record RemoteLogin
{
    private RemoteLogin(string value) => Value = value;

    public string Value { get; }

    public override string ToString() => Value;

    public static RemoteLogin? TryParse(string? login) => HostingIdentifiers.IsOwner(login) ? new RemoteLogin(login!) : null;
}
