namespace AgentStudio.Infrastructure.Skills.GitHub;

/// <summary>
/// Checks the destination of <b>every</b> request that leaves toward GitHub and fails closed (docs/20 D-13 and D-14): it must satisfy
/// <see cref="GitHubApiAddress.IsAllowed"/>, and an <c>Authorization</c> header is only ever let through to that same address. It is the last handler before
/// the network, so a request built by a redirection followed by hand, by a later change of the client or by a test of the pipeline meets it too.
/// </summary>
public sealed class GitHubHostGuardHandler : DelegatingHandler
{
    public GitHubHostGuardHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!GitHubApiAddress.IsAllowed(request.RequestUri) || request.Method != HttpMethod.Get || request.Content is not null)
        {
            // The message names no address: it could carry what the person never sent to GitHub.
            throw new GitHubApiException(AgentStudio.Application.Skills.SkillHealthFailure.RedirectRefused);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
