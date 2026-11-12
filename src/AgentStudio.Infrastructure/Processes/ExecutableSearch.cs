using System.ComponentModel;
using AgentStudio.Application.Execution;

namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// The one rule that decides where an executable may come from (ADR-0018, addendum of 2026-11-12), shared by
/// <see cref="ExecutableLocator"/> and by the runners, which resolve a name given without a path (<c>dotnet</c>, <c>node</c>, <c>cmd</c>)
/// with it, in the order <c>CreateProcess</c> uses (the system folder, the Windows folder, then the PATH) except that neither the
/// folder of Agent Studio nor its current folder are searched: a relative entry of the PATH is dropped, and so is any entry, or any
/// result, that lies inside an untrusted root, wherever a link or a junction leads. A name with a path is not resolved here: the
/// caller named that file.
/// </summary>
internal sealed class ExecutableSearch(IUntrustedRoots? roots, Func<string?>? pathVariable)
{
    private const int ErrorFileNotFound = 2;

    private readonly Func<string?> _path = pathVariable ?? (() => Environment.GetEnvironmentVariable("PATH"));

    /// <summary>The untrusted roots this search follows; null when it trusts the whole PATH.</summary>
    internal IUntrustedRoots? Roots => roots;

    /// <summary>Whether <paramref name="name"/> is a bare program name: no folder, no drive, nothing to resolve against a current directory.</summary>
    internal static bool IsBareName(string name) =>
        name.Length > 0 && name.IndexOfAny(['\\', '/', ':']) < 0;

    /// <summary>The fully qualified, non-empty entries of the PATH, in order. Relative ones would resolve against the current folder, which may be a repository.</summary>
    internal IEnumerable<string> PathDirectories()
    {
        var path = _path();
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var directory = raw.Trim().Trim('"');
            if (directory.Length > 0 && IsFullyQualified(directory))
            {
                yield return directory;
            }
        }
    }

    /// <summary>
    /// The first of <paramref name="names"/> in the first of <paramref name="directories"/> that exists and is not inside an untrusted
    /// root. <paramref name="rejected"/> is the first match that was left out for being inside one.
    /// </summary>
    internal string? Find(IEnumerable<string> directories, IReadOnlyList<string> names, out string? rejected)
    {
        rejected = null;
        var untrusted = UntrustedPaths.Resolve(roots?.Roots);
        foreach (var directory in directories)
        {
            var directoryInside = default(bool?);
            foreach (var name in names)
            {
                var found = TryFile(directory, name);
                if (found is null)
                {
                    continue;
                }

                if (untrusted.Count > 0)
                {
                    directoryInside ??= UntrustedPaths.IsInside(directory, untrusted);
                    if (directoryInside == true || UntrustedPaths.IsInside(found, untrusted))
                    {
                        rejected ??= found;
                        continue;
                    }
                }

                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The full path of the executable a bare name means for a process that starts without a shell: <c>name.exe</c> (or the name as it
    /// is when it already has an extension) in the system folder, the Windows folder or the PATH.
    /// </summary>
    /// <exception cref="Win32Exception">It is nowhere outside the untrusted roots: nothing is started.</exception>
    internal string ResolveBareName(string name)
    {
        var file = Path.HasExtension(name) ? name : name + ".exe";
        var system = new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        var found = Find(system.Concat(PathDirectories()), [file], out var rejected);
        if (found is not null)
        {
            return found;
        }

        throw new Win32Exception(
            ErrorFileNotFound,
            rejected is null
                ? $"'{name}' was not found in the system folders or on the PATH, so the process was not started."
                : $"'{name}' was found only inside a project folder ('{rejected}'), which is not trusted to provide programs, so the process was not started.");
    }

    internal static string? TryFile(string directory, string name)
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
}
