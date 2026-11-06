using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AgentStudio.Desktop.Controls.Info;
using AgentStudio.Desktop.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AgentStudio.Tests.Desktop.Controls;

/// <summary>
/// The ⓘ button (docs/17 D-54, F13-19): built offscreen, never shown. Its accessible name is «Información sobre &lt;sección&gt;»,
/// its texts come from the resources in the language it is given, Intro opens it, Esc closes it and gives the focus back, and
/// «Más ayuda» is only there when it leads somewhere.
/// </summary>
public sealed class InfoButtonTests
{
    private static readonly ResourceTextProvider Spanish = new(CultureInfo.GetCultureInfo("es-ES"));

    private static readonly ResourceTextProvider English = new(CultureInfo.GetCultureInfo("en"));

    private static ToggleButton Toggle(InfoButton button) => (ToggleButton)button.FindName("Toggle");

    /// <summary>The small window is a popup, a window of its own: the tests open the button without it, so nothing is shown.</summary>
    private static InfoButton Offscreen(IUiTextProvider texts, string key = "Info_Language")
    {
        var button = new InfoButton { InfoKey = key, TextProvider = texts };
        var bubble = (Popup)button.FindName("Bubble");
        Assert.Equal(BindingMode.TwoWay, BindingOperations.GetBinding(bubble, Popup.IsOpenProperty)?.Mode);
        BindingOperations.ClearBinding(bubble, Popup.IsOpenProperty);
        return button;
    }

    [Fact]
    public void Its_name_and_its_texts_come_from_the_resources_in_spanish_and_in_english()
    {
        var (spanish, english) = WpfHost.Run(() =>
        {
            var es = Offscreen(Spanish);
            var en = Offscreen(English);
            var peer = UIElementAutomationPeer.CreatePeerForElement(Toggle(es));
            return (
                (peer.GetName(), peer.GetAutomationControlType(), es.SectionName, es.Explanation, es.Actions),
                (UIElementAutomationPeer.CreatePeerForElement(Toggle(en)).GetName(), en.Explanation, en.Actions));
        });

        Assert.Equal("Información sobre Idioma", spanish.Item1);
        Assert.Equal(AutomationControlType.Button, spanish.Item2);
        Assert.Equal("Idioma", spanish.SectionName);
        Assert.Equal(Spanish.Get("Info_Language_What"), spanish.Explanation);
        Assert.Equal(2, spanish.Actions.Count);
        Assert.StartsWith("Elegir otro idioma", spanish.Actions[0], StringComparison.Ordinal);
        Assert.Equal("Information about Language", english.Item1);
        Assert.Equal(English.Get("Info_Language_What"), english.Explanation);
        Assert.StartsWith("Choose another language", english.Actions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_topic_given_by_the_screen_names_the_button()
    {
        var name = WpfHost.Run(() =>
        {
            var button = Offscreen(English);
            button.Topic = "Consumo";
            return UIElementAutomationPeer.CreatePeerForElement(Toggle(button)).GetName();
        });

        Assert.Equal("Information about Consumo", name);
    }

    [Fact]
    public void Intro_opens_it_and_esc_closes_it_and_gives_the_focus_back()
    {
        var (afterToggle, escHandled, afterEsc, toggleProvider) = WpfHost.Run(() =>
        {
            var button = Offscreen(Spanish);
            var toggle = Toggle(button);
            var peer = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle);
            peer.Toggle();
            var opened = button.IsOpen;
            var handled = TestKeys.PreviewKeyDown(toggle, Key.Escape);
            return (opened, handled, button.IsOpen, peer is not null);
        });

        Assert.True(toggleProvider);
        Assert.True(afterToggle);
        Assert.True(escHandled);
        Assert.False(afterEsc);
        Assert.True(WpfHost.Run(() => (bool)Toggle(Offscreen(Spanish)).GetValue(KeyboardNavigation.AcceptsReturnProperty)));
    }

    [Fact]
    public void Esc_does_nothing_while_it_is_closed()
    {
        Assert.False(WpfHost.Run(() => TestKeys.PreviewKeyDown(Toggle(Offscreen(Spanish)), Key.Escape)));
    }

    [Fact]
    public void More_help_is_shown_only_when_it_leads_somewhere()
    {
        var (without, with, text) = WpfHost.Run(() =>
        {
            var button = Offscreen(Spanish);
            var more = (Button)button.FindName("MoreHelp");
            var before = more.Visibility;
            button.MoreHelpCommand = new RelayCommand<string?>(_ => { });
            return (before, more.Visibility, (string)more.Content);
        });

        Assert.Equal(Visibility.Collapsed, without);
        Assert.Equal(Visibility.Visible, with);
        Assert.Equal("Más ayuda", text);
    }
}
