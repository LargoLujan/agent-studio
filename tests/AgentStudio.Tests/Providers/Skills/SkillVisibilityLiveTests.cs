using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Application.Providers;
using AgentStudio.Application.Skills;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Providers;
using AgentStudio.Infrastructure.Processes;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Providers.Skills;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Providers.Codex;
using AgentStudio.Tests.Providers.Live;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Skills;

/// <summary>
/// Criterion F2-10, the "real run per CLI" half: the real <see cref="SkillMaterializer"/> puts a probe skill (unique name,
/// unique codeword) where each CLI looks for skills, and the real CLI is asked about it. They spend subscription quota,
/// so they need AGENTSTUDIO_PROVIDER_TESTS=1.
/// <list type="bullet">
/// <item>Claude Code lists plugins and skills in its <c>system/init</c> event, so the assertion rests on that event and
/// the model's answer is a second, independent confirmation.</item>
/// <item>Codex prints no skill list in <c>exec --json</c>; its own <c>debug prompt-input</c> renders the model-visible
/// prompt (no quota) and the model's answer to a real <c>exec</c> run confirms it end to end.</item>
/// </list>
/// </summary>
[Trait("Category", "ProviderLive")]
public sealed class SkillVisibilityLiveTests
{
    // "Fast and affordable model for easier tasks" in `codex debug models` (0.160.0): enough to read a skill list.
    private const string CodexModel = "gpt-6-luna";

    // Answerable from the skill list alone: the codeword is in the skill's description.
    private static string AskForListedCodeword(ProbeSkill probe) =>
        $"Without running any command or using any tool: look in your list of available skills for the skill named {probe.Name} and reply with only the codeword listed in its description.";

    // Needs the skill to be invoked: the second codeword is only in the body of its SKILL.md.
    private static string AskToInvoke(ProbeSkill probe) =>
        $"Find the skill named {probe.Name} in your list of available skills and invoke it with the Skill tool. Then reply with exactly two codewords separated by a space: first the codeword listed in its description, then the body codeword the skill gave you.";

    [Fact]
    public async Task Real_claude_sees_a_skill_materialized_as_a_session_plugin()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var probe = ProbeSkill.Create(temp.CreateDirectory("catalog"));
        var workingDirectory = temp.CreateDirectory("work");
        var sessions = temp.CreateDirectory("sessions");

        var materializer = new SkillMaterializer();
        var pluginDirectory = await materializer.MaterializeAsync(
            ProviderId.ClaudeCode,
            [probe.Activated],
            new SkillSessionContext("f2-10-claude", sessions, workingDirectory),
            token);
        Assert.NotNull(pluginDirectory);
        Assert.True(LiveTestSupport.IsUnder(pluginDirectory, sessions), "The session plugin must live in the session folder, not in a CLI configuration folder.");

        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        try
        {
            var provider = new ClaudeCodeAgentProvider(new ExecutableLocator(), recorder, TimeProvider.System);
            var request = Request(
                prompt: AskToInvoke(probe),
                model: "haiku",
                budget: new ExecutionBudget(4, TimeSpan.FromMinutes(3)),
                workingDirectory: workingDirectory,
                skills: pluginDirectory);

            await using var run = await provider.StartAsync(request, token);
            var events = await LiveTestSupport.DrainAsync(run, token);
            var outcome = await run.Completion;

            var process = Assert.Single(recorder.Processes);
            LiveTestSupport.SaveCapture(process, "f2-10-claude-skill-plugin");

            // The session plugin travelled in --plugin-dir; the prompt and the secret only in stdin.
            var arguments = process.Spec.Arguments.ToList();
            var flag = arguments.IndexOf("--plugin-dir");
            Assert.True(flag >= 0 && flag + 1 < arguments.Count, "claude was not launched with --plugin-dir.");
            Assert.Equal(pluginDirectory, arguments[flag + 1]);
            Assert.DoesNotContain(arguments, a => a.Contains(probe.Codeword, StringComparison.OrdinalIgnoreCase) || a.Contains(probe.BodyCodeword, StringComparison.OrdinalIgnoreCase));

            // Primary proof, no model involved: the init event of the CLI lists the plugin and its skill.
            var init = ClaudeInitEvent.Find(process.StandardOutput);
            Assert.NotNull(init);
            Assert.True(init.ListsSkill(probe.Name), $"init.skills does not list '{probe.Name}'. Listed: {string.Join(", ", init.Skills)}");
            var plugin = Assert.Single(init.Plugins, p => p.Name == "agent-studio-session");
            Assert.NotNull(plugin.Path);
            Assert.Equal(Path.GetFullPath(pluginDirectory), Path.GetFullPath(plugin.Path), StringComparer.OrdinalIgnoreCase);

            // Second confirmation, from the model: it read the listed codeword from the skill list it was given and, after
            // invoking the skill, the body codeword that only exists in SKILL.md.
            LiveTestSupport.Note($"F2-10 claude: the model answered '{outcome.FinalText}' (listed '{probe.Codeword}', body '{probe.BodyCodeword}')");
            Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
            Assert.Contains(probe.Codeword, outcome.FinalText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(probe.BodyCodeword, outcome.FinalText ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(events.OfType<ToolInvoked>(), invoked => invoked.Tool == "Skill");
            Assert.Contains(events.OfType<ToolCompleted>(), completed => completed.Tool == "Skill" && completed.Succeeded);

            LiveTestSupport.Note($"F2-10 claude: init.skills lists the probe as '{string.Join(", ", init.Skills.Where(s => s.EndsWith(probe.Name, StringComparison.Ordinal)))}' and init.plugins has '{plugin.Name}' ({plugin.Source}) at {plugin.Path}");
            foreach (var cost in events.OfType<UsageReported>().Select(u => u.Usage.CostUsd.Value))
            {
                LiveTestSupport.Note($"F2-10 claude: estimated cost reported by the CLI: {cost} USD");
            }
        }
        finally
        {
            materializer.Cleanup(pluginDirectory);
        }

        Assert.False(Directory.Exists(pluginDirectory), "Cleanup must remove the session plugin.");
        Assert.True(File.Exists(Path.Combine(probe.Activated.LocalPath, "SKILL.md")), "Cleanup must never touch the catalog.");
    }

    [Fact]
    public async Task Real_codex_sees_a_skill_materialized_into_agents_skills()
    {
        LiveTestSupport.RequireOptIn();
        var token = TestContext.Current.CancellationToken;
        var locator = new ExecutableLocator();
        var located = locator.Find("codex");
        Assert.SkipUnless(located is not null, "Codex CLI is not installed.");
        var codex = CodexExecutableResolver.Resolve(located!);

        using var temp = new TempDirectory();
        var probe = ProbeSkill.Create(temp.CreateDirectory("catalog"));
        var repository = temp.CreateDirectory("repo");
        await LiveTestSupport.InitGitRepositoryAsync(repository, token);

        var materializer = new SkillMaterializer();
        var skillsDirectory = await materializer.MaterializeAsync(
            ProviderId.CodexCli,
            [probe.Activated],
            new SkillSessionContext("f2-10-codex", temp.CreateDirectory("sessions"), repository),
            token);
        Assert.NotNull(skillsDirectory);
        Assert.Equal(Path.Combine(repository, ".agents", "skills"), skillsDirectory);
        Assert.True(File.Exists(Path.Combine(skillsDirectory, probe.Name, "SKILL.md")));

        // The copy is excluded through .git/info/exclude: the repository the agent works in stays clean.
        Assert.Equal(string.Empty, await LiveTestSupport.GitStatusAsync(repository, token));

        var recorder = new RecordingProcessRunner(new StreamingProcessRunner());
        try
        {
            // Deterministic proof, no model and no quota: Codex renders the prompt it would send, and the skill is in it.
            var rendered = await new ProcessRunner().RunAsync(
                new ProcessSpec(codex, ["debug", "prompt-input"], repository, Timeout: TimeSpan.FromMinutes(1)),
                token);
            Assert.True(rendered.ExitCode == 0, "codex debug prompt-input failed: " + rendered.StandardError);
            Assert.Contains(probe.Name, rendered.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(probe.Codeword, rendered.StandardOutput, StringComparison.OrdinalIgnoreCase);
            var listed = rendered.StandardOutput.IndexOf(probe.Name, StringComparison.Ordinal);
            LiveTestSupport.Note($"F2-10 codex: debug prompt-input lists: {rendered.StandardOutput.AsSpan(listed, Math.Min(220, rendered.StandardOutput.Length - listed))}");

            // The real run: the model answers from the skill list it was given.
            var provider = new CodexCliAgentProvider(locator, recorder, TimeProvider.System);
            var request = CodexTestSupport.Request(
                workingDirectory: repository,
                prompt: AskForListedCodeword(probe),
                model: CodexModel,
                maxDuration: TimeSpan.FromMinutes(3));

            await using var run = await provider.StartAsync(request, token);
            var events = await LiveTestSupport.DrainAsync(run, token);
            var outcome = await run.Completion;

            var process = Assert.Single(recorder.Processes);
            LiveTestSupport.SaveCapture(process, "f2-10-codex-skill-agents");

            Assert.DoesNotContain(process.Spec.Arguments, a => a.Contains(probe.Codeword, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(probe.Codeword, process.Spec.StandardInput ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.True(outcome.Status == AgentRunStatus.Succeeded, outcome.Error);
            Assert.Contains(probe.Codeword, outcome.FinalText ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var usage = Assert.Single(events.OfType<UsageReported>()).Usage;
            LiveTestSupport.Note($"F2-10 codex: the model answered '{outcome.FinalText}' (codeword '{probe.Codeword}')");
            LiveTestSupport.Note($"F2-10 codex: tokens input {usage.InputTokens.Value}, output {usage.OutputTokens.Value}, cached {usage.CachedInputTokens.Value}");
        }
        finally
        {
            materializer.Cleanup(skillsDirectory);
        }

        Assert.False(Directory.Exists(Path.Combine(repository, ".agents")), "Cleanup must remove the folders it created.");
        Assert.True(File.Exists(Path.Combine(probe.Activated.LocalPath, "SKILL.md")), "Cleanup must never touch the catalog.");
    }
}
