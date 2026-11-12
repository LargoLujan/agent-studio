using System.Text;
using AgentStudio.Application.Diagnostics;
using AgentStudio.Infrastructure.Diagnostics;

namespace AgentStudio.Tests.Infrastructure.Diagnostics;

/// <summary>The readers of what the tools say (docs/20 F6-1 and F6-9), over recorded outputs (see Fixtures/README.md) and over hostile ones.</summary>
public sealed class ToolOutputParsersTests
{
    [Fact]
    public void The_real_list_of_sdks_gives_every_version_and_folder()
    {
        var sdks = ToolOutputParsers.DotNetSdks(DoctorFixtures.Read("real-dotnet-list-sdks.txt"))!;

        Assert.Equal(["8.0.319", "10.0.303", "11.0.100-preview.2.26159.112"], sdks.Select(s => s.Version.Text).ToArray());
        Assert.All(sdks, s => Assert.Equal(@"C:\Program Files\dotnet\sdk", s.Path));
    }

    [Fact]
    public void The_real_list_of_runtimes_gives_name_and_version()
    {
        var runtimes = ToolOutputParsers.DotNetRuntimes(DoctorFixtures.Read("real-dotnet-list-runtimes.txt"))!;

        Assert.Equal(9, runtimes.Count);
        Assert.Contains(runtimes, r => r.Name == "Microsoft.NETCore.App" && r.Version.Text == "10.0.11");
    }

    [Fact]
    public void An_empty_list_is_no_sdk_but_text_with_no_line_of_the_list_is_not_understood()
    {
        Assert.Empty(ToolOutputParsers.DotNetSdks("")!);
        Assert.Null(ToolOutputParsers.DotNetSdks("It was not possible to find any installed .NET SDKs.\r\n"));
        Assert.Null(ToolOutputParsers.DotNetRuntimes("no runtimes here"));
    }

    [Theory]
    [InlineData(DoctorTool.NodeVersion, "real-node-version.txt", "24.14.0")]
    [InlineData(DoctorTool.GitVersion, "real-git-version.txt", "2.45.2")]
    [InlineData(DoctorTool.PythonVersion, "real-python-version.txt", "3.12.5")]
    [InlineData(DoctorTool.PyVersion, "real-python-version.txt", "3.12.5")]
    public void The_version_of_a_tool_is_read_from_its_first_line(DoctorTool tool, string fixture, string expected) =>
        Assert.Equal(expected, ToolOutputParsers.Version(tool, DoctorFixtures.Read(fixture))!.Text);

    [Theory]
    [InlineData("Python 3.13.0rc1", "3.13.0-rc1")]
    [InlineData("Python 3.14.0a1", "3.14.0-a1")]
    [InlineData("Python 3.12.3+", "3.12.3")]
    [InlineData("Python 3.13.0b2\r\n", "3.13.0-b2")]
    public void Python_pre_releases_and_builds_with_a_suffix_are_read(string output, string expected)
    {
        Assert.Equal(expected, ToolOutputParsers.Version(DoctorTool.PythonVersion, output)!.Text);
        Assert.Equal(expected, ToolOutputParsers.Version(DoctorTool.PyVersion, output)!.Text);
        Assert.True(ToolOutputParsers.Version(DoctorTool.PythonVersion, "Python 3.13.0rc1")! < ToolOutputParsers.Version(DoctorTool.PythonVersion, "Python 3.13.0")!);
    }

    [Fact]
    public void Wsl_reads_the_distribution_only_from_the_line_right_before_the_version_and_says_the_version_is_the_default_one()
    {
        // A first line that is not the distribution (a notice with a colon) is not taken as one; lines after the version are not read.
        var ready = ToolOutputParsers.Wsl("Notice: an update exists\r\nDefault Distribution: Ubuntu\r\nDefault Version: 2\r\nKernel version: 5.15.1\r\n", false)!;

        Assert.True(ready.HasOwnDistribution);
        Assert.Equal("Ubuntu", ready.DefaultDistribution);
        Assert.False(ToolOutputParsers.Wsl("Default Version: 2\r\n", false)!.HasOwnDistribution);
    }

    [Fact]
    public void The_message_of_wsl_without_wsl_is_recognized_by_its_untranslated_command_and_nothing_else_is()
    {
        Assert.True(ToolOutputParsers.WslNotInstalled(DoctorFixtures.Read("handmade-wsl-status-garbage.txt")));
        Assert.True(ToolOutputParsers.WslNotInstalled("El subsistema de Windows para Linux no está instalado. Ejecuta wsl.exe --install."));
        Assert.False(ToolOutputParsers.WslNotInstalled("Something went wrong."));
        Assert.False(ToolOutputParsers.WslNotInstalled(DoctorFixtures.Read("handmade-wsl-status-en-ubuntu.txt")));
        Assert.False(ToolOutputParsers.WslNotInstalled(null));
    }

    [Fact]
    public void Npm_says_only_the_number()
    {
        Assert.Equal("10.8.2", ToolOutputParsers.Version(DoctorTool.NpmVersion, "10.8.2\r\n")!.Text);
        Assert.Null(ToolOutputParsers.Version(DoctorTool.NpmVersion, "npm is not a number"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("git version")]
    [InlineData("Python")]
    [InlineData("v99999999999.1.1")]
    public void A_version_line_that_is_not_the_form_of_the_tool_is_not_understood(string output)
    {
        Assert.Null(ToolOutputParsers.Version(DoctorTool.GitVersion, output));
        Assert.Null(ToolOutputParsers.Version(DoctorTool.NodeVersion, output));
        Assert.Null(ToolOutputParsers.Version(DoctorTool.PythonVersion, output));
    }

    [Fact]
    public void Docker_with_the_engine_stopped_has_the_client_and_no_server_as_the_real_capture_says()
    {
        var reading = ToolOutputParsers.Docker(DoctorFixtures.Read("real-docker-version-engine-stopped.json"), truncated: false)!;

        Assert.Equal("27.3.1", reading.Client.Text);
        Assert.Null(reading.Server);
    }

    [Fact]
    public void Docker_running_has_both_versions_by_structure()
    {
        var reading = ToolOutputParsers.Docker(DoctorFixtures.Read("handmade-docker-version-running.json"), truncated: false)!;

        Assert.Equal("27.3.1", reading.Client.Text);
        Assert.Equal("27.3.1", reading.Server!.Text);
    }

    [Theory]
    [InlineData("handmade-docker-version-garbage.txt")]
    public void Docker_text_that_is_not_json_is_not_understood(string fixture) =>
        Assert.Null(ToolOutputParsers.Docker(DoctorFixtures.Read(fixture), truncated: false));

    [Fact]
    public void Docker_json_of_another_shape_a_truncated_one_and_a_deep_one_are_not_understood()
    {
        Assert.Null(ToolOutputParsers.Docker("""{"Server":{"Version":"27.3.1"}}""", false));
        Assert.Null(ToolOutputParsers.Docker("""{"Client":{"Version":"not a version"}}""", false));
        Assert.Null(ToolOutputParsers.Docker("""{"Client":{"Version":"27.3.1"},"Server":{"Version":5}}""", false));
        Assert.Null(ToolOutputParsers.Docker(DoctorFixtures.Read("real-docker-version-engine-stopped.json"), truncated: true));
        Assert.Null(ToolOutputParsers.Docker(new string('[', 5000) + new string(']', 5000), false));
        Assert.Null(ToolOutputParsers.Docker("[1,2]", false));
    }

    [Fact]
    public void A_server_with_the_text_of_an_error_is_not_a_running_engine_whatever_the_words_say()
    {
        // The state comes from the structure: a Server that is null or missing is a stopped engine, in any language.
        var stopped = ToolOutputParsers.Docker("""{"Client":{"Version":"27.3.1"},"ServerErrors":["error during connect: motor parado"]}""", false)!;

        Assert.Null(stopped.Server);
    }

    [Fact]
    public void Wsl_with_only_the_distribution_of_docker_has_no_distribution_of_its_own()
    {
        var reading = ToolOutputParsers.Wsl(DoctorFixtures.Read("real-wsl-status-es-docker-desktop-only.txt"), false)!;

        Assert.Equal("docker-desktop", reading.DefaultDistribution);
        Assert.Equal(2, reading.DefaultVersion);
        Assert.True(reading.IsDockerInternal);
        Assert.False(reading.IsReady);
    }

    [Theory]
    [InlineData("handmade-wsl-status-es-ubuntu.txt", "Ubuntu-24.04", 2, true)]
    [InlineData("handmade-wsl-status-en-ubuntu.txt", "Ubuntu", 2, true)]
    [InlineData("handmade-wsl-status-en-ubuntu-wsl1.txt", "Ubuntu", 1, false)]
    [InlineData("handmade-wsl-status-es-no-distribution.txt", null, 2, false)]
    [InlineData("handmade-wsl-status-en-no-distribution.txt", null, 2, false)]
    public void Wsl_is_read_by_the_shape_of_its_lines_in_both_languages(string fixture, string? distribution, int version, bool ready)
    {
        var reading = ToolOutputParsers.Wsl(DoctorFixtures.Read(fixture), false)!;

        Assert.Equal(distribution, reading.DefaultDistribution);
        Assert.Equal(version, reading.DefaultVersion);
        Assert.Equal(ready, reading.IsReady);
    }

    [Fact]
    public void Wsl_text_with_no_version_line_is_not_understood()
    {
        Assert.Null(ToolOutputParsers.Wsl(DoctorFixtures.Read("handmade-wsl-status-garbage.txt"), false));
        Assert.Null(ToolOutputParsers.Wsl("", false));
        Assert.Null(ToolOutputParsers.Wsl(DoctorFixtures.Read("handmade-wsl-status-en-ubuntu.txt"), truncated: true));
    }

    [Fact]
    public void Wsl_output_in_utf16_read_as_utf8_is_binary_and_is_not_understood()
    {
        // Without WSL_UTF8=1 the tool writes UTF-16 (checked on the development machine): read as UTF-8 it has NULs between the letters.
        var utf16 = Encoding.Unicode.GetBytes(DoctorFixtures.Read("real-wsl-status-es-docker-desktop-only.txt"));
        var misread = Encoding.UTF8.GetString(utf16);

        Assert.Null(ToolOutputParsers.Wsl(misread, false));
        Assert.Null(ToolOutputParsers.DotNetSdks(misread));
        Assert.Null(ToolOutputParsers.Docker(misread, false));
    }

    [Fact]
    public void A_hostile_output_is_bounded_and_never_throws()
    {
        var hugeLine = new string('x', 5_000_000) + "\n";
        var manyLines = string.Concat(Enumerable.Repeat("8.0.1 [C:\\a]\n", 100_000));
        var controlChars = "10.0.1 [C:\\a\u202Eb]\n";

        Assert.Null(ToolOutputParsers.DotNetSdks(hugeLine));
        Assert.True(ToolOutputParsers.DotNetSdks(manyLines)!.Count <= 200);
        // A path with a hidden character is not kept, but the version is.
        Assert.Null(ToolOutputParsers.DotNetSdks(controlChars)![0].Path);
        Assert.Null(ToolOutputParsers.Wsl(new string('a', 1_000_000), false));
    }
}
