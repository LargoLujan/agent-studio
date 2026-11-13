using AgentStudio.Application.Diagnostics;
using AgentStudio.Infrastructure.Diagnostics;
using AgentStudio.Tests.Desktop;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>
/// docs/21 D-31 (P0 of Phase 10): Auto Doctor asks for the version of <c>gh</c> and nothing else, and without letting it go out to the network. <c>gh</c> has telemetry and an
/// update notice that reach the network with ANY command, <c>--version</c> too, so the door launches it with the five variables that stop both, and the allow list removes
/// every variable that could redirect it or give it a token. The ones of the CLI of GitHub are the cases of this file; the door itself is in <see cref="DoctorToolRunnerTests"/>.
/// </summary>
public sealed class GhVersionDoorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private DoctorFolders Folders => DoctorFolders.Under(_temp.Combine("local"));

    private static IReadOnlyDictionary<string, string> Ambient(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private DoctorToolRunner Door(RecordingProcessRunner runner, IReadOnlyDictionary<string, string> ambient) =>
        new(new FakeLocator(c => @"C:\Tools\" + c + ".exe"), runner, Folders, () => ambient, () => false);

    [Fact]
    public void The_catalog_asks_gh_for_its_version_and_nothing_more()
    {
        var entry = DoctorToolCatalog.Of(DoctorTool.GhVersion);

        Assert.Equal("gh", entry.Command);
        Assert.Equal(["--version"], entry.Arguments);
        Assert.False(entry.StderrWhenBlank);
        Assert.False(entry.RefusesStoreAlias);
        Assert.Equal(DoctorTool.GhVersion, Enum.GetValues<DoctorTool>()[^1]);
        Assert.Equal(Enum.GetValues<DoctorTool>().Length, DoctorToolCatalog.All.Count);
    }

    [Fact]
    public async Task The_process_of_gh_version_carries_the_five_variables_that_keep_it_from_the_network()
    {
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner, Ambient());

        await door.RunAsync(DoctorTool.GhVersion, null, CancellationToken.None);

        var spec = runner.Specs.Single();
        Assert.Equal(["--version"], spec.Arguments);
        Assert.Equal(Folders.WorkingFolder, spec.WorkingDirectory);
        Assert.Null(spec.StandardInput);
        var env = spec.Environment!;
        Assert.Equal("1", env["GH_NO_UPDATE_NOTIFIER"]);
        Assert.Equal("1", env["GH_NO_EXTENSION_UPDATE_NOTIFIER"]);
        Assert.Equal("false", env["GH_TELEMETRY"]);
        Assert.Equal("1", env["DO_NOT_TRACK"]);
        Assert.Equal("1", env["GH_PROMPT_DISABLED"]);
        Assert.Equal(
            ["DO_NOT_TRACK", "GH_NO_EXTENSION_UPDATE_NOTIFIER", "GH_NO_UPDATE_NOTIFIER", "GH_PROMPT_DISABLED", "GH_TELEMETRY"],
            DoctorEnvironment.GhOffline.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task What_could_redirect_gh_or_give_it_a_token_is_removed_and_the_five_win_over_the_ones_of_the_process()
    {
        var runner = new RecordingProcessRunner();
        var planted = Ambient(
            ("GH_TOKEN", "ghp_secret"), ("GITHUB_TOKEN", "ghp_secret"), ("GH_ENTERPRISE_TOKEN", "x"), ("GH_HOST", "evil.example"), ("GH_REPO", "evil/repo"),
            ("GH_CONFIG_DIR", @"C:\repo\.gh"), ("GH_DEBUG", "api"), ("GH_PAGER", "evil"), ("GH_BROWSER", "evil"), ("HTTPS_PROXY", "http://evil:3128"),
            ("GH_TELEMETRY", "true"), ("DO_NOT_TRACK", "0"), ("GH_NO_UPDATE_NOTIFIER", "0"), ("SystemRoot", @"C:\Windows"));
        await using var door = Door(runner, planted);

        await door.RunAsync(DoctorTool.GhVersion, null, CancellationToken.None);

        var env = runner.Specs.Single().Environment!;
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GH_HOST", "GH_REPO", "GH_CONFIG_DIR", "GH_DEBUG", "GH_PAGER", "GH_BROWSER", "HTTPS_PROXY" })
        {
            Assert.True(env.ContainsKey(name) && env[name] is null, $"{name} must be removed");
        }

        Assert.Equal("false", env["GH_TELEMETRY"]);
        Assert.Equal("1", env["DO_NOT_TRACK"]);
        Assert.Equal("1", env["GH_NO_UPDATE_NOTIFIER"]);
        Assert.DoesNotContain(env.Keys, k => k.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task No_other_tool_gets_the_variables_of_gh()
    {
        var runner = new RecordingProcessRunner();
        await using var door = Door(runner, Ambient());

        foreach (var tool in Enum.GetValues<DoctorTool>().Where(t => t != DoctorTool.GhVersion))
        {
            await door.RunAsync(tool, null, CancellationToken.None);
        }

        Assert.All(runner.Specs, spec => Assert.DoesNotContain(spec.Environment!.Keys, k => k.StartsWith("GH_", StringComparison.Ordinal) || k == "DO_NOT_TRACK"));
    }

    [Theory]
    [InlineData("gh version 2.96.0 (2026-07-02)\r\nhttps://github.com/cli/cli/releases/tag/v2.96.0\r\n", "2.96.0")]
    [InlineData("gh version 2.96.0 (2026-07-02)\n", "2.96.0")]
    [InlineData("gh version 2.100.1\n", "2.100.1")]
    [InlineData("gh version 3.0.0-rc.1 (2027-01-10)\n", "3.0.0-rc.1")]
    [InlineData("gh version 2.96\n", "2.96")]
    public void The_version_is_read_from_the_first_line_of_what_gh_says(string output, string expected) =>
        Assert.Equal(expected, ToolOutputParsers.Version(DoctorTool.GhVersion, output)!.Text);

    [Theory]
    [InlineData("")]
    [InlineData("git version 2.45.2.windows.1")]
    [InlineData("gh: command not found")]
    [InlineData("gh version")]
    [InlineData("gh version x.y.z")]
    [InlineData("gh version 2.96.0 (hoy)")]
    [InlineData("  gh version 2.96.0")]
    [InlineData("version 2.96.0")]
    [InlineData("gh version 2.96.0 extra words")]
    [InlineData("https://github.com/cli/cli/releases/tag/v2.96.0\r\ngh version 2.96.0")]
    public void What_does_not_start_as_the_answer_of_gh_is_not_understood_and_the_check_would_say_sin_comprobar(string output) =>
        Assert.Null(ToolOutputParsers.Version(DoctorTool.GhVersion, output));

    [Fact]
    public void The_reader_of_gh_does_not_read_the_versions_of_other_tools_and_the_others_do_not_read_gh()
    {
        Assert.Null(ToolOutputParsers.Version(DoctorTool.GhVersion, "git version 2.45.2"));
        Assert.Null(ToolOutputParsers.Version(DoctorTool.GitVersion, "gh version 2.96.0 (2026-07-02)"));
        Assert.Null(ToolOutputParsers.Version(DoctorTool.NodeVersion, "gh version 2.96.0 (2026-07-02)"));
    }
}
