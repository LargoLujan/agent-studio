using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Infrastructure.Persistence;

/// <summary>
/// Keeps the runtime data of a project (database, evidence, temporary files, the trusted verification profile) out of
/// the user's <c>git status</c> by adding lines to the repository's <c>info/exclude</c>, which is local and never
/// committed. The user's <c>.gitignore</c> is never touched, and neither are the journal and the Markdown memory, which
/// are versioned (ADR-0008). In a linked worktree the lines go to the common git directory, the only
/// <c>info/exclude</c> git reads.
/// </summary>
/// <remarks>
/// The lines are a convenience: if anything is suspicious (a link where a folder or file should be, an
/// <c>info/exclude</c> with more than one hard link) or cannot be written, they are skipped and the project still opens.
/// </remarks>
internal static partial class GitExclude
{
    private const string Marker = "# Agent Studio";

    /// <summary>
    /// What is excluded, relative to the project's <c>.agentstudio</c> folder. The lock file matches <c>*.db*</c>;
    /// <c>verification.json</c> is the trusted verification profile (ADR-0016), which is edited from the UI and never
    /// versioned; <c>canvas-layout.json</c> holds where the orchestrator canvas placed its nodes on this machine.
    /// </summary>
    private static readonly string[] Entries = ["*.db*", "evidence/", "tmp/", "verification.json", "canvas-layout.json"];

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint AccessLow;
        public uint AccessHigh;
        public uint WriteLow;
        public uint WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    public static void EnsureExcluded(string projectRoot)
    {
        try
        {
            EnsureExcludedCore(projectRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine($"Agent Studio: info/exclude not updated for '{projectRoot}': {ex.Message}");
        }
    }

    /// <summary>The lines for a project at <paramref name="projectRoot"/> in the work tree whose top is <paramref name="workTreeTop"/>.</summary>
    internal static IReadOnlyList<string> LinesFor(string workTreeTop, string projectRoot)
    {
        var relative = Path.GetRelativePath(workTreeTop, projectRoot);
        var prefix = relative == "." ? string.Empty : Escape(relative.Replace('\\', '/')) + "/";
        return [.. Entries.Select(entry => prefix + ProjectStorage.DataFolderName + "/" + entry)];
    }

    private static void EnsureExcludedCore(string projectRoot)
    {
        var top = FindWorkTreeTop(projectRoot);
        if (top is null)
        {
            return;
        }

        var gitDir = ResolveCommonGitDir(top);
        if (gitDir is null)
        {
            return;
        }

        var infoDir = Path.Combine(gitDir, "info");
        var exclude = Path.Combine(infoDir, "exclude");
        EnsureNotLink(infoDir);
        EnsureNotLink(exclude);
        Directory.CreateDirectory(infoDir);
        AppendMissingLines(exclude, LinesFor(top, projectRoot));
    }

    /// <summary>The nearest folder, from the project upwards, that holds a <c>.git</c> folder or file: where git would look.</summary>
    private static string? FindWorkTreeTop(string projectRoot)
    {
        for (var directory = new DirectoryInfo(projectRoot); directory is not null; directory = directory.Parent)
        {
            var dotGit = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    // <top>/.git is a folder (normal repository) or a file "gitdir: <path>" (linked worktree or submodule). A worktree's
    // git dir names the common one in its "commondir" file; info/exclude lives there.
    private static string? ResolveCommonGitDir(string workTreeTop)
    {
        var dotGit = Path.Combine(workTreeTop, ".git");
        EnsureNotLink(dotGit);
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var gitDir = Path.GetFullPath(Path.Combine(workTreeTop, line["gitdir:".Length..].Trim()));
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            EnsureNotLink(commonFile);
            gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim()));
        }

        return Directory.Exists(gitDir) ? gitDir : null;
    }

    private static void AppendMissingLines(string path, IReadOnlyList<string> lines)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        // Checked on the handle that is written, before anything is read or written: a file with a second name could be
        // a hard link (an agent with a shell can plant one) to a file Agent Studio must never change.
        if (OperatingSystem.IsWindows()
            && (!GetFileInformationByHandle(stream.SafeFileHandle, out var information) || information.NumberOfLinks != 1))
        {
            throw new InvalidOperationException($"Refusing to write '{path}': it has more than one hard link, or its links cannot be counted.");
        }

        string existing;
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
        {
            existing = reader.ReadToEnd();
        }

        var present = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = lines.Where(l => !present.Contains(l)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        if (existing.Length > 0 && !existing.EndsWith('\n'))
        {
            text.Append('\n');
        }

        if (!present.Contains(Marker))
        {
            text.Append(Marker).Append('\n');
        }

        foreach (var line in missing)
        {
            text.Append(line).Append('\n');
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString()));
    }

    // In a pattern, '[' opens a character class and a leading '#' or '!' means comment or negation. Windows names cannot
    // hold '*', '?' or '\', so nothing else needs escaping.
    private static string Escape(string relativePath)
    {
        var escaped = relativePath.Replace("[", "\\[", StringComparison.Ordinal);
        return escaped.StartsWith('#') || escaped.StartsWith('!') ? "\\" + escaped : escaped;
    }

    private static void EnsureNotLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null))
        {
            throw new InvalidOperationException($"Refusing to use '{path}': it is a link (junction or symbolic link).");
        }
    }
}
