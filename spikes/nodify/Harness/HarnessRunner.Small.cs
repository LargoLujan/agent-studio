using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Domain.Work;
using Nodify;
using NodifySpike.Theming;
using NodifySpike.ViewModels;

namespace NodifySpike.Harness;

internal sealed partial class HarnessRunner
{
    /// <summary>
    /// The small graph: all seven kinds and eight statuses on screen, the picture, the theme parity check, the automation
    /// names a screen reader would read and a drag onto the disabled Debate node.
    /// </summary>
    private async Task SmallGraphAsync()
    {
        Keyboard.ClearFocus();
        _vm.Clear();
        _report.SmallGraphColdLoad = await MeasureLoadAsync(() => SampleGraphs.LoadSmall(_vm), 11);
        Log($"12-node sample, cold load in a fresh process: {_report.SmallGraphColdLoad}");
        await SettleAsync(250);
        _editor.FitToScreen();
        await SettleAsync(500);

        var containers = VisualTree.Descendants<ItemContainer>(_editor).ToList();
        var wires = VisualTree.Descendants<Nodify.Connection>(_editor).Count();
        Check("small", "12 nodes and 11 wires are rendered", containers.Count == 12 && wires == 11, $"{containers.Count} containers, {wires} wires");
        Check("small", "all 7 node kinds are shown", _vm.Nodes.Select(n => n.Kind).Distinct().Count() == Enum.GetValues<WorkTaskKind>().Length, string.Join(", ", _vm.Nodes.Select(n => n.Kind).Distinct()));
        Check("small", "all 8 statuses are shown", _vm.Nodes.Select(n => n.Status).Distinct().Count() == Enum.GetValues<NodeStatus>().Length, string.Join(", ", _vm.Nodes.Select(n => n.Status).Distinct()));
        var conditionWires = _vm.Connections.Where(c => c.Branch is not null).Select(c => $"{c.Source.Node.Id}.{c.Source.Title}->{c.Target.Node.Id}").ToList();
        Check("small", "the Condition has two output connectors (Sí/No) with a wire each", _vm.Nodes.First(n => n.Kind == WorkTaskKind.Condition).Outputs.Count == 2 && conditionWires.Count == 2, string.Join("; ", conditionWires));

        var image = Screenshot.Capture(_view);
        CheckTheme(image);
        Shot("fase3-spike-nodify.png", image);

        await CheckAutomationAsync(containers);

        if (_options.Wants("input"))
        {
            await DebateRejectionAsync();
        }
    }

    private void CheckTheme(CapturedImage image)
    {
        var tokens = Application.Current.Resources;
        Color Token(string key) => (Color)tokens[key];

        // 1. Coverage: every colour key of Nodify's own Dark.xaml is either remapped from a token or transparent on purpose.
        var nodifyDark = new ResourceDictionary { Source = new Uri("pack://application:,,,/Nodify;component/Themes/Dark.xaml") };
        var focus = new ResourceDictionary { Source = new Uri("pack://application:,,,/Nodify;component/Themes/FocusVisual.xaml") };
        var keys = nodifyDark.Keys.Cast<object>().Concat(focus.Keys.Cast<object>()).OfType<string>().Where(k => (nodifyDark.Contains(k) ? nodifyDark[k] : focus[k]) is Color).Distinct().ToList();
        var remapped = keys.Where(NodifyThemeTokens.Map.ContainsKey).ToList();
        var transparent = keys.Where(NodifyThemeTokens.TransparentKeys.Contains).ToList();
        var unmapped = keys.Except(remapped).Except(transparent).ToList();
        Check("theme", "every Nodify colour key is remapped from an app token", unmapped.Count == 0, $"{keys.Count} keys: {remapped.Count} from tokens, {transparent.Count} transparent, {unmapped.Count} left in Nodify colours {string.Join(",", unmapped)}");

        // 2. The application resources now hold the token values under the Nodify keys.
        var drift = NodifyThemeTokens.Map.Where(kv => (Color)tokens[kv.Key] != Token(kv.Value)).Select(kv => kv.Key).ToList();
        Check("theme", "Nodify colour keys equal their tokens", drift.Count == 0, drift.Count == 0 ? $"{NodifyThemeTokens.Map.Count} keys checked" : string.Join(",", drift));

        // 3. The controls that actually render read the token colours (DynamicResource reached the live brushes).
        var mismatches = new List<string>();

        void Expect(string what, Brush? brush, string token)
        {
            var actual = (brush as SolidColorBrush)?.Color;
            if (actual != Token(token))
            {
                mismatches.Add($"{what}: {actual?.ToString() ?? "no solid brush"} (expected {token} {Token(token)})");
            }
        }

        var node = VisualTree.Descendants<Node>(_editor).First();
        Expect("Node.Background", node.Background, "SurfaceColor");
        Expect("Node.HeaderBrush", node.HeaderBrush, "SurfaceRaisedColor");
        Expect("Node.FooterBrush", node.FooterBrush, "SurfaceRaisedColor");
        Expect("NodifyEditor.Foreground", _editor.Foreground, "TextPrimaryColor");
        Expect("ItemContainer.SelectedBrush", VisualTree.Descendants<ItemContainer>(_editor).First().SelectedBrush, "AccentHoverColor");
        Expect("NodeInput.BorderBrush", VisualTree.Descendants<NodeInput>(_editor).First().BorderBrush, "AccentHoverColor");
        Check("theme", "live Nodify controls read the token colours", mismatches.Count == 0, mismatches.Count == 0 ? "Node, editor, container and connector brushes match" : string.Join(" | ", mismatches));

        // 4. Pixels: what is rendered is the token colour.
        var scale = Screenshot.Dpi / 96d;
        var origin = image.PixelOf(_editor, 0, 0) ?? throw new InvalidOperationException("The editor is outside the picture.");
        var background = image.ModeColour(origin.X, origin.Y, (int)(_editor.ActualWidth * scale), (int)(_editor.ActualHeight * scale));
        var header = VisualTree.Descendants<Border>(node).First(b => ReferenceEquals(b.Background, node.HeaderBrush));
        var headerAt = image.PixelOf(header, Math.Max(10, header.ActualWidth - 60), 4) ?? throw new InvalidOperationException("The node header is outside the picture.");
        var headerPixel = image.ModeColour(headerAt.X, headerAt.Y, (int)(40 * scale), (int)(Math.Max(6, header.ActualHeight - 8) * scale), 2);
        var backgroundToken = Token("BackgroundColor");
        var headerToken = Token("SurfaceRaisedColor");
        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        static bool Close(Color pixel, Color token) => Math.Abs(pixel.R - token.R) <= 2 && Math.Abs(pixel.G - token.G) <= 2 && Math.Abs(pixel.B - token.B) <= 2;
        Check("theme", "canvas background pixel equals the BackgroundColor token", Close(background, backgroundToken), $"rendered {Hex(background)}, token {Hex(backgroundToken)}");
        Check("theme", "node header pixel equals the SurfaceRaisedColor token", Close(headerPixel, headerToken), $"rendered {Hex(headerPixel)}, token {Hex(headerToken)}");

        _report.Theme = new ThemeReport(keys.Count, remapped.Count, transparent.Count, unmapped, mismatches, Hex(backgroundToken), Hex(background), Hex(headerToken), Hex(headerPixel));
    }

    // Nodify ships no automation peers for its containers and connectors: what a screen reader reads has to come from
    // AutomationProperties.Name in the templates (and, for the item peers, from the item's ToString). The check therefore
    // asks UI Automation itself, from a worker thread (a client on the UI thread of its own process would deadlock).
    private async Task CheckAutomationAsync(IReadOnlyList<ItemContainer> containers)
    {
        var editorName = UIElementAutomationPeer.CreatePeerForElement(_editor)?.GetName();
        Check("a11y", "the editor is named for screen readers", editorName == "Lienzo del orquestador" && _editor.Focusable && _editor.IsTabStop, $"name '{editorName}', focusable {_editor.Focusable}, tab stop {_editor.IsTabStop}");

        var properties = containers.Count(c => System.Windows.Automation.AutomationProperties.GetName(c) == ((NodeViewModel)c.DataContext).AutomationName);
        var connectors = VisualTree.Descendants<Connector>(_editor).ToList();
        var namedConnectors = connectors.Count(c => !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(c)));
        Check("a11y", "templates set AutomationProperties.Name on every node container and connector", properties == containers.Count && namedConnectors == connectors.Count, $"{properties}/{containers.Count} containers, {namedConnectors}/{connectors.Count} connectors");

        var handle = WindowHandle;
        var tree = await Task.Run(() =>
            System.Windows.Automation.AutomationElement.FromHandle(handle)
                .FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<System.Windows.Automation.AutomationElement>()
                .Select(e => (e.Current.Name, Type: e.Current.ControlType.ProgrammaticName, e.Current.IsKeyboardFocusable))
                .ToList());
        var expected = _vm.Nodes.Select(n => n.AutomationName).ToList();
        var seen = expected.Count(name => tree.Any(e => e.Name == name));
        var focusable = tree.Count(e => e.IsKeyboardFocusable && expected.Contains(e.Name));
        var example = tree.FirstOrDefault(e => e.Name.StartsWith("T1,", StringComparison.Ordinal));
        Log($"UIA tree: {tree.Count} elements; named like a node: {seen}; example {example.Name} / {example.Type}");
        Check("a11y", "UI Automation exposes every node by id, kind, title and status", seen == expected.Count, $"{seen}/{expected.Count} nodes found in a tree of {tree.Count} elements; e.g. '{example.Name}' as {example.Type}; keyboard-focusable: {focusable}");
    }

    private async Task DebateRejectionAsync()
    {
        var from = _vm.Nodes.First(n => n.Id.ToString() == "T1");
        var debate = _vm.Nodes.First(n => n.Kind == WorkTaskKind.Debate);
        var before = _vm.Connections.Count;
        string? hover = null;
        await DragAsync(ConnectorPoint(from.Outputs[0]), ConnectorPoint(debate.Inputs[0]), whileHeld: () =>
        {
            hover = _vm.PendingConnection.PreviewText;
            return Task.CompletedTask;
        });
        Check("small", "a drag onto the disabled Debate node is refused with an explanation", _vm.Connections.Count == before && hover == "Debate: disponible en la Fase 5.", $"wires {before}->{_vm.Connections.Count}, hover text '{hover}'");
    }
}
