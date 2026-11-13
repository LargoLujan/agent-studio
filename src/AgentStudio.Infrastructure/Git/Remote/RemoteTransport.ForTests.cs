using System.ComponentModel;
using AgentStudio.Domain.Hosting;

namespace AgentStudio.Infrastructure.Git.Remote;

// TEST SEAM (docs/21 D-06). The only other way to get a RemoteTransport than GitHubHttps, kept in a file of its own so it is easy to find and to scan for:
// nothing under src/ may call ForTests except this file (RemoteTransportSeamTests scans for it; WP1 adds this name to its scan of src/, next to «file://»).
// The tests of the remote executor call it, through InternalsVisibleTo, to push to a bare folder.
internal sealed partial class RemoteTransport
{
    /// <summary>A transport of a test: any scheme of lowercase letters and an address of its own for a repository. Never called by production code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static RemoteTransport ForTests(string scheme, Func<RemoteRepository, string> address) => new(scheme, address);
}
