using AgentStudio.Desktop.ViewModels;

namespace AgentStudio.Tests.Desktop;

/// <summary>Executable paths are shown with the user's profile folder collapsed to "~" (privacy in screenshots).</summary>
public sealed class PathDisplayTests
{
    [Theory]
    [InlineData(@"C:\Users\ana\.local\bin\claude.exe", @"C:\Users\ana", @"~\.local\bin\claude.exe")]
    [InlineData(@"c:\users\ANA\AppData\Roaming\npm\codex.cmd", @"C:\Users\ana", @"~\AppData\Roaming\npm\codex.cmd")]
    [InlineData(@"C:\Users\ana", @"C:\Users\ana", "~")]
    [InlineData(@"C:\Users\anabel\tool.exe", @"C:\Users\ana", @"C:\Users\anabel\tool.exe")]
    [InlineData(@"D:\tools\gemini.cmd", @"C:\Users\ana", @"D:\tools\gemini.cmd")]
    [InlineData(@"C:\Users\ana\x.exe", "", @"C:\Users\ana\x.exe")]
    public void Home_folder_is_collapsed_only_on_a_folder_boundary(string path, string home, string expected)
    {
        Assert.Equal(expected, PathDisplay.CollapseHome(path, home));
    }

    [Fact]
    public void Status_items_never_show_the_real_profile_folder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new AgentStudio.Application.Providers.ProviderProbeResult(
            AgentStudio.Domain.Providers.ProviderId.ClaudeCode,
            "Claude Code",
            AgentStudio.Application.Providers.InstallationState.Installed,
            home + @"\.local\bin\claude.exe",
            "1.0.0",
            AgentStudio.Application.Providers.AuthenticationState.Authenticated,
            "claude.ai",
            []);

        var item = ProviderStatusItem.From(result);

        Assert.Equal(@"~\.local\bin\claude.exe", item.ExecutablePath);
    }
}
