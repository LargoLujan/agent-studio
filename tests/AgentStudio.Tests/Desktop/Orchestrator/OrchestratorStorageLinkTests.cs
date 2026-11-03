using System.IO;
using AgentStudio.Desktop.Orchestrator;
using AgentStudio.Desktop.Orchestrator.Canvas;
using AgentStudio.Desktop.Orchestrator.Session;
using AgentStudio.Domain.Work;
using AgentStudio.Infrastructure.Persistence;
using AgentStudio.Tests.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using static AgentStudio.Tests.Domain.TestData;

namespace AgentStudio.Tests.Desktop.Orchestrator;

/// <summary>
/// The «Orquestador» document over the real project workspace and layout store, with a junction left at the project's
/// <c>.agentstudio</c> (ADR-0018: in the model). Opening the storage is refused (ADR-0010, addendum A-05), and the canvas,
/// which still works, writes nothing through the link.
/// </summary>
public sealed class OrchestratorStorageLinkTests : IDisposable
{
    private readonly TempProjectFolder _folder = new();
    private readonly PlantedLinks _links = new();

    public OrchestratorStorageLinkTests()
    {
        Outside = Path.Combine(Path.GetDirectoryName(_folder.Root)!, "fuera del proyecto");
        Directory.CreateDirectory(Outside);
    }

    /// <summary>A folder next to the project, outside it: where a planted link points.</summary>
    private string Outside { get; }

    public void Dispose()
    {
        // The links go first, as links: the cleanup never walks into what they point to.
        _links.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task The_canvas_of_a_project_whose_storage_was_refused_writes_nothing_through_the_link()
    {
        _links.Junction(_folder.DataDirectory, Outside);
        using var databases = new ProjectDatabases();
        var backend = new OrchestrationBackend(Workspace: new ProjectWorkspace(databases, TimeProvider.System));
        var document = new OrchestratorDocumentViewModel(
            _folder.Root, new OrchestrationSession(backend), new FileCanvasLayoutStore(new ProjectStorageFiles()), new FakeTimeProvider(T0), new InlineSynchronizationContext());

        await document.OpenAsync();
        document.Canvas.AddNodeCommand.Execute(WorkTaskKind.AgentWork);
        document.Canvas.SaveLayoutCommand.Execute(null);

        Assert.Empty(Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories));
        Assert.Contains(document.Missing, m => m.StartsWith("No se pudo abrir el proyecto para orquestar", StringComparison.Ordinal) && m.Contains(".agentstudio", StringComparison.Ordinal));
        Assert.Equal(NoticeKind.Info, document.Canvas.NoticeKind);
    }
}
