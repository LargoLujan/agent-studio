using AgentStudio.Infrastructure.Terminal;

namespace AgentStudio.Tests.Infrastructure.Terminal;

public sealed class WindowsCommandLineTests
{
    [Fact]
    public void Plain_arguments_are_not_quoted()
    {
        Assert.Equal("\"C:\\a\\sh.exe\" /d /k", WindowsCommandLine.Build(@"C:\a\sh.exe", ["/d", "/k"]));
    }

    [Fact]
    public void Executable_with_spaces_is_quoted()
    {
        Assert.Equal("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"", WindowsCommandLine.Build(@"C:\Program Files\PowerShell\7\pwsh.exe", []));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData(@"C:\plain\path", @"C:\plain\path")]
    [InlineData("-Command", "-Command")]
    public void Arguments_are_escaped_for_the_C_runtime(string argument, string expected)
    {
        var line = WindowsCommandLine.Build("x.exe", [argument]);

        Assert.Equal("\"x.exe\" " + expected, line);
    }

    [Fact]
    public void Quote_in_the_executable_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("a\"b.exe", []));
    }

    [Fact]
    public void Environment_block_merges_removes_sorts_and_double_terminates()
    {
        var baseline = new Dictionary<string, string?> { ["Zeta"] = "1", ["alpha"] = "2", ["Drop"] = "3" };
        var overrides = new Dictionary<string, string?> { ["ZETA"] = "9", ["Drop"] = null, ["New"] = "n" };

        var block = WindowsCommandLine.BuildEnvironmentBlock(baseline, overrides);

        Assert.Equal("alpha=2\0New=n\0Zeta=9\0\0", block);
    }

    [Fact]
    public void Empty_environment_block_still_has_two_terminators()
    {
        Assert.Equal("\0\0", WindowsCommandLine.BuildEnvironmentBlock(new Dictionary<string, string?>(), null));
    }
}
