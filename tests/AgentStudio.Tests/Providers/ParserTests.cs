using AgentStudio.Application.Providers;
using AgentStudio.Providers.Cli;

namespace AgentStudio.Tests.Providers;

public sealed class ParserTests
{
    [Theory]
    [InlineData("2.1.290 (Claude Code)", "2.1.290")]
    [InlineData("codex-cli 0.160.0", "0.160.0")]
    [InlineData("codex-cli 0.161.0-alpha.2\r\n", "0.161.0-alpha.2")]
    [InlineData("0.30.1-nightly.20260101+abc\n", "0.30.1-nightly.20260101+abc")]
    [InlineData("v1.2.3", "1.2.3")]
    public void Version_is_extracted(string output, string expected) =>
        Assert.Equal(expected, CliVersionParser.Parse(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("command not found")]
    [InlineData("version 2")]
    public void Version_is_null_when_absent(string? output) =>
        Assert.Null(CliVersionParser.Parse(output));

    private const string ClaudeLoggedIn =
        """{"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty", "analyticsDisabled": false, "projectsDirectory": "C:\\Users\\x\\.claude\\projects", "configDirectory": "C:\\Users\\x\\.claude", "email": "persona@example.com", "orgId": "org-123", "orgName": "Mi Organizacion", "subscriptionType": "max"}""";

    [Fact]
    public void Claude_logged_in_extracts_method_and_subscription()
    {
        var status = ClaudeAuthStatusParser.Parse(ClaudeLoggedIn);

        Assert.Equal(AuthenticationState.Authenticated, status.State);
        Assert.Equal("claude.ai (suscripción max)", status.Method);
    }

    [Fact]
    public void Claude_status_never_leaks_personal_data()
    {
        var status = ClaudeAuthStatusParser.Parse(ClaudeLoggedIn);

        Assert.DoesNotContain("persona@example.com", status.Method, StringComparison.Ordinal);
        Assert.DoesNotContain("org-123", status.Method, StringComparison.Ordinal);
        Assert.DoesNotContain("Mi Organizacion", status.Method, StringComparison.Ordinal);
        Assert.DoesNotContain(".claude", status.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_logged_in_without_subscription_shows_only_the_method()
    {
        var status = ClaudeAuthStatusParser.Parse("""{"loggedIn": true, "authMethod": "api_key"}""");

        Assert.Equal(AuthenticationState.Authenticated, status.State);
        Assert.Equal("api_key", status.Method);
    }

    [Fact]
    public void Claude_logged_out_is_not_authenticated()
    {
        var status = ClaudeAuthStatusParser.Parse("""{"loggedIn": false, "authMethod": "none"}""");

        Assert.Equal(AuthenticationState.NotAuthenticated, status.State);
        Assert.Null(status.Method);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"other": 1}""")]
    [InlineData("""{"loggedIn": "yes"}""")]
    public void Claude_unparseable_output_is_unknown(string? output)
    {
        var status = ClaudeAuthStatusParser.Parse(output);

        Assert.Equal(AuthenticationState.Unknown, status.State);
        Assert.Null(status.Method);
    }

    [Fact]
    public void Claude_suspicious_method_values_are_dropped()
    {
        var status = ClaudeAuthStatusParser.Parse("""{"loggedIn": true, "authMethod": "persona@example.com", "subscriptionType": "C:\\Users\\x"}""");

        Assert.Equal(AuthenticationState.Authenticated, status.State);
        Assert.Null(status.Method);
    }

    [Fact]
    public void Codex_logged_in_extracts_method()
    {
        var status = CodexLoginStatusParser.Parse("Logged in using ChatGPT\r\n");

        Assert.Equal(AuthenticationState.Authenticated, status.State);
        Assert.Equal("ChatGPT", status.Method);
    }

    [Fact]
    public void Codex_api_key_suffix_is_not_kept()
    {
        var status = CodexLoginStatusParser.Parse("Logged in using an API key - sk-proj-***ABCDE");

        Assert.Equal(AuthenticationState.Authenticated, status.State);
        Assert.Equal("API key", status.Method);
    }

    [Theory]
    [InlineData("Logged in using persona@example.com")]
    [InlineData("Logged in using sk-proj-SECRETKEY123")]
    public void Codex_unknown_method_text_is_never_returned(string output)
    {
        var status = CodexLoginStatusParser.Parse(output);

        Assert.Equal(AuthenticationState.Authenticated, status.State);
        Assert.Null(status.Method);
    }

    [Theory]
    [InlineData("Logged in using chatgpt", "ChatGPT")]
    [InlineData("Logged in using ChatGPT (Plus)", "ChatGPT")]
    public void Codex_chatgpt_method_is_normalised(string output, string expected) =>
        Assert.Equal(expected, CodexLoginStatusParser.Parse(output).Method);

    [Theory]
    [InlineData("Not logged in")]
    [InlineData("not logged in\n")]
    public void Codex_not_logged_in(string output)
    {
        var status = CodexLoginStatusParser.Parse(output);

        Assert.Equal(AuthenticationState.NotAuthenticated, status.State);
        Assert.Null(status.Method);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something unexpected")]
    public void Codex_other_output_is_unknown(string? output)
    {
        var status = CodexLoginStatusParser.Parse(output);

        Assert.Equal(AuthenticationState.Unknown, status.State);
        Assert.Null(status.Method);
    }
}
