using System.Security.Cryptography;
using System.Text;
using AgentStudio.Application.Diagrams;
using AgentStudio.Domain.Common;

namespace AgentStudio.Memory.Diagrams;

/// <summary>What the file at a destination is, seen without reading it as anything: nothing, a link (never followed), or a file with its SHA-256.</summary>
internal readonly record struct ExportProbe(bool Exists, bool IsLink, string? Sha256)
{
    public static ExportProbe None { get; } = new(false, false, null);
}

/// <summary>
/// The files «Exportar al repositorio» writes (docs/20 D-25): names of a closed list, a Markdown that GitHub paints, and the writing to the project with every
/// segment of the path checked, so a link or a junction in the folder (a repository is data that is not trusted) is never followed. What it writes is never
/// read back to decide anything: only its SHA-256 is calculated, to know whether it is the file that was written.
/// </summary>
internal static class DiagramExportFiles
{
    /// <summary>The largest file whose SHA-256 is calculated. A larger one cannot be what was written (the text of a diagram has a cap), so it counts as changed.</summary>
    private const long MaxProbeBytes = 8 * 1024 * 1024;

    /// <summary>The name of the file of a diagram: <c>solucion.md</c>, <c>proyecto-&lt;name&gt;.md</c>, <c>plan-&lt;run&gt;.md</c> or <c>secuencia-&lt;run&gt;.md</c>.</summary>
    public static string FileName(DiagramRef diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        return diagram.Kind switch
        {
            DiagramKind.Solution => "solucion.md",
            DiagramKind.ProjectNamespaces => "proyecto-" + SafeName.Of(diagram.Subject) + ".md",
            DiagramKind.RunPlan => "plan-" + SafeName.Of(diagram.Subject) + ".md",
            DiagramKind.RunSequence => "secuencia-" + SafeName.Of(diagram.Subject) + ".md",
            _ => throw new ArgumentOutOfRangeException(nameof(diagram), diagram.Kind, "Unknown kind of diagram."),
        };
    }

    /// <summary>The same name with <paramref name="number"/> before the extension: where «guardar con otro nombre» writes.</summary>
    public static string Numbered(string fileName, int number) =>
        fileName[..^3] + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".md";

    /// <summary>The Markdown of a diagram: a comment that says it is generated, and the text in a <c>mermaid</c> block (it holds no backticks: they are escaped).</summary>
    public static byte[] Markdown(string mermaid)
    {
        ArgumentNullException.ThrowIfNull(mermaid);
        var text = "<!-- Generado por Agent Studio. Editarlo no cambia nada: se pisa al exportar de nuevo. -->\n\n```mermaid\n"
            + mermaid + (mermaid.EndsWith('\n') ? string.Empty : "\n") + "```\n";
        return new UTF8Encoding(false).GetBytes(text);
    }

    /// <summary>Looks at the destination without following a link and without reading the file as anything.</summary>
    public static ExportProbe Probe(string projectRoot, string relativePath)
    {
        var full = Resolve(projectRoot, relativePath);
        FileSystemInfo info = new FileInfo(full);
        if (!info.Exists && !Directory.Exists(full))
        {
            // A link in a folder of the path makes the whole path a link: nothing is written through it.
            return PathHasLink(projectRoot, relativePath) ? new ExportProbe(true, true, null) : ExportProbe.None;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null || PathHasLink(projectRoot, relativePath) || Directory.Exists(full))
        {
            return new ExportProbe(true, true, null);
        }

        var file = (FileInfo)info;
        if (file.Length > MaxProbeBytes)
        {
            return new ExportProbe(true, false, null);
        }

        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            return new ExportProbe(true, false, Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExportProbe(true, false, null);
        }
    }

    /// <summary>
    /// Writes the file: every folder of the path must be a real folder (it is created when it is not there) and the file, when it exists, a plain file. A link, a
    /// junction or a folder where the file goes is an <see cref="IOException"/> and nothing is written. The bytes go to a temporary file in the same folder and are
    /// moved over the real one.
    /// </summary>
    /// <exception cref="IOException">Something on the way is a link, or the destination is not a plain file.</exception>
    public static void Write(string projectRoot, string relativePath, byte[] bytes)
    {
        var full = Resolve(projectRoot, relativePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var current = root;
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (File.Exists(current))
            {
                throw new IOException("A file is where a folder of the export should be.");
            }

            if (Directory.Exists(current))
            {
                var directory = new DirectoryInfo(current);
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || directory.LinkTarget is not null)
                {
                    throw new IOException("A folder of the path of the export is a link: it is not followed.");
                }
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }

        if (Directory.Exists(full))
        {
            throw new IOException("A folder is where the file of the export should be.");
        }

        if (File.Exists(full))
        {
            var existing = new FileInfo(full);
            if (existing.Attributes.HasFlag(FileAttributes.ReparsePoint) || existing.LinkTarget is not null)
            {
                throw new IOException("The file of the export is a link: it is not overwritten.");
            }
        }

        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The SHA-256 of what is written.</summary>
    public static string Sha256(byte[] bytes) => ContentHash.Sha256Hex(bytes);

    private static string Resolve(string projectRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!RepositoryPaths.IsSafeRelative(relativePath))
        {
            throw new ArgumentException("The path of an export is relative to the project and never leads out of it.", nameof(relativePath));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The path of an export leads out of the project.", nameof(relativePath));
        }

        return full;
    }

    private static bool PathHasLink(string projectRoot, string relativePath)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null))
            {
                return true;
            }
        }

        return false;
    }
}
