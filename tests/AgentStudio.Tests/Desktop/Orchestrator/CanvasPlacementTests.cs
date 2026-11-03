using System.Windows;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Domain.Work;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// Where the canvas puts a new node and what «Ajustar vista» shows: a new node never covers another one (to the right of the
/// last, or under it), it is brought into view, and the fit frames every node at a zoom whose words can be read.
/// </summary>
public sealed class CanvasPlacementTests
{
    private static CanvasViewModel NewCanvas()
    {
        var canvas = new CanvasViewModel(new FakeTimeProvider(T0))
        {
            // What the editor writes: 1000 x 600 pixels at 100 %.
            ViewportLocation = new Point(0, 0),
            ViewportSize = new Size(1000, 600),
        };
        return canvas;
    }

    private static Rect Area(NodeViewModel node) => new(node.Location, CanvasViewModel.DefaultNodeSize);

    [Fact]
    public void New_nodes_go_side_by_side_and_never_cover_each_other()
    {
        var canvas = NewCanvas();

        canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        canvas.AddNodeCommand.Execute(WorkTaskKind.Integration);
        canvas.AddNodeCommand.Execute(WorkTaskKind.Review);

        var nodes = canvas.Nodes.ToList();
        Assert.Equal(3, nodes.Count);
        Assert.Equal(nodes[0].Location.Y, nodes[1].Location.Y);
        Assert.Equal(nodes[1].Location.Y, nodes[2].Location.Y);
        Assert.True(nodes[0].Location.X < nodes[1].Location.X && nodes[1].Location.X < nodes[2].Location.X);
        foreach (var (a, b) in new[] { (0, 1), (0, 2), (1, 2) })
        {
            Assert.False(Area(nodes[a]).IntersectsWith(Area(nodes[b])), $"{nodes[a].Id} and {nodes[b].Id} overlap.");
        }
    }

    [Fact]
    public void A_place_taken_is_skipped_for_the_next_free_one_with_the_size_the_view_measured()
    {
        var canvas = NewCanvas();
        canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        var first = canvas.Nodes[0];
        var blocker = canvas.Add(Tid("X1"), WorkTaskKind.ToolRun, "En medio", new Point(first.Location.X + 330, first.Location.Y));
        blocker.Size = new Size(400, 300);
        var last = canvas.Add(Tid("X2"), WorkTaskKind.ToolRun, "Último", first.Location);
        last.Location = first.Location; // the last node sits on the first: the free place is found around it

        var spot = canvas.FreeSpot();

        var place = new Rect(spot, CanvasViewModel.DefaultNodeSize);
        Assert.All(canvas.Nodes, n => Assert.False(place.IntersectsWith(new Rect(n.Location, n.Size is { Width: > 0 } s ? s : CanvasViewModel.DefaultNodeSize)), $"The new place covers {n.Id}."));
    }

    [Fact]
    public void A_new_node_outside_the_visible_area_is_brought_into_view()
    {
        var canvas = NewCanvas();
        canvas.Add(Tid("T9"), WorkTaskKind.AgentWork, "Lejos", new Point(5000, 4000));

        canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);

        var created = canvas.Nodes[^1];
        Assert.True(new Rect(canvas.ViewportLocation, canvas.ViewportSize).Contains(Area(created)));
        Assert.Same(created, canvas.SelectedNode);
    }

    [Fact]
    public void Ajustar_vista_frames_the_nodes_never_below_a_zoom_that_can_be_read()
    {
        var canvas = NewCanvas();
        canvas.Add(Tid("T1"), WorkTaskKind.AgentWork, "Uno", new Point(0, 0));
        canvas.Add(Tid("T2"), WorkTaskKind.AgentWork, "Dos", new Point(320, 0));

        canvas.FitViewCommand.Execute(null);

        // Two nodes fit at 100 %: never zoomed in beyond it, centred.
        Assert.Equal(1d, canvas.ViewportZoom);
        var frame = new Rect(canvas.ViewportLocation, canvas.ViewportSize);
        Assert.True(frame.Contains(new Rect(0, 0, 580, 150)), $"The frame {frame} does not hold the nodes.");

        // A plan far too wide is shown at the least readable zoom, from its top left.
        canvas.Add(Tid("T3"), WorkTaskKind.AgentWork, "Lejos", new Point(9000, 3000));
        canvas.FitViewCommand.Execute(null);
        Assert.Equal(CanvasViewModel.MinFitZoom, canvas.ViewportZoom);
        Assert.True(canvas.ViewportLocation.X <= 0 && canvas.ViewportLocation.Y <= 0);
    }
}
