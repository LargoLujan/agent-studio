using AgentStudio.Domain.Providers;

namespace AgentStudio.Application.Providers;

// Tanda 2 contract (docs/18 P37): the end of every agent run of the process, so the quota of Codex can be read once after
// a run of Codex (docs/14 D-15), with the user's opt-in.

/// <summary>An agent run of <paramref name="Provider"/> ended, with its status when it completed (null when it failed to complete).</summary>
public sealed record AgentRunEnded(ProviderId Provider, AgentRunStatus? Status);

/// <summary>
/// Says when an agent run of any provider ended: every run of the process, an orchestration, a debate, an audit, a judge or an
/// auxiliary run (the provider pipeline sees them all). <see cref="Ended"/> is raised on the thread the run ended on.
/// </summary>
public interface IAgentRunEnds
{
    event EventHandler<AgentRunEnded>? Ended;
}

/// <summary>
/// The ends of the agent runs, as the provider pipeline notes them (<see cref="QuotaObservingAgentProvider"/>). One for the
/// process; a subscriber that throws never touches the run, nor the other subscribers.
/// </summary>
public sealed class AgentRunEnds : IAgentRunEnds
{
    public event EventHandler<AgentRunEnded>? Ended;

    /// <summary>Notes that a run of <paramref name="ended"/>'s provider ended.</summary>
    public void Notify(AgentRunEnded ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        foreach (var handler in Ended?.GetInvocationList().OfType<EventHandler<AgentRunEnded>>() ?? [])
        {
            try
            {
                handler(this, ended);
            }
#pragma warning disable CA1031 // A subscriber only shows: its failure must never reach the run nor the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                System.Diagnostics.Debug.WriteLine($"Agent run ends: a subscriber failed: {ex.Message}");
            }
        }
    }
}
