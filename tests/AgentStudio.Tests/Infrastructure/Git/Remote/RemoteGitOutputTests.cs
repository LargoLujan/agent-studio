using AgentStudio.Application.Hosting;
using AgentStudio.Infrastructure.Git.Remote;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>The classifier of the error output of git (docs/21 D-06): lines that start as git writes them, and no loose word.</summary>
public sealed class RemoteGitOutputTests
{
    [Theory]
    [InlineData("fatal: unable to update url base from redirection:\n  asked for: https://github.com/o/r.git/info/refs\n  redirect: https://github.com/n/r.git/info/refs", RemoteGitCodes.Redirected)]
    [InlineData("warning: redirecting to https://github.com/n/r.git/\r\nfatal: x", RemoteGitCodes.Redirected)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 301", RemoteGitCodes.Redirected)]
    [InlineData("error: The requested URL returned error: 308\nfatal: HTTP request failed", RemoteGitCodes.Redirected)]
    [InlineData("fatal: transport 'file' not allowed", RemoteGitCodes.ProtocolRefused)]
    [InlineData("fatal: protocol 'ftp' is not supported", RemoteGitCodes.ProtocolRefused)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", RemoteGitCodes.CredentialsNeeded)]
    [InlineData("fatal: could not read Password for 'https://u@github.com': terminal prompts disabled", RemoteGitCodes.CredentialsNeeded)]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/o/r.git/'", RemoteGitCodes.CredentialsNeeded)]
    [InlineData("remote: Permission to o/r.git denied to ana.\nfatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 403", RemoteGitCodes.CredentialsNeeded)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/o/r.git/' not found", RemoteGitCodes.CredentialsNeeded)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 401", RemoteGitCodes.CredentialsNeeded)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", RemoteGitCodes.RemoteUnreachable)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Failed to connect to github.com port 443 after 21050 ms: Couldn't connect to server", RemoteGitCodes.RemoteUnreachable)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': SSL certificate problem: unable to get local issuer certificate", RemoteGitCodes.RemoteUnreachable)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': schannel: next InitializeSecurityContext failed", RemoteGitCodes.RemoteUnreachable)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Connection timed out after 300000 milliseconds", RemoteGitCodes.RemoteUnreachable)]
    [InlineData("fatal: 'C:/x/nope.git' does not appear to be a git repository\nfatal: Could not read from remote repository.", RemoteGitCodes.RemoteUnreachable)]
    public void The_lines_git_writes_give_their_code(string error, string code) => Assert.Equal(code, RemoteGitOutput.ClassifyError(error));

    [Theory]
    [InlineData("")]
    [InlineData("fatal: pathspec 'redirect-notes.md' did not match any files")]
    [InlineData("error: failed to push some refs to 'https://github.com/o/classless.git'")]
    [InlineData("hint: this is a redirect of the old name; see 'git help push'")]
    [InlineData("hint: authentication failed? permission denied? check your ssl settings")]
    [InlineData("fatal: bad object 0123456789012345678901234567890123456789")]
    [InlineData("error: remote unpack failed: eof before pack header was fully read")]
    [InlineData("notes: the transport 'x' was not allowed to ssl and could not resolve host")]
    [InlineData("  fatal: could not read Username")]
    [InlineData("a commit message that says fatal: transport 'file' not allowed")]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': something nobody has seen")]
    public void A_loose_word_inside_another_word_a_hint_or_a_message_is_not_a_code(string error) =>
        Assert.Equal(RemoteGitCodes.GitFailed, RemoteGitOutput.ClassifyError(error));

    [Fact]
    public void A_redirect_wins_over_a_failure_to_connect_and_credentials_over_unreachable()
    {
        Assert.Equal(RemoteGitCodes.Redirected, RemoteGitOutput.ClassifyError("fatal: unable to access 'u': Could not resolve host\nfatal: unable to update url base from redirection"));
        Assert.Equal(RemoteGitCodes.CredentialsNeeded, RemoteGitOutput.ClassifyError("fatal: Authentication failed for 'u'\nfatal: Could not read from remote repository."));
    }
}
