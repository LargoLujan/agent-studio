using System.Windows;

namespace AgentStudio.Desktop.Views;

/// <summary>The modal window of the question about unsaved edits (see <c>WpfUnsavedChangesPrompt</c>); no logic here.</summary>
public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow() => InitializeComponent();
}
