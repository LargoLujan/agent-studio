using AgentStudio.Application.Knowledge;
using AgentStudio.Application.Projects;

namespace AgentStudio.Memory.Context;

/// <summary>
/// The brief of a project (docs/17 D-10, ADR-0052): a memory entry of <see cref="MemoryKind.ProjectBrief"/> that the person
/// wrote (<see cref="MemoryOrigin.User"/>), under the topic <see cref="ProjectBriefs.Topic"/> and citing
/// <see cref="ProjectBriefs.Source"/>. It follows every rule of the memory, because it is written through it
/// (<see cref="IProjectMemory"/>): announced in the log with its SHA-256 before its row, redacted, at most
/// <see cref="ProjectBriefs.MaxCharacters"/>, failing closed on a log that does not verify and waiting while a run or a
/// planning of the project goes on.
/// <list type="bullet">
/// <item>Each new version supersedes the one in force, so the brief in force is the trusted version that nothing supersedes;
/// with more than one (two writers at once), the newest.</item>
/// <item>Saving the text in force again writes nothing: it returns the brief in force.</item>
/// <item>The brief is never part of the pack of memory (no tier asks for its category); it reaches a prompt in its own block of
/// data (<see cref="ContextPacker"/>), and never the judge's nor an auditor's.</item>
/// </list>
/// </summary>
public sealed class ProjectBriefStore(IProjectMemory memory) : IProjectBrief
{
    /// <summary>How many versions in force are looked at; more than one only when two were written at once.</summary>
    private const int MaxInForce = 20;

    private static readonly MemoryKind[] Kinds = [MemoryKind.ProjectBrief];

    public async Task<ProjectBrief?> ReadAsync(ProjectContext project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entry = await InForceAsync(project, ct).ConfigureAwait(false);
        return entry is null ? null : new ProjectBrief(entry.Id, entry.Content, entry.ContentSha256, entry.CreatedAt);
    }

    public async Task<ProjectBrief> SaveAsync(ProjectContext project, string text, string decidedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        if (text.Length > ProjectBriefs.MaxCharacters)
        {
            throw new ArgumentException($"The brief has {text.Length} characters; it may have at most {ProjectBriefs.MaxCharacters}.", nameof(text));
        }

        var current = await InForceAsync(project, ct).ConfigureAwait(false);
        if (current is not null && string.Equals(current.Content, text, StringComparison.Ordinal))
        {
            return new ProjectBrief(current.Id, current.Content, current.ContentSha256, current.CreatedAt);
        }

        var saved = await memory.RecordAsync(
            project,
            new MemoryDraft(MemoryKind.ProjectBrief, ProjectBriefs.Topic, text, MemoryOrigin.User, ProjectBriefs.Source, current?.Id),
            ct).ConfigureAwait(false);
        return new ProjectBrief(saved.Id, saved.Content, saved.ContentSha256, saved.CreatedAt);
    }

    /// <summary>The trusted version that nothing supersedes, the newest of them; null when the project has none.</summary>
    private async Task<MemoryEntry?> InForceAsync(ProjectContext project, CancellationToken ct)
    {
        var entries = await memory.RecallAsync(project, new MemoryQuery(Kinds: Kinds, Topic: ProjectBriefs.Topic, MaxEntries: MaxInForce, TrustedOnly: true), ct).ConfigureAwait(false);
        return entries
            .Where(e => e.Kind == MemoryKind.ProjectBrief && e.SupersededBy is null)
            .OrderByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
