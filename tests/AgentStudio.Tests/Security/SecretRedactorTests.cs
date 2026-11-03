using System.Diagnostics;
using AgentStudio.Security.Redaction;

namespace AgentStudio.Tests.Security;

public sealed class SecretRedactorTests
{
    private const string Profile = @"C:\Users\testuser";

    private static readonly SecretRedactor Redactor = new(Profile);

    private static string Redact(string text) => Redactor.Redact(text);

    public static TheoryData<string, string> VendorTokens => new()
    {
        { "OpenAI sk-", FakeSecrets.OpenAiKey() },
        { "Anthropic sk-ant-", FakeSecrets.AnthropicKey() },
        { "Google AIza", FakeSecrets.GoogleApiKey() },
        { "GitHub ghp_", FakeSecrets.GitHubToken() },
        { "GitHub github_pat_", FakeSecrets.GitHubFineGrainedToken() },
        { "Slack xoxb-", FakeSecrets.SlackBotToken() },
        { "AWS AKIA", FakeSecrets.AwsAccessKeyId() },
        { "JWT", FakeSecrets.Jwt() },
    };

    [Theory]
    [MemberData(nameof(VendorTokens))]
    public void Vendor_tokens_are_replaced_and_the_text_around_them_is_kept(string kind, string secret)
    {
        var redacted = Redact($"before {secret} after");

        Assert.True(!redacted.Contains(secret, StringComparison.Ordinal), kind + " token must not survive");
        Assert.Equal("before [REDACTED] after", redacted);
    }

    [Fact]
    public void Free_standing_bearer_credentials_are_hidden_but_the_scheme_stays()
    {
        var credential = FakeSecrets.BearerCredential();

        var redacted = Redact($"the server answered 401 to Bearer {credential} twice");

        Assert.DoesNotContain(credential, redacted, StringComparison.Ordinal);
        Assert.Equal("the server answered 401 to Bearer [REDACTED] twice", redacted);
    }

    [Fact]
    public void Authorization_headers_hide_the_credential_whatever_the_scheme()
    {
        var credential = FakeSecrets.BearerCredential();

        Assert.Equal("Authorization: Bearer [REDACTED]", Redact($"Authorization: Bearer {credential}"));
        Assert.Equal("authorization: Basic [REDACTED]", Redact("authorization: Basic dXNlcjpwYXNzd29yZA=="));
        Assert.Equal("Proxy-Authorization: [REDACTED]", Redact("Proxy-Authorization: opaque-credential-1"));
        Assert.Equal("curl -H 'Authorization: Bearer [REDACTED]' https://x.test", Redact($"curl -H 'Authorization: Bearer {credential}' https://x.test"));
    }

    [Theory]
    [InlineData("password=hunter2", "password=[REDACTED]")]
    [InlineData("Password: hunter2", "Password: [REDACTED]")]
    [InlineData("secret=abc123xyz", "secret=[REDACTED]")]
    [InlineData("token=abc123xyz", "token=[REDACTED]")]
    [InlineData("api_key=abc123xyz", "api_key=[REDACTED]")]
    [InlineData("API-KEY = abc123xyz", "API-KEY = [REDACTED]")]
    [InlineData("apikey=abc123xyz", "apikey=[REDACTED]")]
    [InlineData("access_token=abc123xyz", "access_token=[REDACTED]")]
    [InlineData("client_secret=\"p w\"", "client_secret=\"[REDACTED]\"")]
    [InlineData("{\"password\": \"hunter2\"}", "{\"password\": \"[REDACTED]\"}")]
    [InlineData("{'secret': 'abc 123'}", "{'secret': '[REDACTED]'}")]
    [InlineData("aws_secret_access_key=abc/DEF+ghi", "aws_secret_access_key=[REDACTED]")]
    [InlineData("https://x.test/cb?code=1&token=abc123&state=2", "https://x.test/cb?code=1&token=[REDACTED]&state=2")]
    [InlineData("password=\"unterminated", "password=\"[REDACTED]")]
    [InlineData("password=Tr0ub4dor&3-horse-staple", "password=[REDACTED]")]
    [InlineData("user=bob&password=hunter2&remember=1", "user=bob&password=[REDACTED]&remember=1")]
    [InlineData("GET /cb?access_token=abc123&token_type=bearer", "GET /cb?access_token=[REDACTED]&token_type=bearer")]
    public void Generic_secret_assignments_keep_the_key_and_hide_the_value(string input, string expected)
    {
        Assert.Equal(expected, Redact(input));
        Assert.Equal(expected, Redact(expected)); // a second pass must not change an already redacted text
    }

    [Fact]
    public void Environment_style_assignments_hide_the_value_once()
    {
        var key = FakeSecrets.AnthropicKey();

        Assert.Equal("ANTHROPIC_API_KEY=[REDACTED]", Redact("ANTHROPIC_API_KEY=" + key));
    }

    [Fact]
    public void Connection_string_passwords_are_hidden_even_with_spaces_or_semicolons()
    {
        Assert.Equal(
            "Server=db01;User Id=sa;Password=[REDACTED];Encrypt=true",
            Redact("Server=db01;User Id=sa;Password=p@ss w0rd;Encrypt=true"));
        Assert.Equal("Data Source=app.db;Pwd=[REDACTED];Mode=ReadWrite", Redact("Data Source=app.db;Pwd=s3cr3t;Mode=ReadWrite"));
        Assert.Equal("Server=db01;Password=\"[REDACTED]\";Encrypt=true", Redact("Server=db01;Password=\"a;b c\";Encrypt=true"));
        Assert.Equal("Password=[REDACTED]", Redact("Password=only one setting"));
    }

    [Fact]
    public void Passwords_in_url_user_info_are_hidden()
    {
        Assert.Equal(
            "git clone https://alice:[REDACTED]@example.test/org/repo.git",
            Redact("git clone https://alice:s3cr3t@example.test/org/repo.git"));
        Assert.Equal("postgres://app:[REDACTED]@db:5432/main", Redact("postgres://app:pa55w0rd@db:5432/main"));
    }

    [Fact]
    public void Secret_command_line_flags_hide_the_next_argument()
    {
        Assert.Equal(
            "tool --password [REDACTED] --token [REDACTED] --api-key [REDACTED] --max-tokens 100 --name x",
            Redact("tool --password hunter2 --token abc123 --api-key xyz789 --max-tokens 100 --name x"));
        Assert.Equal("tool --client-secret \"[REDACTED]\" run", Redact("tool --client-secret \"a b c\" run"));
    }

    [Fact]
    public void Private_key_blocks_are_removed_entirely()
    {
        var block = FakeSecrets.PrivateKeyBlock();

        var redacted = Redact($"before\n{block}\nafter");

        Assert.Equal("before\n[REDACTED]\nafter", redacted);
    }

    [Fact]
    public void A_truncated_private_key_block_is_removed_up_to_the_end_of_the_text()
    {
        var truncated = FakeSecrets.PrivateKeyBlock().Split("-----END", StringSplitOptions.None)[0];

        var redacted = Redact($"key follows: {truncated}");

        Assert.Equal("key follows: [REDACTED]", redacted);
    }

    [Theory]
    [InlineData("max_tokens=4096")]
    [InlineData("input_tokens: 1200, output_tokens: 340")]
    [InlineData("tokens=5")]
    [InlineData("token_count=7")]
    [InlineData("Invalid password.")]
    [InlineData("pass=true")]
    [InlineData("key=value")]
    [InlineData("the task-management disk-usage report")]
    [InlineData("Bearer of good news")]
    [InlineData("sk-learn is a library")]
    [InlineData("Saving C:\\repo\\src\\a.cs failed with 3 errors")]
    [InlineData("Agent run 7f3c finished in 12.3 s (exit code 0)")]
    public void Ordinary_text_and_token_counts_are_not_touched(string text)
    {
        Assert.Equal(text, Redact(text));
    }

    [Theory]
    [InlineData(@"C:\Users\testuser\repo\a.cs", @"~\repo\a.cs")]
    [InlineData("C:/Users/testuser/repo/a.cs", "~/repo/a.cs")]
    [InlineData(@"c:\users\TESTUSER\repo", @"~\repo")]
    [InlineData(@"C:\\Users\\testuser\\repo", @"~\\repo")]
    [InlineData(@"open 'C:\Users\testuser'", "open '~'")]
    [InlineData("file:///C:/Users/testuser/x.txt", "file:///~/x.txt")]
    [InlineData("file:///C:/Users/testuser/My%20Docs/x.txt", "file:///~/My%20Docs/x.txt")]
    [InlineData(@"\\?\C:\Users\testuser\x", @"\\?\~\x")]
    [InlineData(@"two: C:\Users\testuser\a and C:\Users\testuser\b", @"two: ~\a and ~\b")]
    public void The_user_profile_path_collapses_to_a_tilde(string input, string expected)
    {
        Assert.Equal(expected, Redact(input));
    }

    [Theory]
    [InlineData(@"C:\Users\testuser2\repo")]
    [InlineData(@"C:\Users\testuserdata")]
    [InlineData(@"D:\Users\testuser\repo")]
    [InlineData(@"C:\Users\Public\Documents")]
    public void Paths_that_only_resemble_the_profile_are_left_alone(string input)
    {
        Assert.Equal(input, Redact(input));
    }

    [Fact]
    public void A_profile_with_spaces_collapses_in_its_escaped_forms_too()
    {
        var redactor = new SecretRedactor(@"C:\Users\Manuel Lujan");

        Assert.Equal(@"~\proj", redactor.Redact(@"C:\Users\Manuel Lujan\proj"));
        Assert.Equal("file:///~/proj", redactor.Redact("file:///C:/Users/Manuel%20Lujan/proj"));
        Assert.Equal("~/proj", redactor.Redact("C:/Users/Manuel Lujan/proj"));
    }

    [Fact]
    public void A_trailing_separator_on_the_profile_is_ignored()
    {
        var redactor = new SecretRedactor(@"C:\Users\testuser\");

        Assert.Equal(@"~\repo", redactor.Redact(@"C:\Users\testuser\repo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData("/")]
    public void A_missing_or_root_profile_never_collapses_anything(string? profile)
    {
        var redactor = new SecretRedactor(profile);

        Assert.Equal(@"C:\Windows\System32", redactor.Redact(@"C:\Windows\System32"));
        Assert.Equal("password=[REDACTED]", redactor.Redact("password=x"));
    }

    [Fact]
    public void The_default_constructor_uses_the_real_user_profile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(profile) || profile.Length < 4, "This machine has no usable user profile path.");

        var redacted = new SecretRedactor().Redact(profile + @"\AppData\Local\AgentStudio\logs");

        Assert.Equal(@"~\AppData\Local\AgentStudio\logs", redacted);
    }

    [Fact]
    public void Redaction_is_idempotent()
    {
        var sample = string.Join(
            "\n",
            "key " + FakeSecrets.OpenAiKey(),
            "Authorization: Bearer " + FakeSecrets.BearerCredential(),
            "Bearer " + FakeSecrets.BearerCredential(),
            "password=hunter2 and {\"token\": \"abc\"}",
            "Password = hunter2",
            "token : abc123",
            "Authorization : Bearer xyz12345678",
            "tool --password  hunter2",
            "Server=db;Password=p w;Pwd=x",
            "https://u:p@h/x",
            "tool --password hunter2 --client-secret \"a b c\" --token 'x y'",
            "password=\"unterminated",
            "Server=db01;Password=\"a;b c\";Encrypt=true",
            "{'secret': 'abc 123'}",
            FakeSecrets.PrivateKeyBlock(),
            @"C:\Users\testuser\repo",
            FakeSecrets.Jwt());

        var once = Redact(sample);

        Assert.Equal(once, Redact(once));
        Assert.Contains("[REDACTED]", once, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_text_is_returned_unchanged_and_null_is_rejected()
    {
        Assert.Equal(string.Empty, Redact(string.Empty));
        Assert.Throws<ArgumentNullException>(() => Redact(null!));
    }

    [Fact]
    public void Pathological_inputs_finish_quickly()
    {
        string[] inputs =
        [
            new string('a', 300_000),
            string.Concat(Enumerable.Repeat("a-", 150_000)),
            string.Concat(Enumerable.Repeat("password", 40_000)),
            string.Concat(Enumerable.Repeat("token=", 50_000)),
            "eyJ" + new string('a', 300_000),
            "Bearer " + new string('a', 300_000),
            new string(' ', 300_000) + "password=",
            "https://" + new string('a', 300_000) + ":",
            "-----BEGIN " + "PRIVATE KEY-----" + new string('A', 300_000),
            string.Concat(Enumerable.Repeat("Server=a;", 50_000)),
        ];

        var stopwatch = Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            _ = Redact(input);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Redaction took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void A_huge_text_never_echoes_a_secret_whether_it_is_redacted_or_rejected()
    {
        // About 1.6 MB. Either the redaction finishes, or a pattern times out and the fixed placeholder is returned:
        // in both cases the secret at the end must not appear in the result.
        var secret = FakeSecrets.OpenAiKey();
        var huge = string.Concat(Enumerable.Repeat("token=a ", 200_000)) + secret;

        var redacted = Redact(huge);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }
}
