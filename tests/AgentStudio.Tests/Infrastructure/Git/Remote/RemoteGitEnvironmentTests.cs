using AgentStudio.Infrastructure.Git.Remote;

namespace AgentStudio.Tests.Infrastructure.Git.Remote;

/// <summary>The environment of the executor is an allow list (docs/21 D-06): what is not on it does not pass, whatever it is called.</summary>
public sealed class RemoteGitEnvironmentTests
{
    private static IReadOnlyDictionary<string, string?> Build(IReadOnlyDictionary<string, string> ambient, RemoteTransport? transport = null) =>
        RemoteGitEnvironment.Build(transport ?? RemoteTransport.GitHubHttps, @"C:\hosting\tmp", ambient);

    [Theory]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("GIT_ASKPASS")]
    [InlineData("GIT_CONFIG_COUNT")]
    [InlineData("GIT_CONFIG_KEY_0")]
    [InlineData("GIT_CONFIG_PARAMETERS")]
    [InlineData("GIT_TRACE")]
    [InlineData("GIT_TRACE_CURL")]
    [InlineData("GIT_CURL_VERBOSE")]
    [InlineData("GIT_EXEC_PATH")]
    [InlineData("GIT_SOMETHING_NEW")]
    [InlineData("git_lowercase")]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GH_ENTERPRISE_TOKEN")]
    [InlineData("GH_HOST")]
    [InlineData("GH_REPO")]
    [InlineData("GH_CONFIG_DIR")]
    [InlineData("GH_DEBUG")]
    [InlineData("GCM_TRACE")]
    [InlineData("GCM_CREDENTIAL_STORE")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("https_proxy")]
    [InlineData("SSH_ASKPASS")]
    [InlineData("NODE_OPTIONS")]
    [InlineData("SOME_OTHER_VARIABLE")]
    public void A_variable_that_is_not_allowed_is_removed_whatever_it_is_called(string name)
    {
        var environment = Build(new Dictionary<string, string> { [name] = "valor" });

        Assert.True(environment.ContainsKey(name));
        Assert.Null(environment[name]);
    }

    [Theory]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("GIT_ASKPASS")]
    [InlineData("GIT_CONFIG_COUNT")]
    [InlineData("GIT_CONFIG_PARAMETERS")]
    [InlineData("GIT_TRACE")]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("GCM_TRACE")]
    [InlineData("HTTPS_PROXY")]
    public void The_dangerous_names_are_removed_even_when_the_snapshot_does_not_have_them(string name)
    {
        var environment = Build(new Dictionary<string, string>());

        Assert.True(environment.ContainsKey(name));
        Assert.Null(environment[name]);
    }

    [Fact]
    public void The_variables_git_and_its_credential_manager_need_go_on_with_the_value_of_the_snapshot_and_path_is_cleaned()
    {
        var ambient = new Dictionary<string, string>
        {
            ["SystemRoot"] = @"C:\Windows", ["USERPROFILE"] = @"C:\Users\ana", ["home"] = @"C:\Users\ana", ["HOMEDRIVE"] = "C:", ["HOMEPATH"] = @"\Users\ana",
            ["APPDATA"] = @"C:\Users\ana\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\ana\AppData\Local", ["ProgramData"] = @"C:\ProgramData", ["USERNAME"] = "ana",
            ["PATH"] = @"C:\Program Files\Git\cmd;.\planted", ["NoDefaultCurrentDirectoryInExePath"] = "1",
        };

        var environment = Build(ambient);

        foreach (var name in new[] { "SystemRoot", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "USERNAME" })
        {
            Assert.Equal(ambient[name], environment[name]);
        }

        Assert.Equal(@"C:\Users\ana", environment["HOME"]);
        Assert.Equal(@"C:\Program Files\Git\cmd", environment["PATH"]);
        Assert.False(environment.ContainsKey("NoDefaultCurrentDirectoryInExePath"));
    }

    [Fact]
    public void What_the_executor_sets_is_the_same_for_every_command_and_the_protocol_list_is_the_scheme_of_the_transport()
    {
        var https = Build(new Dictionary<string, string>());
        var local = Build(new Dictionary<string, string>(), RemoteTransport.ForTests("file", r => r.PushUrl));

        Assert.Equal("0", https["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("0", https["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("cat", https["GIT_PAGER"]);
        Assert.Equal("1", https["GIT_NO_LAZY_FETCH"]);
        Assert.Equal("never", https["GCM_INTERACTIVE"]);
        Assert.Equal("C", https["LC_ALL"]);
        Assert.Equal(@"C:\hosting\tmp", https["TEMP"]);
        Assert.Equal(@"C:\hosting\tmp", https["TMP"]);
        Assert.Equal("https", https["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal("file", local["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal(RemoteTransport.GitHubHttps.Scheme, https["GIT_ALLOW_PROTOCOL"]);
    }

    [Fact]
    public void A_variable_the_executor_sets_wins_over_what_the_snapshot_says()
    {
        var environment = Build(new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "1", ["GIT_ALLOW_PROTOCOL"] = "file:ssh", ["TEMP"] = @"C:\Windows\Temp", ["GCM_INTERACTIVE"] = "always" });

        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("https", environment["GIT_ALLOW_PROTOCOL"]);
        Assert.Equal(@"C:\hosting\tmp", environment["TEMP"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
    }

    [Fact]
    public void The_names_of_the_credentials_and_of_the_transport_are_never_on_the_allow_list()
    {
        Assert.DoesNotContain(RemoteGitEnvironment.Allowed, name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GH_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase));
    }
}
