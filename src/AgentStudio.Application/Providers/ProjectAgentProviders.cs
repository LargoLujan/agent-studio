namespace AgentStudio.Application.Providers;

/// <summary>
/// The agent providers available to one project. Every provider returned is already composed with that project's
/// mandatory prompt journal: bare adapters are never handed out, so no prompt can skip the journal.
/// </summary>
public interface IProjectAgentProviders
{
    /// <summary>One provider per supported CLI for <paramref name="projectRoot"/>, in a stable order.</summary>
    IReadOnlyList<IAgentProvider> For(string projectRoot);
}
