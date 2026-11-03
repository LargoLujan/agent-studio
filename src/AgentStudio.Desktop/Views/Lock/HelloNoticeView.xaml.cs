using System.Windows.Controls;

namespace AgentStudio.Desktop.Views.Lock;

/// <summary>What an approval question says of Windows Hello; bindings only. Its DataContext is an <c>ApprovalDialogViewModel</c>.</summary>
public partial class HelloNoticeView : UserControl
{
    public HelloNoticeView() => InitializeComponent();
}
