using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Desktop.Services;

namespace AgentStudio.Tests.Desktop;

public sealed class LayoutStoreTests
{
    private static FileLayoutStore Create(TempDirectory temp) => new(new AppDataPaths(temp.Path));

    [Fact]
    public void GetLayoutPath_IsStableAndUsesTheSha256OfTheNormalizedPath()
    {
        using var temp = new TempDirectory();
        var store = Create(temp);
        var project = temp.CreateDirectory("proj");
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project.ToUpperInvariant())));

        var path = store.GetLayoutPath(project);

        Assert.Equal(Path.Combine(temp.Path, "layouts", expected + ".xml"), path);
        Assert.Equal(path, store.GetLayoutPath(project));
    }

    [Fact]
    public void GetLayoutPath_IgnoresCaseAndTrailingSeparators_ButDistinguishesProjects()
    {
        using var temp = new TempDirectory();
        var store = Create(temp);
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");

        Assert.Equal(store.GetLayoutPath(a), store.GetLayoutPath(a.ToLowerInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(store.GetLayoutPath(a), store.GetLayoutPath(b));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPerProject()
    {
        using var temp = new TempDirectory();
        var store = Create(temp);
        var a = temp.CreateDirectory("a");
        var b = temp.CreateDirectory("b");

        store.Save(a, "<LayoutRoot><A/></LayoutRoot>");
        store.Save(b, "<LayoutRoot><B/></LayoutRoot>");

        Assert.Equal("<LayoutRoot><A/></LayoutRoot>", store.Load(a));
        Assert.Equal("<LayoutRoot><B/></LayoutRoot>", store.Load(b));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "layouts"), "*.tmp"));
    }

    [Fact]
    public void Load_WithoutSavedLayout_ReturnsNull()
    {
        using var temp = new TempDirectory();

        Assert.Null(Create(temp).Load(temp.CreateDirectory("a")));
    }

    [Theory]
    [InlineData("<LayoutRoot><unclosed>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    public void Load_CorruptXml_IsIgnored(string content)
    {
        using var temp = new TempDirectory();
        var store = Create(temp);
        var project = temp.CreateDirectory("a");
        Directory.CreateDirectory(Path.Combine(temp.Path, "layouts"));
        File.WriteAllText(store.GetLayoutPath(project), content);

        Assert.Null(store.Load(project));
    }
}
