using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;

namespace AgentStudio.Orchestration.Verification;

/// <summary>
/// What the Node and Python profiles share (docs/17 D-26, ADR-0055): the step that says why a tool cannot be started instead of
/// failing blindly, and the environment of an isolated step of those stacks.
/// </summary>
public static class StackStepSupport
{
    /// <summary>Where Windows keeps the Store aliases of programs (<c>python.exe</c> among them): not a real interpreter, and it may not start at Low integrity.</summary>
    public static bool IsStoreAlias(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Replace('/', '\\').Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A step that only fails, and says why in its output: <c>cmd /d /c "echo message 1&gt;&amp;2 &amp; exit /b 1"</c>. It takes the
    /// place of a step whose tool cannot be started, so the evidence names the reason. It does not go through the tool's own
    /// shim: <c>cmd</c> only prints. The message may hold none of <c>&amp; | &lt; &gt; ^ % "</c>.
    /// </summary>
    public static VerificationStep Failing(string id, string title, EvidenceKind kind, string message)
    {
        if (message.IndexOfAny(['&', '|', '<', '>', '^', '%', '"']) >= 0)
        {
            throw new ArgumentException("The message of a failing step holds a character the command interpreter would read.", nameof(message));
        }

        return new VerificationStep(id, title, "cmd", ["/d", "/c", "echo " + message + " 1>&2 & exit /b 1"], kind, TestReportFormat.None, null, TimeSpan.FromMinutes(1));
    }

    private static readonly string[] NodeTools = ["node", "npm", "npx"];

    private static readonly string[] PythonTools = ["python", "python3", "py", "pip", "pip3"];

    /// <summary>
    /// The variables an isolated step of <paramref name="executable"/> needs on top of those of every isolated step: for Node and
    /// Python, the home, the caches and the user configuration all point into the work area, so the tool never reads or writes the
    /// profile of the user (docs/17 D-26; ADR-0055). Empty for any other executable. The empty configuration files are created in
    /// the area when they are not there; a path that already exists is never written through.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> EnvironmentFor(string executable, IIsolatedWorkArea area)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(area);
        var name = Path.GetFileNameWithoutExtension(executable.Trim());
        var node = NodeTools.Contains(name, StringComparer.OrdinalIgnoreCase);
        var python = PythonTools.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (!node && !python)
        {
            return new Dictionary<string, string?>();
        }

        var home = area.CliHomeDirectory;
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["APPDATA"] = home,
            ["LOCALAPPDATA"] = home,
        };
        if (node)
        {
            var userConfig = Path.Combine(home, "npm-userconfig");
            EnsureEmptyFile(userConfig);
            variables["npm_config_cache"] = Path.Combine(home, "npm-cache");
            variables["npm_config_userconfig"] = userConfig;
            variables["npm_config_prefix"] = Path.Combine(home, "npm-prefix");
            variables["npm_config_update_notifier"] = "false";
            variables["npm_config_fund"] = "false";
        }
        else
        {
            var pipConfig = Path.Combine(home, "pip.ini");
            EnsureEmptyFile(pipConfig);
            variables["PIP_CACHE_DIR"] = Path.Combine(home, "pip-cache");
            variables["PIP_CONFIG_FILE"] = pipConfig;
            variables["PYTHONUSERBASE"] = Path.Combine(home, "python-user");
            variables["PYTHONDONTWRITEBYTECODE"] = "1";
        }

        return variables;
    }

    private static void EnsureEmptyFile(string path)
    {
        try
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already there (a link included: CreateNew never writes through one) or not writable: the tool then reads what is there.
        }
    }
}
