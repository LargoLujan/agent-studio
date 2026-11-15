using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using AgentStudio.Desktop.Theming;

namespace AgentStudio.Desktop.Views.Repository.PullRequests;

/// <summary>The pull request section reveals a new action card without running any of its commands.</summary>
public partial class PullRequestsView : UserControl
{
    private object? _revealedCard;
    private object? _focusedCard;

    public PullRequestsView() => InitializeComponent();

    private void OnCardUpdated(object? sender, DataTransferEventArgs e)
    {
        if (e.Property == ContentControl.ContentProperty)
        {
            QueueCardReveal();
        }
    }

    private void OnCardVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            QueueCardReveal();
        }
    }

    private void QueueCardReveal()
    {
        var card = ActionCardContent.Content;
        if (card is not null)
        {
            _ = Dispatcher.InvokeAsync(() => RevealCard(card), DispatcherPriority.Loaded);
        }
    }

    private void RevealCard(object card)
    {
        // A late preview or a hidden section must not move the screen or take the keyboard from another window.
        if (!CardIsInVisibleTree() || !ReferenceEquals(ActionCardContent.Content, card))
        {
            return;
        }

        ActionCard.UpdateLayout();
        if (!ReferenceEquals(_focusedCard, card) && Window.GetWindow(this) is { IsActive: true } && CardFocusTarget()?.Focus() == true)
        {
            _focusedCard = card;
        }

        if (ReferenceEquals(_revealedCard, card))
        {
            return;
        }

        _revealedCard = card;
        // Reveal the card from its heading; a card taller than the viewport must not jump to its last button.
        var height = ActionCard.ActualHeight;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(ActionCard); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer { ViewportHeight: > 0 } scroll)
            {
                height = Math.Min(height, scroll.ViewportHeight);
                break;
            }
        }

        ActionCard.BringIntoView(new Rect(0, 0, ActionCard.ActualWidth, height));
    }

    private bool CardIsInVisibleTree()
    {
        for (DependencyObject? parent = ActionCard; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Start editing the card; a card without fields starts on Cancel, never on its publication button.</summary>
    internal UIElement? CardFocusTarget()
    {
        var stops = InitialFocus.TabStops(ActionCardContent).ToList();
        return stops.OfType<TextBox>().FirstOrDefault()
            ?? (UIElement?)stops.OfType<ComboBox>().FirstOrDefault()
            ?? stops.OfType<Button>().LastOrDefault();
    }
}
