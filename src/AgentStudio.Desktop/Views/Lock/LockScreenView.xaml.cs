using System.Windows.Controls;

namespace AgentStudio.Desktop.Views.Lock;

/// <summary>The lock screen over the interface; bindings only. Its DataContext is a <c>LockScreenViewModel</c>.</summary>
public partial class LockScreenView : UserControl
{
    public LockScreenView() => InitializeComponent();

    /// <summary>Gives the keyboard to «Desbloquear con Windows Hello»: MainWindow calls it when the interface locks.</summary>
    public void FocusUnlock() => UnlockButton.Focus();
}
