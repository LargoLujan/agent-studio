using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Debate.Judging.Debates;

/// <summary>
/// How the package of a dossier of a debate is written under the dossier root, with the rules of the package of a run
/// (<see cref="JudgeDossierBuilder"/>): nothing is deleted nor written through a link (a junction or a symbolic link) from
/// the root down, and every file is a new file. The same rules as Phase 3, kept here because its helpers are private to that
/// builder.
/// </summary>
internal static class DossierPackageWriter
{
    private const int HeadPrefixLength = 16;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary><c>&lt;root&gt;/&lt;project&gt;/&lt;unit&gt;/&lt;first 16 characters of the head&gt;</c>: building again at the same head rewrites the same package.</summary>
    /// <exception cref="InvalidOperationException">The folder would fall outside the root.</exception>
    public static string PackageFolder(string dossierRoot, string projectId, string unit, string headHash)
    {
        var root = Path.GetFullPath(dossierRoot);
        var folder = Path.GetFullPath(Path.Combine(root, Segment(projectId, 64), Segment(unit, 64), headHash[..Math.Min(HeadPrefixLength, headHash.Length)]));
        if (!folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The dossier folder '{folder}' would fall outside the dossier root '{root}'.");
        }

        return folder;
    }

    /// <summary>Creates the package folder anew: the old one at the same head is removed (never through a link) and the evidence folder created.</summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    public static string Prepare(string dossierRoot, string folder)
    {
        RefuseLinks(dossierRoot, folder);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true); // Same head, same content: the package is rebuilt, never merged with an old one.
        }

        var evidence = Path.Combine(folder, "evidence");
        Directory.CreateDirectory(evidence);
        RefuseLinks(dossierRoot, evidence); // again, now that the folders exist: one could have been swapped meanwhile
        return evidence;
    }

    /// <summary>
    /// Refuses a path that goes through a link anywhere from the dossier root, included, down to <paramref name="path"/>,
    /// included. The folders above the root are the user's own and are not checked.
    /// </summary>
    /// <exception cref="InvalidOperationException">A folder on the way is a link.</exception>
    public static void RefuseLinks(string dossierRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dossierRoot));
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (IsLink(current))
            {
                throw new InvalidOperationException(
                    $"The dossier folder '{path}' goes through '{current.FullName}', which is a link (a junction or a symbolic link): the package is never written nor deleted through one.");
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>Writes a file that must not exist yet, so a link planted in its place is never followed.</summary>
    public static async Task WriteNewTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(Utf8NoBom.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes into a new file, but hashes the whole content.</summary>
    public static async Task<(long Size, long Written, string Sha256)> CopyAsync(Stream source, string path, long limit, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (target.ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            long size = 0;
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                var room = (int)Math.Min(read, limit - written);
                if (room > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, room), cancellationToken).ConfigureAwait(false);
                    written += room;
                }

                size += read;
            }

            return (size, written, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    /// <summary>A safe folder or file name part: <c>[A-Za-z0-9_-]</c>, at most <paramref name="maxLength"/> characters.</summary>
    public static string Segment(string text, int maxLength)
    {
        var safe = new string([.. text.Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' ? c : '_')]);
        safe = safe.Length > maxLength ? safe[..maxLength] : safe;
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>A link, even one whose target is gone; a folder that does not exist is not one.</summary>
    private static bool IsLink(DirectoryInfo folder) =>
        folder.LinkTarget is not null || (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint));
}
