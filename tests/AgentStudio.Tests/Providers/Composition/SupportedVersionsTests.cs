using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli;

namespace AgentStudio.Tests.Providers.Composition;

public sealed class SupportedVersionsTests
{
    private sealed class FakeLocator : IExecutableLocator
    {
        public string? Find(string command) => "C:/bin/claude.exe";
    }

    private sealed class FakeRunner(string version) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult(spec.Arguments[0] == "--version"
                ? new ProcessResult(0, version, "", TimeSpan.FromMilliseconds(5), false)
                : new ProcessResult(0, """{"loggedIn": true, "authMethod": "claude.ai"}""", "", TimeSpan.FromMilliseconds(5), false));
    }

    [Fact]
    public void TestedVersionsMatchTheMatrix()
    {
        Assert.Equal(["2.1.290"], SupportedVersions.For(ProviderId.ClaudeCode));
        Assert.Equal(["0.160.0"], SupportedVersions.For(ProviderId.CodexCli));
        Assert.Equal(["0.64.0"], SupportedVersions.For(ProviderId.GeminiCli));
        Assert.Null(SupportedVersions.UntestedNote(ProviderId.CodexCli, "0.160.0"));
        Assert.Contains("0.160.0", SupportedVersions.UntestedNote(ProviderId.CodexCli, "0.161.0"));
    }

    [Fact]
    public async Task Probe_AddsNoteForUntestedVersionWithoutChangingInstallationState()
    {
        var probe = new ClaudeCodeProbe(new FakeLocator(), new FakeRunner("2.9.9 (Claude Code)"));

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InstallationState.Installed, result.Installation);
        Assert.Equal("2.9.9", result.Version);
        Assert.Contains(result.Notes, n => n.Contains("Versión no probada con Agent Studio (probadas: 2.1.290)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_NoNoteForTestedVersion()
    {
        var probe = new ClaudeCodeProbe(new FakeLocator(), new FakeRunner("2.1.290 (Claude Code)"));

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Notes, n => n.Contains("no probada", StringComparison.OrdinalIgnoreCase));
    }
}
