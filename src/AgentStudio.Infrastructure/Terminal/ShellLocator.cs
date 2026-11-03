using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Terminal;

/// <summary>Finds PowerShell 7, Windows PowerShell, cmd and Git Bash.</summary>
public sealed class ShellLocator : IShellLocator
{
    private readonly IExecutableLocator _locator;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _environment;

    public ShellLocator(IExecutableLocator locator, Func<string, bool>? fileExists = null, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _fileExists = fileExists ?? File.Exists;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public IReadOnlyList<ShellInfo> GetAvailableShells()
    {
        var shells = new List<ShellInfo>();

        Add(shells, "pwsh", "PowerShell 7", _locator.Find("pwsh"), ["-NoLogo"]);
        Add(shells, "powershell", "Windows PowerShell", _locator.Find("powershell"), ["-NoLogo"]);
        Add(shells, "cmd", "Símbolo del sistema", FindCmd(), []);
        Add(shells, "git-bash", "Git Bash", FindGitBash(), ["--login", "-i"]);
        return shells;
    }

    private static void Add(List<ShellInfo> shells, string id, string displayName, string? path, IReadOnlyList<string> arguments)
    {
        if (!string.IsNullOrEmpty(path))
        {
            shells.Add(new ShellInfo(id, displayName, path, arguments));
        }
    }

    private string? FindCmd()
    {
        var comSpec = _environment("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && _fileExists(comSpec))
        {
            return comSpec;
        }

        return _locator.Find("cmd");
    }

    // "bash" on the PATH is usually the WSL launcher in System32, not Git Bash: look next to git instead.
    private string? FindGitBash()
    {
        var git = _locator.Find("git");
        if (git is not null)
        {
            // <root>\cmd\git.exe or <root>\bin\git.exe or <root>\mingw64\bin\git.exe
            var directory = Path.GetDirectoryName(git);
            for (var i = 0; i < 3 && directory is not null; i++)
            {
                var candidate = Path.Combine(directory, "bin", "bash.exe");
                if (_fileExists(candidate))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        foreach (var variable in new[] { "ProgramFiles", "ProgramW6432", "LocalAppData" })
        {
            var root = _environment(variable);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var candidate = variable == "LocalAppData"
                ? Path.Combine(root, "Programs", "Git", "bin", "bash.exe")
                : Path.Combine(root, "Git", "bin", "bash.exe");
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
