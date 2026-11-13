using AgentStudio.Domain.Diagnostics;
using AgentStudio.Domain.Environments;

namespace AgentStudio.Tests.Domain.Environments;

/// <summary>
/// docs/20 D-07 and D-08 (WP-E): what a project declares set against what is installed, as pure code with doubles for the inventory. Every row is one check of the
/// section «declared environment»; what cannot be compared is «not checked», never correct.
/// </summary>
public sealed class EnvironmentComparerTests
{
    private const string Root = @"C:\work\proj";

    private static ToolVersion V(string text) => ToolVersion.Parse(text);

    private static DeclaredEnvironment Declare(params (string File, DeclaredKind Kind, string? Name, string Value)[] entries)
    {
        var builder = new DeclaredReadingBuilder();
        foreach (var (file, kind, name, value) in entries)
        {
            builder.Found(file);
            builder.Entry(kind, name, value, new DeclaredSource(file, 1));
        }

        return builder.Build();
    }

    private static InstalledTools Machine(
        string[]? sdks = null, string? node = "22.11.0", string? npm = "10.9.0", string? python = "3.12.5", bool storeAlias = false, DockerStatus? docker = null) =>
        new(
            [.. (sdks ?? ["10.0.303"]).Select(s => new InstalledSdk(V(s), null))],
            [],
            node is null ? null : new InstalledTool(V(node), null),
            npm is null ? null : new InstalledTool(V(npm), null),
            python is null ? null : new InstalledTool(V(python), null),
            storeAlias,
            null,
            docker ?? DockerStatus.NotInstalled,
            WslStatus.NotInstalled,
            null);

    private static DoctorCheck Check(IReadOnlyList<DoctorCheck> checks, string code) => Assert.Single(checks, c => c.Code == code);

    private static IReadOnlyList<DoctorCheck> Compare(DeclaredEnvironment declared, InstalledTools? installed = null, IReadOnlySet<string>? cache = null) =>
        EnvironmentComparer.Compare(declared, installed ?? Machine(), Root, cache);

    [Fact]
    public void A_project_that_declares_nothing_says_so_with_one_check_that_does_not_apply()
    {
        var check = Assert.Single(Compare(DeclaredEnvironment.Empty));

        Assert.Equal(EnvironmentCheckCodes.None, check.Code);
        Assert.Equal(DoctorStatus.NotApplicable, check.Status);
    }

    [Fact]
    public void Every_check_belongs_to_the_declared_environment_section_and_has_a_code_of_the_list()
    {
        var declared = Declare(
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, "10.0.303"),
            (DeclaredFiles.NvmRc, DeclaredKind.NodeVersion, null, "22"),
            (DeclaredFiles.PyProject, DeclaredKind.PythonRequires, null, ">=3.9"),
            (DeclaredFiles.Dockerfile, DeclaredKind.ContainerImage, null, "node:22"));

        var checks = Compare(declared);

        Assert.All(checks, c => Assert.Equal(DoctorSection.DeclaredEnvironment, c.Section));
        Assert.All(checks, c => Assert.Contains(c.Code, EnvironmentCheckCodes.All));
        Assert.Equal(checks.Count, checks.Select(c => c.Code).Distinct().Count());
        Assert.All(EnvironmentCheckCodes.All, code => Assert.True(AgentStudio.Domain.Diagnostics.DoctorCodes.IsCode(code)));
    }

    // ---- .NET ----

    [Fact]
    public void An_sdk_that_the_rule_picks_is_correct_and_the_check_says_which_one()
    {
        var declared = Declare(
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, "10.0.100"),
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetRollForward, null, "latestFeature"));

        var check = Check(Compare(declared, Machine(sdks: ["9.0.100", "10.0.100", "10.0.303"])), EnvironmentCheckCodes.DotNetSdk);

        Assert.Equal(DoctorStatus.Ok, check.Status);
        Assert.Equal("10.0.303", check.Fact("resolved"));
        Assert.Equal("global.json:1", check.Fact("source"));
        Assert.Equal("10.0.303, 10.0.100, 9.0.100", check.Fact("installed"));
        Assert.Equal("sdk 10.0.100", check.Fact("declared"));
        Assert.Null(check.FixCode);
    }

    [Fact]
    public void An_sdk_that_nothing_installed_satisfies_is_an_error_with_the_fix_to_install_it()
    {
        var declared = Declare(
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, "10.0.303"),
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetRollForward, null, "disable"));

        var check = Check(Compare(declared, Machine(sdks: ["10.0.100"])), EnvironmentCheckCodes.DotNetSdk);

        Assert.Equal(DoctorStatus.Error, check.Status);
        Assert.Equal(EnvironmentCheckCodes.FixInstallDotNetSdk, check.FixCode);
        Assert.Null(check.Fact("resolved"));
        Assert.Equal("10.0.100", check.Fact("installed"));
    }

    [Fact]
    public void With_no_sdk_installed_the_check_says_none()
    {
        var declared = Declare((DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, "10.0.303"));

        var check = Check(Compare(declared, Machine(sdks: [])), EnvironmentCheckCodes.DotNetSdk);

        Assert.Equal(DoctorStatus.Error, check.Status);
        Assert.Equal("none", check.Fact("installed"));
    }

    [Theory]
    [InlineData("10.0")]
    [InlineData("ten")]
    public void An_invalid_sdk_version_is_not_checked_and_never_correct(string version)
    {
        var declared = Declare((DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, version));

        Assert.Equal(DoctorStatus.Unknown, Check(Compare(declared), EnvironmentCheckCodes.DotNetSdk).Status);
    }

    [Fact]
    public void Sdk_paths_make_the_check_not_checked_and_one_inside_the_project_is_a_warning_of_its_own()
    {
        var declared = Declare(
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, "10.0.303"),
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkPath, null, ".dotnet"),
            (DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkPath, null, "$host$"));

        var checks = Compare(declared);

        Assert.Equal(DoctorStatus.Unknown, Check(checks, EnvironmentCheckCodes.DotNetSdk).Status);
        var repo = Check(checks, EnvironmentCheckCodes.DotNetSdkInRepo);
        Assert.Equal(DoctorStatus.Warning, repo.Status);
        Assert.Equal("path .dotnet", repo.Facts.First(f => f.Name == "declared").Value);
    }

    [Theory]
    [InlineData(".dotnet", true)]
    [InlineData("./tools/sdk", true)]
    [InlineData("..\\outside", false)]
    [InlineData("..\\proj\\inside", true)]
    [InlineData("$host$", false)]
    [InlineData(@"C:\work\proj\sdk", true)]
    [InlineData(@"c:\WORK\PROJ", true)]
    [InlineData(@"C:\work\proj2\sdk", false)]
    [InlineData(@"C:\Program Files\dotnet", false)]
    [InlineData(@"\\server\share\sdk", false)]
    public void A_path_of_sdk_paths_is_inside_the_project_when_it_is_relative_or_under_the_root(string path, bool inside)
    {
        Assert.Equal(inside, EnvironmentComparer.IsInsideProject(path, Root));
    }

    [Fact]
    public void Without_a_root_only_a_relative_path_is_inside()
    {
        Assert.True(EnvironmentComparer.IsInsideProject(".dotnet", null));
        Assert.False(EnvironmentComparer.IsInsideProject(@"C:\anywhere", null));
    }

    [Fact]
    public void Local_tools_are_checked_against_the_set_of_the_cache_and_not_checked_without_it()
    {
        var declared = Declare(
            (DeclaredFiles.DotNetTools, DeclaredKind.DotNetLocalTool, "dotnet-ef", "10.0.0"),
            (DeclaredFiles.DotNetTools, DeclaredKind.DotNetLocalTool, "Cake.Tool", "5.0.0"));

        var allThere = Check(Compare(declared, cache: new HashSet<string> { EnvironmentComparer.LocalToolKey("dotnet-ef", "10.0.0"), EnvironmentComparer.LocalToolKey("cake.tool", "5.0.0") }), EnvironmentCheckCodes.DotNetLocalTools);
        var oneMissing = Check(Compare(declared, cache: new HashSet<string> { EnvironmentComparer.LocalToolKey("dotnet-ef", "10.0.0") }), EnvironmentCheckCodes.DotNetLocalTools);
        var unknown = Check(Compare(declared, cache: null), EnvironmentCheckCodes.DotNetLocalTools);

        Assert.Equal(DoctorStatus.Ok, allThere.Status);
        Assert.Equal(DoctorStatus.Warning, oneMissing.Status);
        Assert.Equal(EnvironmentCheckCodes.FixRestoreDotNetTools, oneMissing.FixCode);
        Assert.Contains(oneMissing.Facts, f => f is { Name: "installed", Value: "not in the NuGet cache" });
        Assert.Equal(DoctorStatus.Unknown, unknown.Status);
    }

    [Fact]
    public void Many_local_tools_never_exceed_the_facts_of_a_check()
    {
        var declared = Declare([.. Enumerable.Range(0, 60).Select(i => (DeclaredFiles.DotNetTools, DeclaredKind.DotNetLocalTool, (string?)$"tool{i}", "1.0.0"))]);

        var check = Check(Compare(declared, cache: new HashSet<string>()), EnvironmentCheckCodes.DotNetLocalTools);

        Assert.True(check.Facts.Count <= DoctorCheck.MaxFacts);
    }

    // ---- Node ----

    [Theory]
    [InlineData("22", DoctorStatus.Ok)]
    [InlineData("v22.11.0", DoctorStatus.Ok)]
    [InlineData("20", DoctorStatus.Warning)]
    [InlineData("lts/*", DoctorStatus.Unknown)]
    [InlineData("node", DoctorStatus.Unknown)]
    [InlineData("22.11.0-rc.1", DoctorStatus.Unknown)]
    public void A_node_version_file_is_compared_with_the_node_installed(string value, DoctorStatus expected)
    {
        var declared = Declare((DeclaredFiles.NvmRc, DeclaredKind.NodeVersion, null, value));

        var check = Check(Compare(declared), EnvironmentCheckCodes.NodeNvmrc);

        Assert.Equal(expected, check.Status);
        Assert.Equal("22.11.0", check.Fact("installed"));
    }

    [Fact]
    public void The_two_version_files_have_their_own_checks()
    {
        var declared = Declare((DeclaredFiles.NvmRc, DeclaredKind.NodeVersion, null, "22"), (DeclaredFiles.NodeVersionFile, DeclaredKind.NodeVersion, null, "20"));

        var checks = Compare(declared);

        Assert.Equal(DoctorStatus.Ok, Check(checks, EnvironmentCheckCodes.NodeNvmrc).Status);
        Assert.Equal(DoctorStatus.Warning, Check(checks, EnvironmentCheckCodes.NodeVersionFile).Status);
    }

    [Fact]
    public void Engines_are_compared_with_node_and_npm_and_a_missing_tool_is_an_error_with_a_fix()
    {
        var declared = Declare(
            (DeclaredFiles.PackageJson, DeclaredKind.NodeEngine, null, ">=22.11 <23"),
            (DeclaredFiles.PackageJson, DeclaredKind.NpmEngine, null, "^11"));

        var checks = Compare(declared);
        var missing = Compare(declared, Machine(node: null, npm: null));

        Assert.Equal(DoctorStatus.Ok, Check(checks, EnvironmentCheckCodes.NodeEngine).Status);
        Assert.Equal(DoctorStatus.Warning, Check(checks, EnvironmentCheckCodes.NpmEngine).Status);
        var node = Check(missing, EnvironmentCheckCodes.NodeEngine);
        Assert.Equal(DoctorStatus.Error, node.Status);
        Assert.Equal("none", node.Fact("installed"));
        Assert.Equal(EnvironmentCheckCodes.FixInstallNode, node.FixCode);
        Assert.Equal(DoctorStatus.Error, Check(missing, EnvironmentCheckCodes.NpmEngine).Status);
    }

    [Fact]
    public void An_engine_range_outside_the_subset_is_not_checked_never_correct()
    {
        var declared = Declare((DeclaredFiles.PackageJson, DeclaredKind.NodeEngine, null, "1.2.3 - 2.3.4"));

        Assert.Equal(DoctorStatus.Unknown, Check(Compare(declared), EnvironmentCheckCodes.NodeEngine).Status);
    }

    // ---- Python ----

    [Theory]
    [InlineData(">=3.9", DoctorStatus.Ok)]
    [InlineData(">=3.13", DoctorStatus.Warning)]
    [InlineData("~=3.12.1", DoctorStatus.Ok)]
    [InlineData("==3.12.*", DoctorStatus.Ok)]
    [InlineData(">=3.9rc1", DoctorStatus.Unknown)]
    public void Requires_python_is_compared_with_the_python_installed(string value, DoctorStatus expected)
    {
        var declared = Declare((DeclaredFiles.PyProject, DeclaredKind.PythonRequires, null, value));

        Assert.Equal(expected, Check(Compare(declared), EnvironmentCheckCodes.PythonRequires).Status);
    }

    [Fact]
    public void The_python_version_file_and_the_missing_python_and_the_store_alias()
    {
        var declared = Declare((DeclaredFiles.PythonVersionFile, DeclaredKind.PythonVersion, null, "3.12"));

        Assert.Equal(DoctorStatus.Ok, Check(Compare(declared), EnvironmentCheckCodes.PythonVersionFile).Status);
        var missing = Check(Compare(declared, Machine(python: null)), EnvironmentCheckCodes.PythonVersionFile);
        Assert.Equal(DoctorStatus.Error, missing.Status);
        Assert.Equal(EnvironmentCheckCodes.FixInstallPython, missing.FixCode);
        var alias = Check(Compare(declared, Machine(python: null, storeAlias: true)), EnvironmentCheckCodes.PythonVersionFile);
        Assert.Equal(DoctorStatus.Error, alias.Status);
        Assert.Equal("store-alias", alias.Fact("installed"));
    }

    [Fact]
    public void The_venv_is_compared_with_requires_python_and_its_absence_is_said()
    {
        var withVenv = Declare((DeclaredFiles.PyProject, DeclaredKind.PythonRequires, null, ">=3.12"), (DeclaredFiles.PyVenvCfg, DeclaredKind.VenvVersion, null, "3.11.9"));
        var okVenv = Declare((DeclaredFiles.PyProject, DeclaredKind.PythonRequires, null, ">=3.9"), (DeclaredFiles.PyVenvCfg, DeclaredKind.VenvVersion, null, "3.11.9"));
        var withoutVenv = Declare((DeclaredFiles.PyProject, DeclaredKind.PythonRequires, null, ">=3.9"));
        var oddVenv = Declare((DeclaredFiles.PyVenvCfg, DeclaredKind.VenvVersion, null, "three"));

        Assert.Equal(DoctorStatus.Warning, Check(Compare(withVenv), EnvironmentCheckCodes.PythonVenv).Status);
        Assert.Equal(DoctorStatus.Ok, Check(Compare(okVenv), EnvironmentCheckCodes.PythonVenv).Status);
        var absent = Check(Compare(withoutVenv), EnvironmentCheckCodes.PythonVenv);
        Assert.Equal(DoctorStatus.NotApplicable, absent.Status);
        Assert.Equal("absent", absent.Fact("installed"));
        Assert.Equal(DoctorStatus.Unknown, Check(Compare(oddVenv), EnvironmentCheckCodes.PythonVenv).Status);
    }

    // ---- containers and what was not read ----

    [Theory]
    [InlineData(DockerState.Running, DoctorStatus.Ok, null)]
    [InlineData(DockerState.EngineStopped, DoctorStatus.Warning, EnvironmentCheckCodes.FixStartDocker)]
    [InlineData(DockerState.NotInstalled, DoctorStatus.Error, EnvironmentCheckCodes.FixInstallDocker)]
    public void A_project_with_containers_is_linked_to_the_state_of_docker(DockerState state, DoctorStatus status, string? fix)
    {
        var docker = state switch
        {
            DockerState.Running => new DockerStatus(state, V("27.0.1"), V("27.0.1")),
            DockerState.EngineStopped => new DockerStatus(state, V("27.0.1"), null),
            _ => DockerStatus.NotInstalled,
        };
        var declared = Declare(
            (DeclaredFiles.Dockerfile, DeclaredKind.ContainerImage, "build", "mcr.microsoft.com/dotnet/sdk:10.0"),
            (DeclaredFiles.ComposeYaml, DeclaredKind.ComposeFile, null, "1024"),
            (DeclaredFiles.DevContainerFolder, DeclaredKind.Extends, null, "../base.json"));

        var check = Check(Compare(declared, Machine(docker: docker)), EnvironmentCheckCodes.Containers);

        Assert.Equal(status, check.Status);
        Assert.Equal(fix, check.FixCode);
        Assert.Contains(check.Facts, f => f is { Name: "declared", Value: "image mcr.microsoft.com/dotnet/sdk:10.0" });
        Assert.Contains(check.Facts, f => f is { Name: "declared", Value: "compose size 1024 bytes" });
        Assert.Contains(check.Facts, f => f is { Name: "source", Value: "compose.yaml" });
        Assert.StartsWith("docker", check.Fact("installed"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_without_containers_has_no_container_check()
    {
        var declared = Declare((DeclaredFiles.GlobalJson, DeclaredKind.DotNetSdkVersion, null, "10.0.303"));

        Assert.DoesNotContain(Compare(declared), c => c.Code == EnvironmentCheckCodes.Containers);
    }

    [Fact]
    public void What_was_not_read_is_one_warning_with_a_fact_per_issue_named_by_its_kind()
    {
        var builder = new DeclaredReadingBuilder();
        builder.Found(DeclaredFiles.PackageJson);
        builder.Issue(new DeclaredSource(DeclaredFiles.PackageJson), DeclaredIssueKind.TooLarge);
        builder.Issue(new DeclaredSource(DeclaredFiles.GlobalJson, 4), DeclaredIssueKind.DuplicateKey);
        builder.Issue(new DeclaredSource(DeclaredFiles.PyProject, 7), DeclaredIssueKind.NotInterpreted, "requires-python = 3.9");

        var check = Check(Compare(builder.Build()), EnvironmentCheckCodes.NotRead);

        Assert.Equal(DoctorStatus.Warning, check.Status);
        Assert.Equal("package.json", check.Fact("too-large"));
        Assert.Equal("global.json:4", check.Fact("duplicate-key"));
        Assert.Equal("pyproject.toml:7 requires-python = 3.9", check.Fact("not-interpreted"));
    }
}
