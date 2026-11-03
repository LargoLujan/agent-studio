using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Security;
using AgentStudio.Observability.Logging;
using AgentStudio.Security.Redaction;
using AgentStudio.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests.Observability;

/// <summary>Deterministic clock with a fixed +02:00 local zone, so the local-day and UTC-timestamp rules are both exercised.</summary>
internal sealed class LoggingClock(DateTimeOffset now) : TimeProvider
{
    private static readonly TimeZoneInfo Plus2 =
        TimeZoneInfo.CreateCustomTimeZone("log-test-plus-2", TimeSpan.FromHours(2), "test", "test");

    private readonly object _gate = new();
    private DateTimeOffset _now = now;

    public override TimeZoneInfo LocalTimeZone => Plus2;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now.ToUniversalTime();
        }
    }

    public void Set(DateTimeOffset value)
    {
        lock (_gate)
        {
            _now = value;
        }
    }
}

/// <summary>
/// A temporary log folder. It reads the files the way any other reader would (sharing them with the writer), and
/// returns the entries in file order: by day, then by numeric suffix.
/// </summary>
internal sealed partial class TempLogDirectory : IDisposable
{
    public TempLogDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentstudio-logs-" + Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public bool Exists => Directory.Exists(Path);

    /// <summary>Names of every file, ordered by day and then by numeric suffix (foreign files last).</summary>
    public string[] FileNames()
    {
        if (!Directory.Exists(Path))
        {
            return [];
        }

        return Directory.GetFiles(Path)
            .Select(System.IO.Path.GetFileName)
            .OfType<string>()
            .OrderBy(SortKey, StringComparer.Ordinal)
            .ToArray();
    }

    public void WriteFile(string name, string content)
    {
        Directory.CreateDirectory(Path);
        File.WriteAllText(Combine(name), content);
    }

    public byte[] ReadBytes(string name)
    {
        using var stream = new FileStream(Combine(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public string ReadText(string name) => new UTF8Encoding(false).GetString(ReadBytes(name));

    public string[] ReadLines(string name)
    {
        var text = ReadText(name);
        return text.Length == 0 ? [] : text.TrimEnd('\n').Split('\n');
    }

    /// <summary>Every byte of every log file, in file order.</summary>
    public byte[] AllBytes() => FileNames().SelectMany(ReadBytes).ToArray();

    /// <summary>Every log file as UTF-8 text, in file order.</summary>
    public string AllText() => new UTF8Encoding(false).GetString(AllBytes());

    /// <summary>All lines of all log files, in order.</summary>
    public string[] AllLines() => FileNames().Where(IsLogFile).SelectMany(ReadLines).ToArray();

    /// <summary>All entries, parsed. Fails the test if a line is not a JSON object.</summary>
    public List<JsonElement> Records()
    {
        var records = new List<JsonElement>();
        foreach (var line in AllLines())
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            records.Add(document.RootElement.Clone());
        }

        return records;
    }

    public void Dispose() => TempFolders.Delete(Path);

    private static bool IsLogFile(string name) => name.EndsWith(".log", StringComparison.Ordinal);

    private static string SortKey(string name)
    {
        var match = DayFile().Match(name);
        if (!match.Success)
        {
            return "~" + name;
        }

        var suffix = match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return match.Groups["day"].Value + suffix.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^.+-(?<day>\d{8})(?:-(?<index>\d+))?\.log$")]
    private static partial Regex DayFile();
}

/// <summary>
/// A provider wired the way production wires it (real redactor, real rolling file in a temporary folder), with a
/// controllable clock. Disposing the fixture disposes the provider first, which flushes the queue to the folder.
/// </summary>
internal sealed class LoggerFixture : IDisposable
{
    public LoggerFixture()
    {
        Options.LogDirectory = Logs.Path;
    }

    public TempLogDirectory Logs { get; } = new();

    // 10:00 UTC is 12:00 local (+02:00), so the local day is 2026-10-15.
    public LoggingClock Clock { get; } = new(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));

    public FileLoggerOptions Options { get; } = new();

    public ISecretRedactor Redactor { get; set; } = new SecretRedactor(@"C:\Users\testuser");

    public FileLoggerProvider? Provider { get; private set; }

    public FileLoggerProvider Create() => Provider = new FileLoggerProvider(Options, Redactor, Clock);

    public ILogger Logger(string category = "Tests.Category") => (Provider ?? Create()).CreateLogger(category);

    /// <summary>Flushes and closes the provider so the files can be inspected.</summary>
    public void Flush() => Provider?.Dispose();

    public void Dispose()
    {
        Provider?.Dispose();
        Logs.Dispose();
    }
}

internal static class LogAssert
{
    /// <summary>The first eight hex digits of the SHA-256 of the UTF-8 text: the fingerprint the logger writes.</summary>
    public static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    /// <summary>Every string in the element: property names and string values, at any depth.</summary>
    public static IEnumerable<string> AllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var inner in AllStrings(property.Value))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in AllStrings(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    /// <summary>The secret must be nowhere: not in the raw bytes, not in any decoded JSON string.</summary>
    public static void DoesNotLeak(string secret, TempLogDirectory logs)
    {
        Assert.DoesNotContain(secret, logs.AllText(), StringComparison.Ordinal);
        foreach (var record in logs.Records())
        {
            foreach (var text in AllStrings(record))
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }
}
