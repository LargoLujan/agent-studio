using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentStudio.Desktop.Orchestrator;

namespace AgentStudio.Desktop.Views.Orchestrator;

/// <summary>
/// The «Orquestador» document tab: bindings, and the hand-off of the keyboard when another screen brings the person here
/// (<see cref="OrchestratorDocumentViewModel.PendingFocus"/>). The dock creates this view only when the tab is shown, and again
/// when a layout is restored, so the request waits on the document until a view on screen takes it.
/// </summary>
public partial class OrchestratorView : UserControl
{
    private OrchestratorDocumentViewModel? _document;
    private bool _onScreen;

    public OrchestratorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Bands.ScrollChanged += (_, _) => KeepWarningsWithApprove();
    }

    /// <summary>
    /// ADR-0018 §3: «Aprobar plan» is never seen with the warnings of the plan cut above it. Whenever the bands scroll so that the row
    /// of the button is in view and the top of the warnings is not, they scroll back up to the top of the warnings: the button goes
    /// down, out of view if they do not fit together. The box of the warnings is never taller than the bands leave above the row, so
    /// at any size of the window there is a place where both are seen.
    /// </summary>
    private void KeepWarningsWithApprove()
    {
        if (PlanWarningsScroller.ActualHeight <= 0 || DecisionRow.ActualHeight <= 0 || ReviewContent.Visibility != Visibility.Visible)
        {
            return;
        }

        var warningsTop = PlanWarningsScroller.TransformToAncestor(Bands).Transform(default).Y;
        var rowTop = DecisionRow.TransformToAncestor(Bands).Transform(default).Y;
        var rowInView = rowTop < Bands.ViewportHeight && rowTop + DecisionRow.ActualHeight > 0;
        if (rowInView && warningsTop < -0.5)
        {
            Bands.ScrollToVerticalOffset(Math.Max(0, Bands.VerticalOffset + warningsTop));
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Follow(null);
        if (_onScreen)
        {
            Follow(DataContext as OrchestratorDocumentViewModel);
            TakeFocusLater();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _onScreen = true;
        Follow(DataContext as OrchestratorDocumentViewModel);
        TakeFocusLater();
    }

    /// <summary>A view off screen lets go of its document, so a tab the dock recreated does not keep the old view alive.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _onScreen = false;
        Follow(null);
    }

    private void Follow(OrchestratorDocumentViewModel? document)
    {
        if (_document is not null)
        {
            _document.FocusRequested -= OnFocusRequested;
        }

        _document = document;
        if (_document is not null)
        {
            _document.FocusRequested += OnFocusRequested;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) => TakeFocusLater();

    /// <summary>
    /// Once the band «Preparación» it opened is laid out, and the dock is done moving the keyboard (it does so after it activates
    /// a tab, so the shell waits for <see cref="DispatcherPriority.ContextIdle"/> too), the part asked for is brought into view and
    /// given the keyboard. The request stays on the document until the keyboard lands there; it is tried once more if it did not, and then it expires.
    /// </summary>
    private void TakeFocusLater()
    {
        if (_onScreen && _document?.PendingFocus is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => TakeFocus(retry: true));
        }
    }

    private void TakeFocus(bool retry)
    {
        if (!_onScreen || _document?.PendingFocus is not { } target)
        {
            return;
        }

        if (PreparationBand.BringToKeyboard(target))
        {
            _document.TakePendingFocus();
        }
        else if (retry)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => TakeFocus(retry: false));
        }
        else
        {
            // Tried twice: the request expires, so a later showing of the tab never takes the keyboard nor scrolls the bands by surprise.
            _document.TakePendingFocus();
        }
    }
}
