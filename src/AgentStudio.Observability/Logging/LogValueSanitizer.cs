using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Application.Security;

namespace AgentStudio.Observability.Logging;

/// <summary>A text that is not written: only its length (in UTF-16 characters) and the first eight hex digits of its SHA-256.</summary>
internal readonly record struct HashedText(int Length, string Sha256)
{
    public override string ToString() => $"[text len={Length.ToString(CultureInfo.InvariantCulture)} sha256={Sha256}]";
}

/// <summary>An object that is not written: only the name of its type and, when it is cheap to know, how many items it holds.</summary>
internal readonly record struct OpaqueObject(string Type, int? Count)
{
    public override string ToString() =>
        Count is { } count ? $"[{Type} ({count.ToString(CultureInfo.InvariantCulture)} items)]" : $"[{Type}]";
}

/// <summary>
/// A value that may be written.
/// </summary>
/// <param name="Json">
/// What goes into the JSON: <see langword="null"/>, <see cref="bool"/>, <see cref="long"/>, <see cref="ulong"/>,
/// <see cref="double"/>, <see cref="decimal"/>, <see cref="string"/>, <see cref="HashedText"/> or <see cref="OpaqueObject"/>.
/// </param>
/// <param name="Argument">What takes the place of the value in the rendered message (formatted with the invariant culture).</param>
/// <param name="Text">For a text that is written verbatim: that text (already redacted).</param>
/// <param name="Original">For a text that is written verbatim: the text as it was received.</param>
internal readonly record struct SafeValue(object? Json, object? Argument, string? Text = null, string? Original = null);

/// <summary>
/// Decides what may be written of a value that a log call passed as a property. The rule is fail-closed by type:
/// <list type="number">
/// <item>Numbers, booleans, enum names, GUIDs and dates are written as they are: they cannot hold a prompt.</item>
/// <item>Text (<see cref="string"/>, <see cref="Uri"/>, and a non-generic struct with a hand-written <c>ToString()</c>,
/// such as a typed id) is free text. It is written, after secret redaction, only when its property NAME is not
/// sensitive (see <see cref="FileLoggerOptions.SensitiveNameWords"/> and <see cref="FileLoggerOptions.ExemptNameWords"/>)
/// and it has at most <c>MaxValueLength</c> characters; otherwise only its length and a short SHA-256 prefix are
/// written.</item>
/// <item>Anything else (classes, records, record structs, tuples, collections, exceptions) is NEVER serialized or
/// printed: only the name of its type is written, plus the item count of a collection. Printing it would expose
/// whatever its members hold, and a record that carries a prompt prints that prompt.</item>
/// </list>
/// </summary>
internal sealed partial class LogValueSanitizer
{
    private const string NullText = "(null)";
    private const int NameCacheLimit = 1024;
    private const int MaxWordsPerPhrase = 3;

    private readonly ISecretRedactor _redactor;
    private readonly int _maxValueLength;
    private readonly FrozenSet<string> _sensitiveWords;
    private readonly FrozenSet<string> _exemptWords;
    private readonly ConcurrentDictionary<string, bool> _nameCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Type, bool> _ownText = new();

    public LogValueSanitizer(FileLoggerOptions options, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);

        _redactor = redactor;
        _maxValueLength = options.MaxValueLength;
        _sensitiveWords = Normalize(options.SensitiveNameWords);
        _exemptWords = Normalize(options.ExemptNameWords);
    }

    public SafeValue Sanitize(string name, object? value)
    {
        switch (value)
        {
            case null:
                return new SafeValue(null, NullText);
            case string text:
                return SanitizeText(name, text);
            case bool flag:
                return new SafeValue(flag, flag);
            case sbyte or short or int or long or byte or ushort or uint:
                return new SafeValue(Convert.ToInt64(value, CultureInfo.InvariantCulture), value);
            case ulong unsigned:
                return new SafeValue(unsigned, value);
            case float or double:
                var floating = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return new SafeValue(double.IsFinite(floating) ? floating : NonFinite(floating), value);
            case decimal number:
                return new SafeValue(number, value);
            case char character:
                return new SafeValue(character.ToString(), value);
            case Enum named:
                return new SafeValue(named.ToString(), value);
            case Guid id:
                return new SafeValue(id.ToString("D", CultureInfo.InvariantCulture), value);
            case DateTime moment:
                return new SafeValue(moment.ToString("O", CultureInfo.InvariantCulture), value);
            case DateTimeOffset offset:
                return new SafeValue(offset.ToString("O", CultureInfo.InvariantCulture), value);
            case DateOnly day:
                return new SafeValue(day.ToString("O", CultureInfo.InvariantCulture), value);
            case TimeOnly time:
                return new SafeValue(time.ToString("O", CultureInfo.InvariantCulture), value);
            case TimeSpan span:
                return new SafeValue(span.ToString("c", CultureInfo.InvariantCulture), value);
            case Uri uri:
                return SanitizeText(name, uri.OriginalString);
            case Type type:
                return SanitizeText(name, type.FullName ?? type.Name);
            case Exception:
                return Opaque(value, count: null);
            case ValueType when HasOwnText(value.GetType()):
                return SanitizeText(name, SafeToString(value));
            case ICollection collection:
                return Opaque(value, collection.Count);
            default:
                return Opaque(value, count: null);
        }
    }

    /// <summary>
    /// Writes a text under a property name: verbatim after redaction, or as length and hash when the name is sensitive
    /// or the text is longer than the limit.
    /// </summary>
    public SafeValue SanitizeText(string name, string text)
    {
        if (text.Length > _maxValueLength || IsSensitiveName(name))
        {
            return Hashed(text);
        }

        var redacted = _redactor.Redact(text);
        return new SafeValue(redacted, redacted, redacted, text);
    }

    public SafeValue Hashed(string text)
    {
        var hashed = Hash(text);
        return new SafeValue(hashed, hashed.ToString());
    }

    public static HashedText Hash(string text)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new HashedText(text.Length, Convert.ToHexStringLower(digest.AsSpan(0, 4)));
    }

    /// <summary>
    /// True when the name, split into words, contains a run of up to three consecutive words that is in the sensitive
    /// set: <c>SystemInstructions</c> (instructions), <c>modelOutput</c> (output), <c>api_key</c> (apikey).
    /// <c>Context</c> is one word and does not contain <c>text</c>. A name that ENDS in an exempt word (path, root,
    /// type, id...; trailing numbers do not count) is about something other than text and is not sensitive:
    /// <c>ContentRoot</c>, <c>OutputPath</c>.
    /// </summary>
    public bool IsSensitiveName(string name)
    {
        if (_nameCache.TryGetValue(name, out var known))
        {
            return known;
        }

        var result = ComputeIsSensitive(name);
        if (_nameCache.Count < NameCacheLimit)
        {
            _nameCache[name] = result;
        }

        return result;
    }

    private bool ComputeIsSensitive(string name)
    {
        var words = WordBoundary()
            .Split(name.TrimStart('@', '$'))
            .Where(word => word.Length > 0)
            .Select(word => word.ToLowerInvariant())
            .ToArray();

        var last = words.LastOrDefault(word => !word.All(char.IsAsciiDigit));
        if (last is not null && _exemptWords.Contains(last))
        {
            return false;
        }

        for (var start = 0; start < words.Length; start++)
        {
            var phrase = string.Empty;
            for (var length = 0; length < MaxWordsPerPhrase && start + length < words.Length; length++)
            {
                phrase += words[start + length];
                if (_sensitiveWords.Contains(phrase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A struct is trusted to describe itself (a typed id whose ToString() returns its value) only when it is not generic
    // and its own ToString() was written by hand. The ToString() of a record struct, a tuple or a key-value pair prints
    // every member, and a struct without one prints nothing useful: those are written by type name only.
    private bool HasOwnText(Type type) =>
        _ownText.GetOrAdd(
            type,
            static t => !t.IsGenericType
                && t.GetMethod(nameof(ToString), Type.EmptyTypes) is { } toString
                && toString.DeclaringType == t
                && !toString.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));

    private static FrozenSet<string> Normalize(IEnumerable<string> words) =>
        words
            .Select(word => word.Trim().ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToFrozenSet(StringComparer.Ordinal);

    private static string NonFinite(double value) =>
        double.IsNaN(value) ? "NaN" : double.IsPositiveInfinity(value) ? "Infinity" : "-Infinity";

    private static string SafeToString(object value)
    {
        try
        {
            return value.ToString() ?? string.Empty;
        }
#pragma warning disable CA1031 // A value that cannot describe itself must not stop the logger.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            return "[ToString failed: " + failure.GetType().Name + "]";
        }
    }

    private static SafeValue Opaque(object value, int? count)
    {
        var opaque = new OpaqueObject(TypeName(value.GetType()), count);
        return new SafeValue(opaque, opaque.ToString());
    }

    private static string TypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return name + "<" + string.Join(',', type.GetGenericArguments().Select(TypeName)) + ">";
    }

    // Splits at lower-to-upper case changes (FinalText), acronym ends (JSONContent), letter-digit changes and separators.
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=[0-9])|(?<=[0-9])(?=[A-Za-z])|[_\-.\s]+")]
    private static partial Regex WordBoundary();
}
