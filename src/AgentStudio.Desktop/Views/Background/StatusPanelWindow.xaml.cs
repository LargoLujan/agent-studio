using System.Windows;
using System.Windows.Input;

namespace AgentStudio.Desktop.Views.Background;

/// <summary>The window of the status panel (see WindowStatusPanelPresenter); Esc hides it, and nothing else lives here.</summary>
public partial class StatusPanelWindow : Window
{
    public StatusPanelWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }
}
