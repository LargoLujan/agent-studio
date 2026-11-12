using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Desktop.Controls.Info;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The checks of the inventory of the interface (docs/17 D-62, F14-10), each over a screen already laid out. Each returns the
/// problems it finds, one line each, so a failure lists everything at once.
/// </summary>
internal static partial class UiChecks
{
    /// <summary>A text that is a missing key shown as <c>[Key]</c> (<c>IUiTextProvider.Get</c>).</summary>
    [GeneratedRegex(@"\[[A-Z][A-Za-z0-9]*(_[A-Za-z0-9]+)+\]")]
    private static partial Regex MissingKey();

    /// <summary>Spanish function words that no English text of the interface uses.</summary>
    private static readonly HashSet<string> SpanishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "el", "los", "las", "para", "con", "sin", "una", "por", "que", "sus", "está", "están", "cuando", "hay", "tu", "tus",
        "más", "aún", "también", "pero", "este", "esta", "estos", "estas", "puedes", "ningún", "ninguna", "sobre",
    };

    /// <summary>English function words that no Spanish text of the interface uses.</summary>
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "your", "you", "with", "this", "that", "from", "are", "will", "can", "not", "for", "of", "is", "have",
    };

    [GeneratedRegex(@"[¿¡ñáéíóúü]", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishLetters();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();

    /// <summary>The controls a person can use: each must be reachable, and each must have an accessible name.</summary>
    public static bool IsInteractive(FrameworkElement element) =>
        element is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider or TabItem or MenuItem or Expander or ListBoxItem or TreeViewItem;

    /// <summary>
    /// A piece of the template of a control of WPF (the arrow of a tree row, the drop-down button of a combo box, the arrows of a
    /// scroll bar): the control it belongs to is the one that has the name and takes the Tab, not the piece.
    /// </summary>
    private static bool IsTemplatePart(FrameworkElement element) =>
        element.TemplatedParent is ComboBox or ScrollBar or Slider or TreeViewItem or ListBoxItem or TabItem or MenuItem or Expander or TextBoxBase or ScrollViewer or ProgressBar;

    /// <summary>The items of a list, a tree or a tab strip are reached with the arrows once the list has the focus, not with Tab.</summary>
    private static bool IsItemOfAList(FrameworkElement element) => element is TreeViewItem or ListBoxItem or TabItem or MenuItem;

    /// <summary>The exact text of every shown element that says something to the person, with where it is.</summary>
    public static IReadOnlyList<(string Where, string Text)> VisibleTexts(FrameworkElement root)
    {
        var texts = new List<(string, string)>();
        foreach (var element in UiTree.All(root).OfType<FrameworkElement>().Where(e => UiTree.IsShown(e, root)))
        {
            var where = element.GetType().Name + (string.IsNullOrEmpty(element.Name) ? string.Empty : "#" + element.Name);
            if (element is TextBlock block && UiTree.TextOf(block) is { Length: > 0 } text)
            {
                texts.Add((where, text));
            }

            if (element is ContentControl { Content: string content } && content.Length > 0)
            {
                texts.Add((where + ".Content", content));
            }

            if (element is HeaderedContentControl { Header: string header } && header.Length > 0)
            {
                texts.Add((where + ".Header", header));
            }

            if (element.ToolTip is string tip && tip.Length > 0)
            {
                texts.Add((where + ".ToolTip", tip));
            }

            if (AutomationProperties.GetName(element) is { Length: > 0 } name)
            {
                texts.Add((where + ".AutomationName", name));
            }

            if (AutomationProperties.GetHelpText(element) is { Length: > 0 } help)
            {
                texts.Add((where + ".HelpText", help));
            }

            if (element is InfoButton info)
            {
                texts.Add((where + ".SectionName", info.SectionName));
                texts.Add((where + ".Explanation", info.Explanation));
                texts.AddRange(info.Actions.Select(a => (where + ".Action", a)));
            }
        }

        return texts;
    }

    /// <summary>No text comes out as <c>[Key]</c>: every key a screen asks for exists in the resources.</summary>
    public static IEnumerable<string> MissingKeys(IEnumerable<(string Where, string Text)> texts) =>
        texts.Where(t => MissingKey().IsMatch(t.Text)).Select(t => $"missing key: {t.Where} says '{Shorten(t.Text)}'");

    /// <summary>
    /// Each control that can be used has an accessible name that is not empty, not the name of its type and not only an icon;
    /// each ⓘ is called «Información sobre &lt;sección&gt;» in the language of the screen (<paramref name="infoName"/> says it).
    /// </summary>
    public static IEnumerable<string> Names(FrameworkElement root, Func<string, string> infoName)
    {
        foreach (var element in UiTree.Shown<FrameworkElement>(root).Where(e => IsInteractive(e) && !IsTemplatePart(e)))
        {
            var name = UiTree.NameOf(element);
            var where = Path(element, root);
            if (string.IsNullOrWhiteSpace(name))
            {
                yield return $"no accessible name: {where}";
            }
            else if (name == element.GetType().Name || name == element.GetType().FullName || name.StartsWith("AgentStudio.", StringComparison.Ordinal) || name.StartsWith("System.Windows.", StringComparison.Ordinal))
            {
                yield return $"accessible name is a type name: {where} is '{Shorten(name)}'";
            }
            else if (name.All(c => c is >= '' and <= '' || char.IsWhiteSpace(c)))
            {
                yield return $"accessible name is only an icon: {where}";
            }
        }

        foreach (var info in UiTree.Shown<InfoButton>(root))
        {
            var section = info.SectionName;
            var toggle = UiTree.All(info).OfType<ToggleButton>().First();
            var expected = infoName(section);
            var name = UiTree.NameOf(toggle);
            if (string.IsNullOrWhiteSpace(section))
            {
                yield return $"info button without a section name: {Path(info, root)}";
            }
            else if (name != expected)
            {
                yield return $"info button name '{Shorten(name)}' is not '{Shorten(expected)}': {Path(info, root)}";
            }

            if (string.IsNullOrWhiteSpace(info.Explanation))
            {
                yield return $"info button without explanation: {Path(info, root)} ({section})";
            }
        }
    }

    /// <summary>Each control that can be used is reached with Tab: it can take focus and is a tab stop.</summary>
    public static IEnumerable<string> Keyboard(FrameworkElement root)
    {
        foreach (var element in UiTree.Shown<FrameworkElement>(root).Where(e => IsInteractive(e) && e.IsEnabled && !IsTemplatePart(e) && !IsItemOfAList(e)))
        {
            if (!element.Focusable || !KeyboardNavigation.GetIsTabStop(element))
            {
                yield return $"not reachable with Tab: {Path(element, root)} (Focusable={element.Focusable}, IsTabStop={KeyboardNavigation.GetIsTabStop(element)})";
            }
        }
    }

    /// <summary>
    /// No text is cut: a text that cannot wrap and has no trimming must fit the room the layout gave it, and a text that wraps
    /// must show all its lines. A text with <c>TextWrapping</c> or <c>TextTrimming</c> set says what it does when it does not fit,
    /// and the one that wraps is still read in full. The room is read from the layout (<see cref="LayoutInformation.GetLayoutClip"/>
    /// and the clip of the ancestors), never from pixels.
    /// </summary>
    public static IEnumerable<string> Cut(FrameworkElement root)
    {
        foreach (var block in UiTree.Shown<TextBlock>(root))
        {
            var text = UiTree.TextOf(block);
            if (text.Length == 0)
            {
                continue;
            }

            // The selected item of a combo box is shown in the box the combo has, the way every combo box of Windows shows it; the
            // whole text is in the list and in the tooltip.
            if (IsSelectionOfAComboBox(block))
            {
                continue;
            }

            var wraps = block.TextWrapping != TextWrapping.NoWrap;
            var trims = block.TextTrimming != TextTrimming.None;
            if (trims && !wraps)
            {
                continue;
            }

            if (OverflowOf(block, root) is { } overflow)
            {
                var how = wraps ? "wraps but is cut" : "no TextWrapping or TextTrimming";
                yield return $"cut text ({how}): {Path(block, root)} '{Shorten(text)}', {overflow}";
            }
        }
    }

    private static bool IsSelectionOfAComboBox(DependencyObject block)
    {
        for (var current = VisualTreeHelper.GetParent(block); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContentPresenter { TemplatedParent: ComboBox })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How much of the text is outside the room it has, or null when it all fits. The room is the slot the layout gave it (its own
    /// layout clip) and the area every ancestor that clips lets through, up to a scroll viewer, which scrolls what does not fit.
    /// </summary>
    private static string? OverflowOf(TextBlock block, FrameworkElement root)
    {
        var tolerance = 0.75;
        if (LayoutInformation.GetLayoutClip(block) is { } own)
        {
            var slot = own.Bounds;
            if (block.ActualWidth - slot.Width > tolerance || block.ActualHeight - slot.Height > tolerance)
            {
                return $"{block.ActualWidth:0}x{block.ActualHeight:0} in a slot of {slot.Width:0}x{slot.Height:0}";
            }
        }

        var own2 = new Rect(0, 0, block.ActualWidth, block.ActualHeight);
        DependencyObject? current = VisualTreeHelper.GetParent(block);
        var checkRight = true;
        var checkBottom = true;
        while (current is FrameworkElement ancestor && !ReferenceEquals(ancestor, root))
        {
            // The canvas of the orchestrator is panned and zoomed: what lies beyond the viewport is reached by moving it, not cut.
            if (ancestor.GetType().Name == "NodifyCanvas")
            {
                return null;
            }

            // A scroll viewer scrolls what does not fit on the axis it scrolls, and what is inside it is reached by scrolling, however
            // small the room above it: from there up only the other axis can cut.
            if (ancestor is ScrollContentPresenter { ScrollOwner: { } owner })
            {
                checkRight &= owner.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled;
                checkBottom &= owner.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled;
            }

            Rect? room = null;
            if (LayoutInformation.GetLayoutClip(ancestor) is { } clip)
            {
                room = clip.Bounds;
            }
            else if (ancestor.ClipToBounds)
            {
                room = new Rect(0, 0, ancestor.ActualWidth, ancestor.ActualHeight);
            }

            if (room is { } bounds)
            {
                var inside = block.TransformToAncestor(ancestor).TransformBounds(own2);
                if ((checkRight && inside.Right - bounds.Right > tolerance) || (checkBottom && inside.Bottom - bounds.Bottom > tolerance))
                {
                    return $"{inside.Width:0}x{inside.Height:0} at {inside.Left:0},{inside.Top:0} in {ancestor.GetType().Name} of {bounds.Width:0}x{bounds.Height:0}";
                }
            }

            current = VisualTreeHelper.GetParent(ancestor);
        }

        return null;
    }

    /// <summary>
    /// The language: in English no visible text is in Spanish, and in Spanish none is in English. A text is in the other language
    /// when it has a letter only the other writes (¿ ¡ ñ and the accents of Spanish) or a function word of the other.
    /// </summary>
    public static IEnumerable<string> Language(IEnumerable<(string Where, string Text)> texts, bool english)
    {
        foreach (var (where, text) in texts)
        {
            var words = Words().Matches(text).Select(m => m.Value).ToList();
            if (english)
            {
                var spanish = words.Where(SpanishWords.Contains).Distinct().ToList();
                // «Ctrl+Ñ» names a key of the Spanish keyboard, and that is what the English text says it is.
                if (SpanishLetters().IsMatch(text.Replace("Ctrl+Ñ", string.Empty, StringComparison.Ordinal)) || spanish.Count > 0)
                {
                    yield return $"Spanish text in English: {where} says '{Shorten(text)}'";
                }
            }
            else
            {
                var found = words.Where(EnglishWords.Contains).Distinct().ToList();
                if (found.Count > 0)
                {
                    yield return $"English text in Spanish: {where} says '{Shorten(text)}' ({string.Join(", ", found)})";
                }
            }
        }
    }

    /// <summary>Where an element is: the chain of named elements and types from the root, short enough to find it.</summary>
    public static string Path(FrameworkElement element, FrameworkElement root)
    {
        var parts = new List<string>();
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement fe && (!string.IsNullOrEmpty(fe.Name) || ReferenceEquals(fe, element) || ReferenceEquals(fe, root) || fe is UserControl))
            {
                parts.Add(fe.GetType().Name + (string.IsNullOrEmpty(fe.Name) ? string.Empty : "#" + fe.Name));
            }

            if (ReferenceEquals(current, root))
            {
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static string Shorten(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= 70 ? single : single[..67] + "...";
    }
}
