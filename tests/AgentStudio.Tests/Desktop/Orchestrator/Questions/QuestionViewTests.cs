using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using AgentStudio.Desktop.Controls.Info;
using AgentStudio.Desktop.Orchestrator.Questions;
using AgentStudio.Desktop.Views.Orchestrator.Questions;
using AgentStudio.Tests.Desktop.Orchestrator;
using static AgentStudio.Tests.Desktop.Orchestrator.Monitor.MonitorTestSupport;

namespace AgentStudio.Tests.Desktop.Orchestrator.Questions;

/// <summary>
/// F13-14 and F13-8 (docs/17 D-31 and D-17): the window of the question, built offscreen and never shown. It is not modal, it works
/// with the keyboard (arrows choose, Intro answers, Esc decides later), a screen reader reads the question and each option with its
/// cost, and the text of the agent sits inside a frame that says who wrote it.
/// </summary>
public sealed class QuestionViewTests
{
    private static QuestionViewModel Model(bool allowOther = true) =>
        new(Pending(allowOther: allowOther, estimates: [Estimate("a", 9_000, 15_000, 6), Estimate("b", null, null, 2)]), _ => Task.CompletedTask);

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
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

    [Fact]
    public void The_window_is_not_modal_and_holds_the_view_of_the_question()
    {
        var (isWindow, shownInTaskbar, resize, content, inView) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var window = new QuestionWindow { DataContext = Model() };
            return (window is Window, window.ShowInTaskbar, window.ResizeMode, window.Content, new WpfQuestionWindowHost().IsAppInView);
        });

        Assert.True(isWindow);
        Assert.False(shownInTaskbar);
        Assert.Equal(ResizeMode.NoResize, resize);
        Assert.IsType<QuestionView>(content);

        // No main window in the test: the app is not in view, so the tray would be told instead of a window opening.
        Assert.False(inView);
    }

    [Fact]
    public void Every_option_is_a_radio_button_of_one_group_named_with_its_label_detail_recommendation_effort_and_cost()
    {
        var (radios, names, groups) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = Model();
            var view = new QuestionView { DataContext = model };
            var bitmap = OffscreenRender.Render(view, 680, 620);
            OffscreenRender.SaveIfRequested(bitmap, "fase13-pregunta.png");
            var found = Descendants<RadioButton>(view).ToList();
            return (found.Count, found.Select(r => AutomationProperties.GetName(r)).ToList(), found.Select(r => r.GroupName).Distinct().ToList());
        });

        Assert.Equal(3, radios);
        Assert.Equal(["QuestionChoice"], groups);
        Assert.Equal(
            "SQLite. Una base de datos local.. Recomendada por el agente. Esfuerzo pequeño (lo dice el agente). ~9.000 a 15.000 tokens · estimado por Agent Studio · 6 referencias",
            names[0]);
        Assert.Equal(
            "Ficheros JSON. Un fichero por sesión.. Esfuerzo mediano (lo dice el agente). Coste desconocido: hay menos de tres intentos parecidos (2)",
            names[1]);
        Assert.Equal("Otra respuesta", names[2]);
    }

    [Fact]
    public void A_question_that_does_not_allow_other_answers_has_no_box_for_them()
    {
        var (withOther, withoutOther) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            Visibility Of(bool allow)
            {
                var view = new QuestionView { DataContext = Model(allow) };
                OffscreenRender.Render(view, 680, 620);
                return ((FrameworkElement)Descendants<RadioButton>(view).Last().Parent).Visibility;
            }

            return (Of(true), Of(false));
        });

        Assert.Equal(Visibility.Visible, withOther);
        Assert.Equal(Visibility.Collapsed, withoutOther);
    }

    [Fact]
    public void Intro_answers_and_esc_decides_later_and_the_buttons_say_so_in_their_names()
    {
        var (answer, defer, answerName, deferName) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new QuestionView { DataContext = Model() };
            OffscreenRender.Render(view, 680, 620);
            var buttons = Descendants<Button>(view).Where(b => b.IsDefault || b.IsCancel).ToList();
            var a = buttons.Single(b => b.IsDefault);
            var d = buttons.Single(b => b.IsCancel);
            return (a, d, a.Content, d.Content);
        });

        Assert.Equal("Responder", answerName);
        Assert.Equal("Decidir más tarde", deferName);
        Assert.NotSame(answer, defer);
    }

    [Fact]
    public void The_arrows_choose_through_the_radio_buttons_and_a_screen_reader_can_select_them()
    {
        var (selectedByReader, hasPattern) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = Model();
            var view = new QuestionView { DataContext = model };
            OffscreenRender.Render(view, 680, 620);
            var second = Descendants<RadioButton>(view).ElementAt(1);
            var peer = UIElementAutomationPeer.CreatePeerForElement(second);
            var pattern = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
            pattern?.Select();
            return (model.Options[1].IsSelected && model.SelectedOption == model.Options[1], pattern is not null);
        });

        Assert.True(hasPattern);
        Assert.True(selectedByReader);
    }

    [Fact]
    public void The_arrows_move_between_the_options_in_one_cycling_container()
    {
        var (directional, tab) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var view = new QuestionView { DataContext = Model() };
            OffscreenRender.Render(view, 680, 620);
            var container = Descendants<StackPanel>(view).First(p => System.Windows.Input.KeyboardNavigation.GetDirectionalNavigation(p) == System.Windows.Input.KeyboardNavigationMode.Cycle);
            return (System.Windows.Input.KeyboardNavigation.GetDirectionalNavigation(container), System.Windows.Input.KeyboardNavigation.GetTabNavigation(container));
        });

        Assert.Equal(System.Windows.Input.KeyboardNavigationMode.Cycle, directional);
        Assert.Equal(System.Windows.Input.KeyboardNavigationMode.Once, tab);
    }

    [Fact]
    public void The_text_of_the_agent_is_inside_a_frame_that_says_who_wrote_it_and_the_title_and_the_error_are_announced()
    {
        var (frameName, liveTitle, liveError, info) = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = Model();
            var view = new QuestionView { DataContext = model };
            OffscreenRender.Render(view, 680, 620);
            var frame = Descendants<Border>(view).First(b => AutomationProperties.GetName(b) == model.Frame);
            var texts = Descendants<TextBlock>(view).ToList();
            var title = texts.First(t => t.Text == model.Title);
            var error = texts.First(t => t.Style is not null && AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Assertive && !ReferenceEquals(t, title));
            return (AutomationProperties.GetName(frame), AutomationProperties.GetLiveSetting(title), AutomationProperties.GetLiveSetting(error), Descendants<InfoButton>(view).Single().InfoKey);
        });

        Assert.Equal("Pregunta escrita por el agente exec1", frameName);
        Assert.Equal(AutomationLiveSetting.Assertive, liveTitle);
        Assert.Equal(AutomationLiveSetting.Assertive, liveError);
        Assert.Equal("Info_Question", info);
    }

    [Fact]
    public void Preselecting_the_recommended_option_shows_it_chosen_in_the_window()
    {
        var checkedFirst = WpfHost.Run(() =>
        {
            OrchestratorTheme.EnsureLoaded();
            var model = Model();
            var view = new QuestionView { DataContext = model };
            model.PreselectRecommended();
            OffscreenRender.Render(view, 680, 620);
            return Descendants<RadioButton>(view).First().IsChecked;
        });

        Assert.True(checkedFirst);
    }
}
