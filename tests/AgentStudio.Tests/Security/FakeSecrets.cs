using System.Text;

namespace AgentStudio.Tests.Security;

/// <summary>
/// Deterministic fake credentials. They are assembled at run time from fragments so that no real-looking secret is
/// committed to the repository (secret scanners match the literal prefixes).
/// </summary>
internal static class FakeSecrets
{
    private const string AlphaNumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string UpperAndDigits = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string UrlSafe = AlphaNumeric + "_-";

    public static string OpenAiKey() => "sk-" + "proj-" + Body(48, 11);

    public static string AnthropicKey() => "sk-" + "ant-api03-" + Body(80, 12, UrlSafe);

    public static string GoogleApiKey() => "AI" + "za" + Body(35, 13, UrlSafe);

    public static string GitHubToken() => "gh" + "p_" + Body(36, 14);

    public static string GitHubFineGrainedToken() => "github" + "_pat_" + Body(22, 15) + "_" + Body(59, 16);

    public static string SlackBotToken() => "xo" + "xb-" + Body(12, 17, "0123456789") + "-" + Body(24, 18);

    public static string AwsAccessKeyId() => "AK" + "IA" + Body(16, 19, UpperAndDigits);

    public static string Jwt() =>
        Base64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + Base64Url("{\"sub\":\"1234567890\",\"name\":\"Test User\"}") + "." + Body(43, 20, UrlSafe);

    /// <summary>An opaque credential that contains '+', '/' and '=' (characters a JSON encoder may rewrite).</summary>
    public static string BearerCredential() => "q7Zr+8Hx/2LmN0pYw3Vb5Tk9Cd1FgJ4sA6Ee" + "==";

    /// <summary>The PEM block of a private key with a fake body.</summary>
    public static string PrivateKeyBlock() =>
        "-----BEGIN " + "RSA PRIVATE KEY-----\n" + Body(64, 21) + "\n" + Body(64, 22) + "\n-----END " + "RSA PRIVATE KEY-----";

    public static string Body(int length, int seed, string alphabet = AlphaNumeric)
    {
        var random = new Random(seed);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
