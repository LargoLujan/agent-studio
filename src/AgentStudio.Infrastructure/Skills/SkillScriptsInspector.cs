using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Skills;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>
/// The fingerprint of the <c>scripts/</c> folder of a skill (docs/20 D-14): the SHA-256 of the sorted relative paths and the content of every file. Nothing is
/// run, and nothing is followed: a link (a file or a folder) counts by its name and is never opened, so a link planted in the folder cannot take the contents
/// of anything else into the hash or out of the folder. The folder is limited (<see cref="MaxFiles"/> files; each is hashed whole, in chunks): past the limit
/// the fingerprint says so and can never equal one of a smaller folder. The folder is also limited in bytes (<see cref="MaxTotalBytes"/>): past it nothing is hashed and the
/// skill is not reviewable.
/// </summary>
public sealed class SkillScriptsInspector : ISkillScriptsInspector
{
    public const int MaxFiles = 500;

    /// <summary>The most bytes of all the files of one <c>scripts/</c> folder that are read: past it the skill is not reviewable.</summary>
    public const long MaxTotalBytes = 32L * 1024 * 1024;

    private const string ScriptsFolder = "scripts";

    public Task<SkillScriptsInfo?> InspectAsync(string skillFolder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillFolder);
        return Task.Run(() => Inspect(skillFolder, ct), ct);
    }

    private static SkillScriptsInfo? Inspect(string skillFolder, CancellationToken ct)
    {
        var root = new DirectoryInfo(Path.Combine(skillFolder, ScriptsFolder));
        if (!root.Exists && root.LinkTarget is null)
        {
            return null;
        }

        var files = new List<(string Relative, FileInfo File, bool Link)>();
        var truncated = false;
        var pending = new Stack<DirectoryInfo>();
        if (IsLink(root))
        {
            files.Add((ScriptsFolder + "/", new FileInfo(root.FullName), true));
        }
        else
        {
            pending.Push(root);
        }

        while (pending.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root.FullName, entry.FullName).Replace('\\', '/');
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }

                if (IsLink(entry))
                {
                    files.Add((relative, new FileInfo(entry.FullName), true));
                }
                else if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else
                {
                    files.Add((relative, (FileInfo)entry, false));
                }
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        var names = files.Select(f => f.Relative).ToList();

        // A folder over the total budget is not read at all (it would block the screen that asks for it): it is listed, and never reviewable.
        long total = 0;
        foreach (var (_, file, link) in files)
        {
            if (!link)
            {
                total += file.Length;
            }
        }

        if (total > MaxTotalBytes)
        {
            return TooBig(names);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long readTotal = 0;
        foreach (var (relative, file, link) in files)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes((link ? "L:" : "F:") + relative));
            hash.AppendData([0]);
            if (link)
            {
                continue;
            }

            // Whatever its size, the whole content goes into the fingerprint (in chunks): a long file is never reviewed by its length alone.
            hash.AppendData(BitConverter.GetBytes(file.Length));
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                readTotal += read;
                if (readTotal > MaxTotalBytes)
                {
                    // A file that grew while it was read.
                    return TooBig(names);
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        if (truncated)
        {
            hash.AppendData(Encoding.UTF8.GetBytes("TRUNCATED"));
        }

        return new SkillScriptsInfo(Convert.ToHexStringLower(hash.GetHashAndReset()), names);
    }

    /// <summary>The answer for a folder over the budget: the names only, a fingerprint that is not a hash of content (it is never stored as a review), and not reviewable.</summary>
    private static SkillScriptsInfo TooBig(List<string> names) =>
        new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("TOOBIG\0" + string.Join('\n', names)))), names, Reviewable: false);

    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
