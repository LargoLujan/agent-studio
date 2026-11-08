using AgentStudio.Application.Providers;
using AgentStudio.Domain.Models;
using AgentStudio.Domain.Providers;
using AgentStudio.Providers.Cli;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Tests.Providers.Claude;
using AgentStudio.Tests.Providers.Codex;

namespace AgentStudio.Tests.Providers;

/// <summary>
/// The effort of an agent on the command line of each CLI (docs/17 D-25, F13-11): Claude Code gets <c>--effort</c> and Codex
/// <c>-c model_reasoning_effort=…</c>, always a token of the closed list and never free text, only when the effort is set and
/// the CLI takes it. Without one, or with one the CLI does not take, the launch is byte for byte the one of before: the CLI
/// uses its own default, and the measured launch of ADR-0018 does not change.
/// </summary>
public sealed class EffortArgumentsTests
{
    private const string Session = "11111111-1111-1111-1111-111111111111";

    private static AgentRunRequest WithEffort(AgentRunRequest request, ReasoningEffort? effort) =>
        request with { Agent = request.Agent with { Effort = effort } };

    private static IReadOnlyList<string> Claude(ReasoningEffort? effort) =>
        ClaudeCodeArguments.Build(WithEffort(ClaudeTestSupport.Request(), effort), Session, null, "settings.json");

    private static IReadOnlyList<string> Codex(ReasoningEffort? effort, string? resume = null) =>
        CodexExecArguments.Build(WithEffort(CodexTestSupport.Request(resume: resume), effort), null, isRepository: true);

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.XHigh, "xhigh")]
    [InlineData(ReasoningEffort.Max, "max")]
    public void Claude_code_gets_the_effort_right_after_the_model(ReasoningEffort effort, string token)
    {
        var args = Claude(effort).ToList();
        var model = args.IndexOf("--model");

        Assert.Equal(["--model", "haiku", "--effort", token], args.Skip(model).Take(4));
        Assert.Single(args, a => a == "--effort");
    }

    [Theory]
    [InlineData(ReasoningEffort.Minimal, "minimal")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.XHigh, "xhigh")]
    public void Codex_gets_the_effort_as_a_bare_token_of_the_list_right_after_the_model(ReasoningEffort effort, string token)
    {
        var args = Codex(effort).ToList();
        var model = args.IndexOf("-m");

        Assert.Equal(["-m", "gpt-x", "-c", "model_reasoning_effort=" + token], args.Skip(model).Take(4));
        Assert.Equal("-", args[^1]);

        // Like the isolation block: no quote, no space and no character a .cmd shim would read.
        Assert.DoesNotContain(args[model + 3], c => c is '"' or '\'' or ' ' or '&' or '|' or '^' or '%' or '<' or '>');
    }

    [Fact]
    public void Codex_keeps_the_effort_before_resume_so_a_resumed_thread_gets_it_too()
    {
        var args = Codex(ReasoningEffort.High, resume: "01a1-thread").ToList();

        Assert.True(args.IndexOf("model_reasoning_effort=high") < args.IndexOf("resume"));
        Assert.Equal(["resume", "01a1-thread", "-"], args.TakeLast(3));
    }

    [Fact]
    public void Without_an_effort_each_launch_is_the_one_of_before()
    {
        Assert.DoesNotContain("--effort", Claude(null));
        Assert.DoesNotContain(Codex(null), a => a.Contains("reasoning", StringComparison.Ordinal));
        Assert.Equal(
            ["exec", "--json", "--ignore-user-config", "--ignore-rules", "--disable", "hooks", "-c", "windows.sandbox=unelevated", "-s", "read-only", "-C", "C:/work", "-m", "gpt-x", "-"],
            Codex(null));
    }

    /// <summary>An effort a CLI does not take, or a value outside the list (a template edited by hand), never breaks the launch.</summary>
    [Theory]
    [InlineData(ReasoningEffort.Minimal)]
    [InlineData((ReasoningEffort)42)]
    public void An_effort_claude_code_does_not_take_is_left_out_and_the_cli_uses_its_own(ReasoningEffort effort)
    {
        Assert.Equal(Claude(null), Claude(effort));
    }

    [Theory]
    [InlineData(ReasoningEffort.Max)]
    [InlineData((ReasoningEffort)42)]
    public void An_effort_codex_does_not_take_is_left_out_and_the_cli_uses_its_own(ReasoningEffort effort)
    {
        Assert.Equal(Codex(null), Codex(effort));
    }

    [Fact]
    public void The_efforts_each_launch_passes_are_exactly_the_ones_its_cli_takes()
    {
        foreach (var effort in Enum.GetValues<ReasoningEffort>())
        {
            Assert.Equal(CliReasoningEfforts.Accepts(ProviderId.ClaudeCode, effort), Claude(effort).Contains("--effort"));
            Assert.Equal(CliReasoningEfforts.Accepts(ProviderId.CodexCli, effort), Codex(effort).Any(a => a.StartsWith(CodexExecArguments.ReasoningEffortKey + "=", StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// ADR-0018: the relaxation of a writing executor holds only while its launch is the one measured. The effort says what to
    /// run, not what the session may do, so it never enters that fingerprint, and the measured launch is still the one listed.
    /// </summary>
    [Fact]
    public void The_effort_does_not_change_the_measured_launch_of_a_writing_executor()
    {
        Assert.Single(SupportedVersions.WriteConfinementMeasured(ProviderId.ClaudeCode, ClaudeCodeArguments.WriteConfinementLaunch));
        Assert.DoesNotContain("effort", ClaudeCodeArguments.WriteConfinementLaunch, StringComparison.Ordinal);
    }
}
