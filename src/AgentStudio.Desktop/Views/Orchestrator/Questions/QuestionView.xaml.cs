using System.Windows.Controls;

namespace AgentStudio.Desktop.Views.Orchestrator.Questions;

/// <summary>The question of an agent with its options (docs/17 D-31); no logic here, everything is in <c>QuestionViewModel</c>.</summary>
public partial class QuestionView : UserControl
{
    public QuestionView() => InitializeComponent();
}
