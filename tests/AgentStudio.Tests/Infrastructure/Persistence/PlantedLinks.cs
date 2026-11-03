using System.Diagnostics;
using System.IO;

namespace AgentStudio.Tests.Infrastructure.Persistence;

/// <summary>
/// Links left in a project before Agent Studio writes there (ADR-0018: in the model), made with <c>mklink</c>: a
/// directory junction and a hard link need no privilege. Disposing removes them first and as links, so the cleanup never
/// walks into what they point to.
/// </summary>
internal sealed class PlantedLinks : IDisposable
{
    private readonly List<string> _links = [];

    /// <summary>A directory junction made by <c>mklink /J</c>.</summary>
    public void Junction(string link, string target)
    {
        Mklink("/J", link, target);
        _links.Add(link);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>A second name for <paramref name="target"/> made by <c>mklink /H</c>: a hard link is no reparse point.</summary>
    public void HardLink(string link, string target)
    {
        Mklink("/H", link, target);
        _links.Add(link);
        Assert.False(new FileInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public void Dispose()
    {
        foreach (var link in _links.Where(l => Directory.Exists(l) || File.Exists(l)))
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }
        }
    }

    private static void Mklink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", kind, link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink {kind} failed: {error}");
    }
}
