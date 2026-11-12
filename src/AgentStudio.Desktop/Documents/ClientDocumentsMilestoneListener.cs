using AgentStudio.Application.Work;

namespace AgentStudio.Desktop.Documents;

/// <summary>
/// Where the close of a milestone reaches the documentation for clients (docs/16 D-49, docs/17 D-58; <see cref="IMilestoneCloseListener"/>):
/// the screen is told, on the UI thread, so it offers the document of that milestone. Nothing is generated or exported here: the person
/// generates it, reviews what goes out and approves. A failure never undoes the close (the closer catches it).
/// </summary>
public sealed class ClientDocumentsMilestoneListener(ClientDocumentsViewModel screen) : IMilestoneCloseListener
{
    public Task OnClosedAsync(MilestoneClosedNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var name = notice.Milestone.Header.Name;
        var root = notice.Project.RootPath;
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => screen.OfferForClosedMilestoneAsync(root, name)).Task.Unwrap();
        }

        return screen.OfferForClosedMilestoneAsync(root, name);
    }
}
