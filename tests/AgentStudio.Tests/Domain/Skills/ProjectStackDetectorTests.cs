using AgentStudio.Domain.Skills;

namespace AgentStudio.Tests.Domain.Skills;

/// <summary>
/// docs/14 D-28: the context of the deterministic recommender comes from the names of the files the explorer indexed,
/// never from their content. Each rule of the table gives its tags; anything else gives none.
/// </summary>
public sealed class ProjectStackDetectorTests
{
    [Theory]
    [InlineData("src/App/App.csproj", "csharp dotnet")]
    [InlineData("AgentStudio.sln", "csharp dotnet")]
    [InlineData("AgentStudio.slnx", "csharp dotnet")]
    [InlineData("web/package.json", "node")]
    [InlineData("pyproject.toml", "python")]
    [InlineData("tools/requirements.txt", "python")]
    [InlineData("db/migrations/0001.sql", "sql")]
    [InlineData("Dockerfile", "docker")]
    [InlineData("infra/main.tf", "terraform")]
    [InlineData("reports/Ventas.pbip", "power-bi")]
    [InlineData("model/tables/Ventas.tmdl", "power-bi")]
    public void Each_rule_of_the_table_gives_its_tags(string path, string expected)
    {
        Assert.Equal(expected.Split(' '), ProjectStackDetector.Detect([path]));
    }

    [Theory]
    [InlineData("SRC/APP.CSPROJ", "csharp dotnet")]
    [InlineData("dockerfile", "docker")]
    [InlineData("Package.JSON", "node")]
    [InlineData(@"src\App\App.csproj", "csharp dotnet")]
    public void File_names_match_whatever_their_case_and_separators(string path, string expected)
    {
        Assert.Equal(expected.Split(' '), ProjectStackDetector.Detect([path]));
    }

    [Theory]
    [InlineData("docs/package.json.md")]
    [InlineData("Dockerfile.dev")]
    [InlineData("notes.sql.txt")]
    [InlineData("csproj")]
    [InlineData("src/terraform/readme.md")]
    [InlineData("python/notes.md")]
    [InlineData("README.md")]
    public void Only_the_file_name_counts_and_it_must_match_a_rule_exactly(string path)
    {
        Assert.Empty(ProjectStackDetector.Detect([path]));
    }

    [Fact]
    public void A_project_with_several_stacks_gets_every_tag_once_in_a_stable_order()
    {
        var tags = ProjectStackDetector.Detect(["Dockerfile", "src/A/A.csproj", "src/B/B.csproj", "web/package.json", "db/x.sql", "db/y.sql"]);

        Assert.Equal(["csharp", "docker", "dotnet", "node", "sql"], tags);
    }

    [Fact]
    public void The_paths_are_only_names_so_files_that_do_not_exist_are_read_the_same()
    {
        // The detector never touches the disk: a path that exists nowhere is just a name.
        var nowhere = $"Z:/{Guid.NewGuid():N}/missing.csproj";

        Assert.Equal(["csharp", "dotnet"], ProjectStackDetector.Detect([nowhere]));
    }

    [Fact]
    public void Blank_entries_are_skipped_and_no_paths_give_no_tags()
    {
        Assert.Empty(ProjectStackDetector.Detect([]));
        Assert.Empty(ProjectStackDetector.Detect(["", "  ", "src/"]));
        Assert.Throws<ArgumentNullException>(() => ProjectStackDetector.Detect(null!));
    }
}
