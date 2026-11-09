using System.Globalization;
using System.Windows;
using AgentStudio.Application.Git;
using AgentStudio.Application.Orchestration;
using AgentStudio.Application.Projects;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Live;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Desktop.Orchestrator.Team;
using AgentStudio.Domain.Agents;
using AgentStudio.Domain.Common;
using AgentStudio.Domain.Metrics;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Domain.Providers;
using AgentStudio.Domain.Team;
using AgentStudio.Domain.Work;

namespace AgentStudio.Tests.Desktop.Orchestrator.Team;

/// <summary>What the tests of the Team layer share: the texts in a fixed language, a roster, a repository and a live state.</summary>
internal static class TeamTestSupport
{
    public static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static CanvasText Text(CultureInfo? culture = null) => new(new ResourceTextProvider(culture ?? Spanish));

    /// <summary>The default roster: orchestrator and executor of Claude, a judge of Codex and one of Claude, the architect and the critic of the debates.</summary>
    public static IReadOnlyList<AgentDefinition> Roster() => DefaultAgentRoster.Create();

    /// <summary>A roster with a single judge of the other company: the one a team needs.</summary>
    public static IReadOnlyList<AgentDefinition> IndependentRoster() =>
        [.. DefaultAgentRoster.Create().Where(a => a.Id != DefaultAgentRoster.ClaudeJudge && a.Id != DefaultAgentRoster.RevisionJudge)];

    public static RepositoryNode Repository(params string[] folders) => new(
        @"C:\proyectos\calc",
        "main",
        "a1b2c3d",
        [RepositoryRemote.Cleaned("origin", "https://github.com/org/calc.git")],
        false,
        "dotnet, csharp",
        folders.Length == 0 ? ["src/", "tests/", "docs/"] : folders);

    public static ProjectContext Project() => new("p1", @"C:\proyectos\calc");

    public static TeamCanvasViewModel LoadedTeam(IReadOnlyList<AgentDefinition>? roster = null, RepositoryNode? repository = null, CanvasText? text = null)
    {
        var team = new TeamCanvasViewModel(text ?? Text());
        team.Load(roster ?? Roster(), RepositoryReading.Of(repository ?? Repository()), CanvasLayout.Empty);
        return team;
    }

    public static AgentLiveState Live(
        AgentId agent,
        AgentLiveStatus status,
        TaskId? task = null,
        LiveContext? context = null,
        int runs = 0,
        MetricValue<long>? input = null,
        string? why = null) => new(
            agent,
            status,
            task,
            null,
            why,
            context,
            input ?? MetricValue<long>.Unavailable("none"),
            MetricValue<long>.Unavailable("none"),
            MetricValue<long>.Unavailable("none"),
            runs);

    public static RunLiveState State(long generation, int maxRuns, bool integrating, params AgentLiveState[] agents) => new(
        new RunId("20261109-aaaaaa"),
        RunPhase.Running,
        agents.ToDictionary(a => a.Agent),
        new Dictionary<TaskId, TaskLiveState>(),
        agents.Sum(a => a.AgentRuns),
        maxRuns,
        integrating,
        generation);

    public static Dictionary<string, Point> Points(params (string Id, double X, double Y)[] points) =>
        points.ToDictionary(p => p.Id, p => new Point(p.X, p.Y), StringComparer.Ordinal);
}
