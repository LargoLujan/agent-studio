using System.Windows.Controls;

namespace AgentStudio.Desktop.Views.Background;

/// <summary>The status panel of the tray (see StatusPanelViewModel); no logic here.</summary>
public partial class StatusPanelView : UserControl
{
    public StatusPanelView() => InitializeComponent();

    /// <summary>How tall the list of projects may grow before it scrolls (620 by default, for a panel next to the taskbar).</summary>
    public double ContentMaxHeight
    {
        get => Scroller.MaxHeight;
        set => Scroller.MaxHeight = value;
    }
}
