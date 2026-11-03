using System.IO;
using System.Text.Json;
using AgentStudio.Application.Providers;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Skills;
using AgentStudio.Providers.Cli.Claude;
using AgentStudio.Providers.Cli.Codex;
using AgentStudio.Tests.Desktop;
using AgentStudio.Tests.Providers.Codex;
using AgentStudio.Tests.Providers.Live;
using static AgentStudio.Tests.Providers.Claude.ClaudeTestSupport;

namespace AgentStudio.Tests.Providers.Skills;

/// <summary>
/// Criterion F2-10 without spending quota: the real captures of the skill-visibility runs (see Fixtures/README.md) and the
/// helpers the live tests rely on. A format change in a CLI, or a probe the CLIs would reject, breaks a test here instead
/// of a paid run.
/// </summary>
public sealed class SkillVisibilityFixtureTests
{
    private const string PluginPrefix = "agent-studio-session:";

    [Fact]
    public void The_claude_init_event_lists_the_session_plugin_and_its_namespaced_skill()
    {
        var init = ClaudeInitEvent.Find(FixtureLines("04-skill-plugin.jsonl"));

        Assert.NotNull(init);
        var skill = Assert.Single(init.Skills, s => s.StartsWith(PluginPrefix, StringComparison.Ordinal));
        Assert.Matches(@"^agent-studio-session:agentstudio-probe-skill-[0-9a-f]{8}$", skill);
        Assert.True(init.ListsSkill(skill[PluginPrefix.Length..]));

        var plugin = Assert.Single(init.Plugins, p => p.Name == "agent-studio-session");
        Assert.Equal("agent-studio-session@inline", plugin.Source);
        Assert.NotNull(plugin.Path);
        Assert.EndsWith("claude-plugin-f2-10-claude", plugin.Path, StringComparison.Ordinal);
        Assert.Equal("claude-haiku-4-5-20251001", init.Model);
    }

    [Fact]
    public void The_claude_capture_shows_that_setting_sources_project_and_local_keep_the_users_plugins_out()
    {
        var init = ClaudeInitEvent.Find(FixtureLines("04-skill-plugin.jsonl"));

        Assert.NotNull(init);
        // Only Claude's own bundled plugins and the session plugin: nothing from the user's profile.
        Assert.All(init.Plugins.Where(p => p.Name != "agent-studio-session"), p => Assert.Equal("builtin", p.Path));
        Assert.Single(init.Skills, s => s.StartsWith(PluginPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(init.Skills, s => s.Contains("fabric", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_claude_capture_parses_the_skill_invocation_and_the_answer_with_both_codewords()
    {
        var parser = new ClaudeStreamJsonParser(TimeProvider.System);
        var events = FixtureLines("04-skill-plugin.jsonl").SelectMany(parser.Parse).ToList();

        Assert.Equal(0, parser.IgnoredLines);
        // Two steps: the one that invokes the skill and the one that answers; each opens with its context (docs/14 D-09).
        Assert.Equal(
            [typeof(QuotaReported), typeof(SessionStarted), typeof(ContextObserved), typeof(ToolInvoked), typeof(ToolCompleted), typeof(ContextObserved), typeof(MessageEmitted), typeof(UsageReported), typeof(RunFinished)],
            events.Select(e => e.GetType()).ToArray());
        Assert.Equal("Skill", events.OfType<ToolInvoked>().Single().Tool);
        var completed = events.OfType<ToolCompleted>().Single();
        Assert.Equal("Skill", completed.Tool);
        Assert.True(completed.Succeeded);

        // The listed codeword comes from the skill list, the body one from invoking the skill.
        var answer = events.OfType<MessageEmitted>().Single().Text;
        Assert.Matches(@"^[A-Z]+-[A-Z]+-\d{4} [A-Z]+-[A-Z]+-\d{4}$", answer);
        var outcome = events.OfType<RunFinished>().Single().Outcome;
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal(answer, outcome.FinalText);
        Assert.Equal(MetricProvenance.Estimated, events.OfType<UsageReported>().Single().Usage.CostUsd.Provenance);
    }

    [Fact]
    public void The_claude_capture_shows_that_invoking_a_session_skill_needs_no_extra_permission_in_dont_ask_mode()
    {
        // dontAsk denies whatever is not pre-approved, and the adapter pre-approves no tool for a skill: none was denied.
        Assert.Equal("dontAsk", ReadEvent("system", "init", root => root.GetProperty("permissionMode").GetString()));
        Assert.Equal(0, ReadEvent("result", null, root => root.GetProperty("permission_denials").GetArrayLength()));
    }

    private static T ReadEvent<T>(string type, string? subtype, Func<JsonElement, T> read)
    {
        foreach (var line in FixtureLines("04-skill-plugin.jsonl").Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var matchesSubtype = subtype is null || (root.TryGetProperty("subtype", out var found) && found.GetString() == subtype);
            if (root.GetProperty("type").GetString() == type && matchesSubtype)
            {
                return read(root);
            }
        }

        throw new InvalidOperationException($"The fixture has no '{type}' event.");
    }

    [Fact]
    public void The_codex_capture_parses_and_the_model_answers_with_the_codeword_from_its_skill_list()
    {
        var parser = new CodexExecJsonParser("gpt-6-luna");
        var events = CodexTestSupport.FixtureLines("04-skill-agents.jsonl").SelectMany(l => parser.Parse(l, DateTimeOffset.UnixEpoch)).ToList();

        var started = Assert.IsType<SessionStarted>(events[0]);
        Assert.Equal("gpt-6-luna", started.Model);
        var answer = Assert.Single(events.OfType<MessageEmitted>()).Text;
        Assert.Matches(@"^[A-Z]+-[A-Z]+-\d{4}$", answer);
        var outcome = Assert.IsType<RunFinished>(events[^1]).Outcome;
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Status);
        Assert.Equal(answer, outcome.FinalText);
        Assert.Null(parser.LastError);
    }

    [Fact]
    public void The_init_helper_ignores_what_is_not_an_init_event_and_tolerates_missing_lists()
    {
        Assert.False(ClaudeInitEvent.TryParse("", out _));
        Assert.False(ClaudeInitEvent.TryParse("not json", out _));
        Assert.False(ClaudeInitEvent.TryParse("""{"type":"result","subtype":"success"}""", out _));
        Assert.False(ClaudeInitEvent.TryParse("""{"type":"system","subtype":"hook_started"}""", out _));
        Assert.Null(ClaudeInitEvent.Find(["garbage", """{"type":"assistant"}"""]));

        Assert.True(ClaudeInitEvent.TryParse("""{"type":"system","subtype":"init","session_id":"s1"}""", out var bare));
        Assert.Equal("s1", bare.SessionId);
        Assert.Empty(bare.Skills);
        Assert.Empty(bare.Plugins);
    }

    [Fact]
    public void ListsSkill_matches_the_plain_or_namespaced_name_and_nothing_else()
    {
        Assert.True(ClaudeInitEvent.TryParse("""{"type":"system","subtype":"init","skills":["plain","pl:ns-skill"]}""", out var init));

        Assert.True(init.ListsSkill("plain"));
        Assert.True(init.ListsSkill("ns-skill"));
        Assert.False(init.ListsSkill("skill"));
        Assert.False(init.ListsSkill("pl"));
    }

    [Fact]
    public void The_probe_skill_is_a_valid_skill_with_a_unique_name_and_a_codeword_only_in_its_files()
    {
        using var temp = new TempDirectory();

        var one = ProbeSkill.Create(temp.CreateDirectory("one"));
        var two = ProbeSkill.Create(temp.CreateDirectory("two"));

        Assert.Matches(@"^agentstudio-probe-skill-[0-9a-f]{8}$", one.Name);
        Assert.NotEqual(one.Name, two.Name);
        Assert.NotEqual(one.Codeword, two.Codeword);
        Assert.Matches(@"^[A-Z]+-[A-Z]+-\d{4}$", one.Codeword);
        Assert.Matches(@"^[A-Z]+-[A-Z]+-\d{4}$", one.BodyCodeword);
        Assert.NotEqual(one.Codeword, one.BodyCodeword);

        var markdown = File.ReadAllText(Path.Combine(one.Activated.LocalPath, "SKILL.md"));
        var frontmatter = SkillFrontmatter.Parse(markdown);
        Assert.Equal(one.Name, frontmatter.Name);

        // The listed codeword is in the description (what the CLIs list); the body one only below the frontmatter.
        Assert.Contains(one.Codeword, frontmatter.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(one.BodyCodeword, frontmatter.Description, StringComparison.Ordinal);
        Assert.Contains(one.BodyCodeword, markdown, StringComparison.Ordinal);
        Assert.Equal(Path.GetFileName(one.Activated.LocalPath), one.Name);
        var checks = frontmatter.Validate(one.Name);
        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.Equal(SkillHealthStatus.Healthy, check.Status));
    }
}
