using AgentStudio.Providers.Cli;

namespace AgentStudio.Tests.Providers;

public sealed class CliArgumentGuardTests
{
    [Theory]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    [InlineData("\u0090")]
    [InlineData("\u009f")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\t")]
    public void Tool_rules_reject_control_characters_in_every_position(string control)
    {
        string[] rules =
        [
            control + "Read",
            "Re" + control + "ad",
            "Read" + control,
            "Read" + control + "(./src/**)",
            "Read(" + control + "./src/**)",
            "Read(./s" + control + "rc/**)",
            "Read(./src/**" + control + ")",
            "Read(./src/**)" + control,
        ];

        foreach (var rule in rules)
        {
            var error = Assert.Throws<ArgumentException>(() => CliArgumentGuard.ToolName(rule, "tool"));

            Assert.Equal("tool", error.ParamName);
        }
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("mcp__agentstudio__search_project_history")]
    [InlineData("Bash(git log:*)")]
    [InlineData("Read(./folder with spaces/**)")]
    [InlineData("Read(./données/niño.txt)")]
    [InlineData("Read(./資料/😀.txt)")]
    public void Tool_rules_preserve_names_spaces_and_ordinary_unicode(string rule)
    {
        Assert.Equal(rule, CliArgumentGuard.ToolName(rule, "tool"));
    }
}
