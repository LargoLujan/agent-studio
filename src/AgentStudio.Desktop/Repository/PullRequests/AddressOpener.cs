using System.Diagnostics;
using System.Globalization;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Desktop.Repository.PullRequests;

/// <summary>
/// Opens an address in the browser. The screen only ever asks for the address <see cref="PullRequestAddress.Of"/> builds (docs/21 section 7.1): never one that came from an answer of the
/// platform.
/// </summary>
public interface IAddressOpener
{
    /// <summary>Opens <paramref name="address"/>, which must be an address built by <see cref="PullRequestAddress"/>.</summary>
    void Open(Uri address);
}

/// <summary>The address of a pull request on its page, written by the code from the identifiers that were validated: <c>https://github.com/&lt;owner&gt;/&lt;name&gt;/pull/&lt;number&gt;</c>.</summary>
public static class PullRequestAddress
{
    /// <summary>The address, or null when the repository is not one of GitHub or the result is not the expected page.</summary>
    public static Uri? Of(PullRequestRef pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        var repository = pullRequest.Repository;
        if (repository.Provider != HostingProviderKind.GitHub || !string.Equals(repository.Host, RemoteRepository.GitHubHost, StringComparison.Ordinal))
        {
            return null;
        }

        var text = $"https://{RemoteRepository.GitHubHost}/{repository.Owner}/{repository.Name}/pull/{pullRequest.Number.ToString(CultureInfo.InvariantCulture)}";
        return IsPullRequestPage(text, out var uri) ? uri : null;
    }

    /// <summary>Whether an address is exactly a page of a pull request of GitHub: https, the host, no user, no port, no query and no fragment.</summary>
    public static bool IsPullRequestPage(string text, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.Equals(parsed.Host, RemoteRepository.GitHubHost, StringComparison.Ordinal)
            || !parsed.IsDefaultPort
            || parsed.UserInfo.Length > 0
            || parsed.Query.Length > 0
            || parsed.Fragment.Length > 0)
        {
            return false;
        }

        var parts = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[2] != "pull" || RemoteRepository.TryCreateGitHub(parts[0], parts[1]) is null || !parts[3].All(char.IsAsciiDigit))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}

/// <summary>The browser of Windows, through the shell. It refuses any address that is not a page of a pull request of GitHub.</summary>
public sealed class ShellAddressOpener : IAddressOpener
{
    public void Open(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!PullRequestAddress.IsPullRequestPage(address.AbsoluteUri, out var page) || page is null)
        {
            throw new ArgumentException("Only the page of a pull request of GitHub is opened.", nameof(address));
        }

        using var _ = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
    }
}
