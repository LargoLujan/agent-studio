using System.IO;
using System.Text;

namespace AgentStudio.Desktop.Services;

/// <summary>Writes a file so readers never observe a half-written content: temp file in the same folder, then replace.</summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    internal static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("The path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, content, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file is harmless.
        }
    }
}
