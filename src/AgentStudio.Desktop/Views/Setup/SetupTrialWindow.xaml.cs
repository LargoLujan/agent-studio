using System.Windows;

namespace AgentStudio.Desktop.Views.Setup;

/// <summary>The modal window that asks before «Probarla ahora» runs commands not accepted yet (see <c>WpfSetupTrialConfirmation</c>); no logic here.</summary>
public partial class SetupTrialWindow : Window
{
    public SetupTrialWindow() => InitializeComponent();
}
