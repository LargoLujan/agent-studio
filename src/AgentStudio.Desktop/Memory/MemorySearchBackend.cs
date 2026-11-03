using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;
using AgentStudio.Application.Semantic;

namespace AgentStudio.Desktop.Memory;

/// <summary>
/// A download of the model ended with the model in place and verified (<see cref="EmbeddingModelState.Ready"/>): the
/// project open at that moment (null: none), whose index the composition refreshes first, and the state the installer left.
/// </summary>
public sealed class ModelInstalledEventArgs(ProjectContext? project, EmbeddingModelStatus status) : EventArgs
{
    public ProjectContext? Project { get; } = project;

    public EmbeddingModelStatus Status { get; } = status ?? throw new ArgumentNullException(nameof(status));
}

/// <summary>
/// What the «Memoria» document uses beyond the memory itself since Phase 12 (docs/17 D-02 to D-06; ADR-0047): the semantic
/// index of the project (the search by meaning and by words, and the state of the model and of the index), the decision to
/// trust the journal, and the installer of the model. Any of them may be missing: the document then says what is not there
/// and keeps searching by words.
/// </summary>
/// <param name="Index">The index of the projects; without it the search goes by words through the memory, and says so.</param>
/// <param name="JournalTrust">«Confiar en la bitácora de este proyecto», and by days; without it the panel says it is not available.</param>
/// <param name="Installer">Downloads the model, only after the person confirms; without it no download is offered.</param>
/// <param name="UiContext">
/// The context of the UI thread, where the progress of a download is shown whatever thread reports it; null takes the one
/// of the thread that starts the download.
/// </param>
public sealed record MemorySearchBackend(
    ISemanticIndex? Index = null,
    IJournalTrust? JournalTrust = null,
    IEmbeddingModelInstaller? Installer = null,
    SynchronizationContext? UiContext = null)
{
    /// <summary>The model the installer downloads, pinned in the code (its files, sizes, addresses and hashes).</summary>
    public EmbeddingModelDescriptor Model { get; init; } = EmbeddingModels.MultilingualE5Small;
}
