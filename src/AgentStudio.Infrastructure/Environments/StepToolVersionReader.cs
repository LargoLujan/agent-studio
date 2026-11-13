using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Infrastructure.Environments;

/// <summary>
/// The versions of the tools of a verification step for the header of its evidence (docs/20 D-12; ADR-0061), read without starting anything: from the version
/// resource of the executable the launch resolves, which is the file the step starts. A bare name is resolved by the same rule as the runners
/// (<see cref="ExecutableSearch.ResolveBareName"/>: the system folders and the PATH, never a folder of an untrusted root and only <c>.exe</c>), so the number
/// is that of the file that runs, not of a <c>.cmd</c> that comes first on the PATH. A full path is the file the profile named. The resource of <c>node.exe</c>
/// counts only if it is Node's (a shim that carries its own version does not), a script has none, and npm, which the profile starts as <c>node npm-cli.js</c>, is
/// read from the <c>package.json</c> next to its <c>npm-cli.js</c>. For <c>dotnet.exe</c> the resource is that of the host and the note says so: the SDK is the one
/// <c>global.json</c> of the copy picks, and learning it would mean running <c>dotnet</c>, which this reading does not do. Never throws.
/// </summary>
public sealed class StepToolVersionReader : IStepToolVersionReader
{
    private const string NpmCliScript = "npm-cli.js";

    private const long MaxPackageJsonBytes = 64 * 1024;

    private readonly ExecutableSearch _search;

    /// <param name="roots">The open projects: nothing inside them is a tool (ADR-0018).</param>
    /// <param name="pathVariable">The PATH to search; the one of the process by default. A test gives its own.</param>
    public StepToolVersionReader(IUntrustedRoots? roots = null, Func<string?>? pathVariable = null)
    {
        _search = new ExecutableSearch(roots, pathVariable);
    }

    public IReadOnlyList<EvidenceToolVersion> Read(IReadOnlyList<VerificationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var tools = new List<EvidenceToolVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Executable) || tools.Count >= EvidenceToolVersions.MaxEntries)
            {
                continue;
            }

            if (seen.Add(step.Executable))
            {
                tools.Add(Describe(step.Executable));
            }

            if (NpmScript(step) is { } script && seen.Add("npm:" + script))
            {
                tools.Add(NpmVersion(script));
            }
        }

        return tools;
    }

    private EvidenceToolVersion Describe(string executable)
    {
        var tool = EvidenceToolVersion.TokenOf(Path.GetFileNameWithoutExtension(executable.Trim()));
        try
        {
            string path;
            if (Path.IsPathFullyQualified(executable))
            {
                if (Refused(executable) is { } why)
                {
                    return EvidenceToolVersion.Unknown(tool, why);
                }

                path = executable;
            }
            else if (ExecutableSearch.IsBareName(executable))
            {
                path = _search.ResolveBareName(executable);
            }
            else
            {
                return EvidenceToolVersion.Unknown(tool, "a relative path has no fixed file to read");
            }

            var file = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                return EvidenceToolVersion.Unknown(tool, "the file was not found");
            }

            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return EvidenceToolVersion.Unknown(tool, "a script has no version resource");
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            if (tool.Equals("node", StringComparison.OrdinalIgnoreCase) && !string.Equals(info.ProductName, "Node.js", StringComparison.Ordinal))
            {
                return EvidenceToolVersion.Unknown(tool, "the version resource is not that of Node.js (a shim?)");
            }

            return VersionOf(info) is { } version
                ? new EvidenceToolVersion(tool, version, tool.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? "version resource of the dotnet host; the SDK is the one global.json picks"
                    : $"version resource of {EvidenceToolVersion.Clean(file)}")
                : EvidenceToolVersion.Unknown(tool, "the executable has no version resource");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // ResolveBareName says why nothing was found (or that it was found only inside a project folder).
            return EvidenceToolVersion.Unknown(tool, ex is Win32Exception ? "not found outside the project folders" : "the file cannot be read");
        }
    }

    /// <summary>Why a full path is not looked at, or null: a network (UNC) or device path would reach out of the computer or to a device, and a file inside an open project is not trusted.</summary>
    private string? Refused(string path)
    {
        if (path.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "a network or device path is not read";
        }

        return _search.IsUntrusted(path) ? "the file is inside a project folder, which is not trusted" : null;
    }

    /// <summary>The version of a resource: the first word of the product version if it is one (<c>24.14.0</c>, <c>11.0.0-preview.2.26159.112</c>), else the three numbers of the file version.</summary>
    private static string? VersionOf(FileVersionInfo info)
    {
        var product = (info.ProductVersion ?? string.Empty).Trim();
        var word = product.Split([' ', '@'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimStart('v');
        if (word is not null && ToolVersion.TryParse(word, out var parsed))
        {
            return parsed.Text;
        }

        return info.FileMajorPart == 0 && info.FileMinorPart == 0
            ? null
            : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
    }

    /// <summary>The <c>npm-cli.js</c> a step of the node profile passes to <c>node</c>, when it does.</summary>
    private static string? NpmScript(VerificationStep step)
    {
        var first = step.Arguments is { Count: > 0 } ? step.Arguments[0] : null;
        return string.Equals(Path.GetFileNameWithoutExtension(step.Executable), "node", StringComparison.OrdinalIgnoreCase)
            && first is not null && Path.IsPathFullyQualified(first)
            && string.Equals(Path.GetFileName(first), NpmCliScript, StringComparison.OrdinalIgnoreCase)
            ? first
            : null;
    }

    private EvidenceToolVersion NpmVersion(string script)
    {
        try
        {
            if (Refused(script) is { } why)
            {
                return EvidenceToolVersion.Unknown("npm", why);
            }

            // <npm>\bin\npm-cli.js: the package.json of npm is two levels up.
            var package = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script) ?? string.Empty, "..", "package.json"));
            var info = new FileInfo(package);
            if (!info.Exists || info.Length > MaxPackageJsonBytes)
            {
                return EvidenceToolVersion.Unknown("npm", "the package.json of npm was not found");
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(package), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "npm"
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && ToolVersion.TryParse(version.GetString(), out var parsed))
            {
                return new EvidenceToolVersion("npm", parsed.Text, "package.json of npm");
            }

            return EvidenceToolVersion.Unknown("npm", "the package.json of npm has no version");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException)
        {
            return EvidenceToolVersion.Unknown("npm", "the package.json of npm cannot be read");
        }
    }
}
