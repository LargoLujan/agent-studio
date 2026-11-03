using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Terminal;

namespace AgentStudio.Tests.Infrastructure.Terminal;

public sealed class ShellLocatorTests
{
    private sealed class FakeLocator(Dictionary<string, string> found) : IExecutableLocator
    {
        public string? Find(string command) => found.GetValueOrDefault(command);
    }

    [Fact]
    public void Returns_only_installed_shells_in_preference_order()
    {
        var locator = new FakeLocator(new()
        {
            ["pwsh"] = @"C:\p\pwsh.exe",
            ["powershell"] = @"C:\w\powershell.exe",
        });
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\System32\cmd.exe" };
        var shells = new ShellLocator(locator, existing.Contains, name => name == "ComSpec" ? @"C:\Windows\System32\cmd.exe" : null)
            .GetAvailableShells();

        Assert.Equal(["pwsh", "powershell", "cmd"], shells.Select(s => s.Id).ToArray());
        Assert.Equal(@"C:\p\pwsh.exe", shells[0].Path);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", shells[2].Path);
    }

    [Fact]
    public void Git_bash_is_found_next_to_git_not_through_the_PATH_bash()
    {
        var gitExe = Path.Combine(@"C:\Tools\Git", "cmd", "git.exe");
        var bash = Path.Combine(@"C:\Tools\Git", "bin", "bash.exe");
        var locator = new FakeLocator(new() { ["git"] = gitExe, ["bash"] = @"C:\Windows\System32\bash.exe" });

        var shells = new ShellLocator(locator, path => path == bash, _ => null).GetAvailableShells();

        var gitBash = Assert.Single(shells);
        Assert.Equal("git-bash", gitBash.Id);
        Assert.Equal(bash, gitBash.Path);
        Assert.Contains("--login", gitBash.DefaultArguments);
    }

    [Fact]
    public void Git_bash_falls_back_to_the_default_install_location()
    {
        var bash = Path.Combine(@"C:\Program Files", "Git", "bin", "bash.exe");

        var shells = new ShellLocator(
            new FakeLocator([]),
            path => path == bash,
            name => name == "ProgramFiles" ? @"C:\Program Files" : null).GetAvailableShells();

        Assert.Equal(bash, Assert.Single(shells).Path);
    }

    [Fact]
    public void Nothing_installed_gives_an_empty_list()
    {
        Assert.Empty(new ShellLocator(new FakeLocator([]), _ => false, _ => null).GetAvailableShells());
    }

    [Fact]
    public void Real_machine_has_at_least_cmd()
    {
        var shells = new ShellLocator(new AgentStudio.Infrastructure.Processes.ExecutableLocator()).GetAvailableShells();

        Assert.Contains(shells, s => s.Id == "cmd" && File.Exists(s.Path));
    }
}
