using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using AgentStudio.Desktop.Localization;
using AgentStudio.Desktop.Repository.PullRequests;
using AgentStudio.Desktop.Views.Repository.PullRequests;
using AgentStudio.Domain.Orchestration;
using AgentStudio.Tests.Desktop.Orchestrator;

namespace AgentStudio.Tests.Desktop.RepositoryScreen.PullRequests;

/// <summary>Day 0 presentation regressions over the real view and doubles: no window, provider or remote is launched.</summary>
public sealed class PullRequestsViewTests
{
    [Fact]
    public async Task Opening_a_card_scrolls_to_its_heading_and_prepares_its_title_for_editing_without_publishing()
    {
        var (offset, top, viewport, targetName, calls, diagnostic) = await WpfHost.RunAsync(async () =>
        {
            var world = new PrWorld();
            await world.Model.SetProjectAsync(PrData.Project);
            var view = new PullRequestsView { DataContext = world.Model };
            var scroll = ScrolledSection(view);
            Draw(scroll);
            Assert.Equal(0, scroll.VerticalOffset);
            var observation = ObserveReveal(view);

            await world.Model.ShowPublishCardAsync(new RunId("20261115-day0"));
            Draw(scroll);
            var card = (Border)view.FindName("ActionCard");
            var title = Assert.IsType<TextBox>(CardFocusTarget(view));
            return (scroll.VerticalOffset, card.TransformToAncestor(scroll).Transform(new Point()).Y,
                scroll.ViewportHeight, System.Windows.Automation.AutomationProperties.GetName(title), world.Publication.Calls.ToList(), observation());
        });

        Assert.True(offset > 0, "The card opened below the viewport and must be brought into it. " + diagnostic);
        Assert.InRange(viewport, 1, 420);
        Assert.InRange(top, -1, viewport - 24);
        Assert.Equal(Texts.Get("Hosting_Publish_TitleLabel"), targetName);
        Assert.DoesNotContain("publish", calls);
    }

    [Fact]
    public async Task Editing_a_card_does_not_scroll_again_and_an_action_without_fields_starts_on_cancel()
    {
        var (before, after, target, calls) = await WpfHost.RunAsync(async () =>
        {
            var world = new PrWorld();
            world.Projector.Views.Add(PrData.View(PrData.Snapshot()));
            await world.Model.SetProjectAsync(PrData.Project);
            var view = new PullRequestsView { DataContext = world.Model };
            var scroll = ScrolledSection(view);
            Draw(scroll);
            await world.Model.ShowPublishCardAsync(new RunId("20261115-day0"));
            Draw(scroll);

            scroll.ScrollToTop();
            Draw(scroll);
            var offsetBefore = scroll.VerticalOffset;
            Assert.IsType<PublishCardViewModel>(world.Model.Card).Title = "Changed title";
            Draw(scroll);
            var offsetAfter = scroll.VerticalOffset;

            world.Model.Card!.CancelCommand.Execute(null);
            world.Model.Selected = world.Model.Rows.Single();
            world.Model.OpenCloseCardCommand.Execute(null);
            Draw(scroll);
            return (offsetBefore, offsetAfter, (CardFocusTarget(view) as Button)?.Content, world.Publication.Calls.ToList());
        });

        Assert.Equal(before, after);
        Assert.Equal(Texts.Get("Hosting_Card_Cancel"), target);
        Assert.DoesNotContain("publish", calls);
        Assert.DoesNotContain("close", calls);
    }

    [Fact]
    public async Task A_card_that_arrives_in_a_hidden_section_does_not_move_the_screen()
    {
        var (hiddenOffset, shownOffset, calls, diagnostic) = await WpfHost.RunAsync(async () =>
        {
            var world = new PrWorld();
            await world.Model.SetProjectAsync(PrData.Project);
            var view = new PullRequestsView { DataContext = world.Model, Visibility = Visibility.Collapsed };
            var scroll = ScrolledSection(view);
            Draw(scroll);
            var observation = ObserveReveal(view);

            await world.Model.ShowPublishCardAsync(new RunId("20261115-hidden"));
            Draw(scroll);
            var whileHidden = scroll.VerticalOffset;

            world.Model.Card!.CancelCommand.Execute(null);
            view.Visibility = Visibility.Visible;
            await world.Model.ShowPublishCardAsync(new RunId("20261115-visible"));
            Draw(scroll);
            return (whileHidden, scroll.VerticalOffset, world.Publication.Calls.ToList(), observation());
        });

        Assert.Equal(0, hiddenOffset);
        Assert.True(shownOffset > 0, diagnostic);
        Assert.DoesNotContain("publish", calls);
    }

    [Fact]
    public async Task The_header_buttons_match_and_the_empty_state_is_neutral_while_a_missing_remote_is_a_warning()
    {
        var (syncHeight, checkHeight, textColor, warningColor, foreground, expectedForeground, hasFocusStyle) = await WpfHost.RunAsync(async () =>
        {
            var world = new PrWorld();
            await world.Model.SetProjectAsync(PrData.Project);
            var view = new PullRequestsView { DataContext = world.Model };
            Draw(view);
            var sync = (Button)view.FindName("SyncNowButton");
            var check = (Button)view.FindName("CheckConnectionButton");
            var guide = (TextBlock)view.FindName("GuideText");
            var normal = ColorOf(guide.Foreground);
            Assert.Equal(ColorOf((Brush)view.FindResource("TextSecondaryBrush")), normal);
            var permissions = (ToggleButton)view.FindName("PermissionsToggle");

            world.Providers.Repository = null;
            await world.Model.SetProjectAsync(PrData.Project);
            Draw(view);
            Assert.Equal(ColorOf((Brush)view.FindResource("WarningBrush")), ColorOf(guide.Foreground));
            return (sync.ActualHeight, check.ActualHeight, normal, ColorOf(guide.Foreground),
                ColorOf(permissions.Foreground), ColorOf((Brush)view.FindResource("TextPrimaryBrush")), permissions.FocusVisualStyle is not null);
        });

        Assert.Equal(syncHeight, checkHeight, 1);
        Assert.True(syncHeight > 20);
        Assert.NotEqual(textColor, warningColor);
        Assert.Equal(expectedForeground, foreground);
        Assert.True(hasFocusStyle);
    }

    private static ScrollViewer ScrolledSection(PullRequestsView view) => new()
    {
        // VisualBrush may relayout an unparented root with infinite space; keep the viewport the size under test.
        Width = 760,
        Height = 420,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new StackPanel
        {
            Children = { new Border { Height = 700 }, view, new Border { Height = 500 } },
        },
    };

    private static void Draw(FrameworkElement root) => OffscreenRender.Render(root, 760, 420);

    private static Func<string> ObserveReveal(PullRequestsView view)
    {
        var content = (ContentControl)view.FindName("ActionCardContent");
        var card = (Border)view.FindName("ActionCard");
        var updates = 0;
        var requests = 0;
        var target = Rect.Empty;
        Binding.AddTargetUpdatedHandler(content, (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty)
            {
                updates++;
            }
        });
        card.RequestBringIntoView += (_, e) => { requests++; target = e.TargetRect; };
        return () => $"Content updates: {updates}; reveal requests: {requests}; target: {target}; card: {card.ActualWidth} × {card.ActualHeight}, {card.Visibility}; loaded: {view.IsLoaded}.";
    }

    // Offscreen rendering cannot own keyboard focus; inspect the target without publishing or opening a window.
    private static UIElement? CardFocusTarget(PullRequestsView view) =>
        (UIElement?)typeof(PullRequestsView).GetMethod("CardFocusTarget", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;
}
