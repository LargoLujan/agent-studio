using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Domain.Common;

/// <summary>SHA-256 helpers used for tamper-evident records (decision log, evidence, frozen requirements).</summary>
public static class ContentHash
{
    public const int HexLength = 64;

    public static string Sha256Hex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static bool IsSha256Hex(string? value) =>
        value is { Length: HexLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// Length-prefixes each field so that two different field lists can never produce the same canonical text
    /// (e.g. ["ab", "c"] and ["a", "bc"]).
    /// </summary>
    public static string Canonical(params string?[] fields)
    {
        var builder = new StringBuilder();
        foreach (var field in fields)
        {
            var value = field ?? string.Empty;
            builder.Append(value.Length).Append(':').Append(value).Append(';');
        }

        return builder.ToString();
    }
}
