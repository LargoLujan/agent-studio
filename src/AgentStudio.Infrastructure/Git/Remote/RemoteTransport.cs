using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Git.Remote;

/// <summary>
/// The transport of the remote executor of git (docs/21 D-06): the scheme git is allowed to use (it is both the value of <c>GIT_ALLOW_PROTOCOL</c> and the
/// protocol that <c>protocol.&lt;scheme&gt;.allow</c> opens, from this one constant and not from two texts that could diverge) and the function that gives
/// the address of a repository. It is internal, its constructor is private and the only instance the production code can name is <see cref="GitHubHttps"/>: no setting, variable
/// of the environment or file chooses another. The tests of the executor build one of their own with <c>ForTests</c>, in <c>RemoteTransport.ForTests.cs</c> (a seam that production code never calls: a test scans <c>src</c> for it), to push to a bare
/// folder without opening anything in production; the address of a <see cref="RemoteRepository"/> is still the one of an identifier of <c>github.com</c>, and
/// what the function does with it is what changes.
/// </summary>
/// <remarks>The executor itself (<c>RemoteGitRunner</c>) and the rest of this folder are WP1's; the P0 gives only this declaration.</remarks>
internal sealed partial class RemoteTransport
{
    /// <summary>The scheme of the only transport production uses.</summary>
    public const string HttpsScheme = "https";

    private readonly Func<RemoteRepository, string> _address;

    /// <exception cref="ArgumentException">A scheme that is not lowercase letters.</exception>
    private RemoteTransport(string scheme, Func<RemoteRepository, string> address)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        ArgumentNullException.ThrowIfNull(address);
        if (!scheme.All(char.IsAsciiLetterLower))
        {
            throw new ArgumentException($"A scheme is made of lowercase letters, not '{scheme}'.", nameof(scheme));
        }

        Scheme = scheme;
        _address = address;
    }

    /// <summary>The only transport of production: HTTPS to the address <see cref="RemoteRepository.PushUrl"/> writes.</summary>
    public static RemoteTransport GitHubHttps { get; } = new(HttpsScheme, repository => repository.PushUrl);

    /// <summary>The scheme git is allowed to use: <c>GIT_ALLOW_PROTOCOL</c> and <c>protocol.&lt;scheme&gt;.allow=always</c>.</summary>
    public string Scheme { get; }

    /// <summary>The address a push or an <c>ls-remote</c> of <paramref name="repository"/> goes to.</summary>
    public string AddressOf(RemoteRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return _address(repository);
    }
}
