using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentStudio.Desktop.Orchestrator;

namespace AgentStudio.Desktop.Views.Orchestrator;

/// <summary>The preparation band of the orchestrator tab (request, requirements, agents, profile); bindings only.</summary>
public partial class PreparationView : UserControl
{
    public PreparationView() => InitializeComponent();

    /// <summary>
    /// Brings <paramref name="target"/> into view and gives it the keyboard: the box of the request, or the first control of
    /// «Requisitos». The band must be open (the document opens it before it asks). True when the keyboard landed there: in a window
    /// that is not active, when it is the focused element of its scope, which Windows gives the keyboard back to.
    /// </summary>
    public bool BringToKeyboard(OrchestratorFocus target)
    {
        UpdateLayout();
        var element = target == OrchestratorFocus.Request ? RequestBox : FirstTabStop(RequirementsEditor);
        if (target == OrchestratorFocus.Requirements)
        {
            RequirementsSection.BringIntoView();
        }

        if (element is null)
        {
            return false;
        }

        (element as FrameworkElement)?.BringIntoView();
        element.Focus();
        return element.IsKeyboardFocusWithin || ReferenceEquals(FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)), element);
    }

    /// <summary>The first control of <paramref name="root"/> that the keyboard can reach, in the order of the tree.</summary>
    private static UIElement? FirstTabStop(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element && KeyboardNavigation.GetIsTabStop(element))
            {
                return element;
            }

            if (FirstTabStop(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
