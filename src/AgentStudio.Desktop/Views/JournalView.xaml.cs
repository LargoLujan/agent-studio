using System.Windows.Controls;
using System.Windows.Input;

namespace AgentStudio.Desktop.Views;

public partial class JournalView : UserControl
{
    public JournalView() => InitializeComponent();

    /// <summary>
    /// The journal never prints nor finds (V31): its gesture goes on to the window, where Ctrl+P is «Ir al fichero». The
    /// answer is handled, so no handler of the control's class can take the command, and the routing of the key goes on.
    /// </summary>
    private void LetTheWindowHaveIt(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = false;
        e.ContinueRouting = true;
        e.Handled = true;
    }
}
