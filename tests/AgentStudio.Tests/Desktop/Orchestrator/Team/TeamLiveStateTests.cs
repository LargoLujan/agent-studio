using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Team;
using System.Windows.Automation;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// F13-6 and F13-7 (docs/17 D-12, D-14 to D-16): the bars of the nodes and the cards with their origin («no expuesto» with an
/// empty track and the words, never a bar at zero), the states of the agents, the cables that move only while the task writes
/// (twelve at most, none with «Reducir movimiento»), and a grouped change that gives their state only to the nodes it names.
/// </summary>
public sealed class TeamLiveStateTests
{
    private static LiveContext Context(long used, long window, string model = "sonnet") =>
        new(model, MetricValue<long>.Exact(used, "claude contextObserved"), MetricValue<long>.Exact(window, "claude modelUsage"), []);

    [Fact]
    public void A_card_without_observations_says_so_with_an_empty_track_and_codex_says_it_is_not_exposed()
    {
        var team = LoadedTeam();
        var claude = team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
        var codex = team.Agents.Single(a => a.Id == DefaultAgentRoster.Judge);

        Assert.Null(claude.ContextBar.Fraction);
        Assert.Contains("sin observaciones todavía", claude.ContextBar.Text, StringComparison.Ordinal);
        Assert.Null(codex.ContextBar.Fraction);
        Assert.Equal(MetricOrigin.NotExposed, codex.ContextBar.OriginKind);
        Assert.Contains("no expuesto", codex.ContextBar.Text, StringComparison.Ordinal);
        Assert.Null(claude.ConsumptionBar.Fraction);
        Assert.Contains("sin intentos todavía", claude.ConsumptionBar.Text, StringComparison.Ordinal);
        Assert.Null(claude.BudgetBar.Fraction);
        Assert.Equal("máximo sin dato", claude.BudgetBar.Origin);
    }

    [Fact]
    public void The_bars_follow_the_live_state_with_their_origin_and_the_budget_against_the_maximum()
    {
        var team = LoadedTeam();
        var executor = DefaultAgentRoster.Executor;
        var state = State(
            1,
            12,
            false,
            Live(executor, AgentLiveStatus.Working, new TaskId("T2"), Context(47_000, 100_000), runs: 3, input: MetricValue<long>.Exact(120_000, "claude result.usage")));

        team.ApplyLive(state);

        var node = team.Agents.Single(a => a.Id == executor);
        Assert.Equal(0.47, node.ContextBar.Fraction!.Value, 2);
        Assert.Equal("exacto", node.ContextBar.ShortOrigin);
        Assert.Equal("Trabajando en T2", node.StatusText);
        Assert.Equal(AgentLiveStatus.Working, node.Status);
        Assert.Equal(3, node.BudgetBar.Value);
        Assert.Equal(12, node.BudgetBar.Maximum);
        Assert.Equal("exacto", node.ConsumptionBar.ShortOrigin);
        Assert.Contains("120", node.ConsumptionBar.ValueText, StringComparison.Ordinal);
        Assert.Contains("Trabajando en T2", node.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void An_estimated_context_carries_the_tilde_and_never_shows_as_exact()
    {
        var team = LoadedTeam();
        var executor = DefaultAgentRoster.Executor;
        var estimated = new LiveContext(
            "sonnet",
            MetricValue<long>.EstimatedBySystem(30_000, "estimate: 4 chars/token"),
            MetricValue<long>.Exact(100_000, "table"),
            []);

        team.ApplyLive(State(1, 10, false, Live(executor, AgentLiveStatus.Working, new TaskId("T1"), estimated)));

        var bar = team.Agents.Single(a => a.Id == executor).ContextBar;
        Assert.True(bar.IsEstimate);
        Assert.Equal("estimado", bar.ShortOrigin);
        Assert.Contains("~", bar.ValueText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentLiveStatus.Asking, AutomationLiveSetting.Assertive, Tone.Warning)]
    [InlineData(AgentLiveStatus.Blocked, AutomationLiveSetting.Assertive, Tone.Error)]
    [InlineData(AgentLiveStatus.Working, AutomationLiveSetting.Polite, Tone.Active)]
    [InlineData(AgentLiveStatus.Idle, AutomationLiveSetting.Polite, Tone.Neutral)]
    [InlineData(AgentLiveStatus.Finished, AutomationLiveSetting.Polite, Tone.Success)]
    public void Each_state_has_its_text_its_icon_its_tone_and_how_it_is_announced(AgentLiveStatus status, AutomationLiveSetting setting, Tone tone)
    {
        var team = LoadedTeam();
        var agent = DefaultAgentRoster.Executor;

        team.ApplyLive(State(1, 10, false, Live(agent, status, new TaskId("T3"), why: "faltan pruebas")));

        var node = team.Agents.Single(a => a.Id == agent);
        Assert.Equal(setting, node.LiveSetting);
        Assert.Equal(tone, node.Tone);
        Assert.NotEmpty(node.StatusIcon);
        Assert.NotEmpty(node.StatusText);
        Assert.Contains(node.StatusText, node.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_says_why_and_the_states_are_in_words_in_both_languages()
    {
        foreach (var culture in new[] { Spanish, English })
        {
            var team = LoadedTeam(text: Text(culture));
            team.ApplyLive(State(1, 10, false, Live(DefaultAgentRoster.Executor, AgentLiveStatus.Blocked, why: "cuota")));
            var text = team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor).StatusText;
            Assert.EndsWith("cuota", text, StringComparison.Ordinal);
            Assert.StartsWith(culture == Spanish ? "Bloqueado" : "Blocked", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_grouped_change_gives_their_state_only_to_the_nodes_it_names()
    {
        var team = LoadedTeam();
        var executor = team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
        var judge = team.Agents.Single(a => a.Id == DefaultAgentRoster.Judge);
        var first = State(1, 10, false, Live(executor.Id, AgentLiveStatus.Working, new TaskId("T1")), Live(judge.Id, AgentLiveStatus.Idle));
        team.ApplyLive(first);

        var second = State(2, 10, false, Live(executor.Id, AgentLiveStatus.Finished), Live(judge.Id, AgentLiveStatus.Working, new TaskId("T2")));
        team.ApplyLive(second, [executor.Id.Value]);

        Assert.Equal(AgentLiveStatus.Finished, executor.Status);
        Assert.Equal(AgentLiveStatus.Idle, judge.Status);
        Assert.Same(first.Agents[judge.Id], judge.Live);
    }

    [Fact]
    public void The_cable_works_in_moves_only_while_the_task_writes_and_never_with_reduced_motion()
    {
        var team = LoadedTeam();
        var executor = DefaultAgentRoster.Executor;

        team.ApplyLive(State(1, 10, false, Live(executor, AgentLiveStatus.Working, new TaskId("T1"))));
        Assert.True(team.Agents.Single(a => a.Id == executor).IsWriting);
        Assert.Equal(1, team.Connections.Count(c => c.IsAnimated));
        Assert.All(team.Connections.Where(c => c.IsAnimated), c => Assert.Equal(TeamEdgeKind.WorksIn, c.Kind));

        team.AnimationsEnabled = false;
        Assert.Equal(0, team.AnimatedCount);
        Assert.Equal("Trabajando en T1", team.Agents.Single(a => a.Id == executor).StatusText);

        team.AnimationsEnabled = true;
        team.ApplyLive(State(2, 10, false, Live(executor, AgentLiveStatus.Idle)));
        Assert.Equal(0, team.AnimatedCount);
    }

    [Fact]
    public void No_more_than_twelve_cables_move_at_once_and_the_repository_halo_moves_while_integrating()
    {
        var template = Roster().Single(a => a.Id == DefaultAgentRoster.Executor);
        var many = Enumerable.Range(1, 30).Select(n => template with { Id = new AgentId($"e{n}"), Name = $"E{n}", PathGrants = [new PathGrant("src/", FileAccessLevel.ReadWrite), new PathGrant("docs/", FileAccessLevel.ReadWrite)] }).ToList();
        var team = LoadedTeam([.. Roster().Where(a => a.Role != AgentRole.Executor), .. many]);

        team.ApplyLive(State(1, 40, true, [.. many.Select(a => Live(a.Id, AgentLiveStatus.Working, new TaskId("T1")))]));

        Assert.Equal(LiveProjection.MaxAnimations + 1, team.AnimatedCount);
        Assert.Equal(LiveProjection.MaxAnimations, team.Connections.Count(c => c.IsAnimated));
        Assert.True(team.Repository!.IsAnimated);
        Assert.Equal("Integrando o entregando a tu rama…", team.Repository.IntegratingText);

        team.AnimationsEnabled = false;
        Assert.Equal(0, team.AnimatedCount);
        Assert.False(team.Repository.IsAnimated);
    }

    [Fact]
    public void A_quota_that_is_not_exposed_is_an_empty_track_with_the_words()
    {
        var bar = AgentStudio.Desktop.Observability.MetricBars.Quota("Cuota Codex 5 h", MetricValue<double>.NotExposed("codex does not report it"));

        Assert.Null(bar.Fraction);
        Assert.Contains("no expuesto", bar.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_and_the_node_are_the_same_object_so_they_always_say_the_same()
    {
        var team = LoadedTeam();

        Assert.All(team.Agents, a => Assert.Contains(a, team.Nodes));
    }
}
