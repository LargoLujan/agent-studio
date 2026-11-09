using System.IO;
using System.Text.Json;
using System.Windows;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Infrastructure.Persistence;

namespace AgentStudio.Tests.Desktop.Orchestrator.Canvas;

/// <summary>
/// F13-1 (docs/17 D-08): <c>canvas-layout.json</c> is version 2, <c>{version:2, plan:{…}, team:{…}}</c>, and a version 1 file
/// is read as the Plan layer. Saving the plan never loses the positions of the Team layer, and the draft the same file keeps
/// is left as it was.
/// </summary>
public sealed class CanvasLayoutV2Tests : IDisposable
{
    private readonly TempProjectFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static FileCanvasLayoutStore NewStore() => new(new ProjectStorageFiles());

    private string File_ => Path.Combine(_folder.DataDirectory, FileCanvasLayoutStore.FileName);

    private static Dictionary<string, Point> Points(params (string Id, double X, double Y)[] points) =>
        points.ToDictionary(p => p.Id, p => new Point(p.X, p.Y), StringComparer.Ordinal);

    [Fact]
    public void A_version_1_file_is_read_as_the_plan_layer_and_has_no_team_positions()
    {
        Directory.CreateDirectory(_folder.DataDirectory);
        File.WriteAllText(File_, """{"version":1,"nodes":{"T1":{"x":320,"y":160},"T2":{"x":640,"y":0}}}""");

        var layout = NewStore().Load(_folder.Root);

        Assert.Equal(new Point(320, 160), layout.Positions["T1"]);
        Assert.Equal(new Point(640, 0), layout.Positions["T2"]);
        Assert.Empty(layout.TeamPositions);
    }

    [Fact]
    public void It_saves_version_2_with_a_plan_and_a_team_and_reads_both_back()
    {
        var store = NewStore();
        var layout = new CanvasLayout(Points(("T1", 10, 20))) { TeamPositions = Points(("ejecutor", 300, 230), ("repository", 0, 460)) };

        store.Save(_folder.Root, layout);

        using var document = JsonDocument.Parse(File.ReadAllText(File_));
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(10, document.RootElement.GetProperty("plan").GetProperty("T1").GetProperty("x").GetDouble());
        Assert.Equal(300, document.RootElement.GetProperty("team").GetProperty("ejecutor").GetProperty("x").GetDouble());
        Assert.False(document.RootElement.TryGetProperty("nodes", out _));
        var read = store.Load(_folder.Root);
        Assert.Equal(new Point(10, 20), read.Positions["T1"]);
        Assert.Equal(new Point(0, 460), read.TeamPositions["repository"]);
    }

    [Fact]
    public void A_version_1_file_becomes_version_2_on_the_next_save_without_losing_its_plan()
    {
        Directory.CreateDirectory(_folder.DataDirectory);
        File.WriteAllText(File_, """{"version":1,"nodes":{"T1":{"x":1,"y":2}}}""");
        var store = NewStore();

        var read = store.Load(_folder.Root);
        store.Save(_folder.Root, new CanvasLayout(read.Positions) { TeamPositions = Points(("juez", 5, 6)) });

        var again = store.Load(_folder.Root);
        Assert.Equal(new Point(1, 2), again.Positions["T1"]);
        Assert.Equal(new Point(5, 6), again.TeamPositions["juez"]);
    }

    [Fact]
    public void The_draft_the_file_keeps_is_not_lost_when_the_layout_is_saved_and_the_team_stays_when_the_draft_is_saved()
    {
        var store = NewStore();
        store.Save(_folder.Root, new CanvasLayout(Points(("T1", 1, 1))) { TeamPositions = Points(("juez", 9, 9)) });
        store.SaveDraft(_folder.Root, new PlanDraft("pide algo", string.Empty, [], [], null));

        store.Save(_folder.Root, new CanvasLayout(Points(("T1", 2, 2))) { TeamPositions = Points(("juez", 8, 8)) });

        Assert.Equal("pide algo", store.ReadDraft(_folder.Root).Draft!.Request);
        var read = store.Load(_folder.Root);
        Assert.Equal(new Point(2, 2), read.Positions["T1"]);
        Assert.Equal(new Point(8, 8), read.TeamPositions["juez"]);

        store.SaveDraft(_folder.Root, new PlanDraft("otra petición", string.Empty, [], [], null));
        Assert.Equal(new Point(8, 8), store.Load(_folder.Root).TeamPositions["juez"]);
        Assert.Equal(new Point(2, 2), store.Load(_folder.Root).Positions["T1"]);
    }

    [Fact]
    public void A_file_of_an_unknown_version_is_read_as_empty()
    {
        Directory.CreateDirectory(_folder.DataDirectory);
        File.WriteAllText(File_, """{"version":3,"plan":{"T1":{"x":1,"y":2}}}""");

        Assert.Empty(NewStore().Load(_folder.Root).Positions);
    }
}
