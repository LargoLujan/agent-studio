using AgentStudio.Application.Decisions;
using AgentStudio.Domain.Decisions;

namespace AgentStudio.Tests.Application;

/// <summary>
/// The anchors outside the projects, in memory, one per project id as the location's anchor of that project. It keeps every
/// anchor written, so a test can tell that nothing was anchored. <see cref="Foreign"/> puts another project's anchor at a
/// project's location, until that project's own is written there.
/// </summary>
internal sealed class MemoryDecisionLogAnchors : IDecisionLogAnchorStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DecisionLogAnchor> _anchors = new(StringComparer.Ordinal);
    private readonly List<DecisionLogAnchor> _written = [];

    /// <summary>The project id and entry count of the anchor of another project found at a project's location.</summary>
    public Dictionary<string, (string Project, long Entries)> Foreign { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<DecisionLogAnchor> Written
    {
        get
        {
            lock (_gate)
            {
                return [.. _written];
            }
        }
    }

    public DecisionLogAnchor? Of(string projectId)
    {
        lock (_gate)
        {
            return _anchors.GetValueOrDefault(projectId);
        }
    }

    public Task<DecisionLogAnchor?> ReadAsync(string projectId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Foreign.TryGetValue(projectId, out var other))
            {
                throw new DecisionLogAnchorMismatchException($"The anchor of this location belongs to project '{other.Project}'.", other.Project, other.Entries);
            }
        }

        return Task.FromResult(Of(projectId));
    }

    public Task WriteAsync(string projectId, DecisionLogAnchor anchor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Foreign.Remove(projectId);
            _anchors[projectId] = anchor;
            _written.Add(anchor);
        }

        return Task.CompletedTask;
    }
}
