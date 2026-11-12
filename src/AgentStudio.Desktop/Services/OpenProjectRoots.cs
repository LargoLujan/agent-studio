using System.IO;
using AgentStudio.Application.Execution;
using AgentStudio.Infrastructure.Processes;

namespace AgentStudio.Desktop.Services;

/// <summary>
/// The folders no program is resolved from (<see cref="IUntrustedRoots"/>, ADR-0018 addendum of 2026-11-12): the root of every project
/// opened in this session, and the fixed folders the app gives the agents' copies of work. A repository, or an agent that works in
/// one of those folders, can plant a <c>claude.cmd</c>, a <c>git.exe</c> or a <c>node.exe</c> there, so none of them is trusted even
/// when the PATH reaches it with an absolute path or through a link.
/// </summary>
/// <remarks>
/// A project stays in the list until the app ends, not only while it is the open one: a run of it goes on in the background when
/// another project is opened, and its folder is no more trustworthy for that. The list is replaced as a whole (an immutable snapshot),
/// so the threads that read it never see it half changed.
/// A folder too wide to be hostile (the whole profile, a program folder, a drive) is in <see cref="Roots"/> as it was opened and in
/// <see cref="BroadRoots"/>; the locator ignores it, and the app tells the person so (ADR-0018, second addendum of 2026-11-12).
/// </remarks>
public sealed class OpenProjectRoots : IUntrustedRoots
{
    private readonly object _gate = new();
    private IReadOnlyList<string> _roots;
    private IReadOnlyList<string> _broad = [];

    /// <param name="fixedRoots">Folders that are untrusted from the start (the worktree root of the runs); the ones that are not a full path are ignored.</param>
    public OpenProjectRoots(IEnumerable<string>? fixedRoots = null)
    {
        _roots = (fixedRoots ?? []).Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r)).Select(AppSettings.NormalizePath).ToList();
    }

    public IReadOnlyList<string> Roots => Volatile.Read(ref _roots);

    /// <summary>The opened folders that are too wide for the protection to apply to them (the whole profile, a drive...).</summary>
    public IReadOnlyList<string> BroadRoots => Volatile.Read(ref _broad);

    /// <summary>Adds the root of a project that is being opened. Idempotent; a path that is not a full one is ignored.</summary>
    public void Open(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathFullyQualified(projectRoot))
        {
            return;
        }

        var normalized = AppSettings.NormalizePath(projectRoot);
        lock (_gate)
        {
            if (_roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Volatile.Write(ref _roots, [.. _roots, normalized]);
            if (BroadFolders.IsTooBroad(normalized))
            {
                Volatile.Write(ref _broad, [.. _broad, normalized]);
            }
        }
    }
}
