using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentStudio.Desktop.Localization;

namespace AgentStudio.Tests.Desktop.UiInventory;

/// <summary>
/// The checks of the inventory against screens that are wrong on purpose: an inventory that cannot fail would say nothing, so each
/// check must find its defect and leave the right screen alone (docs/17 D-62).
/// </summary>
[Trait("Category", "UiInventory")]
public sealed class UiChecksTests
{
    private static StackPanel Narrow(params UIElement[] children)
    {
        var host = new StackPanel { Width = 60 };
        foreach (var child in children)
        {
            host.Children.Add(child);
        }

        return host;
    }

    private static T Laid<T>(T root, double width = 60, double height = 200)
        where T : FrameworkElement
    {
        UiTree.Lay(root, width, height);
        return root;
    }

    [Fact]
    public void A_text_that_cannot_wrap_in_a_room_too_small_is_cut_and_one_that_trims_or_wraps_is_not()
    {
        var (cut, trimmed, wrapped) = WpfHost.Run(() =>
        {
            var plain = new Border { Width = 60, ClipToBounds = true, Child = new TextBlock { Text = "A text that is much longer than its room" } };
            var trimming = new Border { Width = 60, ClipToBounds = true, Child = new TextBlock { Text = "A text that is much longer than its room", TextTrimming = TextTrimming.CharacterEllipsis } };
            var wrapping = new Border { Width = 60, ClipToBounds = true, Child = new TextBlock { Text = "A text that is much longer than its room", TextWrapping = TextWrapping.Wrap } };
            return (UiChecks.Cut(Laid(Narrow(plain))).ToList(), UiChecks.Cut(Laid(Narrow(trimming))).ToList(), UiChecks.Cut(Laid(Narrow(wrapping))).ToList());
        });

        Assert.Single(cut);
        Assert.Contains("no TextWrapping or TextTrimming", cut[0], StringComparison.Ordinal);
        Assert.Empty(trimmed);
        Assert.Empty(wrapped);
    }

    [Fact]
    public void A_control_without_an_accessible_name_or_named_after_its_type_is_found_and_one_with_a_name_is_not()
    {
        var problems = WpfHost.Run(() =>
        {
            var named = new Button { Content = "Save" };
            var iconOnly = new Button { Content = "" };
            var nameless = new Button();
            var typeName = new Button { Content = "x" };
            AutomationProperties.SetName(typeName, typeof(Button).FullName!);
            var host = new StackPanel();
            foreach (var button in new[] { named, iconOnly, nameless, typeName })
            {
                host.Children.Add(button);
            }

            return UiChecks.Names(Laid(host), section => section).ToList();
        });

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("no accessible name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("accessible name is a type name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("accessible name is only an icon", StringComparison.Ordinal));
    }

    [Fact]
    public void A_control_that_cannot_take_the_tab_key_is_found()
    {
        var problems = WpfHost.Run(() =>
        {
            var host = new StackPanel();
            host.Children.Add(new Button { Content = "Reachable" });
            host.Children.Add(new Button { Content = "Skipped", IsTabStop = false });
            return UiChecks.Keyboard(Laid(host)).ToList();
        });

        Assert.Single(problems);
    }

    [Fact]
    public void A_text_that_is_a_missing_key_is_found()
    {
        var found = UiChecks.MissingKeys([("a", "Guardar"), ("b", "[Main_No_Existe]"), ("c", "a list [1, 2]")]).ToList();

        Assert.Single(found);
        Assert.Contains("[Main_No_Existe]", found[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Spanish_in_english_and_english_in_spanish_are_found_and_the_key_of_the_spanish_keyboard_is_not()
    {
        var spanishInEnglish = UiChecks.Language([("a", "Save the file"), ("b", "Guardar los cambios"), ("c", "Ctrl+`, or Ctrl+Ñ on a Spanish keyboard"), ("d", "Delete the selection (Del)")], english: true).ToList();
        var englishInSpanish = UiChecks.Language([("a", "Guardar el fichero"), ("b", "Save the changes and close")], english: false).ToList();

        Assert.Single(spanishInEnglish);
        Assert.Contains("Guardar los cambios", spanishInEnglish[0], StringComparison.Ordinal);
        Assert.Single(englishInSpanish);
        Assert.Contains("Save the changes", englishInSpanish[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_pseudolocalization_accents_the_letters_lengthens_the_text_and_keeps_the_placeholders()
    {
        var provider = new PseudoTextProvider(new ResourceTextProvider(CultureInfo.GetCultureInfo("en")));
        var source = new ResourceTextProvider(CultureInfo.GetCultureInfo("en"));
        var pseudo = PseudoTextProvider.Pseudo("Open the {0} file");

        Assert.NotEqual("Open the {0} file", pseudo);
        Assert.Contains("{0}", pseudo, StringComparison.Ordinal);

        // 11 letters outside the placeholder: about 40 % more of them is added.
        Assert.Equal("Open the {0} file".Length + 5, pseudo.Length);
        Assert.Equal("Öpéñ", PseudoTextProvider.Pseudo("Open").Split(' ')[0]);
        Assert.Equal(PseudoTextProvider.Pseudo(source.Get("Main_Lock")), provider.Get("Main_Lock"));
        Assert.StartsWith("[", provider.Get("Key_That_Does_Not_Exist"), StringComparison.Ordinal);
        Assert.Contains("7", provider.Format("Info_Button_Name", 7), StringComparison.Ordinal);
    }
}
