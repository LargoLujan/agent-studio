using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Infrastructure.Diagnostics;

/// <summary>
/// Readers of what the tools say (docs/20 D-02, D-09 and §11). What a tool writes is data that is not trusted: every reader is total (it never
/// throws for an output), bounded (lines, characters, JSON depth and number of entries), decides by structure and not by the words of a language
/// when it can, and answers «not understood» (null, or <c>Unreadable</c>) when the output is not what it expected: the check then says «sin
/// comprobar», never «correcto».
/// </summary>
public static partial class ToolOutputParsers
{
    private const int MaxLines = 2_000;
    private const int MaxLineLength = 1_000;
    private const int MaxSdks = 200;
    private const int MaxRuntimes = 500;
    private const int MaxJsonDepth = 32;

    // One to four numbers and an optional pre-release: the shape ToolVersion accepts.
    private const string VersionShape = @"\d{1,9}(?:\.\d{1,9}){0,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]{0,40})?";

    [GeneratedRegex(@"^(?<version>" + VersionShape + @")\s+\[(?<path>[^\]]{1,520})\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkLine();

    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9.]{0,99})\s+(?<version>" + VersionShape + @")\s+\[(?<path>[^\]]{1,520})\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeLine();

    [GeneratedRegex(@"^v?(?<version>" + VersionShape + @")\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainVersion();

    [GeneratedRegex(@"^git version (?<version>\d{1,9}(?:\.\d{1,9}){0,2})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersion();

    [GeneratedRegex(@"^Python (?<version>" + VersionShape + @")\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PythonVersion();

    [GeneratedRegex(@"^(?<key>[^:\r\n]{1,100}):\s*(?<value>.{0,200})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueLine();

    /// <summary>The SDKs of <c>dotnet --list-sdks</c>; null when the output has text and none of it is a line of that list. An empty output is an empty list.</summary>
    public static IReadOnlyList<InstalledSdk>? DotNetSdks(string? output)
    {
        if (IsBinary(output))
        {
            return null;
        }

        var lines = Lines(output);
        var sdks = new List<InstalledSdk>();
        foreach (var line in lines)
        {
            var match = SdkLine().Match(line);
            if (match.Success && ToolVersion.TryParse(match.Groups["version"].Value, out var version) && TryPath(match.Groups["path"].Value, out var path) && sdks.Count < MaxSdks)
            {
                sdks.Add(new InstalledSdk(version, path));
            }
        }

        return sdks.Count == 0 && lines.Count > 0 ? null : sdks;
    }

    /// <summary>The runtimes of <c>dotnet --list-runtimes</c>; null when the output has text and none of it is a line of that list.</summary>
    public static IReadOnlyList<InstalledRuntime>? DotNetRuntimes(string? output)
    {
        if (IsBinary(output))
        {
            return null;
        }

        var lines = Lines(output);
        var runtimes = new List<InstalledRuntime>();
        foreach (var line in lines)
        {
            var match = RuntimeLine().Match(line);
            if (match.Success && ToolVersion.TryParse(match.Groups["version"].Value, out var version) && runtimes.Count < MaxRuntimes)
            {
                runtimes.Add(new InstalledRuntime(match.Groups["name"].Value, version));
            }
        }

        return runtimes.Count == 0 && lines.Count > 0 ? null : runtimes;
    }

    /// <summary>The version a tool says in the first line of its answer to <c>--version</c>; null when it does not say one in the form that tool uses.</summary>
    public static ToolVersion? Version(DoctorTool tool, string? output)
    {
        var first = Lines(output).FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        var match = tool switch
        {
            DoctorTool.NodeVersion or DoctorTool.NpmVersion => PlainVersion().Match(first),
            DoctorTool.GitVersion => GitVersion().Match(first),
            DoctorTool.PythonVersion or DoctorTool.PyVersion => PythonVersion().Match(first),
            _ => Match.Empty,
        };
        return match.Success && ToolVersion.TryParse(match.Groups["version"].Value, out var version) ? version : null;
    }

    /// <summary>
    /// Docker as <c>docker version --format json</c> tells it, by structure: an object with <c>Client</c> and a <c>Server</c> that is an object
    /// (engine running) or null or missing (engine stopped). The text of the messages is never read. Null when it is not that shape.
    /// </summary>
    public static DockerReading? Docker(string? output, bool truncated)
    {
        if (truncated || string.IsNullOrWhiteSpace(output) || output.Length > DoctorToolResult.MaxOutputLength || IsBinary(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Client", out var client) || !TryVersion(client, out var clientVersion))
            {
                return null;
            }

            if (!root.TryGetProperty("Server", out var server) || server.ValueKind == JsonValueKind.Null)
            {
                return new DockerReading(clientVersion, null);
            }

            return TryVersion(server, out var serverVersion) ? new DockerReading(clientVersion, serverVersion) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// WSL as <c>wsl --status</c> tells it, by the shape of its lines and not by the words of the language of Windows: a line <c>name: value</c>
    /// whose value is a number is the default version; the first line whose value is not a number names the default distribution. Null when no
    /// line has a number (the version), which is not the status of WSL.
    /// </summary>
    public static WslReading? Wsl(string? output, bool truncated)
    {
        if (truncated || output is null || IsBinary(output))
        {
            return null;
        }

        string? distribution = null;
        int? version = null;
        var first = true;
        foreach (var line in Lines(output))
        {
            var match = KeyValueLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var isFirst = first;
            first = false;

            var value = match.Groups["value"].Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && value.Length <= 2)
            {
                version ??= number;
            }
            else if (isFirst)
            {
                // The distribution is the first line, and a value that is not a number is a name; any later line (kernel, updates) is not read.
                distribution = value.Length <= 100 && !UntrustedText.HasHidden(value) ? value : null;
            }
        }

        return version is null ? null : new WslReading(distribution, version.Value);
    }

    private static bool TryVersion(JsonElement element, out ToolVersion version)
    {
        version = null!;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Version", out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        return text is not null && ToolVersion.TryParse(text, out version!);
    }

    private static bool TryPath(string text, out string? path)
    {
        path = text.Length is >= 1 and <= 520 && !UntrustedText.HasHidden(text) ? text : null;
        return true;
    }

    /// <summary>An output with a NUL is not text in UTF-8: it is UTF-16 read as UTF-8, or binary. Nothing is understood from it.</summary>
    private static bool IsBinary(string? output) => output is not null && output.Contains('\0', StringComparison.Ordinal);

    /// <summary>The non-blank lines, at most <see cref="MaxLines"/>, each cut at <see cref="MaxLineLength"/> characters.</summary>
    private static List<string> Lines(string? output)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(output))
        {
            return lines;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            lines.Add(line.Length > MaxLineLength ? line[..MaxLineLength] : line);
            if (lines.Count >= MaxLines)
            {
                break;
            }
        }

        return lines;
    }
}

/// <summary>Docker as it was read: the version of the client and, when the engine answered, of the server.</summary>
public sealed record DockerReading(ToolVersion Client, ToolVersion? Server);

/// <summary>WSL as it was read: the default distribution (null when it names none) and the default version.</summary>
public sealed record WslReading(string? DefaultDistribution, int DefaultVersion)
{
    /// <summary>
    /// Whether the distribution is one of Docker Desktop (<c>docker-desktop</c>, <c>docker-desktop-data</c>): it is internal to Docker and a person cannot
    /// work in it, so a computer whose only distribution is that one is a WSL without a distribution of its own (docs/20 D-09; seen on the development machine).
    /// </summary>
    public bool IsDockerInternal => DefaultDistribution is not null && DefaultDistribution.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>A distribution the person can use, at the version 2 the design asks for.</summary>
    public bool IsReady => DefaultDistribution is not null && !IsDockerInternal && DefaultVersion == 2;
}
