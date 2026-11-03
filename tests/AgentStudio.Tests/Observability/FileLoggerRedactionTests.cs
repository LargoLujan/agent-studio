using System.IO;
using System.Text.Json;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.Security;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

/// <summary>
/// Acceptance criterion F1-7: structured logging to a file without secrets or full prompts, checked by inspecting the
/// generated files (their raw bytes and every decoded JSON string), with the real redactor.
/// </summary>
public sealed class FileLoggerRedactionTests : IDisposable
{
    private const string Prompt = "Please refactor the payroll module for TENANT-42 and email everyone";

    private readonly LoggerFixture _fx = new();

    /// <summary>Kind, the text the caller logs, and the part of it that must not survive.</summary>
    public static TheoryData<string, string, string> Secrets => new()
    {
        { "OpenAI sk-", FakeSecrets.OpenAiKey(), FakeSecrets.OpenAiKey() },
        { "Anthropic sk-ant-", FakeSecrets.AnthropicKey(), FakeSecrets.AnthropicKey() },
        { "Google AIza", FakeSecrets.GoogleApiKey(), FakeSecrets.GoogleApiKey() },
        { "GitHub ghp_", FakeSecrets.GitHubToken(), FakeSecrets.GitHubToken() },
        { "GitHub github_pat_", FakeSecrets.GitHubFineGrainedToken(), FakeSecrets.GitHubFineGrainedToken() },
        { "Slack xoxb-", FakeSecrets.SlackBotToken(), FakeSecrets.SlackBotToken() },
        { "AWS AKIA", FakeSecrets.AwsAccessKeyId(), FakeSecrets.AwsAccessKeyId() },
        { "JWT", FakeSecrets.Jwt(), FakeSecrets.Jwt() },
        { "Bearer credential", "Bearer " + FakeSecrets.BearerCredential(), FakeSecrets.BearerCredential() },
        { "password assignment (first half)", "password=Tr0ub4dor&3-horse-staple", "Tr0ub4dor" },
        { "password assignment (after the ampersand)", "password=Tr0ub4dor&3-horse-staple", "3-horse-staple" },
        { "password in a query string", "https://x.test/login?user=bob&password=Tr0ub4dor3-horse&next=%2Fhome", "Tr0ub4dor3-horse" },
        { "secret assignment", "secret=0123456789abcdefSECRET", "0123456789abcdefSECRET" },
        { "token assignment", "token=abcdef0123456789TOKEN", "abcdef0123456789TOKEN" },
        { "api_key assignment", "api_key=ABCDEF0123456789APIKEY", "ABCDEF0123456789APIKEY" },
        { "connection string password", "Server=db01;User Id=sa;Password=p@ssw0rd-CONN;Encrypt=true", "p@ssw0rd-CONN" },
    };

    public void Dispose() => _fx.Dispose();

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_secret_never_reaches_the_file_wherever_the_caller_puts_it(string kind, string secret, string hidden)
    {
        var logger = _fx.Logger("Tests.Secrets");
        var inCategory = _fx.Logger("Tests " + secret);

        logger.LogInformation("In the template: " + secret);
        logger.LogInformation("In a value: {Header}", "x-credential: " + secret);
        logger.LogInformation("In a setting: {Setting}", secret);
        logger.LogError(new InvalidOperationException("rejected " + secret), "In an exception");
        logger.LogError(new InvalidOperationException("outer", new IOException("inner " + secret)), "In an inner exception");
        inCategory.LogInformation("In the category");
        _fx.Flush();

        LogAssert.DoesNotLeak(hidden, _fx.Logs);
        Assert.True(_fx.Logs.AllText().Contains("[REDACTED]", StringComparison.Ordinal), kind + " should be replaced by the placeholder");
        Assert.Equal(6, _fx.Logs.Records().Count);
    }

    [Theory]
    [InlineData("Prompt")]
    [InlineData("SystemInstructions")]
    [InlineData("FinalText")]
    [InlineData("Response")]
    [InlineData("Content")]
    public void Prompt_like_properties_are_written_as_length_and_hash_only(string name)
    {
        var logger = _fx.Logger("Tests.Prompts");

        logger.LogInformation("Sending {" + name + "}", Prompt);
        _fx.Flush();

        var text = _fx.Logs.AllText();
        Assert.DoesNotContain("payroll", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TENANT-42", text, StringComparison.Ordinal);
        var record = _fx.Logs.Records().Single();
        Assert.Equal($"Sending [text len={Prompt.Length} sha256={LogAssert.Fingerprint(Prompt)}]", record.GetProperty("message").GetString());
        var value = record.GetProperty("properties").GetProperty(name);
        Assert.Equal(Prompt.Length, value.GetProperty("length").GetInt32());
        Assert.Equal(LogAssert.Fingerprint(Prompt), value.GetProperty("sha256").GetString());
        Assert.Equal(8, value.GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public void A_long_text_is_written_as_length_and_hash_only_even_under_an_innocent_name()
    {
        var longPrompt = string.Concat(Enumerable.Repeat(Prompt + ". ", 20));

        _fx.Logger("Tests.Long").LogInformation("Sent {Detail}", longPrompt);
        _fx.Flush();

        Assert.DoesNotContain("payroll", _fx.Logs.AllText(), StringComparison.Ordinal);
        var value = _fx.Logs.Records().Single().GetProperty("properties").GetProperty("Detail");
        Assert.Equal(longPrompt.Length, value.GetProperty("length").GetInt32());
    }

    [Fact]
    public void Requests_and_collections_are_written_by_type_only()
    {
        var logger = _fx.Logger("Tests.Objects");

        logger.LogInformation("Starting {Request}", new RequestStub(Prompt, "model-x"));
        logger.LogInformation("Files {Paths}", new List<string> { "secret-project-name.docx" });
        _fx.Flush();

        var text = _fx.Logs.AllText();
        Assert.DoesNotContain("payroll", text, StringComparison.Ordinal);
        Assert.DoesNotContain("model-x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-project-name", text, StringComparison.Ordinal);
        Assert.Equal(
            ["Starting [RequestStub]", "Files [List<String> (1 items)]"],
            _fx.Logs.Records().Select(r => r.GetProperty("message").GetString()));
    }

    [Fact]
    public void The_user_profile_path_is_collapsed_in_messages_values_and_exceptions()
    {
        var logger = _fx.Logger("Tests.Paths");

        logger.LogError(new IOException(@"Access to the path 'C:\Users\testuser\repo\a.cs' is denied."), "Saving {Path} failed", @"C:\Users\testuser\repo\a.cs");
        logger.LogInformation(@"Opened C:\Users\testuser\repo in the explorer");
        _fx.Flush();

        var text = _fx.Logs.AllText();
        Assert.DoesNotContain("testuser", text, StringComparison.Ordinal);
        var records = _fx.Logs.Records();
        Assert.Equal(@"~\repo\a.cs", records[0].GetProperty("properties").GetProperty("Path").GetString());
        Assert.Equal(@"Saving ~\repo\a.cs failed", records[0].GetProperty("message").GetString());
        Assert.Equal(@"Access to the path '~\repo\a.cs' is denied.", records[0].GetProperty("exception").GetProperty("message").GetString());
        Assert.Equal(@"Opened ~\repo in the explorer", records[1].GetProperty("message").GetString());
    }

    [Fact]
    public void The_real_user_profile_never_appears_in_the_file()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(profile) || profile.Length < 5, "This machine has no usable user profile path.");
        _fx.Redactor = new SecretRedactor();
        var logger = _fx.Logger("Tests.RealProfile");

        logger.LogError(new IOException($"Access to the path '{Path.Combine(profile, "x")}' is denied."), "Saving {Path} failed", Path.Combine(profile, "repo", "a.cs"));
        logger.LogInformation("Data folder " + Path.Combine(profile, "AppData", "Local", "AgentStudio"));
        _fx.Flush();

        var text = _fx.Logs.AllText();
        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Replace("\\", "\\\\", StringComparison.Ordinal), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profile.Replace('\\', '/'), text, StringComparison.OrdinalIgnoreCase);

        var first = _fx.Logs.Records()[0];
        Assert.Equal(Path.Combine("~", "repo", "a.cs"), first.GetProperty("properties").GetProperty("Path").GetString());
    }

    [Fact]
    public void F1_7_the_generated_log_has_no_secrets_and_no_full_prompts()
    {
        var secrets = new[]
        {
            FakeSecrets.OpenAiKey(),
            FakeSecrets.AnthropicKey(),
            FakeSecrets.GoogleApiKey(),
            FakeSecrets.GitHubToken(),
            FakeSecrets.Jwt(),
            FakeSecrets.AwsAccessKeyId(),
            FakeSecrets.SlackBotToken(),
            FakeSecrets.BearerCredential(),
            "hunter2-PASSWORD-VALUE",
        };
        var prompts = new[] { Prompt, "System: you are an auditor of the CONFIDENTIAL-PROJECT ledger", string.Concat(Enumerable.Repeat("model output line ", 40)) };
        var logger = _fx.Logger("AgentStudio.Providers.ClaudeCode");

        logger.LogInformation("Run {RunId} started with {Model}", "r-17", "claude-x");
        logger.LogInformation("Prompt sent: {Prompt}", prompts[0]);
        logger.LogInformation("Instructions: {SystemInstructions}", prompts[1]);
        logger.LogInformation("Run {RunId} finished: {FinalText}", "r-17", prompts[2]);
        logger.LogWarning("Provider {Provider} answered {Response}", "claude-code", prompts[0]);
        logger.LogInformation("Using key {Key} and Authorization: Bearer {Credential}", secrets[0], secrets[7]);
        logger.LogInformation($"Interpolated by mistake: {secrets[1]} and password={secrets[8]}");
        logger.LogError(new InvalidOperationException($"stderr said {secrets[2]} and {secrets[3]}"), "Run {RunId} failed", "r-17");
        logger.LogInformation("Tokens {Input} in, {Output} out; token_count={TokenCount}", 1200, 340, 1540);
        logger.LogInformation("jwt {Jwt} aws {Aws} slack {Slack}", secrets[4], secrets[5], secrets[6]);
        _fx.Flush();

        var records = _fx.Logs.Records();
        Assert.Equal(10, records.Count);
        foreach (var record in records)
        {
            Assert.Equal(["timestamp", "level", "category", "eventId", "message"], record.EnumerateObject().Take(5).Select(p => p.Name));
        }

        foreach (var secret in secrets)
        {
            LogAssert.DoesNotLeak(secret, _fx.Logs);
        }

        foreach (var prompt in prompts)
        {
            LogAssert.DoesNotLeak(prompt, _fx.Logs);
        }

        LogAssert.DoesNotLeak("payroll", _fx.Logs);
        LogAssert.DoesNotLeak("CONFIDENTIAL-PROJECT", _fx.Logs);
        LogAssert.DoesNotLeak("model output line", _fx.Logs);

        // Ordinary, harmless diagnostics are still there.
        Assert.Equal("Run r-17 started with claude-x", records[0].GetProperty("message").GetString());
        Assert.Equal(1200, records[8].GetProperty("properties").GetProperty("Input").GetInt32());
        Assert.Equal(1540, records[8].GetProperty("properties").GetProperty("TokenCount").GetInt32());
        Assert.Contains("token_count=1540", records[8].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private sealed record RequestStub(string Prompt, string Model);
}
