using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Skills;

namespace AgentStudio.Infrastructure.Skills;

/// <summary>
/// The fingerprint of the <c>scripts/</c> folder of a skill (docs/20 D-14): the SHA-256 of the sorted relative paths and the content of every file. Nothing is
/// run, and nothing is followed: a link (a file or a folder) counts by its name and is never opened, so a link planted in the folder cannot take the contents
/// of anything else into the hash or out of the folder. The folder is limited (<see cref="MaxFiles"/> files, <see cref="MaxFileBytes"/> each): past the limit
/// the fingerprint says so and can never equal one of a smaller folder.
/// </summary>
public sealed class SkillScriptsInspector : ISkillScriptsInspector
{
    public const int MaxFiles = 500;

    public const long MaxFileBytes = 8 * 1024 * 1024;

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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, file, link) in files)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes((link ? "L:" : "F:") + relative));
            hash.AppendData([0]);
            if (link)
            {
                continue;
            }

            if (file.Length > MaxFileBytes)
            {
                hash.AppendData(Encoding.UTF8.GetBytes("TOO-LARGE:" + file.Length));
                continue;
            }

            hash.AppendData(BitConverter.GetBytes(file.Length));
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }

        if (truncated)
        {
            hash.AppendData(Encoding.UTF8.GetBytes("TRUNCATED"));
        }

        return new SkillScriptsInfo(Convert.ToHexStringLower(hash.GetHashAndReset()), [.. files.Select(f => f.Relative)]);
    }

    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
