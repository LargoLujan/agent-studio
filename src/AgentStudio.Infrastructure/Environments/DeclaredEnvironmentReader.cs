using System.Globalization;
using System.Text;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Environments;
using AgentStudio.Infrastructure.Processes.Isolation;
using Microsoft.Win32.SafeHandles;

namespace AgentStudio.Infrastructure.Environments;

/// <summary>
/// Reads what a project declares about its environment (docs/20 D-05 and D-06) as untrusted data: the closed list of <see cref="DeclaredFiles"/>, relative to the
/// root of the project and checked with <see cref="RepositoryPaths"/>; at most 256 KB of each; never through a symbolic link or a junction (the file or any folder
/// between the root and it; the placeholders of OneDrive and the like are reparse points that are not links, and are read); the file is opened and what was opened
/// is checked, so a link put there between the check and the opening does not lead out; strict UTF-8; and then the pure parsers of <see cref="DeclaredFileParser"/>
/// do the rest. A compose file is not opened: only its existence and its size are said, because YAML is not interpreted in this phase (docs/20 decision 10).
/// Nothing is executed: this type has no way to start a process (no <c>Process</c>, no process runner), and it reads no <c>.env</c>. It never throws for a file:
/// whatever cannot be read is a <see cref="DeclaredIssue"/>.
/// </summary>
public sealed class DeclaredEnvironmentReader : IDeclaredEnvironmentReader
{
    /// <summary>The largest file that is read (docs/20 D-06): the same cap as <c>verification.json</c>.</summary>
    public const int MaxFileBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Whether an entry of the path is a link that is not read through: a symbolic link or a junction, which .NET reports with a target. A reparse point with no target,
    /// such as the placeholder of a file that lives in OneDrive, is a regular file for this purpose.
    /// </summary>
    internal static bool IsLink(FileAttributes attributes, string? linkTarget) =>
        attributes.HasFlag(FileAttributes.ReparsePoint) && linkTarget is not null;

    public async Task<DeclaredEnvironment> ReadAsync(string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var builder = new DeclaredReadingBuilder();
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DeclaredEnvironment.Empty;
        }

        if (!Directory.Exists(root))
        {
            return DeclaredEnvironment.Empty;
        }

        var finalRoot = OperatingSystem.IsWindows() ? IntegrityLabels.FinalPath(root) : null;
        foreach (var file in DeclaredFiles.All)
        {
            ct.ThrowIfCancellationRequested();
            if (!RepositoryPaths.IsSafeRelative(file))
            {
                continue;
            }

            await ReadFileAsync(root, finalRoot, file, builder, ct).ConfigureAwait(false);
        }

        return builder.Build();
    }

    private static async Task ReadFileAsync(string root, string? finalRoot, string file, DeclaredReadingBuilder builder, CancellationToken ct)
    {
        var source = new DeclaredSource(file);
        FileInfo info;
        try
        {
            var state = Inspect(root, file, out info);
            if (state == PathState.Missing)
            {
                return;
            }

            builder.Found(file);
            if (state == PathState.Link)
            {
                builder.Issue(source, DeclaredIssueKind.IsLink);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            builder.Found(file);
            builder.Issue(source, DeclaredIssueKind.Unreadable);
            return;
        }

        if (DeclaredFiles.ComposeFiles.Contains(file, StringComparer.Ordinal))
        {
            // Said, not read: its size is all that is told.
            builder.Entry(DeclaredKind.ComposeFile, null, info.Length.ToString(CultureInfo.InvariantCulture), source);
            return;
        }

        byte[] content;
        try
        {
            var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                // What was opened is what was checked: a file swapped for a link after the inspection ends elsewhere.
                if (OperatingSystem.IsWindows() && finalRoot is not null && !IsExpected(stream.SafeFileHandle, finalRoot, file))
                {
                    builder.Issue(source, DeclaredIssueKind.IsLink);
                    return;
                }

                // One byte more than the cap tells a file that is too large from one that is exactly at it.
                var buffer = new byte[MaxFileBytes + 1];
                var length = 0;
                int read;
                while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
                {
                    length += read;
                }

                if (length > MaxFileBytes)
                {
                    builder.Issue(source, DeclaredIssueKind.TooLarge);
                    return;
                }

                content = buffer.AsSpan(0, length).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            builder.Issue(source, DeclaredIssueKind.Unreadable);
            return;
        }

        string text;
        try
        {
            ReadOnlySpan<byte> byteOrderMark = [0xEF, 0xBB, 0xBF];
            var span = content.AsSpan();
            text = StrictUtf8.GetString(span.StartsWith(byteOrderMark) ? span[byteOrderMark.Length..] : span);
        }
        catch (DecoderFallbackException)
        {
            builder.Issue(source, DeclaredIssueKind.Malformed);
            return;
        }

        DeclaredFileParser.Parse(file, text, builder);
    }

    private enum PathState
    {
        Missing,
        Link,
        File,
    }

    /// <summary>Looks at each part of the path from the root down, without following anything: a link on the way ends the look.</summary>
    private static PathState Inspect(string root, string relative, out FileInfo info)
    {
        var segments = relative.Split('/');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            var folder = new DirectoryInfo(current);
            if (IsLink(folder.Attributes, folder.LinkTarget))
            {
                info = new FileInfo(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                return PathState.Link;
            }

            if (!folder.Exists)
            {
                info = new FileInfo(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                return PathState.Missing;
            }
        }

        info = new FileInfo(Path.Combine(current, segments[^1]));
        if (IsLink(info.Attributes, info.LinkTarget))
        {
            return PathState.Link;
        }

        return info.Exists ? PathState.File : PathState.Missing;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsExpected(SafeFileHandle handle, string finalRoot, string relative)
    {
        var opened = IntegrityLabels.FinalPath(handle);
        var expected = Path.Combine(finalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        return opened is not null && string.Equals(opened, expected, StringComparison.OrdinalIgnoreCase);
    }
}
