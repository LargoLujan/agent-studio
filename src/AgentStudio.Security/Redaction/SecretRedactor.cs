using System.Text.RegularExpressions;
using AgentStudio.Application.Security;

namespace AgentStudio.Security.Redaction;

/// <summary>
/// Pattern-based <see cref="ISecretRedactor"/>. It replaces, with <see cref="Placeholder"/>, the secrets that commonly
/// leak into logs and error messages, and collapses the user profile path to <c>~</c>:
/// <list type="bullet">
/// <item>Vendor tokens by their prefix: <c>sk-</c> (OpenAI and <c>sk-ant-</c> Anthropic), <c>AIza</c> (Google),
/// <c>ghp_</c>/<c>gho_</c>/<c>ghu_</c>/<c>ghs_</c>/<c>ghr_</c>/<c>github_pat_</c> (GitHub), <c>xox?-</c> (Slack), <c>AKIA</c>/<c>ASIA</c>
/// (AWS), plus GitLab, npm, Hugging Face, Stripe and Google OAuth tokens.</item>
/// <item>JSON Web Tokens, <c>Bearer</c> credentials and <c>Authorization</c> header values.</item>
/// <item>PEM private key blocks (up to the end of the text when the block is truncated).</item>
/// <item>Passwords in URL user info (<c>scheme://user:password@host</c>) and in connection strings
/// (<c>Password=</c>/<c>Pwd=</c>, up to the next <c>;</c>, so a password with spaces is hidden entirely).</item>
/// <item>Assignments whose key ends with <c>password</c>, <c>secret</c>, <c>token</c>, <c>api_key</c>, <c>access_key</c>,
/// <c>private_key</c> and similar (<c>key=value</c>, <c>key: value</c>, JSON and quoted forms) and the same words as
/// command line flags (<c>--password value</c>). The key must END with the word, so <c>max_tokens=4096</c> and
/// <c>token_count=7</c> (usage figures, not secrets) are left alone. An unquoted value runs to the next whitespace,
/// comma, semicolon or quote, except in a URL query or form body (<c>?token=abc&amp;state=2</c>), where it ends at the
/// next <c>&amp;</c>.</item>
/// </list>
/// The user profile path (for example <c>C:\Users\someone</c>) is matched case-insensitively with <c>\</c> and <c>/</c>
/// separators, JSON-escaped (<c>\\</c>) and URL-escaped forms, and only as a whole path segment prefix
/// (<c>C:\Users\someone2</c> is not collapsed).
/// </summary>
/// <remarks>
/// The class is stateless after construction and thread-safe. Every pattern is linear and has a match timeout; if a
/// pattern ever times out the text is replaced by a fixed placeholder, never returned unredacted (fail closed). This
/// is a best-effort filter for known formats, not a guarantee: it complements, and does not replace, not logging
/// secrets or free text in the first place.
/// </remarks>
public sealed partial class SecretRedactor : ISecretRedactor
{
    /// <summary>The text that replaces every redacted secret.</summary>
    public const string Placeholder = "[REDACTED]";

    /// <summary>Returned instead of the text when a pattern could not finish in time.</summary>
    public const string RedactionFailed = "[REDACTION FAILED]";

    private const int TimeoutMilliseconds = 1000;

    // Case-insensitive words that mark a secret. The assignment and flag patterns require the word to END the key.
    private const string SecretWords =
        "password|passwd|passphrase|\\bpwd|secret|token|api[_\\-]?key|access[_\\-]?key|private[_\\-]?key|account[_\\-]?key"
        + "|signing[_\\-]?key|encryption[_\\-]?key|credentials?";

    private const string FlagWords =
        "password|passwd|pwd|passphrase|secret|token|api-?key|access-?key|private-?key|client-?secret|credentials?";

    private readonly Regex? _profile;

    /// <summary>Creates a redactor for the user profile of the current process.</summary>
    public SecretRedactor()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>Creates a redactor that collapses <paramref name="userProfilePath"/> to <c>~</c>.</summary>
    /// <param name="userProfilePath">
    /// The profile folder. A missing value or a drive root is ignored, so that <c>C:\</c> never becomes <c>~</c>.
    /// </param>
    public SecretRedactor(string? userProfilePath)
    {
        _profile = BuildProfilePattern(userProfilePath);
    }

    /// <inheritdoc />
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        try
        {
            // Specific token formats first, so the generic key=value rules only see what is left.
            var result = PrivateKeyBlock().Replace(text, Placeholder);
            result = JsonWebToken().Replace(result, Placeholder);
            result = VendorToken().Replace(result, Placeholder);
            result = AuthorizationHeader().Replace(result, "${name}${sep}${scheme}" + Placeholder);
            result = BearerCredential().Replace(result, "${scheme} " + Placeholder);
            result = UrlPassword().Replace(result, "${user}:" + Placeholder + "@");
            result = SecretFlag().Replace(result, "${flag}${sep}${oq}" + Placeholder + "${cq}");
            result = QueryParameter().Replace(result, "${key}${sep}" + Placeholder);
            result = ConnectionStringPassword().Replace(result, "${lead}${key}${sep}" + Placeholder);
            result = SecretAssignment().Replace(result, "${key}${q}${sep}${oq}" + Placeholder + "${cq}");
            return _profile is null ? result : _profile.Replace(result, "~");
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactionFailed;
        }
    }

    private static Regex? BuildProfilePattern(string? userProfilePath)
    {
        if (string.IsNullOrWhiteSpace(userProfilePath))
        {
            return null;
        }

        var profile = userProfilePath.Trim().TrimEnd('\\', '/');
        if (profile.Length < 3)
        {
            return null; // "C:", "C:\" or "/" would turn every path into "~...".
        }

        var slashes = profile.Replace('\\', '/');
        var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            profile, // C:\Users\someone
            slashes, // C:/Users/someone
            profile.Replace("\\", "\\\\", StringComparison.Ordinal), // C:\\Users\\someone, as inside a JSON string
            slashes.Replace(" ", "%20", StringComparison.Ordinal), // file:///C:/Users/some%20one
        };

        var alternatives = forms.OrderByDescending(form => form.Length).Select(Regex.Escape);
        return new Regex(
            "(?:" + string.Join('|', alternatives) + ")(?!\\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(TimeoutMilliseconds));
    }

    [GeneratedRegex(
        "-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----[\\s\\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|\\z)",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(
        "\\beyJ[A-Za-z0-9_\\-]{5,}\\.[A-Za-z0-9_\\-]{5,}\\.[A-Za-z0-9_\\-]*",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(
        "\\b(?:sk-[A-Za-z0-9_\\-]{20,}"
        + "|[sr]k_(?:live|test)_[A-Za-z0-9]{16,}"
        + "|AIza[0-9A-Za-z_\\-]{30,}"
        + "|gh[pousr]_[A-Za-z0-9]{20,}"
        + "|github_pat_[A-Za-z0-9_]{20,}"
        + "|xox[a-z]-[A-Za-z0-9\\-]{10,}"
        + "|xapp-[A-Za-z0-9\\-]{10,}"
        + "|(?:AKIA|ASIA)[A-Z0-9]{16}"
        + "|glpat-[A-Za-z0-9_\\-]{20,}"
        + "|npm_[A-Za-z0-9]{30,}"
        + "|hf_[A-Za-z0-9]{30,}"
        + "|ya29\\.[A-Za-z0-9_\\-]{20,})",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex VendorToken();

    // Authorization: [scheme] credential. The credential runs to the end of the value. The prefix is an atomic group so
    // that the engine cannot backtrack into it and redact an existing placeholder again (idempotence).
    [GeneratedRegex(
        "\\b(?>(?<name>(?:Proxy-)?Authorization)(?<sep>[ \\t]*[:=][ \\t]*)"
        + "(?<scheme>(?:Bearer|Basic|Digest|Negotiate|Token|ApiKey)[ \\t]+)?)"
        + "(?!\\[REDACTED\\])[^\\r\\n\"',;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(
        "\\b(?<scheme>Bearer)[ \\t]+[A-Za-z0-9._~+/\\-]{8,}=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex BearerCredential();

    // scheme://user:password@host
    [GeneratedRegex(
        "(?<=://)(?<user>[^\\s:/@\"']+):(?!\\[REDACTED\\]@)[^\\s/@\"']+@",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex UrlPassword();

    // --password value, --api-key "value", -token value. The next argument is the secret.
    [GeneratedRegex(
        "(?<![\\w\\-])(?>(?<flag>--?(?:[a-z0-9]+-)*(?:" + FlagWords + "))(?![\\w\\-])(?<sep>[ \\t]+))"
        + "(?:(?<oq>\")(?!\\[REDACTED\\]\")[^\"\\r\\n]*(?<cq>\")?|(?<oq>')(?!\\[REDACTED\\]')[^'\\r\\n]*(?<cq>')?|(?!\\[REDACTED\\])(?![\"'])\\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex SecretFlag();

    // ...;Password=any text up to the semicolon;... A password may contain spaces, so it runs to the ';' or the end of the line.
    [GeneratedRegex(
        "(?>(?<lead>(?:^|;)[ \\t]*)(?<key>password|pwd)(?<sep>[ \\t]*=[ \\t]*))(?!\\[REDACTED\\])(?![\"'])[^;\\r\\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline,
        TimeoutMilliseconds)]
    private static partial Regex ConnectionStringPassword();

    // ?name=value&other=... A secret in a URL query or in a form body ends at the next '&' (an '&' inside a value is
    // escaped as %26 there).
    [GeneratedRegex(
        "(?<=[?&])(?<key>[A-Za-z0-9_.\\-]{0,40}(?:" + SecretWords + "))(?<sep>=)(?!\\[REDACTED\\])[^\\s&\"']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex QueryParameter();

    // key=value, key: value, "key": "value", 'key': 'value'. The key must END with a secret word. Outside a URL query an
    // '&' is part of the value ("password=a&b"), so only whitespace, commas, semicolons and quotes end it.
    [GeneratedRegex(
        "(?>(?<key>" + SecretWords + ")(?<q>[\"']?)(?<sep>[ \\t]*[:=][ \\t]*))"
        + "(?:(?<oq>\")(?!\\[REDACTED\\]\")(?:[^\"\\\\\\r\\n]|\\\\.)+(?<cq>\")?"
        + "|(?<oq>')(?!\\[REDACTED\\]')[^'\\r\\n]+(?<cq>')?"
        + "|(?!\\[REDACTED\\])[^\\s,;\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex SecretAssignment();
}
