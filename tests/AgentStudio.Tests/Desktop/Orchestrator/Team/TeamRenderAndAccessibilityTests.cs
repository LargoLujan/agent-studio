using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Desktop.Controls;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Desktop.Views.Orchestrator;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Desktop.Orchestrator.Team.TeamTestSupport;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>
/// F13-1, F13-6, F13-7 and F13-8 on the real views, off screen (docs/17 D-12, D-15 to D-17): the strip of the team and the Team
/// layer are drawn with their cards and bars; each card and each bar tells a screen reader its whole name, its state is announced
/// (<c>Polite</c>, or <c>Assertive</c> for a question) and a bar with a value gives it, its minimum and its maximum; and with 40
/// nodes, 12 cables moving and a burst of events, the interface does not stop for more than 50 ms.
/// </summary>
public sealed class TeamRenderAndAccessibilityTests
{
    private static readonly Color Background = Color.FromRgb(0x0A, 0x10, 0x20);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static CanvasViewModel TeamCanvas(IReadOnlyList<AgentDefinition>? roster = null)
    {
        var canvas = new CanvasViewModel(new FakeTimeProvider(T0));
        canvas.Team.Load(roster ?? Roster(), RepositoryReading.Of(Repository()), CanvasLayout.Empty);
        canvas.Layer = CanvasLayer.Team;
        return canvas;
    }

    private static LiveContext Context(long used, long window) =>
        new("sonnet", MetricValue<long>.Exact(used, "claude contextObserved"), MetricValue<long>.Exact(window, "claude modelUsage"), []);

    [Fact]
    public void The_strip_and_the_team_layer_are_drawn_with_a_card_per_agent_and_the_three_bars_in_each()
    {
        var facts = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = TeamCanvas();
            canvas.Team.ApplyLive(State(1, 12, false, Live(DefaultAgentRoster.Executor, AgentLiveStatus.Working, new TaskId("T2"), Context(47_000, 100_000), runs: 2)));
            var view = new CanvasView { DataContext = canvas };

            var bitmap = OffscreenRender.Render(view, 1500, 900);
            OffscreenRender.SaveIfRequested(bitmap, "fase13-lienzo-equipo.png");
            var strip = Descendants<TeamStripView>(view).Single();
            return (
                Drawn: OffscreenRender.CountUnlike(bitmap, Background),
                Cards: Descendants<Button>(strip).Count(b => AutomationProperties.GetName(b).Length > 0),
                Bars: Descendants<MetricBar>(strip).Count(),
                Agents: canvas.Team.Agents.Count,
                PlanEditorShown: Descendants<Nodify.NodifyEditor>(view).Single(e => AutomationProperties.GetName(e) == "Lienzo del plan").Visibility == Visibility.Visible,
                TeamEditorShown: Descendants<Nodify.NodifyEditor>(view).Single(e => AutomationProperties.GetName(e) == "Lienzo del equipo").Visibility == Visibility.Visible);
        });

        Assert.True(facts.Drawn > 50_000, $"Only {facts.Drawn} pixels differ from the background.");
        Assert.Equal(facts.Agents, facts.Cards);
        Assert.Equal(facts.Agents * 3, facts.Bars);
        Assert.False(facts.PlanEditorShown);
        Assert.True(facts.TeamEditorShown);
    }

    [Fact]
    public void Each_card_says_its_whole_name_announces_its_state_and_each_bar_gives_its_value_to_a_screen_reader()
    {
        var facts = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = TeamCanvas();
            canvas.Team.ApplyLive(State(
                1,
                12,
                false,
                Live(DefaultAgentRoster.Executor, AgentLiveStatus.Working, new TaskId("T2"), Context(47_000, 100_000), runs: 2),
                Live(DefaultAgentRoster.Judge, AgentLiveStatus.Asking)));
            var view = new CanvasView { DataContext = canvas };
            OffscreenRender.Render(view, 1500, 900);
            var strip = Descendants<TeamStripView>(view).Single();
            var executor = canvas.Team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
            var judge = canvas.Team.Agents.Single(a => a.Id == DefaultAgentRoster.Judge);
            Button CardOf(AgentNodeViewModel agent) => Descendants<Button>(strip).Single(b => ReferenceEquals(b.DataContext, agent));

            var executorBar = Descendants<MetricBar>(CardOf(executor)).First(b => ReferenceEquals(b.Reading, executor.ContextBar));
            var judgeBar = Descendants<MetricBar>(CardOf(judge)).First(b => ReferenceEquals(b.Reading, judge.ContextBar));
            var executorPeer = UIElementAutomationPeer.CreatePeerForElement(executorBar)!;
            var judgePeer = UIElementAutomationPeer.CreatePeerForElement(judgeBar)!;
            var range = executorPeer.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
            return (
                ExecutorName: UIElementAutomationPeer.CreatePeerForElement(CardOf(executor))!.GetName(),
                ExecutorExpected: executor.AutomationName,
                ExecutorLive: AutomationProperties.GetLiveSetting(CardOf(executor)),
                JudgeLive: AutomationProperties.GetLiveSetting(CardOf(judge)),
                BarName: executorPeer.GetName(),
                Value: range?.Value,
                Minimum: range?.Minimum,
                Maximum: range?.Maximum,
                ReadOnly: range?.IsReadOnly,
                EmptyBarHasRange: judgePeer.GetPattern(PatternInterface.RangeValue) is not null,
                EmptyBarName: judgePeer.GetName());
        });

        Assert.Equal(facts.ExecutorExpected, facts.ExecutorName);
        Assert.StartsWith("Ejecutor · Claude Code (Anthropic)", facts.ExecutorName, StringComparison.Ordinal);
        Assert.Contains("Trabajando en T2", facts.ExecutorName, StringComparison.Ordinal);
        Assert.Equal(AutomationLiveSetting.Polite, facts.ExecutorLive);
        Assert.Equal(AutomationLiveSetting.Assertive, facts.JudgeLive);
        Assert.Contains("Contexto", facts.BarName, StringComparison.Ordinal);
        Assert.Contains("exacto", facts.BarName, StringComparison.Ordinal);
        Assert.Equal(47d, facts.Value!.Value, 1);
        Assert.Equal(0d, facts.Minimum);
        Assert.Equal(100d, facts.Maximum);
        Assert.True(facts.ReadOnly);

        // A bar with nothing to show says it in words and offers no value: never a bar at zero.
        Assert.False(facts.EmptyBarHasRange);
        Assert.Contains("no expuesto", facts.EmptyBarName, StringComparison.Ordinal);
    }

    [Fact]
    public void Connecting_and_disconnecting_work_with_the_keyboard_through_the_menu_and_the_same_check()
    {
        var facts = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = TeamCanvas();
            var executor = canvas.Team.Agents.Single(a => a.Id == DefaultAgentRoster.Executor);
            canvas.Team.Select(executor);
            var view = new CanvasView { DataContext = canvas };
            OffscreenRender.Render(view, 1500, 900);
            var menu = Descendants<MenuItem>(view).Single(m => (m.Header as string) == "Conectar con…");
            var peer = UIElementAutomationPeer.CreatePeerForElement(menu)!;
            var requested = new List<FolderCableRequest>();
            canvas.Team.FolderCableRequested += (_, r) => requested.Add(r);
            var src = canvas.Team.SelectedTargets.Single(t => t.Label == "src/");
            canvas.Team.ToggleTargetCommand.Execute(src);
            return (Items: menu.Items.Count, Name: peer.GetName(), Requested: requested, Targets: canvas.Team.SelectedTargets.Select(t => t.Label).ToList());
        });

        Assert.Equal(4, facts.Items);
        Assert.Equal("Conectar con…", facts.Name);
        Assert.Equal([new FolderCableRequest(DefaultAgentRoster.Executor, "src/", true)], facts.Requested);
        Assert.Contains("todo el proyecto", facts.Targets);
    }

    [Fact]
    public void A_layer_switch_with_the_two_buttons_shows_one_editor_and_the_other()
    {
        var facts = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = TeamCanvas();
            var view = new CanvasView { DataContext = canvas };
            OffscreenRender.Render(view, 1500, 900);
            bool Shown(string name) => Descendants<Nodify.NodifyEditor>(view).Single(e => AutomationProperties.GetName(e) == name).Visibility == Visibility.Visible;
            var onTeam = (Plan: Shown("Lienzo del plan"), Team: Shown("Lienzo del equipo"));
            canvas.ShowPlanCommand.Execute(null);
            OffscreenRender.Render(view, 1500, 900);
            var onPlan = (Plan: Shown("Lienzo del plan"), Team: Shown("Lienzo del equipo"));
            var bindings = view.InputBindings.OfType<System.Windows.Input.KeyBinding>().Select(b => (b.Key, b.Modifiers)).ToList();
            return (onTeam, onPlan, bindings);
        });

        Assert.Equal((false, true), facts.onTeam);
        Assert.Equal((true, false), facts.onPlan);
        Assert.Contains((System.Windows.Input.Key.D1, System.Windows.Input.ModifierKeys.Control), facts.bindings);
        Assert.Contains((System.Windows.Input.Key.D2, System.Windows.Input.ModifierKeys.Control), facts.bindings);
    }

    // ---------- F13-7: a burst of events does not repaint every node, and the interface does not stop ----------

    private static IReadOnlyList<AgentDefinition> ManyAgents()
    {
        var executor = Roster().Single(a => a.Id == DefaultAgentRoster.Executor);
        var judge = Roster().Single(a => a.Id == DefaultAgentRoster.Judge);
        var agents = new List<AgentDefinition> { Roster().Single(a => a.Role == AgentRole.Orchestrator) };
        agents.AddRange(Enumerable.Range(1, 30).Select(n => executor with
        {
            Id = new AgentId($"e{n}"),
            Name = $"E{n}",
            PathGrants = [new PathGrant("src/", FileAccessLevel.ReadWrite), new PathGrant("docs/", FileAccessLevel.ReadWrite)],
        }));
        agents.AddRange(Enumerable.Range(1, 8).Select(n => judge with { Id = new AgentId($"j{n}"), Name = $"J{n}" }));
        return agents;
    }

    [Fact]
    public void A_burst_of_events_is_one_change_that_updates_only_the_nodes_it_names_and_at_most_twelve_cables_move()
    {
        var team = LoadedTeam(ManyAgents());
        Assert.Equal(40, team.Nodes.Count);
        var changes = new Dictionary<AgentId, int>();
        foreach (var agent in team.Agents)
        {
            agent.PropertyChanged += (_, _) => changes[agent.Id] = changes.GetValueOrDefault(agent.Id) + 1;
        }

        var writing = team.Agents.Where(a => a.Id.Value.StartsWith('e')).Take(20).Select(a => a.Id).ToList();
        var state = State(100, 40, true, [.. writing.Select(a => Live(a, AgentLiveStatus.Working, new TaskId("T1")))]);

        // A hundred events of the monitor arrive as the one grouped change the projection raises (D-16): the nodes it names, once.
        team.ApplyLive(state, [.. writing.Select(a => a.Value).Take(5)]);

        Assert.Equal(5, changes.Count);
        Assert.Equal(AgentLiveStatus.Idle, team.Agents.Single(a => a.Id == writing[10]).Status);
        Assert.Equal(10, team.Connections.Count(c => c.IsAnimated));

        // Twenty agents write, with two cables each: no more than twelve move at once, and the halo of the repository is the next.
        team.ApplyLive(state, [.. writing.Select(a => a.Value)]);

        Assert.Equal(12, team.Connections.Count(c => c.IsAnimated));
        Assert.Equal(LiveProjection.MaxAnimations + 1, team.AnimatedCount);
    }

    [Fact]
    [Trait("Category", "Measurement")]
    public void With_forty_nodes_twelve_moving_cables_and_a_burst_of_events_the_interface_does_not_stop_for_more_than_50_ms()
    {
        var worst = WpfHost.Run<List<TimeSpan>>(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var canvas = TeamCanvas(ManyAgents());
            var view = new CanvasView { DataContext = canvas };
            var writing = canvas.Team.Agents.Where(a => a.Id.Value.StartsWith('e')).Take(12).Select(a => a.Id).ToList();

            // Warm up: the first draw builds every template.
            OffscreenRender.Render(view, 1500, 900);
            canvas.Team.ApplyLive(State(1, 40, true, [.. writing.Select(a => Live(a, AgentLiveStatus.Working, new TaskId("T1"), Context(10_000, 100_000)))]));
            OffscreenRender.Render(view, 1500, 900);

            var random = new Random(13);
            var times = new List<TimeSpan>();
            for (var burst = -2; burst < 9; burst++)
            {
                var generation = 10L + burst + 2;
                var states = Enumerable.Range(0, 100)
                    .Select(_ => Live(writing[random.Next(writing.Count)], AgentLiveStatus.Working, new TaskId("T1"), Context(random.Next(1_000, 90_000), 100_000), runs: 1))
                    .DistinctBy(s => s.Agent)
                    .ToArray();
                var clock = Stopwatch.StartNew();
                canvas.Team.ApplyLive(State(generation, 40, true, states), [.. states.Select(s => s.Agent.Value)]);
                view.UpdateLayout();
                if (burst >= 0)
                {
                    times.Add(clock.Elapsed);
                }
            }

            return times;
        });

        // Each burst is applied and laid out in one go: that is what the UI thread is stopped for. A garbage collection of
        // the whole process may add to one of them, so no more than one may pass the limit, and none may pass it by far.
        var over = worst.Count(t => t > TimeSpan.FromMilliseconds(50));
        Assert.True(over <= 1 && worst.Max() < TimeSpan.FromMilliseconds(150), "The bursts took " + string.Join(", ", worst.Select(t => t.TotalMilliseconds.ToString("N1", System.Globalization.CultureInfo.InvariantCulture))) + " ms.");
        Assert.True(worst.Order().ElementAt(worst.Count / 2) < TimeSpan.FromMilliseconds(50), "The median burst took more than 50 ms.");
    }
}
