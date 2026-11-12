namespace AgentStudio.Infrastructure.Processes;

/// <summary>
/// Folders too wide to be treated as hostile (ADR-0018, second addendum of 2026-11-12): the whole profile of the person, the program
/// folders, the system, a whole drive, or any folder that contains one of them. A project opened there cannot be an untrusted root:
/// that would hide git, node and the programs of the agents the person installed for themself, and the whole disk cannot be hostile.
/// The app says so when such a folder is opened instead.
/// </summary>
public static class BroadFolders
{
    private static readonly Lazy<string[]> Known = new(() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(UntrustedPaths.Canonical)
        .Where(p => p.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray());

    /// <summary>Whether <paramref name="path"/> is a drive root, or contains (or is) the profile of the person, a program folder or the system.</summary>
    public static bool IsTooBroad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
        {
            return false;
        }

        return IsTooBroadCanonical(UntrustedPaths.Canonical(path));
    }

    internal static bool IsTooBroadCanonical(string canonical)
    {
        if (canonical.Length == 0)
        {
            return false;
        }

        // "C:" is what the root of a drive becomes without its trailing separator; a share root of the network, "\\server\share", likewise.
        if ((canonical.Length == 2 && canonical[1] == ':') || (canonical.StartsWith(@"\\", StringComparison.Ordinal) && canonical.Count(c => c == '\\') <= 3))
        {
            return true;
        }

        return Known.Value.Any(known => UntrustedPaths.IsInsideCanonical(known, canonical));
    }
}
