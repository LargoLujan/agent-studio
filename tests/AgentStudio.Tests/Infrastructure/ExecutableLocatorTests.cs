using System.IO;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Tests.Infrastructure;

public sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "as-locator-" + Guid.NewGuid().ToString("N"));

    public ExecutableLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Touch(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, "");
        return path;
    }

    private static ExecutableLocator Locator(string path, string? pathExt = ".COM;.EXE;.BAT;.CMD") => new(() => path, () => pathExt);

    [Fact]
    public void Honours_PATH_order()
    {
        Assert.True(OperatingSystem.IsWindows());
        var a = MakeDir("a");
        var b = MakeDir("b");
        Touch(a, "tool.exe");
        Touch(b, "tool.exe");

        var found = Locator(b + ";" + a).Find("tool");

        Assert.Equal(Path.Combine(b, "tool.exe"), found, ignoreCase: true);
    }

    [Fact]
    public void Honours_PATHEXT_order_within_a_directory()
    {
        var a = MakeDir("a");
        Touch(a, "tool.cmd");
        Touch(a, "tool.exe");

        Assert.Equal(Path.Combine(a, "tool.exe"), Locator(a).Find("tool"), ignoreCase: true);
        Assert.Equal(Path.Combine(a, "tool.cmd"), Locator(a, ".CMD;.EXE").Find("tool"), ignoreCase: true);
    }

    [Fact]
    public void Finds_a_command_that_already_has_an_extension()
    {
        var a = MakeDir("a");
        var expected = Touch(a, "codex.cmd");

        Assert.Equal(expected, Locator(a).Find("codex.cmd"), ignoreCase: true);
    }

    [Fact]
    public void Returns_null_when_not_found()
    {
        var a = MakeDir("a");

        Assert.Null(Locator(a).Find("nothing-here"));
    }

    [Fact]
    public void Ignores_empty_quoted_and_invalid_entries()
    {
        var a = MakeDir("a");
        var expected = Touch(a, "tool.exe");
        var path = $";;\"{Path.Combine(_root, "missing")}\";<>|invalid;\"{a}\"";

        Assert.Equal(expected, Locator(path).Find("tool"), ignoreCase: true);
    }

    [Fact]
    public void Uses_default_PATHEXT_when_it_is_not_set()
    {
        var a = MakeDir("a");
        var expected = Touch(a, "tool.bat");

        Assert.Equal(expected, Locator(a, pathExt: null).Find("tool"), ignoreCase: true);
    }

    [Fact]
    public void Ignores_relative_PATH_entries_such_as_the_current_directory()
    {
        // A tool planted in the process working directory must not be found through "." or a relative entry.
        var name = "as-planted-" + Guid.NewGuid().ToString("N");
        var planted = Path.Combine(Directory.GetCurrentDirectory(), name + ".cmd");
        File.WriteAllText(planted, "");
        try
        {
            Assert.Null(Locator(".").Find(name));
            Assert.Null(Locator("." + Path.DirectorySeparatorChar).Find(name));
            Assert.Null(Locator(Path.GetFileName(Directory.GetCurrentDirectory())).Find(name));
            Assert.Null(Locator(".." + Path.PathSeparator + ".").Find(name));
        }
        finally
        {
            File.Delete(planted);
        }
    }

    [Fact]
    public void Returns_an_absolute_path_skipping_relative_entries_before_it()
    {
        var a = MakeDir("a");
        var expected = Touch(a, "tool.exe");

        var found = Locator(".;bin;" + a).Find("tool");

        Assert.Equal(expected, found, ignoreCase: true);
        Assert.True(Path.IsPathFullyQualified(found!));
    }

    [Fact]
    public void Only_exe_com_cmd_and_bat_extensions_from_PATHEXT_are_used()
    {
        var a = MakeDir("a");
        Touch(a, "tool.js");
        Touch(a, "tool.vbs");

        Assert.Null(Locator(a, ".JS;.VBS").Find("tool"));

        var expected = Touch(a, "tool.bat");
        Assert.Equal(expected, Locator(a, ".JS;.vbs;.bat").Find("tool"), ignoreCase: true);
    }

    [Fact]
    public void Returns_null_when_PATH_is_empty_or_missing()
    {
        Assert.Null(new ExecutableLocator(() => null, () => null).Find("tool"));
        Assert.Null(new ExecutableLocator(() => "", () => null).Find("tool"));
    }
}
