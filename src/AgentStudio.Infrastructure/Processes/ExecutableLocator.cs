using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>Finds executables on the PATH, honouring PATHEXT on Windows.</summary>
public sealed class ExecutableLocator : IExecutableLocator
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    // Only real executables: PATHEXT may list scripting hosts (.JS, .VBS...) that must not be launched from a lookup.
    private static readonly string[] AllowedExtensions = [".EXE", ".COM", ".CMD", ".BAT"];

    private readonly Func<string?> _pathVariable;
    private readonly Func<string?> _pathExtVariable;
    private readonly ExecutableSearch _search;

    public ExecutableLocator(Func<string?>? pathVariable = null, Func<string?>? pathExtVariable = null, IUntrustedRoots? roots = null)
    {
        _pathVariable = pathVariable ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _pathExtVariable = pathExtVariable ?? (() => Environment.GetEnvironmentVariable("PATHEXT"));
        UntrustedRoots = roots;
        _search = new ExecutableSearch(roots, () => _pathVariable());
    }

    /// <summary>The folders nothing is resolved from; null for a locator that trusts the whole PATH (the relative entries are still dropped).</summary>
    public IUntrustedRoots? UntrustedRoots { get; }

    public string? Find(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        // Only fully qualified directories are searched (a relative entry would resolve against the current directory, which may
        // be a repository that plants its own claude.cmd), and nothing inside an untrusted root is taken, wherever a link leads.
        return _search.Find(_search.PathDirectories(), BuildCandidateNames(command), out _);
    }

    public string? FindInUntrustedRoot(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (UntrustedRoots is null)
        {
            return null;
        }

        _search.Find(_search.PathDirectories(), BuildCandidateNames(command), out var rejected);
        return rejected;
    }

    private List<string> BuildCandidateNames(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [command];
        }

        var names = new List<string>();
        if (Path.HasExtension(command))
        {
            names.Add(command);
        }

        var pathExt = _pathExtVariable();
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            pathExt = DefaultPathExt;
        }

        foreach (var extension in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(command + extension);
            }
        }

        return names;
    }
}
