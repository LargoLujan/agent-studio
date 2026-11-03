using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;

namespace AgentStudio.Memory.Journal;

/// <summary>
/// Opens a <see cref="MarkdownDailyJournal"/> under the project's <c>.agentstudio/bitacora</c> folder, written through
/// <paramref name="files"/>, the policy of the project's storage.
/// </summary>
public sealed class MarkdownDailyJournalFactory(IProjectStorageFiles files, TimeProvider timeProvider) : IPromptJournalFactory
{
    public IPromptJournal ForProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return new MarkdownDailyJournal(projectRoot, files, timeProvider);
    }
}
