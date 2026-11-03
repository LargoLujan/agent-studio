using AgentStudio.Desktop.Controls.Editor;

namespace AgentStudio.Tests.Desktop.Editor;

public sealed class LanguageMapTests
{
    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("tool.py", "python")]
    [InlineData("query.sql", "sql")]
    [InlineData("appsettings.json", "json")]
    [InlineData("ci.yaml", "yaml")]
    [InlineData("ci.yml", "yaml")]
    [InlineData("README.md", "markdown")]
    [InlineData("App.xaml", "xml")]
    [InlineData("data.xml", "xml")]
    [InlineData("AgentStudio.Desktop.csproj", "xml")]
    [InlineData("Directory.Build.props", "xml")]
    [InlineData("run.ps1", "powershell")]
    [InlineData("app.ts", "typescript")]
    [InlineData("editor.js", "javascript")]
    [InlineData("index.html", "html")]
    [InlineData("site.css", "css")]
    [InlineData("notes.txt", "plaintext")]
    [InlineData("Makefile", "plaintext")]
    [InlineData("archive.tar.gz", "plaintext")]
    public void Extension_maps_to_the_monaco_language(string path, string expected)
    {
        Assert.Equal(expected, LanguageMap.FromPath(path));
    }

    [Fact]
    public void Lookup_ignores_case_and_directories()
    {
        Assert.Equal("csharp", LanguageMap.FromPath(@"C:\src\FOO.CS"));
        Assert.Equal("yaml", LanguageMap.FromExtension("YML"));
        Assert.Equal("yaml", LanguageMap.FromExtension(".yml"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_path_is_plain_text(string? path)
    {
        Assert.Equal("plaintext", LanguageMap.FromPath(path));
        Assert.Equal("plaintext", LanguageMap.FromExtension(path));
    }
}
