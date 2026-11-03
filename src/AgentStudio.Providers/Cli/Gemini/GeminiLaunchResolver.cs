using AgentStudio.Application.Execution;

namespace AgentStudio.Providers.Cli.Gemini;

/// <summary>The executable and the leading arguments that start Gemini CLI.</summary>
public sealed record GeminiLaunch(string FileName, IReadOnlyList<string> LeadingArguments);

/// <summary>
/// Decides how to start Gemini CLI. The npm install puts a <c>gemini.cmd</c> shim on the PATH that runs
/// <c>node "%dp0%\node_modules\@google\gemini-cli\bundle\gemini.js" %*</c>; when both node and that bundle are found,
/// node is started directly so cmd.exe never re-parses the arguments. Otherwise the located file is used as is.
/// </summary>
public sealed class GeminiLaunchResolver(IExecutableLocator locator)
{
    private static readonly string[] BundleSegments = ["node_modules", "@google", "gemini-cli", "bundle", "gemini.js"];

    /// <summary>Null when Gemini CLI is not installed.</summary>
    public GeminiLaunch? Resolve()
    {
        var shim = locator.Find("gemini");
        if (shim is null)
        {
            return null;
        }

        var extension = Path.GetExtension(shim);
        var isShim = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        if (!isShim)
        {
            return new GeminiLaunch(shim, []);
        }

        var directory = Path.GetDirectoryName(shim);
        if (directory is not null)
        {
            var bundle = Path.Combine([directory, .. BundleSegments]);
            var node = locator.Find("node");
            if (node is not null && File.Exists(bundle))
            {
                return new GeminiLaunch(node, [bundle]);
            }
        }

        return new GeminiLaunch(shim, []);
    }
}
