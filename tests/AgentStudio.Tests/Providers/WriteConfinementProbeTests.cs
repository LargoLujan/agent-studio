using System.ComponentModel;
using AgentStudio.Application.Execution;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Claude;

namespace AgentStudio.Tests.Providers;

/// <summary>
/// ADR-0018, «Medición»: the engine relaxes the check of the user's repository for an executor without a shell only on
/// a CLI whose installed version was measured to keep its writes in the worktree, with the launch the adapter builds
/// today. Claude Code 2.1.290 is: in <c>dontAsk</c> with path rules and <c>--tools</c> limited to the tools of the profile
/// it neither wrote nor read above its worktree (in <c>acceptEdits</c> with bare file tools it had written there). The
/// probe reads the installed version with
/// <c>--version</c> (no prompt, no quota) and fails closed.
/// </summary>
public sealed class WriteConfinementProbeTests
{
    private const string Claude = "C:/bin/claude.exe";

    private sealed class FakeLocator(string? path) : IExecutableLocator
    {
        public List<string> Asked { get; } = [];

        public string? Find(string command)
        {
            Asked.Add(command);
            return path;
        }
    }

    private sealed class FakeRunner(Func<ProcessSpec, ProcessResult> respond) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            return Task.FromResult(respond(spec));
        }
    }

    private static ProcessResult Ok(string stdout, int exit = 0, bool timedOut = false) => new(exit, stdout, "", TimeSpan.FromMilliseconds(5), timedOut);

    /// <summary>A list of measured versions as it would be once one is measured: Claude Code 9.9.9, nothing else.</summary>
    private static IReadOnlyList<string> Measured999(ProviderId provider) => provider == ProviderId.ClaudeCode ? ["9.9.9"] : [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Claude_Code_2_1_290_is_the_one_version_listed_as_confined_and_no_other_CLI_has_one()
    {
        Assert.Equal(["2.1.290"], SupportedVersions.WriteConfinementMeasured(ProviderId.ClaudeCode));
        Assert.Empty(SupportedVersions.WriteConfinementMeasured(ProviderId.CodexCli));
        Assert.Empty(SupportedVersions.WriteConfinementMeasured(ProviderId.GeminiCli));
    }

    [Fact]
    public void A_version_is_listed_only_while_the_adapter_builds_the_launch_it_was_measured_with()
    {
        Assert.True(
            SupportedVersions.WriteConfinementMeasured(ProviderId.ClaudeCode, ClaudeCodeArguments.WriteConfinementLaunch).Count == 1,
            "The launch of Claude Code changed since its confinement was measured: run ClaudeWriteConfinementLiveTests on it and register the new launch in SupportedVersions.");

        // Any other launch, back to acceptEdits for one, turns the relaxation off: nobody measured it.
        var other = ClaudeCodeArguments.WriteConfinementLaunch.Replace("dontAsk", "acceptEdits", StringComparison.Ordinal);
        Assert.Empty(SupportedVersions.WriteConfinementMeasured(ProviderId.ClaudeCode, other));
        Assert.Empty(SupportedVersions.WriteConfinementMeasured(ProviderId.ClaudeCode, null));
    }

    [Fact]
    public async Task Claude_Code_2_1_290_as_installed_is_confined_and_only_its_version_is_asked()
    {
        var locator = new FakeLocator(Claude);
        var runner = new FakeRunner(_ => Ok("2.1.290 (Claude Code)"));
        var probe = new CliWriteConfinementProbe(locator, runner);

        Assert.True(await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct));
        Assert.Equal(["claude"], locator.Asked);
        Assert.Equal(["--version"], Assert.Single(runner.Calls).Arguments);
    }

    [Theory]
    [InlineData("2.1.291 (Claude Code)")]
    [InlineData("2.1.289 (Claude Code)")]
    public async Task Claude_Code_at_a_version_never_measured_is_not_confined(string output)
    {
        var probe = new CliWriteConfinementProbe(new FakeLocator(Claude), new FakeRunner(_ => Ok(output)));

        Assert.False(await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct));
    }

    [Fact]
    public async Task A_CLI_at_a_version_listed_as_measured_keeps_its_writes_in_the_worktree()
    {
        var runner = new FakeRunner(_ => Ok("9.9.9 (Claude Code)"));
        var probe = new CliWriteConfinementProbe(new FakeLocator(Claude), runner, Measured999);

        Assert.True(await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct));

        var call = Assert.Single(runner.Calls);
        Assert.Equal(Claude, call.FileName);
        Assert.Equal(["--version"], call.Arguments);
        Assert.Null(call.StandardInput);
        Assert.Equal(TimeSpan.FromSeconds(20), call.Timeout);
    }

    [Theory]
    [InlineData("9.9.10 (Claude Code)")]
    [InlineData("2.1.290 (Claude Code)")]
    [InlineData("Claude Code")]
    [InlineData("")]
    public async Task A_CLI_at_any_other_version_or_one_it_cannot_say_counts_as_able_to_write_anywhere(string output)
    {
        var runner = new FakeRunner(_ => Ok(output));
        var probe = new CliWriteConfinementProbe(new FakeLocator(Claude), runner, Measured999);

        Assert.False(await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("not installed")]
    [InlineData("exit code")]
    [InlineData("timeout")]
    [InlineData("cannot start")]
    public async Task A_CLI_that_cannot_be_asked_counts_as_able_to_write_anywhere(string failure)
    {
        var locator = new FakeLocator(failure == "not installed" ? null : Claude);
        var runner = new FakeRunner(_ => failure switch
        {
            "exit code" => Ok("9.9.9 (Claude Code)", exit: 1),
            "timeout" => Ok("9.9.9 (Claude Code)", timedOut: true),
            "cannot start" => throw new Win32Exception(2, "The system cannot find the file specified."),
            _ => Ok("9.9.9 (Claude Code)"),
        });
        var probe = new CliWriteConfinementProbe(locator, runner, Measured999);

        Assert.False(await probe.IsWriteConfinedAsync(ProviderId.ClaudeCode, Ct));
        Assert.Equal(["claude"], locator.Asked);
    }

    [Fact]
    public async Task A_CLI_with_no_version_listed_is_not_asked_and_not_confined()
    {
        var locator = new FakeLocator("C:/bin/cli.exe");
        var runner = new FakeRunner(_ => Ok("9.9.9"));
        var probe = new CliWriteConfinementProbe(locator, runner, Measured999);

        Assert.False(await probe.IsWriteConfinedAsync(ProviderId.CodexCli, Ct));
        Assert.False(await probe.IsWriteConfinedAsync(ProviderId.GeminiCli, Ct));
        Assert.False(await probe.IsWriteConfinedAsync(new ProviderId("otro-cli"), Ct));
        Assert.Empty(locator.Asked);
        Assert.Empty(runner.Calls);
    }
}
