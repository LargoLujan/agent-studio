using System.IO;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Infrastructure.Hosting.Cli;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Infrastructure.Hosting.Cli;

/// <summary>
/// docs/21 D-11 and F10-11: the environment of <c>gh</c> is an allow list. <c>ProcessSpec.Environment</c> is a change over what the child inherits, so what is not allowed is there with a null value, which
/// removes it. No variable that hands <c>gh</c> a token or sends it elsewhere survives, and the six of the person that must cross do so only in the form the design says.
/// </summary>
public sealed class GhEnvironmentTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private GhFolders Folders => GhFolders.Under(_temp.Combine("local"));

    private static IReadOnlyDictionary<string, string> Ambient(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Everything_that_hands_gh_a_token_or_sends_it_elsewhere_is_removed_even_when_the_process_has_none_of_them()
    {
        var environment = GhEnvironment.For(Folders, Ambient(), null);

        foreach (var name in new[]
        {
            "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_DEBUG", "DEBUG", "GH_PAGER", "GH_BROWSER", "GH_FORCE_TTY", "GH_PATH",
        })
        {
            Assert.True(environment.ContainsKey(name) && environment[name] is null, name);
        }
    }

    [Fact]
    public void Every_variable_of_the_process_that_is_not_allowed_is_removed_the_git_ones_and_the_tokens_among_them()
    {
        var ambient = Ambient(
            ("GH_TOKEN", "ghp_sentinel"), ("GITHUB_TOKEN", "ghp_sentinel"), ("GH_ENTERPRISE_TOKEN", "x"), ("GH_HOST", "evil.example"), ("GH_REPO", "evil/repo"), ("GH_DEBUG", "api"), ("GIT_ASKPASS", "evil"),
            ("GIT_SSH_COMMAND", "evil"), ("GIT_CONFIG_COUNT", "1"), ("GIT_TRACE", "1"), ("GIT_CURL_VERBOSE", "1"), ("GCM_TRACE", "1"), ("NODE_OPTIONS", "--evil"), ("SystemRoot", @"C:\Windows"), ("APPDATA", @"C:\Users\x\AppData\Roaming"),
            ("LOCALAPPDATA", @"C:\Users\x\AppData\Local"), ("PATH", @"C:\Windows"));

        var environment = GhEnvironment.For(Folders, ambient, null);

        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_DEBUG", "GIT_ASKPASS", "GIT_SSH_COMMAND", "GIT_CONFIG_COUNT", "GIT_TRACE", "GIT_CURL_VERBOSE", "GCM_TRACE", "NODE_OPTIONS" })
        {
            Assert.True(environment.ContainsKey(name) && environment[name] is null, name);
        }

        // What gh needs from Windows (its configuration lives under APPDATA) is not touched: it is not in the list because the child inherits it.
        foreach (var name in new[] { "SystemRoot", "APPDATA", "LOCALAPPDATA", "PATH" })
        {
            Assert.DoesNotContain(environment.Keys, k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void No_value_of_any_variable_is_a_token_and_the_fixed_ones_are_there()
    {
        var environment = GhEnvironment.For(Folders, Ambient(("GH_TOKEN", "ghp_sentinel"), ("GITHUB_TOKEN", "ghp_sentinel"), ("HTTPS_PROXY", "http://proxy:3128")), null);

        Assert.DoesNotContain(environment.Values, v => v is not null && v.Contains("ghp_sentinel", StringComparison.Ordinal));
        Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
        Assert.Equal("1", environment["GH_NO_UPDATE_NOTIFIER"]);
        Assert.Equal("1", environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"]);
        Assert.Equal("false", environment["GH_TELEMETRY"]);
        Assert.Equal("1", environment["DO_NOT_TRACK"]);
        Assert.Equal("1", environment["NO_COLOR"]);
        Assert.Equal("1", environment["GH_SPINNER_DISABLED"]);
        Assert.Equal(Folders.TempFolder, environment["TEMP"]);
        Assert.Equal(Folders.TempFolder, environment["TMP"]);
        foreach (var (name, value) in DoctorEnvironment.GhOffline)
        {
            Assert.Equal(value, environment[name]);
        }
    }

    [Theory]
    [InlineData("HTTPS_PROXY")]
    [InlineData("HTTP_PROXY")]
    [InlineData("NO_PROXY")]
    [InlineData("SSL_CERT_FILE")]
    public void The_proxy_and_the_certificates_cross_as_they_are_and_are_removed_when_empty(string name)
    {
        var passes = GhEnvironment.For(Folders, Ambient((name, "value")), null);
        var empty = GhEnvironment.For(Folders, Ambient((name, "  ")), null);
        var absent = GhEnvironment.For(Folders, Ambient(), null);

        Assert.DoesNotContain(passes.Keys, k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.True(empty[name] is null);
        Assert.True(absent[name] is null);
    }

    [Theory]
    [InlineData("XDG_CONFIG_HOME")]
    [InlineData("GH_CONFIG_DIR")]
    public void A_configuration_folder_crosses_only_when_it_is_a_full_path_outside_the_open_projects(string name)
    {
        var project = _temp.CreateDirectory("project");
        var outside = _temp.CreateDirectory("config");
        var roots = new FixedRoots(project);

        var good = GhEnvironment.For(Folders, Ambient((name, outside)), roots);
        var inside = GhEnvironment.For(Folders, Ambient((name, Path.Combine(project, ".gh"))), roots);
        var relative = GhEnvironment.For(Folders, Ambient((name, @"..\.gh")), roots);
        var empty = GhEnvironment.For(Folders, Ambient((name, string.Empty)), roots);
        var absent = GhEnvironment.For(Folders, Ambient(), roots);

        Assert.DoesNotContain(good.Keys, k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert.True(inside[name] is null);
        Assert.True(relative[name] is null);
        Assert.True(empty[name] is null);
        Assert.True(absent[name] is null);
    }

    [Fact]
    public void The_input_dictionary_is_not_changed_and_the_result_is_a_change_over_the_environment_not_a_copy_of_it()
    {
        var ambient = Ambient(("GH_TOKEN", "t"), ("SystemRoot", @"C:\Windows"));

        _ = GhEnvironment.For(Folders, ambient, null);

        Assert.Equal(2, ambient.Count);
        Assert.Equal("t", ambient["GH_TOKEN"]);
    }
}
