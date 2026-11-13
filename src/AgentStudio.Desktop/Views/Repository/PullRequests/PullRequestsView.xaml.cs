using System.Windows.Controls;

namespace AgentStudio.Desktop.Views.Repository.PullRequests;

/// <summary>The section «Peticiones de fusión (PR)» of «Repositorio y cambios»: its data context is a <c>PullRequestsViewModel</c>; bindings only.</summary>
public partial class PullRequestsView : UserControl
{
    public PullRequestsView() => InitializeComponent();
}
