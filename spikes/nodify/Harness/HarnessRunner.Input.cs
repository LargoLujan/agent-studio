using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Domain.Work;
using Nodify;
using NodifySpike.ViewModels;

namespace NodifySpike.Harness;

internal sealed partial class HarnessRunner
{
    private NodeViewModel NodeById(string id) => _vm.Nodes.First(n => n.Id.ToString() == id);

    // A Nodify wire is a bezier whose end handles are symmetric, so it passes through the midpoint of its two anchors.
    private (int X, int Y) WireMidpoint(ConnectionViewModel wire)
    {
        var from = ConnectorPoint(wire.Source);
        var to = ConnectorPoint(wire.Target);
        return ((from.X + to.X) / 2, (from.Y + to.Y) / 2);
    }

    private NodeViewModel? FocusedNode() =>
        VisualTree.Ancestor<ItemContainer>(Keyboard.FocusedElement as DependencyObject)?.DataContext as NodeViewModel;

    /// <summary>Drags a node by its header so that its top-left corner lands on the given editor-local point (DIPs).</summary>
    private async Task MoveNodeToAsync(NodeViewModel node, double x, double y)
    {
        var container = ContainerOf(node);
        var current = VisualTree.ClientPoint(container, 0, 0);
        var target = EditorPoint(x, y);
        var header = NodeHeaderPoint(node);

        // The press must land on this node, not on one stacked above it.
        await MoveToAsync(header, 60);
        var under = VisualTree.Ancestor<ItemContainer>(Mouse.DirectlyOver as DependencyObject)?.DataContext as NodeViewModel;

        // Small steps: Nodify starts the drag once the pointer leaves a threshold and measures from there, so a coarse step is lost.
        await DragAsync(header, (header.X + (target.X - current.X), header.Y + (target.Y - current.Y)), steps: 70, stepDelay: 8);
        var landed = VisualTree.ClientPoint(ContainerOf(node), 0, 0);
        var error = Math.Max(Math.Abs(landed.X - target.X), Math.Abs(landed.Y - target.Y));
        Check("input", $"left-drag moves {node.Id}", under == node && error <= 45, $"pressed on {under?.Id}; started ({current.X},{current.Y}), target ({target.X},{target.Y}), landed ({landed.X},{landed.Y}): within {error}px (client pixels; the node snaps to the 10-unit grid and Nodify measures the drag from its start threshold)");
    }

    private async Task ConnectAsync(ConnectorViewModel output, ConnectorViewModel input, bool expectWire, string name)
    {
        var before = _vm.Connections.Count;
        var began = await DragAsync(ConnectorPoint(output), ConnectorPoint(input));
        var created = _vm.Connections.Count - before;
        var wire = _vm.Connections.LastOrDefault();
        var wired = created == 1 && wire is not null && ReferenceEquals(wire.Source, output) && ReferenceEquals(wire.Target, input);

        // A drag that never started proves nothing about a refusal, so it fails the check either way.
        Check("input", name, began && (expectWire ? wired && output.IsConnected && input.IsConnected : created == 0), $"drag started {began}; wires {before}->{_vm.Connections.Count}; notice '{_vm.NoticeText}'");
    }

    /// <summary>
    /// Scripted use of the canvas with mouse and keyboard messages on an empty canvas: create, move, connect, refuse, select,
    /// pan, zoom, delete, keyboard. Each observation is a check in the report.
    /// </summary>
    private async Task InteractionsAsync()
    {
        _vm.Clear();
        _vm.ViewportZoom = 1;
        _vm.ViewportLocation = new Point(0, 0);
        await SettleAsync(300);
        await FocusEditorAsync();

        // ---- Create: one click per palette button; Debate is disabled and must not create anything ----
        var debateButton = PaletteButton(WorkTaskKind.Debate);
        Check("create", "the Debate palette button is disabled", !debateButton.IsEnabled && !_vm.AddNodeCommand.CanExecute(WorkTaskKind.Debate), $"IsEnabled {debateButton.IsEnabled}, tooltip '{debateButton.ToolTip}'");
        await ClickAsync(VisualTree.Centre(debateButton));
        Check("create", "clicking the disabled Debate button creates nothing", _vm.Nodes.Count == 0, $"{_vm.Nodes.Count} nodes");

        WorkTaskKind[] kinds = [WorkTaskKind.AgentWork, WorkTaskKind.Condition, WorkTaskKind.ToolRun, WorkTaskKind.Review, WorkTaskKind.HumanApproval, WorkTaskKind.Integration];
        foreach (var kind in kinds)
        {
            var before = _vm.Nodes.Count;
            await ClickUntilAsync(VisualTree.Centre(PaletteButton(kind)), () => _vm.Nodes.Count == before + 1);
            Check("create", $"palette click creates a {kind} node", _vm.Nodes.Count == before + 1 && _vm.Nodes[^1].Kind == kind, $"{_vm.Nodes.Count} nodes, last {_vm.Nodes.LastOrDefault()?.Id}");
        }

        await SettleAsync(200);
        Check("create", "ids are unique and the new node is selected", _vm.Nodes.Select(n => n.Id).Distinct().Count() == 6 && _vm.SelectedNodes.Count == 1, $"ids {string.Join(",", _vm.Nodes.Select(n => n.Id))}, selected {_vm.SelectedNodes.Count}");

        // ---- Status colouring: the legend buttons apply a status to the selected node; its border follows the status brush ----
        var selected = _vm.SelectedNodes[0];
        var shell = VisualTree.Descendants<Node>(ContainerOf(selected)).First();
        var wrongStatuses = new List<string>();
        foreach (var choice in _vm.Statuses)
        {
            var legend = VisualTree.Descendants<System.Windows.Controls.Button>(_view).First(b => b.DataContext is StatusChoice sc && sc.Value == choice.Value);
            await ClickUntilAsync(VisualTree.Centre(legend), () => selected.Status == choice.Value);
            await SettleAsync(60);
            var expected = ((SolidColorBrush)Application.Current.FindResource($"Status.{choice.Value}Brush")).Color;
            var actual = (shell.BorderBrush as SolidColorBrush)?.Color;
            if (selected.Status != choice.Value || actual != expected || selected.StatusLabel != choice.Label)
            {
                wrongStatuses.Add($"{choice.Value}: status {selected.Status}, border {actual}, label '{selected.StatusLabel}'");
            }
        }

        Check("status", "each of the 8 statuses recolours the node border live and updates its label", wrongStatuses.Count == 0, wrongStatuses.Count == 0 ? string.Join(", ", _vm.Statuses.Select(s => s.Label)) : string.Join(" | ", wrongStatuses));
        await ClickUntilAsync(VisualTree.Centre(VisualTree.Descendants<System.Windows.Controls.Button>(_view).First(b => b.DataContext is StatusChoice { Value: NodeStatus.Pending })), () => selected.Status == NodeStatus.Pending);

        // ---- Move: spread the six nodes with left-drags on their headers ----
        _vm.ClearNotice();
        var (t1, t2, t3, t4, t5, t6) = (NodeById("T1"), NodeById("T2"), NodeById("T3"), NodeById("T4"), NodeById("T5"), NodeById("T6"));

        // New nodes pile up around the centre of the view (cascade of 24 units). T4's target is inside that pile, so it goes last
        // or it would land on top of a node that has not moved yet.
        await MoveNodeToAsync(t6, 360, 430);
        await MoveNodeToAsync(t5, 1040, 200);
        await MoveNodeToAsync(t3, 700, 110);
        await MoveNodeToAsync(t2, 360, 200);
        await MoveNodeToAsync(t1, 40, 200);
        await MoveNodeToAsync(t4, 700, 330);
        await SettleAsync(200);

        // ---- Connect: drag from an output connector to an input connector ----
        await ConnectAsync(t1.Outputs[0], t2.Inputs[0], true, "drag T1.out -> T2.in creates a wire");
        var yes = t2.Outputs.First(o => o.Branch == Branches.Yes);
        var no = t2.Outputs.First(o => o.Branch == Branches.No);
        await ConnectAsync(yes, t3.Inputs[0], true, "drag T2.Sí -> T3.in creates a wire");
        await ConnectAsync(no, t4.Inputs[0], true, "drag T2.No -> T4.in creates a wire");
        await ConnectAsync(t3.Outputs[0], t5.Inputs[0], true, "drag T3.out -> T5.in creates a wire");
        await ConnectAsync(t4.Outputs[0], t5.Inputs[0], true, "drag T4.out -> T5.in creates a second wire into the same input (fan-in)");
        await ConnectAsync(t4.Outputs[0], t6.Inputs[0], true, "drag T4.out -> T6.in creates a wire");
        var branches = _vm.Connections.Where(c => c.Branch is not null).Select(c => $"{c.Source.Node.Id}.{c.Source.Title}->{c.Target.Node.Id}").ToList();
        Check("input", "the Condition's two outputs carry the Sí and No branches", branches.SequenceEqual(["T2.Sí->T3", "T2.No->T4"]), string.Join("; ", branches));

        // ---- Refuse: duplicate, wrong direction, self ----
        await ConnectAsync(t1.Outputs[0], t2.Inputs[0], false, "a duplicate wire is not created");
        await ConnectAsync(t1.Outputs[0], t3.Outputs[0], false, "an output cannot be dragged onto an output");
        _vm.ClearNotice();
        await ConnectAsync(t2.Outputs[0], t2.Inputs[0], false, "a node cannot be connected to itself");
        Check("cycle", "the self-connection is reported as a cycle by the domain", _vm.NoticeText is { } selfNotice && selfNotice.Contains("Ciclo: T2 → T2", StringComparison.Ordinal), $"notice '{_vm.NoticeText}'");

        // ---- Cycle: drag T5.out -> T1.in. The hover text and the red wire show before the drop; the drop is refused with the path ----
        _vm.ClearNotice();
        var wiresBefore = _vm.Connections.Count;
        string? hover = null;
        var hoverIsError = false;
        await DragAsync(ConnectorPoint(t5.Outputs[0]), ConnectorPoint(t1.Inputs[0]), whileHeld: async () =>
        {
            await Task.Delay(250);
            hover = _vm.PendingConnection.PreviewText;
            hoverIsError = _vm.PendingConnection.IsPreviewError;
            Shot("fase3-spike-nodify-ciclo.png");
        });
        var cyclePattern = new Regex("^Ciclo: T1 → T2 → T[34] → T5 → T1$");
        Check("cycle", "while dragging, the cycle is announced next to the cursor", hover is not null && cyclePattern.IsMatch(hover) && hoverIsError, $"preview text '{hover}', error style {hoverIsError}");
        Check("cycle", "dropping it creates no wire", _vm.Connections.Count == wiresBefore, $"wires {wiresBefore}->{_vm.Connections.Count}");
        var notice = _vm.NoticeText ?? string.Empty;
        Check("cycle", "a visible message names the cycle path", _vm.HasNotice && _vm.NoticeKind == NoticeKind.Error && notice.StartsWith("Conexión rechazada. Ciclo: T1 → T2", StringComparison.Ordinal), $"banner '{notice}'");
        await SettleAsync(250);
        var banner = VisualTree.Descendants<System.Windows.Controls.TextBlock>(_view).FirstOrDefault(t => t.Text == _vm.NoticeText && t.IsVisible);
        Check("cycle", "the banner is rendered", banner is not null, banner is null ? "no visible TextBlock with the message" : $"visible at {banner.ActualWidth:0}x{banner.ActualHeight:0} DIPs");
        Shot("fase3-spike-nodify-rechazo.png");

        // ---- Select: rubber band, click, Ctrl+click, wire ----
        _vm.ClearNotice();
        await DragAsync(EditorPoint(1000, 30), EditorPoint(680, 380));
        var band = _vm.SelectedNodes.Select(n => n.Id.ToString()).Order().ToList();
        Check("select", "a rubber band selects the nodes it touches (T3, T4)", band.SequenceEqual(["T3", "T4"]), $"selected {string.Join(",", band)}");
        await ClickUntilAsync(NodeHeaderPoint(t1), () => _vm.SelectedNodes.Count == 1 && _vm.SelectedNodes[0] == t1);
        Check("select", "a click selects one node", _vm.SelectedNodes.Count == 1 && _vm.SelectedNodes[0] == t1, $"selected {string.Join(",", _vm.SelectedNodes.Select(n => n.Id))}");

        _input.Hold(Key.LeftCtrl);
        await Task.Delay(40);
        var ctrlSeen = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        await ClickUntilAsync(NodeHeaderPoint(t2), () => _vm.SelectedNodes.Count == 2);
        _input.Release(Key.LeftCtrl);
        await Task.Delay(60);
        Check("select", "Ctrl+click adds a node to the selection", ctrlSeen && _vm.SelectedNodes.Count == 2, $"Ctrl seen by WPF {ctrlSeen}; selected {string.Join(",", _vm.SelectedNodes.Select(n => n.Id))}");
        await ClickUntilAsync(EditorPoint(1300, 560), () => _vm.SelectedNodes.Count == 0);
        Check("select", "a click on empty canvas clears the selection", _vm.SelectedNodes.Count == 0, $"selected {_vm.SelectedNodes.Count}");

        var wire = _vm.Connections.First(c => c.Source.Node == t3 && c.Target.Node == t5);
        await ClickUntilAsync(WireMidpoint(wire), () => _vm.SelectedConnections.Count == 1);
        var picked = _vm.SelectedConnections.FirstOrDefault();
        Check("select", "a click on a wire selects it", _vm.SelectedConnections.Count == 1 && _vm.SelectedConnections[0] == wire, $"selected wires {_vm.SelectedConnections.Count}: {picked?.Source.Node.Id}->{picked?.Target.Node.Id}");
        await SettleAsync(100);
        var wireCount = _vm.Connections.Count;
        await PressAsync(Key.Delete);
        Check("delete", "Delete removes the selected wire", _vm.Connections.Count == wireCount - 1 && !_vm.Connections.Contains(wire), $"wires {wireCount}->{_vm.Connections.Count}");
        Check("delete", "its connectors are released (IsConnected updated)", !t3.Outputs[0].IsConnected && t5.Inputs[0].IsConnected, $"T3.out connected {t3.Outputs[0].IsConnected}, T5.in connected {t5.Inputs[0].IsConnected} (T4 still feeds it)");

        // ---- Delete with cascade: T2 has three wires ----
        await ClickUntilAsync(NodeHeaderPoint(t2), () => _vm.SelectedNodes.Count == 1 && _vm.SelectedNodes[0] == t2);
        var nodesBefore = _vm.Nodes.Count;
        var attached = _vm.Connections.Count(c => c.Source.Node == t2 || c.Target.Node == t2);
        var wiresNow = _vm.Connections.Count;
        await PressAsync(Key.Delete);
        Check("delete", "Delete on a node removes it and every wire attached to it (cascade)", _vm.Nodes.Count == nodesBefore - 1 && !_vm.Nodes.Contains(t2) && _vm.Connections.Count == wiresNow - attached && attached == 3, $"nodes {nodesBefore}->{_vm.Nodes.Count}, wires {wiresNow}->{_vm.Connections.Count} ({attached} attached); notice '{_vm.NoticeText}'");
        Check("delete", "no connector is left marked as connected without a wire", _vm.Nodes.SelectMany(n => n.Inputs.Concat(n.Outputs)).All(c => c.IsConnected == _vm.Connections.Any(w => w.Source == c || w.Target == c)), "IsConnected matches the wires for every connector");
        await SettleAsync(200);
        var rebuilt = PlanGraph.Build(_vm.Nodes, _vm.Connections, DateTimeOffset.UtcNow);
        Check("delete", "the domain graph rebuilt from the canvas is consistent after the cascade", rebuilt.Tasks.Count == _vm.Nodes.Count, $"{rebuilt.Tasks.Count} tasks");

        // ---- Pan (right-drag) and zoom (wheel) ----
        var location = _vm.ViewportLocation;
        await DragAsync(EditorPoint(1000, 560), EditorPoint(800, 500), MouseButton.Right);
        var moved = _vm.ViewportLocation - location;
        Check("pan", "right-drag pans the viewport", Math.Abs(moved.X) >= 150 && Math.Abs(moved.Y) >= 40, $"viewport moved by ({moved.X:0},{moved.Y:0}) for a (-200,-60) drag");
        var zoom = _vm.ViewportZoom;
        var wheelAt = EditorPoint(700, 300);
        await MoveToAsync(wheelAt, 60);
        for (var i = 0; i < 3; i++)
        {
            _input.Wheel(120, wheelAt.X, wheelAt.Y);
            await Task.Delay(100);
        }

        var zoomedIn = _vm.ViewportZoom;
        for (var i = 0; i < 6; i++)
        {
            _input.Wheel(-120, wheelAt.X, wheelAt.Y);
            await Task.Delay(100);
        }

        var zoomedOut = _vm.ViewportZoom;
        Check("zoom", "the mouse wheel zooms in and out", zoomedIn > zoom * 1.2 && zoomedOut < zoomedIn * 0.7, $"zoom {zoom:0.00} -> in {zoomedIn:0.00} -> out {zoomedOut:0.00}; status bar '{_vm.ZoomText}'");
        _vm.ViewportZoom = 1;
        _vm.ViewportLocation = new Point(0, 0);
        await SettleAsync(250);

        await KeyboardAsync();
    }

    /// <summary>Tab into the canvas, arrow keys between nodes, Space to select, Escape, Delete, and the focus ring.</summary>
    private async Task KeyboardAsync()
    {
        _vm.ClearNotice();
        // Tab from the last toolbar button must land in the canvas.
        var lastButton = VisualTree.Descendants<System.Windows.Controls.Button>(_view).Last(b => b.DataContext is StatusChoice && b.IsEnabled);
        lastButton.Focus();
        await Task.Delay(100);
        await PressAsync(Key.Tab);
        var afterTab = Keyboard.FocusedElement as DependencyObject;
        var inEditor = ReferenceEquals(afterTab, _editor) || VisualTree.Ancestor<NodifyEditor>(afterTab) is not null;
        Check("keyboard", "Tab from the toolbar enters the canvas", inEditor, $"focus on {afterTab?.GetType().Name}");

        await ClickUntilAsync(EditorPoint(1300, 560), () => _editor.IsKeyboardFocusWithin);
        await PressAsync(Key.Right);
        var first = FocusedNode();
        Check("keyboard", "an arrow key moves the focus to a node", first is not null && Keyboard.FocusedElement is ItemContainer, $"focused {first?.Id} ({Keyboard.FocusedElement?.GetType().Name})");
        await SettleAsync(250);
        var picture = Screenshot.Capture(_view);
        FocusRingCheck(picture, first);
        Shot("fase3-spike-nodify-foco.png", picture);

        await PressAsync(Key.Space);
        Check("keyboard", "Space selects the focused node", first is not null && _vm.SelectedNodes.Count == 1 && _vm.SelectedNodes[0] == first, $"selected {string.Join(",", _vm.SelectedNodes.Select(n => n.Id))}");
        await PressAsync(Key.Right);
        var second = FocusedNode();
        await PressAsync(Key.Space);
        Check("keyboard", "arrow + Space builds a multi-selection", second is not null && second != first && _vm.SelectedNodes.Count == 2, $"focused {second?.Id}, selected {string.Join(",", _vm.SelectedNodes.Select(n => n.Id))}");
        await PressAsync(Key.Escape);
        Check("keyboard", "Escape clears the selection", _vm.SelectedNodes.Count == 0, $"selected {_vm.SelectedNodes.Count}");

        if (second is not null)
        {
            await PressAsync(Key.Space);
            var nodes = _vm.Nodes.Count;
            var attached = _vm.Connections.Count(c => c.Source.Node == second || c.Target.Node == second);
            var wires = _vm.Connections.Count;
            await PressAsync(Key.Delete);
            Check("keyboard", "Delete removes the keyboard-selected node and its wires", _vm.Nodes.Count == nodes - 1 && !_vm.Nodes.Contains(second) && _vm.Connections.Count == wires - attached, $"nodes {nodes}->{_vm.Nodes.Count}, wires {wires}->{_vm.Connections.Count} ({attached} attached)");
        }
    }

    // The focus ring is an adorner drawn with the NodifyEditor.FocusVisualColor token: count its pixels around the focused container.
    private void FocusRingCheck(CapturedImage image, NodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var container = ContainerOf(node);
        var topLeft = image.PixelOf(container, 0, 0);
        var bottomRight = image.PixelOf(container, container.ActualWidth, container.ActualHeight);
        if (topLeft is null || bottomRight is null)
        {
            Check("keyboard", "the focused node shows the dashed focus ring in the TextPrimary token colour", false, $"{node.Id} is outside the picture");
            return;
        }

        var ring = (Color)Application.Current.Resources["TextPrimaryColor"];
        var hits = 0;
        for (var offset = 1; offset <= 4; offset++)
        {
            for (var x = topLeft.Value.X - 4; x <= bottomRight.Value.X + 4; x++)
            {
                hits += Near(image.PixelAt(x, topLeft.Value.Y - offset), ring) ? 1 : 0;
                hits += Near(image.PixelAt(x, bottomRight.Value.Y + offset), ring) ? 1 : 0;
            }
        }

        Check("keyboard", "the focused node shows the dashed focus ring in the TextPrimary token colour", hits >= 20, $"{hits} ring pixels around {node.Id}");
    }

    private static bool Near(Color? pixel, Color token) =>
        pixel is { } p && Math.Abs(p.R - token.R) <= 40 && Math.Abs(p.G - token.G) <= 40 && Math.Abs(p.B - token.B) <= 40;
}
