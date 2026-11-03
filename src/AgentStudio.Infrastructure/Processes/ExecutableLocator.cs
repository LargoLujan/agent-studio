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

    public ExecutableLocator(Func<string?>? pathVariable = null, Func<string?>? pathExtVariable = null)
    {
        _pathVariable = pathVariable ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _pathExtVariable = pathExtVariable ?? (() => Environment.GetEnvironmentVariable("PATHEXT"));
    }

    public string? Find(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var path = _pathVariable();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidates = BuildCandidateNames(command);
        foreach (var rawDirectory in path.Split(Path.PathSeparator))
        {
            var directory = rawDirectory.Trim().Trim('"');
            // A relative entry ("." or "bin") would resolve against the current directory, which may be a repository
            // that plants its own claude.cmd: only fully qualified directories are searched.
            if (directory.Length == 0 || !IsFullyQualified(directory))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var found = TryFile(directory, candidate);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
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

    private static bool IsFullyQualified(string directory)
    {
        try
        {
            return Path.IsPathFullyQualified(directory);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? TryFile(string directory, string name)
    {
        try
        {
            var full = Path.Combine(directory, name);
            return File.Exists(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
