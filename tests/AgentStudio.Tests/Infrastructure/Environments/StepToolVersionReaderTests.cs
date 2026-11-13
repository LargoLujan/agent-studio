using System.Diagnostics;
using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Verification;
using AgentStudio.Domain.Evidence;
using AgentStudio.Infrastructure.Environments;
using AgentStudio.Tests.TestSupport;

namespace AgentStudio.Tests.Infrastructure.Environments;

/// <summary>
/// docs/20 D-12 (WP-E, F6-8): where the versions of the tools of a verification step come from. Nothing is run: the version resource of the executable that the launch
/// resolves, by the same rule as the runners (never a file inside an open project), with the resource of <c>node.exe</c> trusted only if it is Node's.
/// The executables here are copies of <c>cmd.exe</c> under other names: their resource is real and nothing ever starts them.
/// </summary>
public sealed class StepToolVersionReaderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "as versiones " + Guid.NewGuid().ToString("N")[..12]);

    public StepToolVersionReaderTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => TempFolders.Delete(_temp);

    private sealed class FakeRoots(params string[] roots) : IUntrustedRoots
    {
        public IReadOnlyList<string> Roots { get; } = roots;
    }

    private static VerificationStep Step(string executable, params string[] arguments) => new(
        "s", "s", executable, arguments, EvidenceKind.CommandOutput, TestReportFormat.None, null, TimeSpan.FromMinutes(1));

    private string Plant(string relative)
    {
        var path = Path.Combine(_temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        return path;
    }

    private static string Product(string path) => FileVersionInfo.GetVersionInfo(path).ProductVersion!.Split(' ')[0];

    [Fact]
    public void The_version_is_that_of_the_resource_of_the_executable_and_dotnet_says_it_is_the_host_not_the_sdk()
    {
        var dotnet = Plant(@"bin\dotnet.exe");

        var tool = Assert.Single(new StepToolVersionReader().Read([Step(dotnet, "build")]));

        Assert.Equal("dotnet", tool.Tool);
        Assert.Equal(Product(dotnet), tool.Version);
        Assert.Contains("host", tool.Note, StringComparison.Ordinal);
        Assert.Contains("global.json", tool.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_name_is_resolved_as_the_launch_resolves_it_in_the_system_folder_first()
    {
        var tool = Assert.Single(new StepToolVersionReader().Read([Step("cmd", "/c", "echo")]));

        Assert.Equal("cmd", tool.Tool);
        Assert.Equal(Product(Path.Combine(Environment.SystemDirectory, "cmd.exe")), tool.Version);
        Assert.Contains("cmd.exe", tool.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_name_found_only_inside_an_open_project_is_not_a_tool_and_without_roots_it_is()
    {
        var project = Path.Combine(_temp, "proyecto");
        Plant(@"proyecto\bin\tooltrip.exe");
        string? Path_() => Path.Combine(project, "bin");

        var trusted = new StepToolVersionReader(null, Path_).Read([Step("tooltrip")]);
        var untrusted = new StepToolVersionReader(new FakeRoots(project), Path_).Read([Step("tooltrip")]);

        Assert.NotNull(Assert.Single(trusted).Version);
        var refused = Assert.Single(untrusted);
        Assert.Null(refused.Version);
        Assert.Contains("outside the project folders", refused.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_is_nowhere_and_a_relative_path_and_a_script_and_a_missing_file_are_unknown_with_a_reason()
    {
        var script = Path.Combine(_temp, "tool.cmd");
        File.WriteAllText(script, "@echo off");
        var reader = new StepToolVersionReader(null, () => string.Empty);

        var tools = reader.Read([Step("no-such-tool-xyz"), Step(@".\bin\tool.exe"), Step(script), Step(Path.Combine(_temp, "missing.exe"))]);

        Assert.Equal(4, tools.Count);
        Assert.All(tools, t => Assert.Null(t.Version));
        Assert.Equal(
            ["not found outside the project folders", "a relative path has no fixed file to read", "a script has no version resource", "the file was not found"],
            tools.Select(t => t.Note));
    }

    [Fact]
    public void The_resource_of_a_node_exe_counts_only_if_it_is_nodes_a_shim_with_its_own_version_does_not()
    {
        var shim = Plant(@"volta\node.exe");

        var tool = Assert.Single(new StepToolVersionReader().Read([Step(shim, "--test")]));

        Assert.Equal("node", tool.Tool);
        Assert.Null(tool.Version);
        Assert.Contains("not that of Node.js", tool.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_node_exe_when_the_machine_has_one_gives_its_version()
    {
        var node = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") }
            .Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.Combine(p!, "nodejs", "node.exe")).FirstOrDefault(File.Exists);
        if (node is null || FileVersionInfo.GetVersionInfo(node).ProductName != "Node.js")
        {
            Assert.Skip("This machine has no Node.js in Program Files.");
        }

        var tool = Assert.Single(new StepToolVersionReader().Read([Step(node, "--test")]));

        Assert.Equal(Product(node), tool.Version);
    }

    [Fact]
    public void A_step_that_runs_npm_through_node_names_npm_with_the_version_of_its_package_json()
    {
        var node = Plant(@"nodejs\node.exe");
        var npmRoot = Path.Combine(_temp, "nodejs", "node_modules", "npm");
        Directory.CreateDirectory(Path.Combine(npmRoot, "bin"));
        var script = Path.Combine(npmRoot, "bin", "npm-cli.js");
        File.WriteAllText(script, "// npm");
        File.WriteAllText(Path.Combine(npmRoot, "package.json"), "{\"name\":\"npm\",\"version\":\"10.9.0\"}");

        var tools = new StepToolVersionReader().Read([Step(node, script, "ci"), Step(node, script, "run", "build")]);

        Assert.Equal(["node", "npm"], tools.Select(t => t.Tool));
        var npm = tools[1];
        Assert.Equal("10.9.0", npm.Version);
        Assert.Equal("package.json of npm", npm.Note);
    }

    [Theory]
    [InlineData("{\"name\":\"other\",\"version\":\"10.9.0\"}", "has no version")]
    [InlineData("{\"name\":\"npm\"}", "has no version")]
    [InlineData("{\"name\":\"npm\",\"version\":\"1.0 beta\"}", "has no version")]
    [InlineData("[not json", "cannot be read")]
    public void An_npm_whose_package_json_does_not_say_its_version_is_unknown(string json, string reason)
    {
        var node = Plant(@"nodejs\node.exe");
        var npmRoot = Path.Combine(_temp, "nodejs", "node_modules", "npm");
        Directory.CreateDirectory(Path.Combine(npmRoot, "bin"));
        var script = Path.Combine(npmRoot, "bin", "npm-cli.js");
        File.WriteAllText(script, "// npm");
        File.WriteAllText(Path.Combine(npmRoot, "package.json"), json);

        var npm = new StepToolVersionReader().Read([Step(node, script, "ci")])[1];

        Assert.Null(npm.Version);
        Assert.Contains(reason, npm.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_step_that_is_not_npm_names_only_node_and_each_executable_is_named_once()
    {
        var node = Plant(@"nodejs\node.exe");

        var tools = new StepToolVersionReader().Read([Step(node, "--test"), Step(node, "--test", "--other"), Step(node.ToUpperInvariant(), "x")]);

        Assert.Equal(["node"], tools.Select(t => t.Tool));
    }

    [Fact]
    public void The_reader_never_throws_for_a_hostile_executable_name_and_every_note_is_valid_for_the_header()
    {
        var reader = new StepToolVersionReader(null, () => "relative;" + new string('x', 5000));
        string[] hostile = ["", " ", "a\0b", new string('a', 400), "con", "nul.exe", "\u202Eevil", "..\\..\\x", "x*?.exe", @"\\?\C:\nope.exe", "C:", @"C:\" + new string('d', 400) + ".exe"];

        var tools = reader.Read([.. hostile.Select(h => Step(h))]);

        Assert.All(tools, t => Assert.Equal(t, new EvidenceToolVersion(t.Tool, t.Version, t.Note)));
        Assert.True(tools.Count <= EvidenceToolVersions.MaxEntries);
    }
}
