using System.Runtime.InteropServices;

namespace AgentStudio.Providers.Cli.Codex;

/// <summary>
/// Codex installed with npm is a <c>codex.cmd</c> shim, which would make every launch go through cmd.exe. The
/// package ships the native binary inside <c>node_modules</c>; when it is found there it is used directly.
/// The platform folder and the target triple are discovered, never hard-coded.
/// </summary>
public static class CodexExecutableResolver
{
    private const string NativeName = "codex.exe";

    public static string Resolve(string locatedPath) => Resolve(locatedPath, RuntimeInformation.ProcessArchitecture);

    public static string Resolve(string locatedPath, Architecture architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locatedPath);
        if (!IsShim(locatedPath))
        {
            return locatedPath;
        }

        try
        {
            var shimDirectory = Path.GetDirectoryName(locatedPath);
            if (shimDirectory is null)
            {
                return locatedPath;
            }

            var scope = Path.Combine(shimDirectory, "node_modules", "@openai", "codex", "node_modules", "@openai");
            if (!Directory.Exists(scope))
            {
                return locatedPath;
            }

            var arch = architecture == Architecture.Arm64 ? "arm64" : "x64";
            var candidates = Directory.EnumerateDirectories(scope, "codex-win32-*")
                .OrderByDescending(d => Path.GetFileName(d).EndsWith("-" + arch, StringComparison.OrdinalIgnoreCase))
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var platformPackage in candidates)
            {
                var vendor = Path.Combine(platformPackage, "vendor");
                if (!Directory.Exists(vendor))
                {
                    continue;
                }

                foreach (var triple in Directory.EnumerateDirectories(vendor).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var native = Path.Combine(triple, "bin", NativeName);
                    if (File.Exists(native))
                    {
                        return native;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the shim: the runner rejects cmd.exe metacharacters, so it stays safe.
        }

        return locatedPath;
    }

    private static bool IsShim(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }
}
