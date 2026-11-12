using AgentStudio.Domain.Environments;

namespace AgentStudio.Tests.Domain.Environments;

/// <summary>
/// docs/20 D-05, D-06, D-07 and D-09 (P0 of Phase 6): what a project declares and what is installed, as values with their invariants. What a project
/// declares came from a file of a repository and is data; what is installed came from the tools of the computer.
/// </summary>
public sealed class DeclaredAndInstalledTests
{
    private static readonly DeclaredSource GlobalJson = new(DeclaredFiles.GlobalJson, 3);

    private static ToolVersion V(string text) => ToolVersion.Parse(text);

    // ---- the closed list of files ----

    [Fact]
    public void Only_the_files_of_the_closed_list_are_read_and_all_of_them_are_safe_relative_paths()
    {
        Assert.Equal(15, DeclaredFiles.All.Count);
        Assert.Equal(DeclaredFiles.All.Count, DeclaredFiles.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(DeclaredFiles.All, file => Assert.True(AgentStudio.Domain.Common.RepositoryPaths.IsSafeRelative(file), file));
        Assert.Equal(["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"], DeclaredFiles.ComposeFiles);
        Assert.All(DeclaredFiles.ComposeFiles, file => Assert.True(DeclaredFiles.IsDeclared(file)));
    }

    [Theory]
    [InlineData("global.json", true)]
    [InlineData(".config/dotnet-tools.json", true)]
    [InlineData(".venv/pyvenv.cfg", true)]
    [InlineData("Dockerfile", true)]
    [InlineData("GLOBAL.JSON", false)]
    [InlineData("sub/global.json", false)]
    [InlineData("../global.json", false)]
    [InlineData(".env", false)]
    [InlineData("package-lock.json", false)]
    [InlineData("global.json\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_file_is_read_only_if_it_is_exactly_one_of_the_list_and_never_in_a_subfolder(string? path, bool declared)
    {
        Assert.Equal(declared, DeclaredFiles.IsDeclared(path));
    }

    [Fact]
    public void A_source_is_a_file_of_the_list_and_a_line_from_one()
    {
        Assert.Equal(3, GlobalJson.Line);
        Assert.Null(new DeclaredSource(DeclaredFiles.Dockerfile).Line);
        Assert.Throws<ArgumentException>(() => new DeclaredSource("src/secret.json"));
        Assert.Throws<ArgumentException>(() => new DeclaredSource("../global.json"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeclaredSource(DeclaredFiles.GlobalJson, 0));
    }

    // ---- entries and issues ----

    [Fact]
    public void An_entry_is_data_of_one_line_with_a_cap_of_two_hundred_characters()
    {
        var entry = new DeclaredEntry(DeclaredKind.DotNetSdkVersion, null, "10.0.303", GlobalJson);

        Assert.Equal("10.0.303", entry.Value);
        Assert.Equal(DeclaredEntry.MaxValueLength, new DeclaredEntry(DeclaredKind.NodeEngine, null, new string('x', 200), GlobalJson).Value.Length);
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.NodeEngine, null, new string('x', 201), GlobalJson));
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.NodeEngine, null, string.Empty, GlobalJson));
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.NodeEngine, null, ">=20\nrm -rf /", GlobalJson));
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.DotNetLocalTool, "bad\nname", "1.0.0", GlobalJson));
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.DotNetLocalTool, string.Empty, "1.0.0", GlobalJson));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeclaredEntry((DeclaredKind)99, null, "x", GlobalJson));
        Assert.Throws<ArgumentNullException>(() => new DeclaredEntry(DeclaredKind.NodeEngine, null, "x", null!));
    }

    [Theory]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2066b")]
    [InlineData("a\u200Bb")]
    [InlineData("a\uFEFFb")]
    public void A_hidden_character_is_refused_in_a_value_a_name_and_an_issue_text(string text)
    {
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.NodeEngine, null, text, GlobalJson));
        Assert.Throws<ArgumentException>(() => new DeclaredEntry(DeclaredKind.DotNetLocalTool, text, "1.0.0", GlobalJson));
        Assert.Throws<ArgumentException>(() => new DeclaredIssue(GlobalJson, DeclaredIssueKind.NotInterpreted, text));
        Assert.Throws<ArgumentException>(() => new InstalledRuntime(text, V("1.0")));
        Assert.Throws<ArgumentException>(() => new WslStatus(WslState.Ready, text));
        Assert.Throws<ArgumentException>(() => new InstalledTool(V("1.0"), text));
    }

    [Fact]
    public void An_issue_says_where_and_why_and_may_show_what_was_there_as_data()
    {
        var issue = new DeclaredIssue(GlobalJson, DeclaredIssueKind.NotInterpreted, "lts/*");

        Assert.Equal("lts/*", issue.Text);
        Assert.Null(new DeclaredIssue(GlobalJson, DeclaredIssueKind.TooLarge).Text);
        Assert.Throws<ArgumentException>(() => new DeclaredIssue(GlobalJson, DeclaredIssueKind.NotInterpreted, "a\nb"));
        Assert.Throws<ArgumentException>(() => new DeclaredIssue(GlobalJson, DeclaredIssueKind.NotInterpreted, new string('x', 201)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeclaredIssue(GlobalJson, (DeclaredIssueKind)99));
    }

    [Fact]
    public void The_kinds_of_issue_are_the_rules_of_reading_of_the_design()
    {
        Assert.Equal(["TooLarge", "IsLink", "NotInterpreted", "Malformed", "TooDeep", "DuplicateKey", "LineTooLong", "Unreadable"], Enum.GetNames<DeclaredIssueKind>());
    }

    // ---- the declared environment ----

    [Fact]
    public void A_project_that_declares_nothing_has_no_entries_no_issues_and_no_files()
    {
        Assert.Empty(DeclaredEnvironment.Empty.Entries);
        Assert.Empty(DeclaredEnvironment.Empty.Issues);
        Assert.Empty(DeclaredEnvironment.Empty.FilesFound);
        Assert.False(DeclaredEnvironment.Empty.DeclaresContainers);
    }

    [Fact]
    public void The_files_found_are_of_the_list_and_each_once()
    {
        Assert.Throws<ArgumentException>(() => new DeclaredEnvironment([], [], ["notes.txt"]));
        Assert.Throws<ArgumentException>(() => new DeclaredEnvironment([], [], [DeclaredFiles.GlobalJson, DeclaredFiles.GlobalJson]));
        Assert.Equal([DeclaredFiles.GlobalJson], new DeclaredEnvironment([], [], [DeclaredFiles.GlobalJson]).FilesFound);
    }

    [Fact]
    public void An_environment_holds_up_to_five_hundred_entries_and_issues_none_of_them_null()
    {
        var entry = new DeclaredEntry(DeclaredKind.NodeVersion, null, "22", new DeclaredSource(DeclaredFiles.NvmRc, 1));
        var issue = new DeclaredIssue(GlobalJson, DeclaredIssueKind.TooLarge);

        Assert.Equal(DeclaredEnvironment.MaxItems, new DeclaredEnvironment([.. Enumerable.Repeat(entry, DeclaredEnvironment.MaxItems)], [], []).Entries.Count);
        Assert.Throws<ArgumentException>(() => new DeclaredEnvironment([.. Enumerable.Repeat(entry, DeclaredEnvironment.MaxItems + 1)], [], []));
        Assert.Throws<ArgumentException>(() => new DeclaredEnvironment([], [.. Enumerable.Repeat(issue, DeclaredEnvironment.MaxItems + 1)], []));
        Assert.Throws<ArgumentException>(() => new DeclaredEnvironment([null!], [], []));
    }

    [Fact]
    public void An_environment_is_copied_filtered_by_kind_and_compared_by_what_it_holds()
    {
        var sdk = new DeclaredEntry(DeclaredKind.DotNetSdkVersion, null, "10.0.303", GlobalJson);
        var roll = new DeclaredEntry(DeclaredKind.DotNetRollForward, null, "latestFeature", new DeclaredSource(DeclaredFiles.GlobalJson, 4));
        var list = new List<DeclaredEntry> { sdk };

        var environment = new DeclaredEnvironment(list, [], [DeclaredFiles.GlobalJson]);
        list.Add(roll);

        Assert.Single(environment.Entries);
        Assert.Equal([sdk], environment.Of(DeclaredKind.DotNetSdkVersion));
        Assert.Empty(environment.Of(DeclaredKind.NodeVersion));
        Assert.Equal(environment, new DeclaredEnvironment([sdk], [], [DeclaredFiles.GlobalJson]));
        Assert.NotEqual(environment, new DeclaredEnvironment([sdk, roll], [], [DeclaredFiles.GlobalJson]));
    }

    [Theory]
    [InlineData(DeclaredFiles.DevContainerFolder, true)]
    [InlineData(DeclaredFiles.DevContainerRoot, true)]
    [InlineData(DeclaredFiles.Dockerfile, true)]
    [InlineData(DeclaredFiles.ComposeYaml, true)]
    [InlineData(DeclaredFiles.DockerComposeYml, true)]
    [InlineData(DeclaredFiles.GlobalJson, false)]
    [InlineData(DeclaredFiles.PackageJson, false)]
    public void Docker_matters_when_the_project_declares_a_container_and_only_then(string file, bool declares)
    {
        Assert.Equal(declares, new DeclaredEnvironment([], [], [file]).DeclaresContainers);
    }

    // ---- what is installed ----

    [Fact]
    public void Docker_has_three_states_and_each_goes_with_its_versions_and_no_others()
    {
        Assert.Equal(["NotInstalled", "EngineStopped", "Running"], Enum.GetNames<DockerState>());
        Assert.Equal(DockerState.NotInstalled, DockerStatus.NotInstalled.State);
        Assert.Equal(DockerState.EngineStopped, new DockerStatus(DockerState.EngineStopped, V("27.3.1"), null).State);
        Assert.Equal(V("27.3.1"), new DockerStatus(DockerState.Running, V("27.3.1"), V("27.3.1")).ServerVersion);
        Assert.Throws<ArgumentException>(() => new DockerStatus(DockerState.NotInstalled, V("27.3.1"), null));
        Assert.Throws<ArgumentException>(() => new DockerStatus(DockerState.EngineStopped, null, null));
        Assert.Throws<ArgumentException>(() => new DockerStatus(DockerState.EngineStopped, V("27.3.1"), V("27.3.1")));
        Assert.Throws<ArgumentException>(() => new DockerStatus(DockerState.Running, V("27.3.1"), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DockerStatus((DockerState)99, null, null));
    }

    [Fact]
    public void Wsl_has_three_states_and_only_a_ready_one_names_its_distribution()
    {
        Assert.Equal(["NotInstalled", "NoDistribution", "Ready"], Enum.GetNames<WslState>());
        Assert.Equal("Ubuntu", new WslStatus(WslState.Ready, "Ubuntu").DefaultDistribution);
        Assert.Null(WslStatus.NotInstalled.DefaultDistribution);
        Assert.Null(new WslStatus(WslState.NoDistribution, null).DefaultDistribution);
        Assert.Throws<ArgumentException>(() => new WslStatus(WslState.Ready, null));
        Assert.Throws<ArgumentException>(() => new WslStatus(WslState.NoDistribution, "Ubuntu"));
        Assert.Throws<ArgumentException>(() => new WslStatus(WslState.Ready, "Ubu\nntu"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WslStatus((WslState)99, null));
    }

    [Fact]
    public void An_installed_tool_has_a_version_and_a_path_of_one_line_or_none()
    {
        Assert.Equal(@"C:\Program Files\dotnet\sdk", new InstalledSdk(V("10.0.303"), @"C:\Program Files\dotnet\sdk").Path);
        Assert.Null(new InstalledTool(V("22.11.0"), null).Path);
        Assert.Throws<ArgumentException>(() => new InstalledTool(V("22.11.0"), "C:\\x\ny"));
        Assert.Throws<ArgumentException>(() => new InstalledSdk(V("10.0.303"), new string('x', 521)));
        Assert.Throws<ArgumentNullException>(() => new InstalledTool(null!, null));
        Assert.Equal("Microsoft.NETCore.App", new InstalledRuntime("Microsoft.NETCore.App", V("10.0.7")).Name);
        Assert.Throws<ArgumentException>(() => new InstalledRuntime(" Microsoft.NETCore.App", V("10.0.7")));
    }

    [Fact]
    public void An_inventory_is_sorted_by_version_and_a_computer_with_nothing_has_nothing()
    {
        var sdks = new InstalledSdk[] { new(V("10.0.303"), null), new(V("8.0.319"), null), new(V("11.0.100-preview.2.26159.112"), null) };

        var inventory = new InstalledTools(sdks, [], null, null, null, false, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null);

        Assert.Equal(["8.0.319", "10.0.303", "11.0.100-preview.2.26159.112"], inventory.DotNetSdks.Select(s => s.Version.Text));
        Assert.Empty(InstalledTools.None.DotNetSdks);
        Assert.Null(InstalledTools.None.Node);
        Assert.Equal(DockerState.NotInstalled, InstalledTools.None.Docker.State);
        Assert.Equal(InstalledTools.None, new InstalledTools([], [], null, null, null, false, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null));
        Assert.NotEqual(InstalledTools.None, inventory);
    }

    [Fact]
    public void The_alias_of_the_store_is_not_a_python_that_counts_as_installed()
    {
        var python = new InstalledTool(V("3.12.4"), null);

        Assert.Throws<ArgumentException>(() => new InstalledTools([], [], null, null, python, true, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null));
        var alias = new InstalledTools([], [], null, null, null, true, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null);
        Assert.True(alias.PythonIsStoreAlias);
        Assert.Null(alias.Python);
    }

    [Fact]
    public void An_inventory_holds_no_null_and_no_more_than_its_caps()
    {
        Assert.Throws<ArgumentNullException>(() => new InstalledTools(null!, [], null, null, null, false, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null));
        Assert.Throws<ArgumentNullException>(() => new InstalledTools([], [], null, null, null, false, null, null!, WslStatus.NotInstalled, null));
        Assert.Throws<ArgumentException>(() => new InstalledTools([null!], [], null, null, null, false, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null));
        var many = Enumerable.Range(1, 201).Select(i => new InstalledSdk(V($"1.0.{i}"), null)).ToList();
        Assert.Throws<ArgumentException>(() => new InstalledTools(many, [], null, null, null, false, null, DockerStatus.NotInstalled, WslStatus.NotInstalled, null));
    }
}
