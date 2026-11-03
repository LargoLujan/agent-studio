using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentStudio.Application.Security;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Observability.Logging;

/// <summary>
/// Turns one log call into one JSON line: <c>timestamp</c> (ISO-8601 UTC), <c>level</c>, <c>category</c>,
/// <c>eventId</c> (and <c>eventName</c>), <c>message</c>, <c>properties</c> and <c>exception</c>, in that order. It runs on
/// the caller's thread and is the last place where the text can still be controlled, so everything that reaches the
/// file passes through it:
/// <list type="bullet">
/// <item>The message is rendered here from the message template and the SANITIZED values (see
/// <see cref="LogValueSanitizer"/>), never from the framework's own rendering, which would print the raw values.</item>
/// <item>The rendered message is then redacted as a whole, because a secret can span the template and a value
/// (<c>"password={Value}"</c>). A text property whose value that pass removed from the message is written as length
/// and hash too.</item>
/// <item>A message longer than <see cref="FileLoggerOptions.MaxMessageLength"/> is written as length and hash only.
/// Exception messages follow the same rule; stack frames are redacted and limited in number.</item>
/// <item>A state that is a list of key-value pairs but has no template does not get its message from the caller's
/// formatter (which could print anything): the message is built from the sanitized pairs.</item>
/// </list>
/// Lines are valid JSON with no raw control characters, so one record is always exactly one line.
/// </summary>
internal sealed class LogRecordFormatter
{
    private const string OriginalFormatKey = "{OriginalFormat}";
    private const int MaxInnerDepth = 3;
    private const int MaxFrameLength = 512;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ISecretRedactor _redactor;
    private readonly LogValueSanitizer _sanitizer;
    private readonly int _maxMessageLength;
    private readonly int _maxStackFrames;

    public LogRecordFormatter(FileLoggerOptions options, ISecretRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);

        _redactor = redactor;
        _sanitizer = new LogValueSanitizer(options, redactor);
        _maxMessageLength = options.MaxMessageLength;
        _maxStackFrames = options.MaxStackFrames;
    }

    private readonly record struct NamedValue(string Name, SafeValue Value);

    /// <summary>Redacts a short label such as a category or an event name.</summary>
    public string SafeLabel(string text) => _redactor.Redact(text);

    public byte[] Format<TState>(
        DateTimeOffset utcNow,
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string? template = null;
        var structured = false;
        var properties = new List<NamedValue>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            structured = true;
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, OriginalFormatKey, StringComparison.Ordinal))
                {
                    template = pair.Value as string;
                }
                else
                {
                    properties.Add(new NamedValue(pair.Key, _sanitizer.Sanitize(pair.Key, pair.Value)));
                }
            }
        }

        var rendered = Render(template, structured, properties, state, exception, formatter);
        var message = _redactor.Redact(rendered);
        HideValuesRemovedFromTheMessage(properties, rendered, message);
        if (message.Length > _maxMessageLength)
        {
            message = LogValueSanitizer.Hash(rendered).ToString();
        }

        return Write(utcNow, category, level, eventId, message, properties, exception);
    }

    /// <summary>A record written by the logger itself, for example "N entries were dropped". Its text is not user text.</summary>
    public byte[] FormatNotice(DateTimeOffset utcNow, LogLevel level, string category, string message, params (string Name, long Value)[] numbers)
    {
        var properties = numbers.Select(n => new NamedValue(n.Name, new SafeValue(n.Value, n.Value))).ToList();
        return Write(utcNow, category, level, default, message, properties, exception: null);
    }

    /// <summary>
    /// A record for a call that could not be formatted: its level, category and event id, and only the TYPE of the
    /// failure, never any content of the call.
    /// </summary>
    public byte[] FormatFailure(DateTimeOffset utcNow, string category, LogLevel level, EventId eventId, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var message = "[log entry could not be formatted: " + failure.GetType().Name + "]";
        return Write(utcNow, category, level, eventId, message, properties: null, exception: null);
    }

    private static string Render<TState>(
        string? template,
        bool structured,
        List<NamedValue> properties,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (template is not null)
        {
            var arguments = properties.Select(p => p.Value.Argument).ToArray();
            return MessageTemplate.TryRender(template, arguments, out var text) ? text : template;
        }

        if (structured)
        {
            // Key-value pairs without a template: the caller's formatter could print the raw values, so it is not used.
            return string.Join(' ', properties.Select(p => p.Name + "=" + Show(p.Value.Argument)));
        }

        try
        {
            return formatter(state, exception) ?? string.Empty;
        }
#pragma warning disable CA1031 // A formatter that fails must not stop the logger.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            return "[message unavailable: " + failure.GetType().Name + "]";
        }
    }

    private static string Show(object? argument) => argument switch
    {
        null => "(null)",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => argument.ToString() ?? string.Empty,
    };

    // A value that is harmless alone can be a secret in its context ("password={Value}"). When redacting the message
    // removed a property's text from it, that property is written as length and hash as well.
    private void HideValuesRemovedFromTheMessage(List<NamedValue> properties, string rendered, string redacted)
    {
        if (string.Equals(rendered, redacted, StringComparison.Ordinal))
        {
            return;
        }

        for (var i = 0; i < properties.Count; i++)
        {
            var value = properties[i].Value;
            if (value.Text is { Length: > 0 } text
                && rendered.Contains(text, StringComparison.Ordinal)
                && !redacted.Contains(text, StringComparison.Ordinal))
            {
                properties[i] = properties[i] with { Value = _sanitizer.Hashed(value.Original ?? text) };
            }
        }
    }

    private byte[] Write(
        DateTimeOffset utcNow,
        string category,
        LogLevel level,
        EventId eventId,
        string message,
        List<NamedValue>? properties,
        Exception? exception)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", utcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            json.WriteString("level", LevelName(level));
            WriteString(json, "category", category);
            json.WriteNumber("eventId", eventId.Id);
            if (!string.IsNullOrEmpty(eventId.Name))
            {
                WriteString(json, "eventName", SafeLabel(eventId.Name));
            }

            WriteString(json, "message", message);

            if (properties is { Count: > 0 })
            {
                json.WriteStartObject("properties");
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties)
                {
                    WriteValue(json, UniqueName(used, property.Name), property.Value.Json);
                }

                json.WriteEndObject();
            }

            if (exception is not null)
            {
                json.WriteStartObject("exception");
                WriteException(json, exception, depth: 0);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.Flush();
        }

        var terminator = buffer.GetSpan(1);
        terminator[0] = (byte)'\n';
        buffer.Advance(1);
        return buffer.WrittenSpan.ToArray();
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var suffix = 2; !used.Add(candidate); suffix++)
        {
            candidate = name + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private static void WriteValue(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(CleanText.Clean(name));
                break;
            case bool flag:
                json.WriteBoolean(CleanText.Clean(name), flag);
                break;
            case long integer:
                json.WriteNumber(CleanText.Clean(name), integer);
                break;
            case ulong unsigned:
                json.WriteNumber(CleanText.Clean(name), unsigned);
                break;
            case double floating:
                json.WriteNumber(CleanText.Clean(name), floating);
                break;
            case decimal number:
                json.WriteNumber(CleanText.Clean(name), number);
                break;
            case string text:
                WriteString(json, name, text);
                break;
            case HashedText hashed:
                json.WriteStartObject(CleanText.Clean(name));
                json.WriteNumber("length", hashed.Length);
                json.WriteString("sha256", hashed.Sha256);
                json.WriteEndObject();
                break;
            case OpaqueObject opaque:
                json.WriteStartObject(CleanText.Clean(name));
                WriteString(json, "type", opaque.Type);
                if (opaque.Count is { } count)
                {
                    json.WriteNumber("count", count);
                }

                json.WriteEndObject();
                break;
        }
    }

    private static void WriteString(Utf8JsonWriter json, string name, string value) =>
        json.WriteString(CleanText.Clean(name), CleanText.Clean(value));

    private void WriteException(Utf8JsonWriter json, Exception exception, int depth)
    {
        var type = exception.GetType();
        WriteString(json, "type", type.FullName ?? type.Name);
        WriteString(json, "message", ExceptionMessage(exception));

        if (depth == 0 && _maxStackFrames > 0)
        {
            var frames = StackFrames(exception);
            if (frames.Count > 0)
            {
                json.WriteStartArray("stackTrace");
                foreach (var frame in frames)
                {
                    json.WriteStringValue(CleanText.Clean(frame));
                }

                json.WriteEndArray();
            }
        }

        if (exception.InnerException is { } inner && depth < MaxInnerDepth)
        {
            json.WriteStartObject("inner");
            WriteException(json, inner, depth + 1);
            json.WriteEndObject();
        }
    }

    private string ExceptionMessage(Exception exception)
    {
        string message;
        try
        {
            message = exception.Message ?? string.Empty;
        }
#pragma warning disable CA1031 // An exception that cannot describe itself must not stop the logger.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            return "[message unavailable: " + failure.GetType().Name + "]";
        }

        return message.Length > _maxMessageLength ? LogValueSanitizer.Hash(message).ToString() : _redactor.Redact(message);
    }

    private List<string> StackFrames(Exception exception)
    {
        var frames = new List<string>();
        string? stack;
        try
        {
            stack = exception.StackTrace;
        }
#pragma warning disable CA1031 // A stack that cannot be read is simply left out.
        catch (Exception)
#pragma warning restore CA1031
        {
            return frames;
        }

        if (string.IsNullOrWhiteSpace(stack))
        {
            return frames;
        }

        foreach (var raw in stack.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var redacted = _redactor.Redact(line);
            frames.Add(redacted.Length > MaxFrameLength ? redacted[..MaxFrameLength] : redacted);
            if (frames.Count >= _maxStackFrames)
            {
                break;
            }
        }

        return frames;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None",
    };
}
