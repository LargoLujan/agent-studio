using AgentStudio.Domain.Environments;

namespace AgentStudio.Tests.Domain.Environments;

/// <summary>
/// docs/20 D-05 and D-06 (WP-E): what each manifest of the closed list yields, as pure code, and what a hostile text cannot do. The text of a file is data:
/// no parser throws, a value is one clean line of at most 200 characters, and what is not understood is an issue with its line.
/// </summary>
public sealed class DeclaredFileParserTests
{
    private static DeclaredEnvironment Read(string file, string text)
    {
        var builder = new DeclaredReadingBuilder();
        builder.Found(file);
        DeclaredFileParser.Parse(file, text, builder);
        return builder.Build();
    }

    private static string Value(DeclaredEnvironment environment, DeclaredKind kind) => Assert.Single(environment.Of(kind)).Value;

    // ---- global.json ----

    [Fact]
    public void Global_json_gives_the_version_the_policy_the_prerelease_flag_and_each_path_with_its_line_and_admits_comments()
    {
        var environment = Read(DeclaredFiles.GlobalJson, "{\r\n  // the SDK\r\n  \"sdk\": {\r\n    \"version\": \"10.0.303\", /* c */\r\n    \"rollForward\": \"latestFeature\",\r\n    \"allowPrerelease\": false,\r\n    \"paths\": [\".dotnet\", \"$host$\"],\r\n  }\r\n}");

        Assert.Empty(environment.Issues);
        var version = Assert.Single(environment.Of(DeclaredKind.DotNetSdkVersion));
        Assert.Equal("10.0.303", version.Value);
        Assert.Equal(4, version.Source.Line);
        Assert.Equal("latestFeature", Value(environment, DeclaredKind.DotNetRollForward));
        Assert.Equal("false", Value(environment, DeclaredKind.DotNetAllowPrerelease));
        Assert.Equal([".dotnet", "$host$"], environment.Of(DeclaredKind.DotNetSdkPath).Select(e => e.Value));
    }

    [Theory]
    [InlineData("{ \"sdk\": { \"version\": 10 } }")]
    [InlineData("{ \"sdk\": [] }")]
    [InlineData("{ \"sdk\": { \"allowPrerelease\": \"no\" } }")]
    [InlineData("{ \"sdk\": { \"paths\": \".dotnet\" } }")]
    [InlineData("{ \"sdk\": { \"paths\": [1] } }")]
    public void A_field_of_another_type_is_not_interpreted_and_no_entry_is_invented(string text)
    {
        var environment = Read(DeclaredFiles.GlobalJson, text);

        Assert.Empty(environment.Entries);
        Assert.All(environment.Issues, issue => Assert.Equal(DeclaredIssueKind.NotInterpreted, issue.Kind));
        Assert.NotEmpty(environment.Issues);
    }

    [Theory]
    [InlineData("", DeclaredIssueKind.Malformed)]
    [InlineData("[]", DeclaredIssueKind.Malformed)]
    [InlineData("42", DeclaredIssueKind.Malformed)]
    [InlineData("{ \"sdk\": ", DeclaredIssueKind.Malformed)]
    [InlineData("{ \"sdk\": {} } trailing", DeclaredIssueKind.Malformed)]
    [InlineData("{ \"sdk\": {}, \"sdk\": {} }", DeclaredIssueKind.DuplicateKey)]
    [InlineData("{ \"sdk\": { \"version\": \"1.0.0\", \"version\": \"2.0.0\" } }", DeclaredIssueKind.DuplicateKey)]
    public void Invalid_json_and_repeated_keys_are_one_issue_and_nothing_is_read(string text, DeclaredIssueKind kind)
    {
        var environment = Read(DeclaredFiles.GlobalJson, text);

        Assert.Empty(environment.Entries);
        var issue = Assert.Single(environment.Issues);
        Assert.Equal(kind, issue.Kind);
    }

    [Fact]
    public void Json_nested_deeper_than_32_levels_is_refused_and_one_of_32_is_read()
    {
        static string Nested(int levels) => string.Concat(Enumerable.Repeat("{\"a\":", levels - 1)) + "{\"sdk\":{\"version\":\"1.0.0\"}}" + new string('}', levels - 1);

        var deep = Read(DeclaredFiles.GlobalJson, Nested(40));
        var limit = Read(DeclaredFiles.GlobalJson, "{\"sdk\":{\"version\":\"1.0.0\"},\"x\":" + string.Concat(Enumerable.Repeat("[", 30)) + new string(']', 30) + "}");
        var over = Read(DeclaredFiles.GlobalJson, "{\"x\":" + string.Concat(Enumerable.Repeat("[", 32)) + new string(']', 32) + "}");

        Assert.Equal(DeclaredIssueKind.TooDeep, Assert.Single(deep.Issues).Kind);
        Assert.Empty(deep.Entries);
        Assert.Empty(limit.Issues);
        Assert.Equal("1.0.0", Value(limit, DeclaredKind.DotNetSdkVersion));
        Assert.Equal(DeclaredIssueKind.TooDeep, Assert.Single(over.Issues).Kind);
    }

    [Fact]
    public void A_json_that_nests_a_million_levels_does_not_overflow_the_stack_or_throw()
    {
        var text = new string('[', 200_000);

        var environment = Read(DeclaredFiles.GlobalJson, "{\"x\":" + text);

        Assert.Single(environment.Issues);
    }

    [Fact]
    public void A_value_is_one_clean_line_cut_at_200_characters_and_never_makes_the_reading_throw()
    {
        var hostile = "10.0\u202E.303\n" + new string('9', 500);

        var environment = Read(DeclaredFiles.GlobalJson, "{\"sdk\":{\"version\":" + System.Text.Json.JsonSerializer.Serialize(hostile) + ",\"rollForward\":\"\"}}");

        var version = Assert.Single(environment.Of(DeclaredKind.DotNetSdkVersion));
        Assert.True(version.Value.Length <= DeclaredEntry.MaxValueLength);
        Assert.DoesNotContain('\u202E', version.Value);
        Assert.DoesNotContain('\n', version.Value);
        Assert.EndsWith("…", version.Value, StringComparison.Ordinal);
        // An empty value is not an entry: it is an issue.
        Assert.Empty(environment.Of(DeclaredKind.DotNetRollForward));
        Assert.Contains(environment.Issues, i => i.Kind == DeclaredIssueKind.NotInterpreted);
    }

    [Fact]
    public void A_manifest_with_more_than_500_things_is_cut_and_the_environment_is_still_valid()
    {
        var paths = string.Join(",", Enumerable.Range(0, 800).Select(i => $"\"p{i}\""));

        var environment = Read(DeclaredFiles.GlobalJson, "{\"sdk\":{\"paths\":[" + paths + "]}}");

        Assert.Equal(DeclaredEnvironment.MaxItems, environment.Entries.Count);
    }

    // ---- other JSON ----

    [Fact]
    public void Dotnet_tools_gives_the_package_and_version_of_each_local_tool()
    {
        var environment = Read(DeclaredFiles.DotNetTools, "{\"version\":1,\"isRoot\":true,\"tools\":{\n\"dotnet-ef\":{\"version\":\"10.0.0\",\"commands\":[\"dotnet-ef\"]},\n\"Bad\":{\"version\":7}}}");

        var tool = Assert.Single(environment.Of(DeclaredKind.DotNetLocalTool));
        Assert.Equal(("dotnet-ef", "10.0.0"), (tool.Name, tool.Value));
        var issue = Assert.Single(environment.Issues);
        Assert.Equal(DeclaredIssueKind.NotInterpreted, issue.Kind);
        Assert.Equal(3, issue.Source.Line);
        Assert.Equal("Bad", issue.Text);
    }

    [Fact]
    public void Dotnet_tools_does_not_admit_comments_because_its_format_does_not()
    {
        var environment = Read(DeclaredFiles.DotNetTools, "{ // no\n \"tools\": {} }");

        Assert.Equal(DeclaredIssueKind.Malformed, Assert.Single(environment.Issues).Kind);
    }

    [Fact]
    public void Package_json_gives_only_the_engines_and_ignores_everything_else()
    {
        var environment = Read(DeclaredFiles.PackageJson, "{\"name\":\"x\",\"scripts\":{\"postinstall\":\"evil\"},\"engines\":{\"node\":\">=22.11 <23\",\"npm\":\"^10\",\"vscode\":\"1\"}}");

        Assert.Equal(2, environment.Entries.Count);
        Assert.Equal(">=22.11 <23", Value(environment, DeclaredKind.NodeEngine));
        Assert.Equal("^10", Value(environment, DeclaredKind.NpmEngine));
    }

    [Fact]
    public void Package_json_with_engines_as_an_array_is_not_interpreted()
    {
        var environment = Read(DeclaredFiles.PackageJson, "{\"engines\":[\"node >= 4\"]}");

        Assert.Empty(environment.Entries);
        Assert.Equal(DeclaredIssueKind.NotInterpreted, Assert.Single(environment.Issues).Kind);
    }

    [Fact]
    public void A_dev_container_names_what_it_references_and_never_opens_it()
    {
        const string text = """
            {
              // a dev container
              "image": "mcr.microsoft.com/devcontainers/dotnet:10.0",
              "build": { "dockerfile": "../Dockerfile.dev" },
              "dockerComposeFile": ["../compose.yaml", "extra.yml"],
              "extends": "../base/devcontainer.json",
              "features": { "ghcr.io/devcontainers/features/node:1": {}, "ghcr.io/devcontainers/features/git:1": { "version": "latest" } },
            }
            """;

        var environment = Read(DeclaredFiles.DevContainerFolder, text);

        Assert.Empty(environment.Issues);
        Assert.Equal("mcr.microsoft.com/devcontainers/dotnet:10.0", Value(environment, DeclaredKind.ContainerImage));
        Assert.Equal("../Dockerfile.dev", Value(environment, DeclaredKind.DevContainerDockerfile));
        Assert.Equal(["../compose.yaml", "extra.yml"], environment.Of(DeclaredKind.DevContainerComposeFile).Select(e => e.Value));
        Assert.Equal("../base/devcontainer.json", Value(environment, DeclaredKind.Extends));
        Assert.Equal(2, environment.Of(DeclaredKind.DevContainerFeature).Count());
        Assert.True(environment.DeclaresContainers);
    }

    // ---- text ----

    [Theory]
    [InlineData("v22.11.0\n", "v22.11.0")]
    [InlineData("lts/*\r\nsecond line", "lts/*")]
    [InlineData("\uFEFF  22  \r", "22")]
    public void The_first_line_of_nvmrc_is_the_version_as_written(string text, string expected)
    {
        Assert.Equal(expected, Value(Read(DeclaredFiles.NvmRc, text), DeclaredKind.NodeVersion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("   ")]
    public void An_empty_version_file_is_not_interpreted(string text)
    {
        var environment = Read(DeclaredFiles.PythonVersionFile, text);

        Assert.Empty(environment.Entries);
        Assert.Equal(DeclaredIssueKind.NotInterpreted, Assert.Single(environment.Issues).Kind);
    }

    [Fact]
    public void A_line_of_more_than_4096_characters_cuts_the_reading_and_is_reported_once()
    {
        var environment = Read(DeclaredFiles.NodeVersionFile, new string('1', DeclaredFileParser.MaxLineLength + 1));

        Assert.Empty(environment.Entries);
        var issue = Assert.Single(environment.Issues);
        Assert.Equal(DeclaredIssueKind.LineTooLong, issue.Kind);
        Assert.Equal(1, issue.Source.Line);
    }

    [Fact]
    public void Pyvenv_cfg_gives_the_version_line_and_flags_what_is_not_key_value()
    {
        var environment = Read(DeclaredFiles.PyVenvCfg, "home = C:\\Python312\ninclude-system-site-packages = false\nversion = 3.12.5\nversion = 9.9\nnonsense\n");

        Assert.Equal("3.12.5", Value(environment, DeclaredKind.VenvVersion));
        var issue = Assert.Single(environment.Issues);
        Assert.Equal(5, issue.Source.Line);
        Assert.Equal("nonsense", issue.Text);
    }

    [Fact]
    public void Dockerfile_gives_each_from_with_its_flags_aliases_and_continuations_handled()
    {
        const string text = "# FROM commented:1\nARG BASE=x\nFROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build\nRUN echo \\\n FROM not-a-from\nfrom ${BASE}\nFROM\nCOPY . .\n";

        var environment = Read(DeclaredFiles.Dockerfile, text);

        var images = environment.Of(DeclaredKind.ContainerImage).ToList();
        Assert.Equal(["mcr.microsoft.com/dotnet/sdk:10.0", "${BASE}"], images.Select(i => i.Value));
        Assert.Equal("build", images[0].Name);
        Assert.Equal(3, images[0].Source.Line);
        Assert.Equal(DeclaredIssueKind.NotInterpreted, Assert.Single(environment.Issues).Kind);
    }

    // ---- pyproject.toml ----

    [Theory]
    [InlineData("[project]\nname = \"x\"\nrequires-python = \">=3.9\"  # comment\n", ">=3.9")]
    [InlineData("[project]\nrequires-python = '~=3.10'\n", "~=3.10")]
    [InlineData("[ project ]\n\"requires-python\" = \">=3.8,<4\"\n", ">=3.8,<4")]
    [InlineData("[build-system]\nrequires = []\n[project]\nrequires-python=\">=3.11\"\n[tool.black]\n", ">=3.11")]
    public void Requires_python_is_taken_from_the_project_table(string text, string expected)
    {
        var environment = Read(DeclaredFiles.PyProject, text);

        Assert.Empty(environment.Issues);
        Assert.Equal(expected, Value(environment, DeclaredKind.PythonRequires));
    }

    [Theory]
    [InlineData("[tool.poetry]\nrequires-python = \">=3.9\"\n")]
    [InlineData("[tool.project]\nrequires-python = \">=3.9\"\n")]
    [InlineData("[[project]]\nrequires-python = \">=3.9\"\n")]
    [InlineData("requires-python = \">=3.9\"\n")]
    [InlineData("[project]\nname = \"x\"\n[tool.x]\nrequires-python = \">=3.9\"\n")]
    [InlineData("[tool.x]\ntext = \"\"\"\n[project]\nrequires-python = \">=3.9\"\n\"\"\"\n")]
    public void Requires_python_outside_the_project_table_is_not_read(string text)
    {
        var environment = Read(DeclaredFiles.PyProject, text);

        Assert.Empty(environment.Entries);
    }

    [Theory]
    [InlineData("[project]\nrequires-python = \"\"\"\n>=3.9\"\"\"\n")]
    [InlineData("[project]\nrequires-python = \">=3.9\" extra\n")]
    [InlineData("[project]\nrequires-python = \">=3.9\n")]
    [InlineData("[project]\nrequires-python = 3.9\n")]
    [InlineData("[project]\nrequires-python = \"\\u003e=3.9\"\n")]
    public void Requires_python_in_a_form_the_reader_does_not_understand_is_not_interpreted(string text)
    {
        var environment = Read(DeclaredFiles.PyProject, text);

        Assert.Empty(environment.Entries);
        var issue = Assert.Single(environment.Issues);
        Assert.Equal(DeclaredIssueKind.NotInterpreted, issue.Kind);
        Assert.Equal(2, issue.Source.Line);
    }

    // ---- the list is closed ----

    [Fact]
    public void A_compose_file_has_no_parser_and_a_file_outside_the_list_is_refused()
    {
        var builder = new DeclaredReadingBuilder();

        Assert.Throws<ArgumentException>(() => DeclaredFileParser.Parse(DeclaredFiles.ComposeYaml, "services: {}", builder));
        Assert.Throws<ArgumentException>(() => DeclaredFileParser.Parse(".env", "SECRET=1", builder));
    }

    [Fact]
    public void No_parser_throws_for_garbage_in_any_file_of_the_list()
    {
        string[] garbage = ["", "\0\0\0", "{{{{", "[[[[", "\u202E\u202E", "= = =", new string('\n', 10_000), "\"\"\"", "[project]\nrequires-python = \"", new string('{', 5000)];
        var parsed = DeclaredFiles.All.Where(f => !DeclaredFiles.ComposeFiles.Contains(f, StringComparer.Ordinal)).ToList();

        foreach (var file in parsed)
        {
            foreach (var text in garbage)
            {
                var environment = Read(file, text);
                Assert.True(environment.Entries.Count <= DeclaredEnvironment.MaxItems);
            }
        }
    }
}
